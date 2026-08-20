namespace Puck.World;

/// <summary>
/// Injection seam for validating an authored icon badge-override's family name against the engine's controller-
/// family vocabulary (<c>Puck.Input.Devices.GamepadType</c>) — <c>Puck.World.Schema</c> must not reference
/// <c>Puck.Input</c> at all (the architecture gate's structural denial), the same seam shape
/// <see cref="InputSourceVocabularyHook"/> already crosses for a physical control's id. Each composition root wires
/// this hook before any validator can run.
/// </summary>
public static class GamepadFamilyVocabularyHook {
    /// <summary>Gets or sets the hook that answers whether a name is a declared, non-<c>Unknown</c>
    /// <c>GamepadType</c> member, by exact (case-sensitive) member name. A <see langword="null"/> hook — an
    /// offline caller with no installed vocabulary — skips the name check rather than refusing every family name;
    /// the composition root's post-build sweep re-covers the boot documents.</summary>
    public static Func<string, bool>? IsKnownFamilyName { get; set; }
}
