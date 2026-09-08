namespace Puck.World;

/// <summary>
/// Injection seam for the built-in context-family names an authored <see cref="WorldSeatModeFamily"/> must not
/// collide with (<c>Puck.World.Client.WorldContextFamilies.Families</c>) — <c>Puck.World.Schema</c> sits beneath
/// <c>Puck.World.Client</c> in the layering, the same seam shape <see cref="InputSourceVocabularyHook"/> crosses for
/// the physical-control vocabulary. Each composition root wires this hook with a
/// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method, installed before any validator
/// can run.
/// </summary>
public static class ContextFamilyVocabularyHook {
    /// <summary>Gets or sets the built-in family names, in the order the collision refusal lists them. A
    /// <see langword="null"/> hook — an offline caller with no installed vocabulary — skips the collision check
    /// rather than refusing every family name; the composition root's post-build sweep re-covers the boot
    /// documents.</summary>
    public static IReadOnlyList<string>? ReservedFamilyNames { get; set; }
}
