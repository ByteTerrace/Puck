using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Commands;
using Puck.Input.Devices;
using Puck.Input.Output;

namespace Puck.Platform.Windows.Gamepad;

/// <summary>
/// An XInput-backed controller (Xbox). Unlike the HID devices it has no read loop of its own — the manager's
/// shared XInput poll thread calls <see cref="Apply"/> with each polled state and <see cref="TryTakeRumble"/>
/// to flush queued rumble — but it exposes the same <see cref="IGamepadConnection"/> surface so it flows
/// through the identical coalescer → command pipeline. Xbox controllers have no motion sensor, so gyro and
/// orientation are always neutral.
/// </summary>
public sealed class XInputGamepadConnection : IGamepadConnection {
    private const short LeftThumbDeadzone = 7849;   // XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE
    private const short RightThumbDeadzone = 8689;  // XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE
    private const byte TriggerThreshold = 30;       // XINPUT_GAMEPAD_TRIGGER_THRESHOLD
    private const int CorrelationIntervalTicks = 12; // ~48ms at 250Hz between GameInput bind attempts while held

    private readonly GamepadCoalescer m_coalescer = new();
    private readonly GameInputHaptics? m_haptics;
    private readonly GamepadOutput m_output;
    private readonly ConcurrentQueue<GamepadOutputCommand> m_outputQueue = new();
    private readonly uint m_slot;
    private int m_correlationCountdown;
    private volatile bool m_faulted;
    private float m_highFrequency;
    private uint m_latestGameInputButtons;
    private float m_leftTrigger;
    private float m_lowFrequency;
    private bool m_rumbleActive;
    private long m_rumbleExpiry = long.MaxValue;
    private float m_rightTrigger;

    public XInputGamepadConnection(uint slot, InputDeviceId deviceId, int playerIndex, GameInputHaptics? haptics) {
        DeviceId = deviceId;
        PlayerIndex = playerIndex;
        m_haptics = haptics;
        m_output = new GamepadOutput(
            // GameInput drives all four motors, so the Xbox connection advertises trigger (impulse) rumble when
            // GameInput is available; without it only the two main XInput motors can be reached.
            capabilities: ((haptics is not null)
                ? GamepadOutputCapabilities.Rumble | GamepadOutputCapabilities.TriggerRumble
                : GamepadOutputCapabilities.Rumble),
            deviceId: deviceId,
            queue: m_outputQueue
        );
        m_slot = slot;
    }

    /// <inheritdoc />
    public GamepadCoalescer Coalescer => m_coalescer;
    /// <inheritdoc />
    public InputDeviceId DeviceId { get; }
    /// <inheritdoc />
    public bool IsFaulted => m_faulted;
    /// <inheritdoc />
    public string Key => $"xinput:{m_slot}";
    /// <inheritdoc />
    public IGamepadOutput Output => m_output;
    /// <inheritdoc />
    public int PlayerIndex { get; }
    /// <summary>The XInput user index (0-3) this connection polls.</summary>
    public uint Slot => m_slot;
    /// <summary>
    /// The GameInput device this connection rumbles, correlated to this XInput slot by input state and reused
    /// for the connection's whole life so its ON and OFF writes always reach the same controller. Owned by the
    /// haptics device dictionary; this is a borrowed reference cleared (not released) on dispose.
    /// </summary>
    public IGameInputDevice? GameInputDevice { get; set; }
    /// <summary>The latest gamepad buttons in GameInput's bitmask layout, used to correlate this slot to its device.</summary>
    public uint LatestGameInputButtons => m_latestGameInputButtons;
    /// <inheritdoc />
    public GamepadType Type => GamepadType.XboxSeries;
    /// <inheritdoc />
    // Xbox controllers have analog triggers but no motion sensor.
    public GamepadInputCapabilities InputCapabilities => GamepadInputCapabilities.AnalogTriggers;

    /// <inheritdoc />
    public void Start() {
        // Driven by the manager's shared XInput poll loop; nothing per-connection to start.
    }

    /// <summary>
    /// Actuates this connection's pending output on the manager's poll thread: correlates its GameInput device
    /// (throttled, while a distinctive button is held) and writes any changed rumble to BOTH the correlated
    /// GameInput device (all four motors, and the wireless transports XInput can't reach) and the legacy XInput
    /// motors (which reach a pad whose GameInput output is owned by an overlay such as Steam Input). Called once
    /// per polled tick, after <see cref="Apply"/>.
    /// </summary>
    public void ServiceOutput() {
        // Bind this slot to its physical GameInput device while a button is held — the global "current reading"
        // device can't tell controllers apart. Throttled: a human holds a button far longer than the interval,
        // so binding stays imperceptible while avoiding per-tick COM marshalling and lock contention.
        if ((m_haptics is not null) && (GameInputDevice is null) && (m_latestGameInputButtons != 0u)) {
            if (m_correlationCountdown > 0) {
                --m_correlationCountdown;
            } else {
                m_correlationCountdown = CorrelationIntervalTicks;
                GameInputDevice = m_haptics.Bind(targetButtons: m_latestGameInputButtons);
            }
        }

        if (!TryTakeRumble(rumble: out var rumble)) {
            return;
        }

        var device = GameInputDevice;

        if ((m_haptics is not null) && (device is not null)
            && !m_haptics.RumbleDevice(device: device, rumbleParams: in rumble)) {
            // The bound device disconnected; drop the stale binding so the next held button re-correlates.
            m_haptics.Unbind(device: device);
            GameInputDevice = null;
        }

        var vibration = new XInputVibration {
            LeftMotorSpeed = ((ushort)(rumble.LowFrequency * ushort.MaxValue)),
            RightMotorSpeed = ((ushort)(rumble.HighFrequency * ushort.MaxValue)),
        };

        _ = XInput.SetState(userIndex: m_slot, vibration: in vibration);
    }

    /// <summary>Feeds a freshly polled XInput state into the coalescer (called on the poll thread).</summary>
    /// <param name="pad">The polled gamepad state.</param>
    public void Apply(in XInputGamepad pad) {
        var state = Convert(pad: in pad);

        m_latestGameInputButtons = ToGameInputButtons(xinputButtons: pad.Buttons);
        m_coalescer.Update(state: in state);
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

    /// <summary>
    /// Drains queued rumble / trigger-rumble requests and honors finite-duration expiry, returning the
    /// four-motor state to apply this tick (called on the poll thread).
    /// </summary>
    /// <param name="rumble">The four motor intensities to apply when a write is needed.</param>
    /// <returns><see langword="true"/> if the motors changed and a write should be issued.</returns>
    public bool TryTakeRumble(out GameInputRumbleParams rumble) {
        var changed = false;

        while (m_outputQueue.TryDequeue(result: out var command)) {
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

        if (m_rumbleActive && (Stopwatch.GetTimestamp() >= m_rumbleExpiry)) {
            m_highFrequency = 0f;
            m_leftTrigger = 0f;
            m_lowFrequency = 0f;
            m_rightTrigger = 0f;
            m_rumbleActive = false;
            m_rumbleExpiry = long.MaxValue;
            changed = true;
        }

        rumble = new GameInputRumbleParams {
            HighFrequency = m_highFrequency,
            LeftTrigger = m_leftTrigger,
            LowFrequency = m_lowFrequency,
            RightTrigger = m_rightTrigger,
        };

        return changed;
    }

    private void ApplyRumble(RumbleEffect effect) {
        m_highFrequency = Math.Clamp(value: effect.HighFrequency, max: 1f, min: 0f);
        m_lowFrequency = Math.Clamp(value: effect.LowFrequency, max: 1f, min: 0f);
        ScheduleExpiry(durationMilliseconds: effect.DurationMilliseconds);
    }
    private void ApplyTriggerRumble(TriggerRumbleEffect effect) {
        m_leftTrigger = Math.Clamp(value: effect.Left, max: 1f, min: 0f);
        m_rightTrigger = Math.Clamp(value: effect.Right, max: 1f, min: 0f);
        ScheduleExpiry(durationMilliseconds: effect.DurationMilliseconds);
    }
    private void ScheduleExpiry(uint durationMilliseconds) {
        if ((0f >= m_lowFrequency) && (0f >= m_highFrequency) && (0f >= m_leftTrigger) && (0f >= m_rightTrigger)) {
            m_rumbleActive = false;
            m_rumbleExpiry = long.MaxValue;
        } else {
            m_rumbleActive = true;
            m_rumbleExpiry = ((0u < durationMilliseconds)
                ? (Stopwatch.GetTimestamp() + ((long)(durationMilliseconds * (Stopwatch.Frequency / 1000.0))))
                : long.MaxValue);
        }
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
            LeftStick: NormalizeStick(rawX: pad.ThumbLeftX, rawY: pad.ThumbLeftY, deadzone: LeftThumbDeadzone),
            LeftTrigger: NormalizeTrigger(raw: pad.LeftTrigger),
            Orientation: Quaternion.Identity,
            RightStick: NormalizeStick(rawX: pad.ThumbRightX, rawY: pad.ThumbRightY, deadzone: RightThumbDeadzone),
            RightTrigger: NormalizeTrigger(raw: pad.RightTrigger)
        );
    }
    private static Vector2 NormalizeStick(short rawX, short rawY, short deadzone) {
        var stick = new Vector2(x: (rawX / 32767f), y: (rawY / 32767f));
        var magnitude = stick.Length();
        var normalizedDeadzone = (deadzone / 32767f);

        if (magnitude <= normalizedDeadzone) {
            return Vector2.Zero;
        }

        var scaled = ((MathF.Min(x: magnitude, y: 1f) - normalizedDeadzone) / (1f - normalizedDeadzone));

        return ((stick / magnitude) * scaled);
    }
    private static float NormalizeTrigger(byte raw) {
        if (raw <= TriggerThreshold) {
            return 0f;
        }

        return ((raw - TriggerThreshold) / (255f - TriggerThreshold));
    }

    public void Dispose() {
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
    }
}
