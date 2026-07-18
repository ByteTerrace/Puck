namespace Puck.Input;

/// <summary>
/// An OPTIONAL sink for raw pointer <see cref="WindowInputEvent"/>s, resolved by the window pump as a HELD root
/// <c>IHostContext</c> capability (mirroring how the pump resolves <c>IInputFocus</c>) — never referenced by
/// <see cref="Puck.Input"/> itself, so the pump stays engine-agnostic. Pointer buttons carry no
/// <see cref="InputSources.Pointer"/> vocabulary entry (<see cref="WindowInputMapper"/> passes
/// <see cref="WindowInputKind.PointerButton"/> through inert): a draggable overlay panel needs continuous
/// per-frame held state, not a one-shot bound command, and pointer state is presentation/session-only — it must
/// never ride the <see cref="Puck.Commands.InputSignal"/> → command-binding → <c>CommandSnapshot</c> pipeline into the
/// deterministic simulation. A composition root that wants live pointer state (e.g. the demo's pointer store)
/// contributes an implementation as a <c>HostCapabilityContribution</c>; with none registered, the pump simply
/// skips the notification.
/// </summary>
public interface IPointerInputSink {
    /// <summary>Observes one raw window input event, called for every event the pump dequeues (not only pointer
    /// kinds); implementations should ignore kinds they do not care about.</summary>
    /// <param name="inputEvent">The dequeued event.</param>
    void Observe(in WindowInputEvent inputEvent);
}
