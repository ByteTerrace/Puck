using System.Numerics;
using Puck.Commands;
using Puck.Input.Devices;

namespace Puck.Input;

/// <summary>
/// The command-system seam for controllers. Each frame it drains the <see cref="GamepadManager"/>, turns every
/// connected (and input-focused) device's coalesced state into provider-neutral <see cref="InputSignal"/>s —
/// button press/release edges, stick and trigger axes, and the gyro — and feeds them through an internal
/// <see cref="BindingCommandSource"/> so the same binding/chord logic the keyboard uses applies here too. Each
/// signal carries its device's <see cref="InputDeviceId"/>, preserving per-controller focus and routing.
/// </summary>
public sealed class GamepadInputSource : ICommandSource {
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
    private readonly Action<string>? m_diagnostics;
    private readonly List<GamepadDrain> m_drains = [];
    private readonly BindingCommandSource m_inner;
    private readonly Func<InputDeviceId, bool> m_isActiveFor;
    private readonly GamepadManager m_manager;

    /// <summary>Initializes a new instance of the <see cref="GamepadInputSource"/> class.</summary>
    /// <param name="manager">The manager whose connected devices supply input.</param>
    /// <param name="bindings">The table mapping each gamepad source id to the commands it activates.</param>
    /// <param name="isActiveFor">
    /// A predicate gating a device's input by focus (typically <c>IInputFocus.IsActiveFor</c>); when
    /// <see langword="null"/>, every device is treated as focused.
    /// </param>
    /// <param name="diagnostics">
    /// An optional sink that receives a per-device line on each button press (the source id stamped with its
    /// <see cref="InputDeviceId"/>), making multi-controller routing observable. Pass <see langword="null"/> to disable.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> or <paramref name="bindings"/> is <see langword="null"/>.</exception>
    public GamepadInputSource(
        GamepadManager manager,
        IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> bindings,
        Func<InputDeviceId, bool>? isActiveFor = null,
        Action<string>? diagnostics = null
    ) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(manager);

        m_diagnostics = diagnostics;
        m_inner = new BindingCommandSource(bindings: bindings);
        m_isActiveFor = (isActiveFor ?? (static _ => true));
        m_manager = manager;
    }

    /// <inheritdoc />
    public void Collect(ICommandSink sink) {
        ArgumentNullException.ThrowIfNull(sink);

        m_manager.Drain(buffer: m_drains);

        foreach (var drain in m_drains) {
            if (!m_isActiveFor(arg: drain.DeviceId)) {
                continue;
            }

            EnqueueSignals(drain: in drain);
        }

        m_inner.Collect(sink: sink);
    }

    private void EnqueueSignals(in GamepadDrain drain) {
        var deviceId = drain.DeviceId;
        var latest = drain.Latest;

        // Emit both press and release edges. A release is inert by default at the binding
        // (CommandBinding.ActivateOn ignores Completed) and the registry updates held state without re-running
        // press-driven handlers, so a held button is pollable without double-firing a tap's handler.
        foreach (var (flag, source) in ButtonSources) {
            if (0 != (drain.Pressed & flag)) {
                m_inner.Enqueue(input: InputSignal.Press(source: source, deviceId: deviceId));
                m_diagnostics?.Invoke($"[gamepad] {deviceId} pressed {source}");
            }

            if (0 != (drain.Released & flag)) {
                m_inner.Enqueue(input: InputSignal.Release(source: source, deviceId: deviceId));
            }
        }

        if (Vector2.Zero != latest.LeftStick) {
            m_inner.Enqueue(input: InputSignal.Axis(deviceId: deviceId, source: InputSources.Gamepad.LeftStick, value: latest.LeftStick));
        }

        if (Vector2.Zero != latest.RightStick) {
            m_inner.Enqueue(input: InputSignal.Axis(deviceId: deviceId, source: InputSources.Gamepad.RightStick, value: latest.RightStick));
        }

        // Touchpad contacts gate on the active flag, not a non-zero position: a finger resting at the top-left
        // corner is a real touch at (0, 0), which a zero-vector gate would wrongly swallow.
        if (latest.Touch0.IsActive) {
            m_inner.Enqueue(input: InputSignal.Axis(deviceId: deviceId, source: InputSources.Gamepad.Touchpad0, value: latest.Touch0.Position));
        }

        if (latest.Touch1.IsActive) {
            m_inner.Enqueue(input: InputSignal.Axis(deviceId: deviceId, source: InputSources.Gamepad.Touchpad1, value: latest.Touch1.Position));
        }

        if (0f < latest.LeftTrigger) {
            m_inner.Enqueue(input: new InputSignal(
                DeviceId: deviceId,
                Phase: CommandPhase.Active,
                Source: InputSources.Gamepad.LeftTrigger,
                Value: CommandValue.Axis(value: latest.LeftTrigger)
            ));
        }

        if (0f < latest.RightTrigger) {
            m_inner.Enqueue(input: new InputSignal(
                DeviceId: deviceId,
                Phase: CommandPhase.Active,
                Source: InputSources.Gamepad.RightTrigger,
                Value: CommandValue.Axis(value: latest.RightTrigger)
            ));
        }

        if (Vector3.Zero != drain.Gyro) {
            m_inner.Enqueue(input: new InputSignal(
                DeviceId: deviceId,
                Phase: CommandPhase.Active,
                Source: InputSources.Gamepad.Gyro,
                Value: CommandValue.Axis(value: drain.Gyro)
            ));
        }

        // The accelerometer reads gravity at rest, so it streams continuously on devices that have one and drives
        // tilt-based controls; absent devices report zero and emit nothing. The fused orientation rides the same
        // gate (a device with an accelerometer is the one running the fusion filter).
        if (Vector3.Zero != latest.Accelerometer) {
            m_inner.Enqueue(input: new InputSignal(
                DeviceId: deviceId,
                Phase: CommandPhase.Active,
                Source: InputSources.Gamepad.Accelerometer,
                Value: CommandValue.Axis(value: latest.Accelerometer)
            ));
            m_inner.Enqueue(input: new InputSignal(
                DeviceId: deviceId,
                Phase: CommandPhase.Active,
                Source: InputSources.Gamepad.Orientation,
                Value: CommandValue.Orientation(value: latest.Orientation)
            ));
        }
    }
}
