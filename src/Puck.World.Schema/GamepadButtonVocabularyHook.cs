namespace Puck.World;

/// <summary>
/// Injection seam for validating an authored binding-bar slot name against the engine's PHYSICAL device vocabulary
/// (<c>Puck.Input.Devices.GamepadButtons</c>) — <c>Puck.World.Schema</c> must not reference <c>Puck.Input</c> at all
/// (the architecture gate's structural denial), the same seam shape <see cref="BindingVocabularyHook"/> already
/// crosses for the command/channel vocabulary. The composition root (<c>Puck.World</c>) wires this hook with a
/// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method, installed before any validator
/// can run.
/// </summary>
public static class GamepadButtonVocabularyHook {
    /// <summary>Gets or sets the hook that answers whether a name is a declared, non-<c>None</c>
    /// <c>GamepadButtons</c> flag, by exact (case-sensitive) member name. A <see langword="null"/> hook — an offline
    /// caller with no installed vocabulary — skips the name check rather than refusing every slot name; the
    /// composition root's post-build sweep re-covers the boot documents.</summary>
    public static Func<string, bool>? IsKnownButtonName { get; set; }
}
