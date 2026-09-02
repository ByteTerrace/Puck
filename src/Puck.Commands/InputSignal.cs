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
/// <param name="Text">An optional text payload, such as typed characters. It reaches a text CONSUMER through the
/// signal itself; it is NOT snapshot payload, and binding a text-bearing source drops it — see
/// <see cref="Typed(string, string, InputDeviceId, ulong)"/>.</param>
/// <param name="CaptureTick">
/// The monotonic capture time, in engine ticks (<see cref="IInputClock"/>), stamped at the earliest accurate
/// point in the producing backend. <c>0</c> means unstamped — the router attributes the signal to the current tick.
/// This is the authority for attributing
/// the input to a fixed-step simulation tick and for rhythm-grade edge timing.
/// </param>
/// <param name="Transient">Whether an active analog sample is an impulse rather than persistent device state.
/// Transient channel destinations receive an automatic inactive edge on the following tick.</param>
/// <param name="Posture">Whether the sample is a reading of the device's attitude (an accelerometer's gravity
/// vector, a fused orientation) rather than an act upon it. A posture sample streams every report whether or not a
/// hand is on the device, so it is never the player's activity — it drives motion controls, not idle accounting.</param>
/// <param name="Slot">The lane the signal addresses directly, for a source whose seat is authored rather than
/// discovered (a document-bound sense measurement), or <see cref="UnresolvedSlot"/> to resolve the lane from
/// <paramref name="DeviceId"/> through the slot resolver. An authored-lane signal never seats a device and never
/// counts as player activity.</param>
public readonly record struct InputSignal(
    string Source,
    InputDeviceId DeviceId,
    CommandValue Value,
    CommandPhase Phase,
    string? Text = null,
    ulong CaptureTick = 0UL,
    bool Transient = false,
    int Slot = InputSignal.UnresolvedSlot,
    bool Posture = false
) {
    /// <summary>The <see cref="Slot"/> value meaning "resolve the lane from the device".</summary>
    public const int UnresolvedSlot = -1;

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
    /// <remarks>
    /// <para><b>Bound dispatch drops <see cref="Text"/>, deliberately.</b> <see cref="InputRouter"/> resolves a text
    /// signal through the binding table like any other, but the <see cref="CommandEntry"/> it produces carries only
    /// the text the BINDING ROW authored (<c>CommandBinding.Text</c>, composed into a <c>"verb args"</c> line), never
    /// this payload. <see cref="CommandEntry.Text"/> is a whole submitted command line that
    /// <c>CommandRegistry.ApplySnapshot</c> re-parses at tick time, and a typed character is not one: forwarding
    /// <c>"n"</c> would have the registry refuse <c>n</c> as an unknown verb once per keystroke.</para>
    /// <para>Nothing authorable reaches that path anyway — the only producer is the window text channel, whose
    /// source (<c>InputSources.Keyboard.Text</c>) is marked <c>InputSourceUnaddressable</c> precisely because its
    /// payload cannot ride a binding, so no binding document may name it. A consumer that wants typed characters
    /// reads them off the signal, upstream of the router.</para>
    /// </remarks>
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
