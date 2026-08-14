namespace Puck.Input;

/// <summary>
/// An OPTIONAL observer of every raw window input event, resolved by the window pump as a HELD root
/// <c>IHostContext</c> capability — never referenced by <see cref="Puck.Input"/> itself, so the pump stays
/// engine-agnostic. Called for EVERY event the pump dequeues (not only pointer kinds); implementations should
/// ignore kinds they do not care about. Absolute cursor position and derived browsing state — hover, capture,
/// cursor visibility — remain presentation-only. Relative mouse motion, wheel motion, and button edges also project
/// through <see cref="WindowInputMapper"/> into ordinary command bindings; observing the raw event here does not
/// consume or replace that command-plane projection. A composition root contributes an implementation
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
