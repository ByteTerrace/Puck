namespace Puck.World;

/// <summary>
/// Injection seam for validating an authored PHYSICAL-control id — a binding-bar slot, an icon badge row — against
/// the engine's canonical input-source vocabulary (<c>Puck.Input.InputSourceVocabulary</c>), the one catalog a
/// binding entry's own <c>sources</c> already resolve through. <c>Puck.World.Schema</c> must not reference
/// <c>Puck.Input</c> at all (the architecture gate's structural denial), the same seam shape
/// <see cref="BindingVocabularyHook"/> crosses for the command/channel vocabulary. Each composition root wires this
/// hook with a <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method, installed before any
/// validator can run.
/// </summary>
public static class InputSourceVocabularyHook {
    /// <summary>Gets or sets the hook that answers whether a string names a recognized input source. A
    /// <see langword="null"/> hook — an offline caller with no installed vocabulary — skips the name check rather
    /// than refusing every id; the composition root's post-build sweep re-covers the boot documents.</summary>
    public static Func<string, bool>? IsKnownSourceId { get; set; }
}
