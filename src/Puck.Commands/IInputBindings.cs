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
    /// <returns>The command bindings for <paramref name="slot"/> and <paramref name="source"/>, or <see langword="null"/>.</returns>
    IReadOnlyList<CommandBinding>? Resolve(int slot, string source);

    /// <summary>
    /// Returns the bindings a slot maps a signal to. The default delegates to <see cref="Resolve(int, string)"/>;
    /// a stateful implementation (such as <see cref="PagedInputBindings"/>) overrides this to see the signal's
    /// phase and value in the router's deterministic capture order — how a modifier press can change what the
    /// signals after it resolve to.
    /// </summary>
    /// <param name="slot">The logical player slot.</param>
    /// <param name="signal">The captured signal being resolved.</param>
    /// <returns>The command bindings for <paramref name="slot"/> and the signal's source, or <see langword="null"/>.</returns>
    IReadOnlyList<CommandBinding>? Resolve(int slot, in InputSignal signal) {
        return Resolve(slot: slot, source: signal.Source);
    }
}
