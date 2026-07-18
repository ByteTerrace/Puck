using System.Numerics;

using Puck.Commands;
using Puck.Input.Devices;

namespace Puck.Input;

/// <summary>
/// The snapshot input path's capture step: turns one frame's already-drained per-device state (an
/// <see cref="IInputArbiter"/>'s <see cref="IInputArbiter.DrainedDevices"/>) into provider-neutral, timestamped
/// <see cref="InputSignal"/>s — button press/release edges, stick/trigger/touch/gyro/accel axes, and the fused
/// orientation — appending each to an <see cref="InputRouter"/>, replacing <see cref="GamepadInputSource"/> on the
/// router path. The destructive <see cref="GamepadManager.Drain"/> itself lives one layer up, in the arbiter: this
/// type never drains — its caller drains once (<see cref="IInputArbiter.DrainFrame"/>) and hands the result in.
/// </summary>
public sealed class GamepadCaptureSource {
    private static readonly (GamepadButtons Flag, string Source)[] ButtonSources = [
        (GamepadButtons.ButtonSouth, InputSources.Gamepad.ButtonSouth),
        (GamepadButtons.ButtonEast, InputSources.Gamepad.ButtonEast),
        (GamepadButtons.ButtonWest, InputSources.Gamepad.ButtonWest),
        (GamepadButtons.ButtonNorth, InputSources.Gamepad.ButtonNorth),
        (GamepadButtons.DpadUp, InputSources.Gamepad.DpadUp),
        (GamepadButtons.DpadDown, InputSources.Gamepad.DpadDown),
        (GamepadButtons.DpadLeft, InputSources.Gamepad.DpadLeft),
        (GamepadButtons.DpadRight, InputSources.Gamepad.DpadRight),
        (GamepadButtons.LeftShoulder, InputSources.Gamepad.LeftShoulder),
        (GamepadButtons.RightShoulder, InputSources.Gamepad.RightShoulder),
        (GamepadButtons.LeftStickPress, InputSources.Gamepad.LeftStickPress),
        (GamepadButtons.RightStickPress, InputSources.Gamepad.RightStickPress),
        (GamepadButtons.Back, InputSources.Gamepad.Back),
        (GamepadButtons.Start, InputSources.Gamepad.Start),
        (GamepadButtons.Guide, InputSources.Gamepad.Guide),
        (GamepadButtons.Touchpad, InputSources.Gamepad.Touchpad),
        (GamepadButtons.Mute, InputSources.Gamepad.Mute),
    ];
    private readonly IInputClock m_clock;
    private readonly Func<InputDeviceId, bool> m_isActiveFor;
    private readonly InputRouter m_router;
    // Which analog controls each device last reported active, so the first return-to-rest emits an explicit zero
    // without streaming redundant zeroes forever. The edge clears InputRouter's carried sample while ensuring a newly
    // connected, untouched pad does not reserve/join a player lane merely because its sticks are centered.
    private readonly Dictionary<InputDeviceId, AnalogLatch> m_analogLatches = [];

    private struct AnalogLatch {
        public bool LeftStick;
        public bool RightStick;
        public bool LeftTrigger;
        public bool RightTrigger;
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

    /// <summary>Captures every already-drained device's signals into the router. Call once per frame with the same
    /// frame's <see cref="IInputArbiter.DrainedDevices"/> (after that frame's <see cref="IInputArbiter.DrainFrame"/>
    /// has run) — this type performs no drain of its own.</summary>
    /// <param name="drains">This frame's per-device drain, from the arbiter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="drains"/> is <see langword="null"/>.</exception>
    public void Capture(IReadOnlyList<GamepadDrain> drains) {
        ArgumentNullException.ThrowIfNull(drains);

        // The frame's clock read is only the FALLBACK stamp, for a report that arrived unstamped (no capture
        // clock wired, or the XInput poll path that doesn't stamp arrival). HID reports carry their own per-report
        // arrival time and each press its own first-press edge time (B2), so most signals stamp sub-frame.
        var frameTick = m_clock.NowTicks;

        foreach (var drain in drains) {
            if (!m_isActiveFor(arg: drain.DeviceId)) {
                continue;
            }

            EmitSignals(drain: in drain, frameTick: frameTick);
        }
    }

    private void EmitSignals(in GamepadDrain drain, ulong frameTick) {
        var deviceId = drain.DeviceId;
        var latest = drain.Latest;
        // The latest report's arrival stamps continuous signals (axes/triggers/motion) and release edges; each
        // press edge gets its own first-press time below. A zero stamp (unstamped report) falls back to the frame.
        var latestTick = ((latest.ArrivalTicks != 0UL) ? latest.ArrivalTicks : frameTick);
        var edges = drain.PressEdges;

        _ = m_analogLatches.TryGetValue(
            key: deviceId,
            value: out var analogLatch
        );

        foreach (var (flag, source) in ButtonSources) {
            if (0 != (drain.Pressed & flag)) {
                var edge = edges[BitOperations.TrailingZeroCount(value: (uint)flag)];

                m_router.Capture(signal: InputSignal.Press(captureTick: ((edge != 0UL) ? edge : latestTick), deviceId: deviceId, source: source));
            }

            if (0 != (drain.Released & flag)) {
                m_router.Capture(signal: InputSignal.Release(captureTick: latestTick, deviceId: deviceId, source: source));
            }
        }

        // Sticks are sampled state, not impulses. Stream active values and emit exactly one zero at the return to rest;
        // InputRouter carries the latest active sample across fixed ticks, so that zero is the explicit clear edge.
        analogLatch.LeftStick = EmitStick(deviceId: deviceId, source: InputSources.Gamepad.LeftStick, tick: latestTick, value: latest.LeftStick, wasActive: analogLatch.LeftStick);
        analogLatch.RightStick = EmitStick(deviceId: deviceId, source: InputSources.Gamepad.RightStick, tick: latestTick, value: latest.RightStick, wasActive: analogLatch.RightStick);

        if (latest.Touch0.IsActive) {
            m_router.Capture(signal: InputSignal.Axis(captureTick: latestTick, deviceId: deviceId, source: InputSources.Gamepad.Touchpad0, value: latest.Touch0.Position));
        }

        if (latest.Touch1.IsActive) {
            m_router.Capture(signal: InputSignal.Axis(captureTick: latestTick, deviceId: deviceId, source: InputSources.Gamepad.Touchpad1, value: latest.Touch1.Position));
        }

        analogLatch.LeftTrigger = EmitTrigger(deviceId: deviceId, source: InputSources.Gamepad.LeftTrigger, tick: latestTick, value: latest.LeftTrigger, wasActive: analogLatch.LeftTrigger);
        analogLatch.RightTrigger = EmitTrigger(deviceId: deviceId, source: InputSources.Gamepad.RightTrigger, tick: latestTick, value: latest.RightTrigger, wasActive: analogLatch.RightTrigger);
        m_analogLatches[deviceId] = analogLatch;

        if (Vector3.Zero != drain.Gyro) {
            EmitGyro(deviceId: deviceId, gyro: drain.Gyro, tick: latestTick);
        }

        EmitAccelerometer(deviceId: deviceId, latest: in latest, tick: latestTick);
    }
    private bool EmitStick(InputDeviceId deviceId, string source, ulong tick, Vector2 value, bool wasActive) {
        if (value != Vector2.Zero) {
            m_router.Capture(signal: InputSignal.Axis(captureTick: tick, deviceId: deviceId, source: source, value: value));

            return true;
        }

        if (wasActive) {
            m_router.Capture(signal: InputSignal.Axis(captureTick: tick, deviceId: deviceId, source: source, value: Vector2.Zero));
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
    private void EmitGyro(InputDeviceId deviceId, Vector3 gyro, ulong tick) {
        m_router.Capture(signal: new InputSignal(
            CaptureTick: tick,
            DeviceId: deviceId,
            Phase: CommandPhase.Active,
            Source: InputSources.Gamepad.Gyro,
            Value: CommandValue.Axis(value: gyro)
        ));
    }

    // The accelerometer reads gravity at rest, so a device that has one streams continuously and drives the
    // fused orientation on the same gate; absent devices report zero and emit nothing.
    private void EmitAccelerometer(InputDeviceId deviceId, in GamepadState latest, ulong tick) {
        if (Vector3.Zero == latest.Accelerometer) {
            return;
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
    }
}
