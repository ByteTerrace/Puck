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
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingProfileDocument(
    string Version,
    IReadOnlyList<BindingModifierDefinition> Modifiers,
    IReadOnlyList<BindingChordDefinition> Chords,
    IReadOnlyList<BindingContextDefinition>? Contexts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BindingWheelDefinition>? Wheels = null
) {
    /// <summary>The schema version this engine build authors and accepts. A stored profile whose version differs is
    /// rejected by <see cref="BindingProfile.Compile"/> and reseeded from defaults.</summary>
    public const string CurrentVersion = "puck.bindings.v1";
}
