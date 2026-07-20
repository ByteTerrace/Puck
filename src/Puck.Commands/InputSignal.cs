using System.Numerics;

namespace Puck.Commands;

/// <summary>
/// A raw input activation identified by a provider-neutral source id, before it is bound to any command.
/// </summary>
/// <remarks>
/// The single input event in the engine: the platform emits these (keyed by a physical
/// <c>InputSources</c> control, with the held <see cref="InputModifiers"/>), and a
/// <see cref="BindingCommandSource"/> rewrites each into one or more <see cref="CommandSignal"/>s using a
/// binding table. Mirrors <see cref="CommandSignal"/> but is keyed by a physical input rather than a command
/// name.
/// </remarks>
/// <param name="Source">The provider-neutral identifier of the input that produced the activation.</param>
/// <param name="DeviceId">The globally unique identifier of the device that produced the activation.</param>
/// <param name="Value">The value carried by the activation (for example, a mouse delta or a digital press).</param>
/// <param name="Phase">The transition the activation represents.</param>
/// <param name="Modifiers">The modifier keys held when the activation fired (for chords).</param>
/// <param name="Text">An optional text payload, such as typed characters.</param>
/// <param name="CaptureTick">
/// The monotonic capture time, in engine ticks (<see cref="IInputClock"/>), stamped at the earliest accurate
/// point in the producing backend. <c>0</c> means unstamped — the router attributes the signal to the current tick.
/// This is the authority for attributing
/// the input to a fixed-step simulation tick and for rhythm-grade edge timing.
/// </param>
public readonly record struct InputSignal(
    string Source,
    InputDeviceId DeviceId,
    CommandValue Value,
    CommandPhase Phase,
    InputModifiers Modifiers = InputModifiers.None,
    string? Text = null,
    ulong CaptureTick = 0UL
) {
    /// <summary>A digital press of a control (<see cref="CommandPhase.Started"/>, digital value).</summary>
    public static InputSignal Press(string source, InputModifiers modifiers = InputModifiers.None, InputDeviceId deviceId = default, ulong captureTick = 0UL) {
        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: deviceId,
            Modifiers: modifiers,
            Phase: CommandPhase.Started,
            Source: source,
            Value: CommandValue.Digital(active: true)
        );
    }
    /// <summary>A digital release of a control (<see cref="CommandPhase.Completed"/>, inactive digital value).</summary>
    public static InputSignal Release(string source, InputModifiers modifiers = InputModifiers.None, InputDeviceId deviceId = default, ulong captureTick = 0UL) {
        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: deviceId,
            Modifiers: modifiers,
            Phase: CommandPhase.Completed,
            Source: source,
            Value: CommandValue.Digital(active: false)
        );
    }
    /// <summary>A two-dimensional axis activation (for example, a pointer delta), as a continuous update.</summary>
    public static InputSignal Axis(string source, Vector2 value, InputModifiers modifiers = InputModifiers.None, InputDeviceId deviceId = default, ulong captureTick = 0UL) {
        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: deviceId,
            Modifiers: modifiers,
            Phase: CommandPhase.Active,
            Source: source,
            Value: CommandValue.Axis(value: value)
        );
    }
    /// <summary>A text activation carrying typed characters.</summary>
    public static InputSignal Typed(string source, string text, InputDeviceId deviceId = default, ulong captureTick = 0UL) {
        ArgumentNullException.ThrowIfNull(text);

        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: deviceId,
            Phase: CommandPhase.Started,
            Source: source,
            Text: text,
            Value: CommandValue.Digital(active: true)
        );
    }
}
