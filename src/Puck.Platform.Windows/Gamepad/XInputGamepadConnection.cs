using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Commands;
using Puck.Input;
using Puck.Input.Devices;
using Puck.Input.Output;

namespace Puck.Platform.Windows.Gamepad;

/// <summary>
/// An XInput-backed controller (Xbox and XInput-compatible pads). It has no read loop of its own: the acquisition
/// source's shared poll thread calls <see cref="Apply"/> with each polled state and <see cref="ServiceOutput"/>
/// once per tick, and it exposes the same <see cref="IGamepadConnection"/> surface as the HID devices so it flows
/// through the identical coalescer and command pipeline. Xbox pads have no motion sensor.
/// </summary>
/// <remarks>
/// Rumble takes one path per write: the four GameInput motors (body and impulse triggers) once the slot is bound
/// to its GameInput device, and the two XInput body motors until then. The body pair and the trigger pair expire
/// independently, and a zero-duration effect stops only its own pair. The Share button and Elite paddles are not
/// reachable through XInput; a plugged-in headset reads through the headset battery report.
/// </remarks>
public sealed class XInputGamepadConnection : IGamepadConnection {
    private const int CorrelationIntervalTicks = 12; // ~48 ms at 250 Hz between GameInput bind attempts
    private const short LeftThumbDeadzone = 7849;   // XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE
    private const short RightThumbDeadzone = 8689;  // XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE
    private const byte TriggerThreshold = 30;       // XINPUT_GAMEPAD_TRIGGER_THRESHOLD
    private const ushort VendorMicrosoft = 0x045E;

    // The Xbox Series X|S controller's product ids (USB and Bluetooth LE); every other XInput pad reports as an
    // Xbox One controller.
    private static ReadOnlySpan<ushort> SeriesProductIds => [0x0B12, 0x0B13,];

    private readonly IInputClock m_clock;
    private readonly GamepadCoalescer m_coalescer = new();
    private readonly Lock m_gate = new();
    private readonly GameInputHaptics? m_haptics;
    private readonly GamepadOutput m_output;
    private readonly GamepadOutputQueue m_outputQueue = new();
    private readonly uint m_slot;
    private readonly TimeProvider m_time;

    private GamepadAccessories m_accessories;
    private long m_bodyExpiry = long.MaxValue;
    private int m_correlationCountdown;
    private volatile bool m_disposed;
    private volatile bool m_faulted;
    private bool m_hasPacket;
    private float m_highFrequency;
    private uint m_lastPacketNumber;
    private uint m_latestGameInputButtons;
    private float m_leftTrigger;
    private float m_lowFrequency;
    private GamepadPower m_power;
    private float m_rightTrigger;
    private bool m_soleBound;
    private long m_soleTopologyVersion;
    private IGameInputDevice? m_stateDevice;
    private long m_triggerExpiry = long.MaxValue;
    private ulong m_sequence;

    /// <summary>Initializes a new instance of the <see cref="XInputGamepadConnection"/> class.</summary>
    /// <param name="slot">The XInput user index, 0 through 3.</param>
    /// <param name="deviceId">The device's stable identity.</param>
    /// <param name="playerIndex">The zero-based player slot.</param>
    /// <param name="haptics">The GameInput rumble service, or <see langword="null"/> where GameInput is unavailable.</param>
    /// <param name="clock">The capture clock that stamps each polled state's arrival.</param>
    /// <param name="type">The controller family the slot's product id identifies.</param>
    /// <param name="timeProvider">The time source rumble durations expire on; <see langword="null"/> reads the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is <see langword="null"/>.</exception>
    public XInputGamepadConnection(uint slot, InputDeviceId deviceId, int playerIndex, GameInputHaptics? haptics, IInputClock clock, GamepadType type, TimeProvider? timeProvider = null) {
        ArgumentNullException.ThrowIfNull(clock);

        DeviceId = deviceId;
        PlayerIndex = playerIndex;
        Type = type;
        m_clock = clock;
        m_haptics = haptics;
        m_output = new GamepadOutput(
            // Only GameInput reaches the impulse-trigger motors.
            capabilities: ((haptics is not null)
            ? GamepadOutputCapabilities.Rumble | GamepadOutputCapabilities.TriggerRumble
            : GamepadOutputCapabilities.Rumble),
            deviceId: deviceId,
            queue: m_outputQueue
        );
        m_slot = slot;
        m_time = (timeProvider ?? TimeProvider.System);
    }

    /// <inheritdoc />
    public GamepadCoalescer Coalescer => m_coalescer;
    /// <inheritdoc />
    public InputDeviceId DeviceId { get; }
    /// <summary>
    /// The GameInput device this connection rumbles, correlated to this XInput slot and reused for the
    /// connection's whole life so its writes always reach the same controller. Owned by the haptics device
    /// dictionary; this is a borrowed reference cleared (not released) on dispose.
    /// </summary>
    public IGameInputDevice? GameInputDevice { get; set; }
    /// <inheritdoc />
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.AnalogTriggers;
    /// <inheritdoc />
    public bool IsFaulted => m_faulted;
    /// <inheritdoc />
    public string Key => $"xinput:{m_slot}";
    /// <summary>The latest gamepad buttons in GameInput's bitmask layout, used to correlate this slot to its device.</summary>
    public uint LatestGameInputButtons => m_latestGameInputButtons;
    /// <inheritdoc />
    public IGamepadOutput Output => m_output;
    /// <inheritdoc />
    public int PlayerIndex { get; }
    /// <summary>The XInput user index (0-3) this connection polls.</summary>
    public uint Slot => m_slot;
    /// <inheritdoc />
    public GamepadType Type { get; }

    private void ApplyRumble(RumbleEffect effect) {
        if (effect.DurationMilliseconds == 0u) {
            StopBody();

            return;
        }

        m_highFrequency = NormalizeIntensity(intensity: effect.HighFrequency);
        m_lowFrequency = NormalizeIntensity(intensity: effect.LowFrequency);
        m_bodyExpiry = ScheduleExpiry(
            active: ((m_lowFrequency > 0f) || (m_highFrequency > 0f)),
            durationMilliseconds: effect.DurationMilliseconds
        );
    }
    private void ApplyTriggerRumble(TriggerRumbleEffect effect) {
        if (effect.DurationMilliseconds == 0u) {
            StopTriggers();

            return;
        }

        m_leftTrigger = NormalizeIntensity(intensity: effect.Left);
        m_rightTrigger = NormalizeIntensity(intensity: effect.Right);
        m_triggerExpiry = ScheduleExpiry(
            active: ((m_leftTrigger > 0f) || (m_rightTrigger > 0f)),
            durationMilliseconds: effect.DurationMilliseconds
        );
    }
    private static GamepadState Convert(in XInputGamepad pad) {
        var raw = pad.Buttons;
        var buttons = GamepadButtons.None;

        // Face buttons by position: A (bottom) → South, B (right) → East, X (left) → West, Y (top) → North.
        if (0 != (raw & 0x1000)) { buttons |= GamepadButtons.ButtonSouth; }
        if (0 != (raw & 0x2000)) { buttons |= GamepadButtons.ButtonEast; }
        if (0 != (raw & 0x4000)) { buttons |= GamepadButtons.ButtonWest; }
        if (0 != (raw & 0x8000)) { buttons |= GamepadButtons.ButtonNorth; }
        if (0 != (raw & 0x0001)) { buttons |= GamepadButtons.DpadUp; }
        if (0 != (raw & 0x0002)) { buttons |= GamepadButtons.DpadDown; }
        if (0 != (raw & 0x0004)) { buttons |= GamepadButtons.DpadLeft; }
        if (0 != (raw & 0x0008)) { buttons |= GamepadButtons.DpadRight; }
        if (0 != (raw & 0x0010)) { buttons |= GamepadButtons.Start; }
        if (0 != (raw & 0x0020)) { buttons |= GamepadButtons.Back; }
        if (0 != (raw & 0x0040)) { buttons |= GamepadButtons.LeftStickPress; }
        if (0 != (raw & 0x0080)) { buttons |= GamepadButtons.RightStickPress; }
        if (0 != (raw & 0x0100)) { buttons |= GamepadButtons.LeftShoulder; }
        if (0 != (raw & 0x0200)) { buttons |= GamepadButtons.RightShoulder; }
        if (0 != (raw & XInput.GamepadGuide)) { buttons |= GamepadButtons.Guide; }

        return new GamepadState(
            Buttons: buttons,
            Gyro: Vector3.Zero,
            LeftStick: NormalizeStick(
                deadzone: LeftThumbDeadzone,
                rawX: pad.ThumbLeftX,
                rawY: pad.ThumbLeftY
            ),
            LeftTrigger: GamepadNormalization.NormalizeTrigger(
                range: byte.MaxValue,
                raw: pad.LeftTrigger,
                threshold: TriggerThreshold
            ),
            Orientation: Quaternion.Identity,
            RightStick: NormalizeStick(
                deadzone: RightThumbDeadzone,
                rawX: pad.ThumbRightX,
                rawY: pad.ThumbRightY
            ),
            RightTrigger: GamepadNormalization.NormalizeTrigger(
                range: byte.MaxValue,
                raw: pad.RightTrigger,
                threshold: TriggerThreshold
            )
        );
    }
    private static float NormalizeIntensity(float intensity) =>
        (float.IsFinite(f: intensity)
            ? Math.Clamp(
                max: 1f,
                min: 0f,
                value: intensity
            )
            : 0f
        );
    private static Vector2 NormalizeStick(short rawX, short rawY, short deadzone) {
        var stick = Vector2.Clamp(
            max: Vector2.One,
            min: -Vector2.One,
            value1: new Vector2(
                x: (rawX / 32767f),
                y: (rawY / 32767f)
            )
        );

        return GamepadNormalization.ApplyRadialDeadzone(
            deadzone: (deadzone / 32767f),
            stick: stick
        );
    }
    private GameInputRumbleParams CurrentRumble() =>
        new() {
            HighFrequency = m_highFrequency,
            LeftTrigger = m_leftTrigger,
            LowFrequency = m_lowFrequency,
            RightTrigger = m_rightTrigger,
        };
    private bool HasMotionOn() =>
        ((m_lowFrequency > 0f) || (m_highFrequency > 0f) || (m_leftTrigger > 0f) || (m_rightTrigger > 0f));
    // Drops a sole binding the moment the topology that justified it changes: a second slot connected, or a second
    // GameInput device appeared, so the only-device assumption no longer holds.
    private void DropSoleBindingIfStale(bool soleConnection) {
        if (
            !m_soleBound ||
            (m_haptics is null)
        ) {
            return;
        }

        if (
            soleConnection &&
            (m_haptics.TopologyVersion == m_soleTopologyVersion)
        ) {
            return;
        }

        var device = GameInputDevice;

        if (device is not null) {
            m_haptics.Unbind(device: device);
        }

        GameInputDevice = null;
        m_soleBound = false;
        m_stateDevice = null;
    }
    private long ScheduleExpiry(bool active, uint durationMilliseconds) =>
        (active
            ? (m_time.GetTimestamp() + ((long)(durationMilliseconds * (m_time.TimestampFrequency / 1000.0))))
            : long.MaxValue
        );
    private void StopBody() {
        m_bodyExpiry = long.MaxValue;
        m_highFrequency = 0f;
        m_lowFrequency = 0f;
    }
    private void StopTriggers() {
        m_leftTrigger = 0f;
        m_rightTrigger = 0f;
        m_triggerExpiry = long.MaxValue;
    }
    // Maps XInput's button bitmask to GameInput's, so a slot's held buttons can be matched against a GameInput
    // device's reading to correlate the two identity spaces.
    private static uint ToGameInputButtons(ushort xinputButtons) {
        var result = 0u;

        if (0 != (xinputButtons & 0x1000)) { result |= 0x00000004u; }   // A      → South
        if (0 != (xinputButtons & 0x2000)) { result |= 0x00000008u; }   // B      → East
        if (0 != (xinputButtons & 0x4000)) { result |= 0x00000010u; }   // X      → West
        if (0 != (xinputButtons & 0x8000)) { result |= 0x00000020u; }   // Y      → North
        if (0 != (xinputButtons & 0x0001)) { result |= 0x00000040u; }   // DpadUp
        if (0 != (xinputButtons & 0x0002)) { result |= 0x00000080u; }   // DpadDown
        if (0 != (xinputButtons & 0x0004)) { result |= 0x00000100u; }   // DpadLeft
        if (0 != (xinputButtons & 0x0008)) { result |= 0x00000200u; }   // DpadRight
        if (0 != (xinputButtons & 0x0010)) { result |= 0x00000001u; }   // Start  → Menu
        if (0 != (xinputButtons & 0x0020)) { result |= 0x00000002u; }   // Back   → View
        if (0 != (xinputButtons & 0x0040)) { result |= 0x00001000u; }   // LThumb
        if (0 != (xinputButtons & 0x0080)) { result |= 0x00002000u; }   // RThumb
        if (0 != (xinputButtons & 0x0100)) { result |= 0x00000400u; }   // LShoulder
        if (0 != (xinputButtons & 0x0200)) { result |= 0x00000800u; }   // RShoulder

        return result;
    }

    /// <summary>Classifies an XInput pad from its USB identity.</summary>
    /// <param name="vendorId">The pad's USB vendor id.</param>
    /// <param name="productId">The pad's USB product id.</param>
    /// <returns><see cref="GamepadType.XboxSeries"/> for an Xbox Series X|S controller; otherwise
    /// <see cref="GamepadType.XboxOne"/>.</returns>
    public static GamepadType ClassifyType(ushort vendorId, ushort productId) {
        return (((VendorMicrosoft == vendorId) && SeriesProductIds.Contains(value: productId))
            ? GamepadType.XboxSeries
            : GamepadType.XboxOne
        );
    }
    /// <summary>Converts an XInput battery report into a power report.</summary>
    /// <param name="information">The battery report of <c>XInputGetBatteryInformation</c>.</param>
    /// <returns>The power report: a cable-powered pad has no battery, and the four charge levels read as 10, 40,
    /// 70 and 100 percent (SDL).</returns>
    public static GamepadPower ToPower(in XInputBatteryInformation information) {
        return information.BatteryType switch {
            XInput.BatteryTypeWired => new GamepadPower(
                Percent: 0,
                State: GamepadPowerState.NoBattery
            ),
            XInput.BatteryTypeAlkaline or XInput.BatteryTypeNickelMetalHydride => new GamepadPower(
                Percent: ((byte)((Math.Min(
                    val1: information.BatteryLevel,
                    val2: ((byte)3)
                ) * 30) + 10)),
                State: GamepadPowerState.OnBattery
            ),
            _ => GamepadPower.Unknown,
        };
    }
    /// <summary>Converts an XInput headset battery report into the accessories it implies.</summary>
    /// <param name="information">The headset battery report of <c>XInputGetBatteryInformation</c>.</param>
    /// <returns>Headphones and microphone when a headset is plugged in (any battery type but disconnected);
    /// otherwise none. XInput reports one headset device, so it cannot tell the two apart.</returns>
    public static GamepadAccessories ToAccessories(in XInputBatteryInformation information) =>
        ((XInput.BatteryTypeDisconnected == information.BatteryType)
            ? GamepadAccessories.None
            : (GamepadAccessories.Headphones | GamepadAccessories.Microphone)
        );
    /// <summary>Feeds a freshly polled XInput state into the coalescer (called on the poll thread). A state whose
    /// packet number has not changed is the same report again and is not fed. Does nothing once disposed.</summary>
    /// <param name="state">The polled state.</param>
    public void Apply(in XInputState state) {
        lock (m_gate) {
            if (
                m_disposed ||
                (m_hasPacket && (state.PacketNumber == m_lastPacketNumber))
            ) {
                return;
            }

            m_hasPacket = true;
            m_lastPacketNumber = state.PacketNumber;
            m_latestGameInputButtons = ToGameInputButtons(xinputButtons: state.Gamepad.Buttons);

            var converted = Convert(pad: in state.Gamepad) with {
                Accessories = m_accessories,
                ArrivalTicks = m_clock.NowTicks,
                Power = m_power,
                SequenceNumber = ++m_sequence,
            };

            m_coalescer.Update(state: in converted);
        }
    }
    /// <summary>Records a fresh battery report; the next fed state carries it. Does nothing once disposed.</summary>
    /// <param name="information">The battery report of <c>XInputGetBatteryInformation</c> for the gamepad.</param>
    public void ApplyBattery(in XInputBatteryInformation information) {
        lock (m_gate) {
            var power = ToPower(information: in information);

            if (
                m_disposed ||
                (power == m_power)
            ) {
                return;
            }

            m_power = power;
            m_hasPacket = false;    // the next poll re-feeds its state so the new charge reaches the coalescer
        }
    }
    /// <summary>Records a fresh headset report; the next fed state carries it. Does nothing once disposed.</summary>
    /// <param name="information">The battery report of <c>XInputGetBatteryInformation</c> for the headset device:
    /// any battery type but disconnected means a headset (earpieces and microphone) is plugged in.</param>
    public void ApplyHeadset(in XInputBatteryInformation information) {
        lock (m_gate) {
            var accessories = ToAccessories(information: in information);

            if (
                m_disposed ||
                (accessories == m_accessories)
            ) {
                return;
            }

            m_accessories = accessories;
            m_hasPacket = false;    // the next poll re-feeds its state so the new accessory reaches the coalescer
        }
    }
    /// <inheritdoc />
    public void Dispose() {
        lock (m_gate) {
            if (m_disposed) {
                return;
            }

            m_disposed = true;
            m_faulted = true;
            m_output.Kill();

            var device = GameInputDevice;

            if (device is not null) {
                // Silence the motors so a disconnect mid-rumble can't latch a controller on, then release the
                // binding. The handle itself is owned by the haptics dictionary; we only drop our borrow.
                if (OperatingSystem.IsWindows()) {
                    var off = default(GameInputRumbleParams);

                    try {
                        device.SetRumbleState(rumbleParams: in off);
                    } catch (COMException) {
                        // The device is already gone; nothing to silence.
                    } catch (InvalidComObjectException) {
                        // The RCW was torn down; nothing to silence.
                    }
                }

                m_haptics?.Unbind(device: device);
                GameInputDevice = null;
            }

            m_soleBound = false;
            m_stateDevice = null;

            // The XInput-only path (and a pad whose GameInput device never bound) stops here: a disconnect must
            // never leave the body motors latched on.
            var silence = default(XInputVibration);

            try {
                _ = XInput.SetState(
                    userIndex: m_slot,
                    vibration: in silence
                );
            } catch (DllNotFoundException) {
                // XInput is unavailable; there is nothing to silence.
            } catch (EntryPointNotFoundException) {
                // XInput is unavailable; there is nothing to silence.
            }
        }
    }
    /// <summary>
    /// Actuates this connection's pending output on the poll thread: correlates its GameInput device and writes
    /// any changed rumble through exactly one path, the bound GameInput device (all four motors) or else XInput
    /// (the two body motors). Called once per polled tick, after <see cref="Apply"/>. A device bound or replaced
    /// while motors are running receives the current unexpired state at once, without restarting any duration.
    /// Does nothing once disposed.
    /// </summary>
    /// <param name="soleConnection">Whether this is the only connected XInput slot and that topology has held
    /// through the source's settle window, which lets it bind the only GameInput gamepad without waiting for a held
    /// button to disambiguate. A sole binding is dropped as soon as this turns <see langword="false"/> or a second
    /// GameInput device appears.</param>
    public void ServiceOutput(bool soleConnection) {
        lock (m_gate) {
            if (m_disposed) {
                return;
            }

            DropSoleBindingIfStale(soleConnection: soleConnection);

            if (
                (m_haptics is not null) &&
                (GameInputDevice is null)
            ) {
                if (m_correlationCountdown > 0) {
                    --m_correlationCountdown;
                } else {
                    m_correlationCountdown = CorrelationIntervalTicks;

                    var version = 0L;
                    var sole = (soleConnection
                        ? m_haptics.BindSole(topologyVersion: out version)
                        : null
                    );

                    if (sole is not null) {
                        GameInputDevice = sole;
                        m_soleBound = true;
                        m_soleTopologyVersion = version;
                    } else {
                        GameInputDevice = m_haptics.Bind(targetButtons: m_latestGameInputButtons);
                    }
                }
            }

            var changed = TryTakeRumble(rumble: out var rumble);
            var device = GameInputDevice;

            if (!ReferenceEquals(
                objA: device,
                objB: m_stateDevice
            )) {
                if (
                    (device is not null) &&
                    HasMotionOn()
                ) {
                    changed = true;     // a device just bound: hand it the state already running
                } else {
                    m_stateDevice = device;
                }
            }

            if (!changed) {
                return;
            }

            if (
                (m_haptics is not null) &&
                (device is not null)
            ) {
                if (m_haptics.RumbleDevice(
                    device: device,
                    rumbleParams: in rumble
                )) {
                    m_stateDevice = device;

                    return;
                }

                // The bound device disconnected; drop the stale binding and fall back to XInput for this write.
                m_haptics.Unbind(device: device);
                GameInputDevice = null;
                m_soleBound = false;
                m_stateDevice = null;
            }

            var vibration = new XInputVibration {
                LeftMotorSpeed = ((ushort)(rumble.LowFrequency * ushort.MaxValue)),
                RightMotorSpeed = ((ushort)(rumble.HighFrequency * ushort.MaxValue)),
            };

            _ = XInput.SetState(
                userIndex: m_slot,
                vibration: in vibration
            );
        }
    }
    /// <inheritdoc />
    public void Start() {
        // Driven by the acquisition source's shared poll loop; nothing per-connection to start.
    }
    /// <summary>
    /// Drains queued rumble and trigger-rumble requests and honors finite-duration expiry, returning the
    /// four-motor state to apply this tick (called on the poll thread). The body pair and the trigger pair expire
    /// independently: one pair's expiry or zero-duration stop leaves the other pair running.
    /// </summary>
    /// <param name="rumble">The four motor intensities to apply when a write is needed.</param>
    /// <returns><see langword="true"/> if the motors changed and a write should be issued; always
    /// <see langword="false"/> once disposed.</returns>
    public bool TryTakeRumble(out GameInputRumbleParams rumble) {
        lock (m_gate) {
            if (m_disposed) {
                rumble = default;

                return false;
            }

            var changed = false;

            while (m_outputQueue.TryDequeue(command: out var command)) {
                switch (command.Kind) {
                    case GamepadOutputKind.Rumble:
                        ApplyRumble(effect: command.Rumble);
                        changed = true;

                        break;
                    case GamepadOutputKind.TriggerRumble:
                        ApplyTriggerRumble(effect: command.TriggerRumble);
                        changed = true;

                        break;
                    default:
                        break;
                }
            }

            var now = m_time.GetTimestamp();

            if (now >= m_bodyExpiry) {
                StopBody();
                changed = true;
            }

            if (now >= m_triggerExpiry) {
                StopTriggers();
                changed = true;
            }

            rumble = CurrentRumble();

            return changed;
        }
    }
}
