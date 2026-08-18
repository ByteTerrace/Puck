using Puck.Assets.Documents;

namespace Puck.Commands;

/// <summary>
/// One context row of a binding profile: <c>(family, state) → group</c>. A family is the published output of a
/// single per-seat engine state source holding exactly one state value at a time (the host owns family/state
/// admission — this project never interprets the names); while the family currently holds
/// <paramref name="State"/> for a seat, this row derives the seat's active group to <paramref name="Group"/>,
/// overriding the seat's requested group and the profile default. Across families, precedence is authored row
/// order: the first matching row in document order wins, and a later matching row is shadowed — reported by the
/// derivation read-back, never silently ignored. A <c>(family, state)</c> with no row contributes nothing (the
/// seat falls through to its requested group, then the profile default).
/// </summary>
/// <remarks>Unmapped members are REJECTED (see <see cref="BindingProfileDocument"/>'s remarks).
/// <see cref="BindingProfile.Compile"/> refuses a row missing any member, a duplicate <c>(family, state)</c> key,
/// and a group no chord row declares; family/state admission against the engine's published registry is the host's
/// vocabulary gate, beside the command/channel checks.</remarks>
/// <param name="Family">The host-admitted context family this row keys on (e.g. <c>roster</c>, <c>engagement</c>).</param>
/// <param name="State">The family state this row matches (e.g. <c>pending</c>, <c>engaged</c>).</param>
/// <param name="Group">The binding group the seat derives to while the family holds <paramref name="State"/> —
/// must name a group the composed document declares. A containing world may bind the name to a Text state cell
/// with <c>state.&lt;row&gt;[.&lt;key&gt;]</c>.</param>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingContextDefinition(
    string Family,
    string State,
    DocumentIdentifier Group
);
