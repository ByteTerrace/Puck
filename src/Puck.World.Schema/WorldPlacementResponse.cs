using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>
/// One entry of a placement's response trait: while <see cref="When"/> holds against the lattice field at the
/// placement's own coupled cell, the row's rendered/collided prototype becomes <see cref="PrototypeId"/> instead of
/// its currently authored one — the bridge that lets a placement react to the lattice chemistry under it (a burning
/// tree becomes a charred stump). Absent <see cref="WorldPlacement.Respond"/> is today's behavior exactly: the
/// placement always shows its own authored <see cref="WorldPlacement.PrototypeId"/>.
/// </summary>
/// <remarks>
/// <para>Entries are tried in authored order every response sweep
/// (<c>Server.WorldServer.SweepPlacementResponses</c>, run once per tick immediately after the field lattice steps);
/// the FIRST whose condition holds wins, and the sweep stops looking there. When no entry holds, the row is left
/// exactly as it currently reads — the facet only ever SELECTS among the authored responses on a match; it never
/// reverts a prior swap back toward the row's own base <see cref="WorldPlacement.PrototypeId"/>. A world that wants
/// a fall-through authors one, ordered last, whose condition is trivially true.</para>
/// <para><see cref="When"/> is the same <see cref="WorldFieldCondition"/> grammar a <c>fields.reactions</c>
/// Transform/Expose condition already uses — field name, comparison, literal-or-state-row value — tested at the cell
/// the placement's own authored <see cref="WorldPlacement.Position"/> couples to, the identical body-coupling
/// resolve <see cref="WorldReaction.Emit"/>/<see cref="WorldReaction.Expose"/> already use for a population body
/// (<c>Server.WorldFieldLattice.TryBodyCellOf</c>).</para>
/// <para>A matching swap lands as an ordinary <c>WorldMutation.UpsertPlacement</c> under
/// <c>WorldPrincipal.World</c>, so it revalidates, rebuilds derived state (colliders included), and journals through
/// the one mutation pipeline like any other engine-driven placement write — <c>world.undo</c> puts a swap back, and
/// a replay of the same tape reproduces it on the same tick because the trigger is simulation state.</para>
/// <para>Refused together with <see cref="WorldPlacement.Attach"/> and <see cref="WorldPlacement.Inhabit"/> (a
/// sibling concern owns body locomotion) and <see cref="WorldPlacement.FaceSources"/> (its per-instance overrides
/// pin to the creation the row validated against, which a response is free to change). Every response entry's
/// <see cref="PrototypeId"/>, and the row's own base one, must resolve to a declared, non-animated creation (no
/// timeline frames) — a response never turns a static stamp into an animated one.</para>
/// </remarks>
/// <param name="When">The lattice condition, tested at the placement's coupled cell.</param>
/// <param name="PrototypeId">The creation the placement shows/collides as while <paramref name="When"/> holds. Must
/// resolve to a declared, non-animated creation row.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementResponse(WorldFieldCondition When, string PrototypeId);
/// <summary>Capacity constants for <see cref="WorldPlacement.Respond"/>.</summary>
public static class WorldResponseCapacity {
    /// <summary>The most response entries a single placement may declare.</summary>
    public const int MaxEntries = 8;
}
