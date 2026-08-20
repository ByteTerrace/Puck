using System.Text.Json.Serialization;

namespace Puck.Commands;

/// <summary>
/// The serializable root of a binding profile: the modifiers chords are made of and the chord rows
/// (<c>(group, ordered chord) → page-or-command meaning</c>, see <see cref="BindingChordDefinition"/>). This
/// document is the single source of truth for a player's controller mapping — it loads straight from JSON,
/// compiles via <see cref="BindingProfile.Compile"/>, and an editor round-trips it back to storage.
/// </summary>
/// <remarks>Unmapped members are rejected, here and on every record it nests
/// (<see cref="BindingModifierDefinition"/>, <see cref="BindingChordDefinition"/>, <see cref="BindingPageDefinition"/>,
/// <see cref="BindingPageEntryDefinition"/>, <see cref="BindingCommandDefinition"/>): a retired authoring key fails
/// by name rather than being silently dropped.</remarks>
/// <param name="Version">The document schema version; currently <see cref="CurrentVersion"/>.</param>
/// <param name="Modifiers">The modifier declarations chord rows reference by id.</param>
/// <param name="Chords">The chord rows. Exactly one empty-chord (resting) page row per group; the first row's
/// group is the profile's default group (the group a fresh slot resolves in).</param>
/// <param name="Contexts">The optional context rows (<see cref="BindingContextDefinition"/>: <c>(family, state) →
/// group</c>) deriving a seat's active group from published engine state; <see langword="null"/> declares none.
/// First matching row in document order wins across families; a shadowed later match is reported by the
/// derivation read-back.</param>
/// <param name="Wheels">The optional named radial presentations (<see cref="BindingWheelDefinition"/>: several may
/// share a group and each may name several hold pages); <see langword="null"/> declares none. Omitted from
/// a saved document when <see langword="null"/>, so a document authored before this member existed round-trips
/// byte-identical.</param>
/// <param name="BindingBar">The player's on-screen binding-bar LOOK overrides (never bindings); <see langword="null"/>
/// carries none, so the world-authored policy applies unmodified. Omitted from a saved document when
/// <see langword="null"/>, so a document authored before this member existed round-trips byte-identical.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingProfileDocument(
    string Version,
    IReadOnlyList<BindingModifierDefinition> Modifiers,
    IReadOnlyList<BindingChordDefinition> Chords,
    IReadOnlyList<BindingContextDefinition>? Contexts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BindingWheelDefinition>? Wheels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindingBarPreferences? BindingBar = null
) {
    /// <summary>The schema version this engine build authors and accepts. A stored profile whose version differs is
    /// rejected by <see cref="BindingProfile.Compile"/> and reseeded from defaults.</summary>
    public const string CurrentVersion = "puck.bindings.v1";
}
/// <summary>A player's on-screen binding-bar LOOK preferences — presentation only, never a binding. Each field
/// overrides the world-authored policy when set; <see langword="null"/> defers to it.</summary>
/// <param name="HideUnbound">Overrides whether a slot with no bound act on its page renders at all.</param>
/// <param name="Stacked">Overrides whether every authored bank renders (<see langword="true"/>) or only the active
/// bank (<see langword="false"/>).</param>
/// <param name="Scale">Overrides the authored layout's uniform cluster scale.</param>
/// <param name="ContrastBoost">A multiplier, in <c>[1, 2]</c>, applied over the resolved theme's scrim alphas and
/// text contrast at the one theme resolve point — 1 leaves the authored theme unchanged; 2 pushes every scrim
/// toward opaque and every text role toward its highest-contrast tone. An accessibility seam, never a binding.</param>
/// <param name="UiScale">A multiplier, in <c>[0.5, 2]</c>, applied over the resolved theme's spacing and type sizes
/// at the same resolve point — 1 leaves the authored theme unchanged. An accessibility seam, never a binding.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingBarPreferences(
    bool? HideUnbound = null,
    bool? Stacked = null,
    float? Scale = null,
    float? ContrastBoost = null,
    float? UiScale = null
);
