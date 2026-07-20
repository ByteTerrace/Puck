namespace Puck.Commands;

/// <summary>
/// The serializable root of a binding profile: the modifiers chords are made of and the chord rows
/// (<c>(group, ordered chord) → page-or-command meaning</c>, see <see cref="BindingChordDefinition"/>). This
/// document is the single source of truth for a player's controller mapping — it loads straight from JSON,
/// compiles via <see cref="BindingProfile.Compile"/>, and an editor round-trips it back to storage.
/// </summary>
/// <param name="Version">The document schema version; currently <see cref="CurrentVersion"/>.</param>
/// <param name="Modifiers">The modifier declarations chord rows reference by id.</param>
/// <param name="Chords">The chord rows. Exactly one empty-chord (resting) PAGE row per group; the first row's
/// group is the profile's DEFAULT group (the group a fresh slot resolves in).</param>
public sealed record BindingProfileDocument(
    string Version,
    IReadOnlyList<BindingModifierDefinition> Modifiers,
    IReadOnlyList<BindingChordDefinition> Chords
) {
    /// <summary>The schema version this engine build authors and accepts. A stored profile whose version differs is
    /// rejected by <see cref="BindingProfile.Compile"/> and reseeded from defaults.</summary>
    public const string CurrentVersion = "puck.bindings.v1";
}
