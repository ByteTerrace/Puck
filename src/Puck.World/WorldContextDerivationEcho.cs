namespace Puck.World;

/// <summary>One admitted family's slice of a seat's context derivation, for the <c>player.bindings</c> read-back:
/// the family's current published state and the context row it matched, if any.</summary>
/// <param name="Family">The admitted family name (see <see cref="WorldContextFamilies"/>).</param>
/// <param name="State">The family's current state for the seat.</param>
/// <param name="Group">The group of the context row this family's state matched, or <see langword="null"/> when the
/// composed document declares no row for <c>(family, state)</c> — the defined fall-through, not silence.</param>
/// <param name="Wins">Whether the matched row is the FIRST match in document order — the row the derivation applied.
/// A matched row with <see langword="false"/> here is SHADOWED by an earlier family's match.</param>
internal readonly record struct WorldContextFamilyEcho(
    string Family,
    string State,
    string? Group,
    bool Wins
) {
    /// <summary>Whether this family matched a row that an earlier row's match shadowed — "applied and lost", which the
    /// read-back must keep visibly distinct from "didn't apply" (<see cref="Group"/> <see langword="null"/>).</summary>
    public bool Shadowed => ((Group is not null) && !Wins);
}
/// <summary>A seat's whole context derivation, surfaced by <see cref="WorldSeatBindings.DescribeContextDerivation"/> —
/// the read-back rule's payload: every admitted family's state and match, the requested group, and the resolved
/// active group with the derivation step that produced it.</summary>
/// <param name="ActiveGroup">The group the seat actually resolves in right now.</param>
/// <param name="Families">Each admitted family's slice, in <see cref="WorldContextFamilies.Families"/> order.</param>
/// <param name="RequestedGroup">The seat's requested group (the mode pointer), or <see langword="null"/> when none is
/// requested (the profile default would apply absent a context row).</param>
/// <param name="RequestedShadowed">Whether a winning context row is currently overriding the requested group.</param>
/// <param name="Step">The derivation step that resolved <paramref name="ActiveGroup"/>: <c>context
/// &lt;family&gt;=&lt;state&gt;</c>, <c>requested</c>, or <c>default</c>.</param>
internal sealed record WorldContextDerivationEcho(
    string ActiveGroup,
    IReadOnlyList<WorldContextFamilyEcho> Families,
    string? RequestedGroup,
    bool RequestedShadowed,
    string Step
);
