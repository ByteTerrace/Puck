using System.Numerics;

using Puck.Commands;
using Puck.Input.Devices;

namespace Puck.Input;

/// <summary>
/// The single canonical gamepad drain for the snapshot input path. Once per frame it drains the
/// <see cref="GamepadManager"/> and turns every device's coalesced state into provider-neutral, timestamped
/// <see cref="InputSignal"/>s — button press/release edges, stick/trigger/touch/gyro/accel axes, and the fused
/// orientation — appending each to an <see cref="InputRouter"/>. It is the sole drainer (the destructive
/// per-device coalescer drain has one consumer), replacing <see cref="GamepadInputSource"/> on the router path.
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
    private readonly List<GamepadDrain> m_drains = [];
    private readonly GamepadManager m_manager;
    private readonly InputRouter m_router;
    // Which triggers each device last reported active, so a trigger snapping back to rest emits an explicit
    // release edge. Without it a consumer that latches on the trigger's VALUE (a binding-page modifier with
    // hysteresis) can stick held: the value stream simply stops at rest, and the last emitted sample may sit
    // above the release threshold. Buttons get their edges from the coalescer; triggers get theirs here.
    private readonly Dictionary<InputDeviceId, TriggerLatch> m_triggerLatches = [];

    private struct TriggerLatch {
        public bool Left;
        public bool Right;
    }

    /// <summary>Initializes a new instance of the <see cref="GamepadCaptureSource"/> class.</summary>
    /// <param name="manager">The manager whose connected devices supply input.</param>
    /// <param name="router">The router each device's signals are captured into.</param>
    /// <param name="clock">The capture clock that stamps each signal's <see cref="InputSignal.CaptureTick"/>.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GamepadCaptureSource(GamepadManager manager, InputRouter router, IInputClock clock) {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(router);

        m_clock = clock;
        m_manager = manager;
        m_router = router;
    }

    /// <summary>Drains the manager once and captures every device's signals into the router. Call once per frame.</summary>
    public void Capture() {
        // The frame's clock read is only the FALLBACK stamp, for a report that arrived unstamped (no capture
        // clock wired, or the XInput poll path that doesn't stamp arrival). HID reports carry their own per-report
        // arrival time and each press its own first-press edge time (B2), so most signals stamp sub-frame.
        var frameTick = m_clock.NowTicks;

        m_manager.Drain(buffer: m_drains);

        foreach (var drain in m_drains) {
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

        foreach (var (flag, source) in ButtonSources) {
            if (0 != (drain.Pressed & flag)) {
                var edge = edges[BitOperations.TrailingZeroCount(value: (uint)flag)];

                m_router.Capture(signal: InputSignal.Press(captureTick: ((edge != 0UL) ? edge : latestTick), deviceId: deviceId, source: source));
            }

            if (0 != (drain.Released & flag)) {
                m_router.Capture(signal: InputSignal.Release(captureTick: latestTick, deviceId: deviceId, source: source));
            }
        }

        if (Vector2.Zero != latest.LeftStick) {
            m_router.Capture(signal: InputSignal.Axis(captureTick: latestTick, deviceId: deviceId, source: InputSources.Gamepad.LeftStick, value: latest.LeftStick));
        }

        if (Vector2.Zero != latest.RightStick) {
            m_router.Capture(signal: InputSignal.Axis(captureTick: latestTick, deviceId: deviceId, source: InputSources.Gamepad.RightStick, value: latest.RightStick));
        }

        if (latest.Touch0.IsActive) {
            m_router.Capture(signal: InputSignal.Axis(captureTick: latestTick, deviceId: deviceId, source: InputSources.Gamepad.Touchpad0, value: latest.Touch0.Position));
        }

        if (latest.Touch1.IsActive) {
            m_router.Capture(signal: InputSignal.Axis(captureTick: latestTick, deviceId: deviceId, source: InputSources.Gamepad.Touchpad1, value: latest.Touch1.Position));
        }

        _ = m_triggerLatches.TryGetValue(
            key: deviceId,
            value: out var triggerLatch
        );
        triggerLatch.Left = EmitTrigger(deviceId: deviceId, source: InputSources.Gamepad.LeftTrigger, tick: latestTick, value: latest.LeftTrigger, wasActive: triggerLatch.Left);
        triggerLatch.Right = EmitTrigger(deviceId: deviceId, source: InputSources.Gamepad.RightTrigger, tick: latestTick, value: latest.RightTrigger, wasActive: triggerLatch.Right);
        m_triggerLatches[deviceId] = triggerLatch;

        if (Vector3.Zero != drain.Gyro) {
            EmitGyro(deviceId: deviceId, gyro: drain.Gyro, tick: latestTick);
        }

        EmitAccelerometer(deviceId: deviceId, latest: in latest, tick: latestTick);
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
