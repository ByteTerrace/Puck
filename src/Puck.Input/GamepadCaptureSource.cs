using System.Numerics;
using System.Reflection;

using Puck.Commands;
using Puck.Input.Devices;

namespace Puck.Input;

/// <summary>
/// The snapshot input path's capture step: turns one frame's already-drained per-device state (an
/// <see cref="IInputArbiter"/>'s <see cref="IInputArbiter.CopyDrainedDevices"/>) into provider-neutral, timestamped
/// <see cref="InputSignal"/>s — button press/release edges, stick/trigger/touch/gyro/accel axes, and the fused
/// orientation — appending each to an <see cref="InputRouter"/>. The destructive <see cref="GamepadManager.Drain"/>
/// itself lives one layer up, in the arbiter: this type never drains — its caller drains once
/// (<see cref="IInputArbiter.DrainFrame"/>) and hands the result in.
/// </summary>
public sealed class GamepadCaptureSource {
    // Derived from GamepadButtons itself, not a hand-kept parallel list: every named flag (other than None)
    // must have a same-named constant in InputSources.Gamepad (ButtonSouth -> InputSources.Gamepad.ButtonSouth).
    // Built once via Enum.GetValues at type init and throws immediately if a flag has no matching source,
    // rather than the bit silently never reaching GamepadState.
    private static readonly (GamepadButtons Flag, string Source)[] ButtonSources = BuildButtonSources();

    // Which analog and motion controls each device last reported active, so the first return-to-rest emits an explicit zero
    // without streaming redundant zeroes forever. The edge clears InputRouter's carried sample while ensuring a newly
    // connected, untouched pad does not reserve/join a player lane merely because its sticks are centered.
    // HeldButtons mirrors the presses this source has emitted and not yet released, so a device that leaves the
    // captured set (unplug, fault, receiver park, or focus-predicate rejection) can be given the release edges
    // it can no longer deliver through this path.
    private readonly Dictionary<InputDeviceId, AnalogLatch> m_analogLatches = [];
    private readonly HashSet<InputDeviceId> m_capturedDeviceIds = [];
    private readonly IInputClock m_clock;
    private readonly Func<InputDeviceId, bool> m_isActiveFor;
    private readonly InputRouter m_router;

    private static (GamepadButtons Flag, string Source)[] BuildButtonSources() {
        var gamepadSources = typeof(InputSources.Gamepad);
        var flags = Enum.GetValues<GamepadButtons>();
        var map = new List<(GamepadButtons Flag, string Source)>(capacity: flags.Length);
        var highestBit = -1;

        foreach (var flag in flags) {
            if (flag == GamepadButtons.None) {
                continue;
            }

            var name = flag.ToString();
            var field = (gamepadSources.GetField(
                bindingAttr: BindingFlags.Public | BindingFlags.Static,
                name: name
            )
                ?? throw new InvalidOperationException(message: ((string)$"GamepadButtons.{name} has no matching InputSources.Gamepad.{name} source constant. Every digital button must be reachable as an InputSignal source or it can never be bound (nor synthesized by an addon). Add 'public const string {name} = \"gamepad.{char.ToLowerInvariant(c: name[0])}{name[1..]}\";' to InputSources.Gamepad.")));

            map.Add(item: (flag, ((string)field.GetValue(obj: null)!)));

            var bit = BitOperations.TrailingZeroCount(value: ((uint)flag));

            if (bit > highestBit) {
                highestBit = bit;
            }
        }

        // The sibling hand-sync: GamepadButtonEdges reserves one press-stamp slot per bit, sized by a compile-time
        // constant (InlineArray requires one) that cannot itself be derived from the enum at compile time. This
        // assert is the runtime backstop — it fails loudly, once, at the same type-init point as the check above,
        // instead of the coalescer indexing out of range on a fresh pad's first press of the forgotten button.
        if ((highestBit + 1) > GamepadButtonEdges.Count) {
            throw new InvalidOperationException(message: ((string)$"GamepadButtons defines a flag at bit {highestBit} but GamepadButtonEdges.Count is only {GamepadButtonEdges.Count}. Bump GamepadButtonEdges.Count to at least {(highestBit + 1)}."));
        }

        return [.. map];
    }
    // A device absent from this frame's accepted set — disconnected OR rejected by the focus predicate — can no
    // longer deliver a return-to-rest through this capture path. InputRouter carries samples until an explicit
    // zero/release, so the latch pays every edge it still owes before it is dropped. Removing during enumeration
    // is supported by Dictionary on the repository's net10.0 target.
    private void ClearDepartedDevices(ulong frameTick) {
        foreach (var (deviceId, latch) in m_analogLatches) {
            if (m_capturedDeviceIds.Contains(item: deviceId)) {
                continue;
            }

            foreach (var (flag, source) in ButtonSources) {
                if (0 != (latch.HeldButtons & flag)) {
                    m_router.Capture(signal: InputSignal.Release(
                        captureTick: frameTick,
                        deviceId: deviceId,
                        source: source
                    ));
                }
            }

            if (latch.LeftStick) {
                _ = EmitStick(
                    deviceId: deviceId,
                    source: InputSources.Gamepad.LeftStick,
                    tick: frameTick,
                    value: Vector2.Zero,
                    wasActive: true
                );
            }

            if (latch.RightStick) {
                _ = EmitStick(
                    deviceId: deviceId,
                    source: InputSources.Gamepad.RightStick,
                    tick: frameTick,
                    value: Vector2.Zero,
                    wasActive: true
                );
            }

            if (latch.Touch0) {
                _ = EmitTouch(
                    deviceId: deviceId,
                    source: InputSources.Gamepad.Touchpad0,
                    tick: frameTick,
                    touch: default,
                    wasActive: true
                );
            }

            if (latch.Touch1) {
                _ = EmitTouch(
                    deviceId: deviceId,
                    source: InputSources.Gamepad.Touchpad1,
                    tick: frameTick,
                    touch: default,
                    wasActive: true
                );
            }

            if (latch.LeftTrigger) {
                _ = EmitTrigger(
                    deviceId: deviceId,
                    source: InputSources.Gamepad.LeftTrigger,
                    tick: frameTick,
                    value: 0f,
                    wasActive: true
                );
            }

            if (latch.RightTrigger) {
                _ = EmitTrigger(
                    deviceId: deviceId,
                    source: InputSources.Gamepad.RightTrigger,
                    tick: frameTick,
                    value: 0f,
                    wasActive: true
                );
            }

            if (latch.Gyro) {
                _ = EmitGyro(
                    deviceId: deviceId,
                    gyro: Vector3.Zero,
                    tick: frameTick,
                    wasActive: true
                );
            }

            if (latch.MotionPose) {
                _ = EmitMotionPose(
                    deviceId: deviceId,
                    latest: default,
                    tick: frameTick,
                    wasActive: true
                );
            }

            _ = m_analogLatches.Remove(key: deviceId);
        }
    }
    // The accelerometer reads gravity at rest, so a device that has one streams continuously and drives the fused
    // orientation on the same gate. The first missing sample explicitly clears both carried values; this matters to
    // future tilt controls just as the gyro clear matters to current motion look.
    private bool EmitMotionPose(InputDeviceId deviceId, in GamepadState latest, ulong tick, bool wasActive) {
        if (Vector3.Zero == latest.Accelerometer) {
            if (wasActive) {
                m_router.Capture(signal: new InputSignal(
                    CaptureTick: tick,
                    DeviceId: deviceId,
                    Phase: CommandPhase.Active,
                    Source: InputSources.Gamepad.Accelerometer,
                    Value: CommandValue.Axis(value: Vector3.Zero)
                ));
                m_router.Capture(signal: new InputSignal(
                    CaptureTick: tick,
                    DeviceId: deviceId,
                    Phase: CommandPhase.Active,
                    Source: InputSources.Gamepad.Orientation,
                    Value: CommandValue.Inactive(kind: CommandValueKind.Orientation)
                ));
            }

            return false;
        }

        m_router.Capture(signal: new InputSignal(
            CaptureTick: tick,
            DeviceId: deviceId,
            Phase: CommandPhase.Active,
            Source: InputSources.Gamepad.Accelerometer,
            Value: CommandValue.Axis(value: latest.Accelerometer)
        ));
        m_router.Capture(signal: new InputSignal(
            CaptureTick: tick,
            DeviceId: deviceId,
            Phase: CommandPhase.Active,
            Source: InputSources.Gamepad.Orientation,
            Value: CommandValue.Orientation(value: latest.Orientation)
        ));

        return true;
    }
    private bool EmitGyro(InputDeviceId deviceId, Vector3 gyro, ulong tick, bool wasActive) {
        if (gyro != Vector3.Zero) {
            m_router.Capture(signal: new InputSignal(
                CaptureTick: tick,
                DeviceId: deviceId,
                Phase: CommandPhase.Active,
                Source: InputSources.Gamepad.Gyro,
                Value: CommandValue.Axis(value: gyro)
            ));

            return true;
        }

        if (wasActive) {
            m_router.Capture(signal: new InputSignal(
                CaptureTick: tick,
                DeviceId: deviceId,
                Phase: CommandPhase.Active,
                Source: InputSources.Gamepad.Gyro,
                Value: CommandValue.Axis(value: Vector3.Zero)
            ));
        }

        return false;
    }
    private void EmitSignals(in GamepadDrain drain, ulong frameTick) {
        var deviceId = drain.DeviceId;
        var latest = drain.Latest;
        // The latest report's arrival stamps continuous signals (axes/triggers/motion) and release edges; each
        // press edge gets its own first-press time below. A zero stamp (unstamped report) falls back to the frame.
        var latestTick = ((latest.ArrivalTicks != 0UL)
            ? latest.ArrivalTicks
            : frameTick
        );
        var edges = drain.PressEdges;

        _ = m_analogLatches.TryGetValue(
            key: deviceId,
            value: out var analogLatch
        );

        foreach (var (flag, source) in ButtonSources) {
            if (0 != (drain.Pressed & flag)) {
                var edge = edges[BitOperations.TrailingZeroCount(value: ((uint)flag))];

                m_router.Capture(signal: InputSignal.Press(
                    captureTick: ((edge != 0UL)
                    ? edge
                    : latestTick),
                    deviceId: deviceId,
                    source: source
                ));
            }

            if (0 != (drain.Released & flag)) {
                m_router.Capture(signal: InputSignal.Release(
                    captureTick: latestTick,
                    deviceId: deviceId,
                    source: source
                ));
            }
        }

        // Sticks are sampled state, not impulses. Stream active values and emit exactly one zero at the return to rest;
        // InputRouter carries the latest active sample across fixed ticks, so that zero is the explicit clear edge.
        analogLatch.LeftStick = EmitStick(
            deviceId: deviceId,
            source: InputSources.Gamepad.LeftStick,
            tick: latestTick,
            value: latest.LeftStick,
            wasActive: analogLatch.LeftStick
        );
        analogLatch.RightStick = EmitStick(
            deviceId: deviceId,
            source: InputSources.Gamepad.RightStick,
            tick: latestTick,
            value: latest.RightStick,
            wasActive: analogLatch.RightStick
        );

        analogLatch.Touch0 = EmitTouch(
            deviceId: deviceId,
            source: InputSources.Gamepad.Touchpad0,
            tick: latestTick,
            touch: latest.Touch0,
            wasActive: analogLatch.Touch0
        );
        analogLatch.Touch1 = EmitTouch(
            deviceId: deviceId,
            source: InputSources.Gamepad.Touchpad1,
            tick: latestTick,
            touch: latest.Touch1,
            wasActive: analogLatch.Touch1
        );

        analogLatch.LeftTrigger = EmitTrigger(
            deviceId: deviceId,
            source: InputSources.Gamepad.LeftTrigger,
            tick: latestTick,
            value: latest.LeftTrigger,
            wasActive: analogLatch.LeftTrigger
        );
        analogLatch.RightTrigger = EmitTrigger(
            deviceId: deviceId,
            source: InputSources.Gamepad.RightTrigger,
            tick: latestTick,
            value: latest.RightTrigger,
            wasActive: analogLatch.RightTrigger
        );
        analogLatch.Gyro = EmitGyro(
            deviceId: deviceId,
            gyro: drain.Gyro,
            tick: latestTick,
            wasActive: analogLatch.Gyro
        );
        analogLatch.MotionPose = EmitMotionPose(
            deviceId: deviceId,
            latest: in latest,
            tick: latestTick,
            wasActive: analogLatch.MotionPose
        );
        analogLatch.HeldButtons = (analogLatch.HeldButtons | drain.Pressed) & ~drain.Released;
        m_analogLatches[deviceId] = analogLatch;
    }
    private bool EmitStick(InputDeviceId deviceId, string source, ulong tick, Vector2 value, bool wasActive) {
        if (value != Vector2.Zero) {
            m_router.Capture(signal: InputSignal.Axis(
                captureTick: tick,
                deviceId: deviceId,
                source: source,
                value: value
            ));

            return true;
        }

        if (wasActive) {
            m_router.Capture(signal: InputSignal.Axis(
                captureTick: tick,
                deviceId: deviceId,
                source: source,
                value: Vector2.Zero
            ));
        }

        return false;
    }
    private bool EmitTouch(InputDeviceId deviceId, string source, ulong tick, GamepadTouchPoint touch, bool wasActive) {
        if (touch.IsActive) {
            m_router.Capture(signal: InputSignal.Axis(
                captureTick: tick,
                deviceId: deviceId,
                source: source,
                value: touch.Position
            ));

            return true;
        }

        if (wasActive) {
            m_router.Capture(signal: InputSignal.Axis(
                captureTick: tick,
                deviceId: deviceId,
                source: source,
                value: Vector2.Zero
            ));
        }

        return false;
    }
    // An active trigger streams its analog value; the first rest report after activity emits one explicit release
    // edge (Completed, value 0) so latching consumers always see the let-go. Returns whether the trigger is active.
    private bool EmitTrigger(InputDeviceId deviceId, string source, float value, bool wasActive, ulong tick) {
        if (0f < value) {
            m_router.Capture(signal: new InputSignal(
                CaptureTick: tick,
                DeviceId: deviceId,
                Phase: CommandPhase.Active,
                Source: source,
                Value: CommandValue.Axis(value: value)
            ));

            return true;
        }

        if (wasActive) {
            m_router.Capture(signal: new InputSignal(
                CaptureTick: tick,
                DeviceId: deviceId,
                Phase: CommandPhase.Completed,
                Source: source,
                Value: CommandValue.Axis(value: 0f)
            ));
        }

        return false;
    }

    /// <summary>Captures every already-drained device's signals into the router. Call once per frame with the same
    /// frame's copied drain (after that frame's <see cref="IInputArbiter.DrainFrame"/>
    /// has run) — this type performs no drain of its own.</summary>
    /// <param name="drains">This frame's per-device drain, from the arbiter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="drains"/> is <see langword="null"/>.</exception>
    public void Capture(IReadOnlyList<GamepadDrain> drains) {
        ArgumentNullException.ThrowIfNull(drains);

        // The frame's clock read is only the FALLBACK stamp, for a report that arrived unstamped (no capture
        // clock wired, or the XInput poll path that doesn't stamp arrival). HID reports carry their own per-report
        // arrival time and each press its own first-press edge time (B2), so most signals stamp sub-frame.
        var frameTick = m_clock.NowTicks;

        m_capturedDeviceIds.Clear();

        foreach (var drain in drains) {
            if (!m_isActiveFor(arg: drain.DeviceId)) {
                continue;
            }

            _ = m_capturedDeviceIds.Add(item: drain.DeviceId);
            EmitSignals(
                drain: in drain,
                frameTick: frameTick
            );
        }

        ClearDepartedDevices(frameTick: frameTick);
    }

    private struct AnalogLatch {
        public bool LeftStick;
        public bool RightStick;
        public bool LeftTrigger;
        public bool RightTrigger;
        public bool Touch0;
        public bool Touch1;
        public bool Gyro;
        public bool MotionPose;
        public GamepadButtons HeldButtons;
    }

    /// <summary>Initializes a new instance of the <see cref="GamepadCaptureSource"/> class.</summary>
    /// <param name="router">The router each device's signals are captured into.</param>
    /// <param name="clock">The capture clock that stamps each signal's <see cref="InputSignal.CaptureTick"/>.</param>
    /// <param name="isActiveFor">An optional predicate that selects devices whose signals should be captured.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GamepadCaptureSource(InputRouter router, IInputClock clock, Func<InputDeviceId, bool>? isActiveFor = null) {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(router);

        m_clock = clock;
        m_isActiveFor = (isActiveFor ?? (static _ => true));
        m_router = router;
    }
}
