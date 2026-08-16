using System.Numerics;

namespace Puck.Commands;

/// <summary>
/// A raw input activation identified by a provider-neutral source id, before it is bound to any command.
/// </summary>
/// <remarks>
/// The single input event in the engine: the platform emits these, keyed by a physical <c>InputSources</c>
/// control — the OS-modifier keys (Control/Shift/Alt/Super) are themselves controls with their own left/right
/// source ids, not a side channel on every signal — and the <see cref="InputRouter"/> rewrites each into one or more
/// <see cref="CommandEntry"/> rows using the slot's binding table. A chord is
/// <see cref="BindingModifierDefinition"/> tracking the modifier's own source alongside the rest, the same way it
/// tracks any other held source. Mirrors <see cref="CommandEntry"/> but is keyed by a physical input rather than
/// a command id, and carries no principal: who a signal acts as is the lane's answer, resolved at snapshot assembly.
/// </remarks>
/// <param name="Source">The provider-neutral identifier of the input that produced the activation.</param>
/// <param name="DeviceId">The globally unique identifier of the device that produced the activation.</param>
/// <param name="Value">The value carried by the activation (for example, a mouse delta or a digital press).</param>
/// <param name="Phase">The transition the activation represents.</param>
/// <param name="Text">An optional text payload, such as typed characters.</param>
/// <param name="CaptureTick">
/// The monotonic capture time, in engine ticks (<see cref="IInputClock"/>), stamped at the earliest accurate
/// point in the producing backend. <c>0</c> means unstamped — the router attributes the signal to the current tick.
/// This is the authority for attributing
/// the input to a fixed-step simulation tick and for rhythm-grade edge timing.
/// </param>
/// <param name="Transient">Whether an active analog sample is an impulse rather than persistent device state.
/// Transient channel destinations receive an automatic inactive edge on the following tick.</param>
public readonly record struct InputSignal(
    string Source,
    InputDeviceId DeviceId,
    CommandValue Value,
    CommandPhase Phase,
    string? Text = null,
    ulong CaptureTick = 0UL,
    bool Transient = false
) {
    /// <summary>A two-dimensional axis activation (for example, a pointer delta), as a continuous update.</summary>
    public static InputSignal Axis(string source, Vector2 value, InputDeviceId deviceId = default, ulong captureTick = 0UL, bool transient = false) {
        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: deviceId,
            Phase: CommandPhase.Active,
            Source: source,
            Value: CommandValue.Axis(value: value),
            Transient: transient
        );
    }
    /// <summary>A digital press of a control (<see cref="CommandPhase.Started"/>, digital value).</summary>
    public static InputSignal Press(string source, InputDeviceId deviceId = default, ulong captureTick = 0UL) {
        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: deviceId,
            Phase: CommandPhase.Started,
            Source: source,
            Value: CommandValue.Digital(active: true)
        );
    }
    /// <summary>Creates a digital held-state reassertion (<see cref="CommandPhase.Active"/>). This may recover
    /// continuous channel state and binding modifiers but never represents a fresh command edge.</summary>
    public static InputSignal Reassert(string source, InputDeviceId deviceId = default, ulong captureTick = 0UL) {
        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: deviceId,
            Phase: CommandPhase.Active,
            Source: source,
            Value: CommandValue.Digital(active: true)
        );
    }
    /// <summary>A digital release of a control (<see cref="CommandPhase.Completed"/>, inactive digital value).</summary>
    public static InputSignal Release(string source, InputDeviceId deviceId = default, ulong captureTick = 0UL) {
        return new InputSignal(
            CaptureTick: captureTick,
            DeviceId: deviceId,
            Phase: CommandPhase.Completed,
            Source: source,
            Value: CommandValue.Digital(active: false)
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
