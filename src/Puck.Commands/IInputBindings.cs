namespace Puck.Commands;

/// <summary>
/// Resolves the command bindings for a given player slot and physical input source, so each player can carry
/// their own controller mapping. The <see cref="InputRouter"/> resolves a signal's device to a slot, then asks
/// this seam which commands that slot binds the source to — so a remap or a per-profile mapping is a matter of
/// which <see cref="IInputBindings"/> is installed, not a change to the router.
/// </summary>
public interface IInputBindings {
    /// <summary>Returns the bindings a slot maps a source to, or <see langword="null"/> when the slot binds nothing to it.</summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="source">The provider-neutral input source id (an <c>InputSources</c> control).</param>
    /// <returns>The command bindings for <paramref name="slot"/> and <paramref name="source"/>, or <see langword="null"/>.
    /// A returned list is immutable runtime data: replace the list (and raise
    /// <see cref="IInputBindingsReloadSource.Reloading"/> when mutable) to change bindings rather than mutating it
    /// in place.</returns>
    IReadOnlyList<CommandBinding>? Resolve(int slot, string source);
    /// <summary>
    /// Returns the bindings a slot maps a signal to. The default delegates to <see cref="Resolve(int, string)"/>;
    /// a stateful implementation (such as <see cref="PagedInputBindings"/>) overrides this to see the signal's
    /// phase and value in the router's deterministic capture order — how a modifier press can change what the
    /// signals after it resolve to.
    /// </summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="signal">The captured signal being resolved.</param>
    /// <param name="pressesWithheld">Whether the <see cref="InputRouter"/> will DISCARD every press this resolve
    /// produces — the state a signal captured while its device's terminal focus is released arrives in (see
    /// <see cref="InputRouter.CaptureFocusExempt(in InputSignal)"/>). Such a signal is forwarded only so the
    /// resolver's own held state can be RELEASED: the page must still flip back and a broken row must still emit
    /// its completion. A stateful resolver must therefore arm nothing and start nothing while this is
    /// <see langword="true"/> — a row armed here would owe a completion for a command that never started, and
    /// could not fire again until it delivered one.</param>
    /// <returns>The command bindings for <paramref name="slot"/> and the signal's source, or <see langword="null"/>.</returns>
    IReadOnlyList<CommandBinding>? Resolve(int slot, in InputSignal signal, bool pressesWithheld) {
        // A stateless resolver is a pure table: it arms nothing, so it has nothing to withhold.
        return Resolve(
            slot: slot,
            source: signal.Source
        );
    }
    /// <summary>
    /// Returns whether this resolver currently carries state for <paramref name="source"/> that the source's own
    /// release would clear — a press latch, a held modifier, a chord-consumed press, or an open activator gate.
    /// </summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="source">The provider-neutral input source id.</param>
    /// <returns><see langword="true"/> when a release of the source has something here to release. The default is
    /// <see langword="false"/>: a stateless resolver is a pure table and carries nothing.</returns>
    /// <remarks>The <see cref="InputRouter"/>'s one question when deciding whether to forward an INACTIVE continuous
    /// sample captured under focus exemption. A continuous producer streams inactive samples forever (a stick sitting
    /// at centre reports every frame), and those are the device reporting rather than a release — forwarding them
    /// would consult the authored page on every frame a seat console stays open. Only a source this resolver is
    /// holding down is releasing when it next reports inactive, and only this resolver can say so: the router's own
    /// memory of having seen a control deflected goes stale whenever it withdraws holds without resetting the
    /// resolver (a device reseat), while the resolver's answer is about the very state the release would clear.
    /// Called on the router's snapshot thread, immediately before the resolve; it must not create or mutate state.</remarks>
    bool HoldsSource(int slot, string source) {
        return false;
    }
    /// <summary>Releases one slot's held chord/modifier and press-latch state. Runtime modality transitions use this
    /// seam before held digital controls reassert through the new command surface. Default no-op for stateless
    /// resolvers.</summary>
    /// <param name="slot">The logical player slot.</param>
    void Reset(int slot) {
    }
    /// <summary>
    /// Releases every slot's held chord/modifier and press-latch state a stateful implementation tracks
    /// (<see cref="PagedInputBindings"/>) — wire to OS window focus loss, where a modifier's own release can be
    /// delivered to whatever window stole focus and never reach this process at all, permanently stranding a
    /// slot mid-chord. Default no-op: a stateless resolver (a flat table) has nothing to release.
    /// </summary>
    void ResetAll() {
    }
}
