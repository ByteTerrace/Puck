namespace Puck.Input;

/// <summary>
/// An OPTIONAL observer of every raw window input event, resolved by the window pump as a HELD root
/// <c>IHostContext</c> capability — never referenced by <see cref="Puck.Input"/> itself, so the pump stays
/// engine-agnostic. Called for EVERY event the pump dequeues (not only pointer kinds); implementations should
/// ignore kinds they do not care about. This is the pointer's WHOLE path: no <see cref="InputSources"/> entry names
/// a pointer control, and the pump skips <see cref="WindowInputKind.PointerMove"/>,
/// <see cref="WindowInputKind.PointerPosition"/>, <see cref="WindowInputKind.PointerButton"/>, and
/// <see cref="WindowInputKind.PointerWheel"/> before <see cref="WindowInputMapper"/> ever sees them. Browsing state — where the cursor is, what it is over, which
/// buttons are held — is presentation/session-only and must never ride the
/// <see cref="Puck.Commands.InputSignal"/> → command-binding → <c>CommandSnapshot</c> pipeline into the
/// deterministic simulation; a pointer act reaches the simulation only when a consumer of that state dispatches an
/// ordinary console verb, through the same door a typed line uses. A composition root contributes an implementation
/// as a <c>HostCapabilityContribution</c>; with none registered, the pump simply skips the notification. Only ONE
/// instance resolves per <c>IHostContext.HoldsCapability</c> call — a composition root with multiple observers
/// (in <c>Puck.World</c>, the camera-orbit sink and the console text sink) must fan them out through one composite
/// instance.
/// </summary>
public interface IWindowInputObserver {
    /// <summary>Observes one raw window input event, called for every event the pump dequeues (not only pointer
    /// kinds); implementations should ignore kinds they do not care about.</summary>
    /// <param name="inputEvent">The dequeued event.</param>
    void Observe(in WindowInputEvent inputEvent);
}
