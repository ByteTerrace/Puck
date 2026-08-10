using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>Who travels through a <see cref="WorldPlacementPortal"/> — mirrors
/// <c>Puck.World.WorldInstanceHost.TransferScope</c>.</summary>
public enum WorldPortalTravel {
    /// <summary>The traveling seat's whole active local-seat party — mirrors <c>WorldInstanceHost.TransferScope.Party</c>.</summary>
    Party,

    /// <summary>One seat only — mirrors <c>WorldInstanceHost.TransferScope.Body</c>.</summary>
    Body,
}

/// <summary>Where a traveler lands at a <see cref="WorldPlacementPortal"/>'s destination — the positional-continuity
/// decision a portal facet authors on top of which destination it names.</summary>
public enum WorldPortalArrival {
    /// <summary>The destination instance's ordinary seat spawn point — today's behavior, unchanged for every facet
    /// authored before this member existed. <see cref="WorldPlacementPortal.Counterpart"/> is refused (by name) when
    /// paired with this.</summary>
    Spawn,

    /// <summary>The pose relative to this facet's own placement frame, mapped through the isometry between this
    /// frame and <see cref="WorldPlacementPortal.Counterpart"/>'s frame in the destination document — the seamless
    /// border-crossing half of a portal pair. Requires <see cref="WorldPlacementPortal.Counterpart"/> (refused by
    /// name when absent).</summary>
    Mapped,
}

/// <summary>
/// A <see cref="WorldPlacementFace"/>'s portal facet — the authored decision that a face is a door: which
/// <see cref="WorldDestination"/> row it leads to, and under what travel scope. Absent (the default) means the face
/// is not a door — nothing here fires anything by itself; turning the decision into a diegetic step-into trigger is
/// <c>WorldInstanceHost.TriggerPortal</c>'s job, never this facet's. Durability, scope, and process-local instance
/// selection live on the named <see cref="WorldDestination"/> row this facet points at, not here — a facet composes
/// one destination selection with a travel scope, never re-authors how that destination is minted. Extensible deliberately (an optional-member
/// record, the same widen-without-moving-existing-members shape <see cref="WorldWaterSection"/>'s own remarks
/// describe): a future fact-gate field (an authored predicate a traveler must satisfy to pass) adds cleanly as a new
/// trailing member.
/// </summary>
/// <param name="Destination">The <see cref="WorldDestination.Name"/> of the <c>destinations</c> row naming the
/// selected destination. Must resolve to an existing row — an undeclared name refuses by name (see
/// <see cref="WorldDefinitionValidator"/>). No boot-time file-existence check on the destination's own referenced
/// document — resolving the document is a future consumer's job; resolving the name, against this document's own
/// <c>destinations</c> section, is this facet's own.</param>
/// <param name="Travel">Who travels through (see <see cref="WorldPortalTravel"/>). Absent resolves against the
/// world's own <c>portals.portalDefaults.travel</c> (see <see cref="WorldPortalsSection"/>), or
/// <see cref="WorldPortalTravel.Body"/> when the world declares no <c>portals</c> section at all — see
/// <see cref="WorldDefinitionValidator"/>'s resolution order, echoed by <c>world.portals</c>. Omitted from the wire
/// when null.</param>
/// <param name="Arrival">Where a traveler lands at the destination (see <see cref="WorldPortalArrival"/>). Default
/// <see cref="WorldPortalArrival.Spawn"/> — unauthored worlds and every facet authored before this member existed
/// are unchanged. Optional and trailing (the same widen-without-moving-existing-members shape <paramref name="Travel"/>
/// itself already follows).</param>
/// <param name="Counterpart">The destination document's border face this facet's frame maps onto under
/// <see cref="WorldPortalArrival.Mapped"/> — <c>"&lt;placementId&gt;/&lt;face&gt;"</c>, the placement id and
/// declared face name whose own placement transform (position + yaw) is the arrival anchor. Required exactly when
/// <paramref name="Arrival"/> is <see cref="WorldPortalArrival.Mapped"/> (refused by name otherwise, in either
/// direction — see <see cref="WorldDefinitionValidator"/>). No boot-time cross-document existence check: the named
/// document is not resolved at boot (same reason <paramref name="Destination"/> is not), so an absent placement/face
/// refuses at transfer time instead, against the destination's own delivered definition (see
/// <see cref="WorldPortalCounterpart"/>). Omitted from the wire when null.</param>
/// <param name="MarginDepth">The depth (world units) of the shared ground strip this face and its
/// <paramref name="Counterpart"/> face both author for a contiguous border — the terrain either side keeps solid so a
/// body straddling the seam always has ground under it, never a wall it happens to be authoring past. Meaningful only
/// when <paramref name="Arrival"/> is <see cref="WorldPortalArrival.Mapped"/> (refused by name otherwise, the same
/// rule <paramref name="Counterpart"/> follows), and only for a Global destination while contact fields remain shared
/// by one authority. Required — when a reachable <see cref="IWorldNeighbourResolver"/> is supplied — to be no
/// shallower than the derived floor (interaction reach plus max closing speed times tape latency, all read from this
/// document and the neighbour's own; see <see cref="WorldDefinitionValidator"/>), bit-identical to the value the
/// neighbour document authors on the counterpart face, and reciprocal: that face must map back to this exact face.
/// A strip only one side widens, or whose other half points at a different face, is not shared. <see langword="null"/>
/// (the default) authors no strip: an unmapped or unauthored face is unchanged. Omitted from the wire when null.</param>
public sealed record WorldPlacementPortal(
    string Destination,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPortalTravel? Travel = null,
    WorldPortalArrival Arrival = WorldPortalArrival.Spawn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Counterpart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? MarginDepth = null
);

/// <summary>Parses and resolves a <see cref="WorldPlacementPortal.Counterpart"/> string — the one spelling shared by
/// <see cref="WorldDefinitionValidator"/>'s boot-time format check and <c>Puck.World.WorldInstanceHost</c>'s
/// transfer-time existence check against the destination's own delivered definition, so the two can never disagree
/// on what "malformed" or "missing" means.</summary>
public static class WorldPortalCounterpart {
    /// <summary>Splits a counterpart string into its placement id and face name, at the first <c>/</c>.</summary>
    /// <param name="counterpart">The authored counterpart string.</param>
    /// <param name="placementId">The placement id half, when parsing succeeds.</param>
    /// <param name="face">The face name half, when parsing succeeds.</param>
    /// <returns><see langword="true"/> when <paramref name="counterpart"/> is non-empty, contains exactly one
    /// meaningful <c>/</c> split, and both halves are non-empty/non-whitespace.</returns>
    public static bool TryParse(string? counterpart, out string placementId, out string face) {
        placementId = string.Empty;
        face = string.Empty;

        if (string.IsNullOrWhiteSpace(value: counterpart)) {
            return false;
        }

        var separator = counterpart.IndexOf(value: '/', comparisonType: StringComparison.Ordinal);

        if (separator <= 0) {
            return false;
        }

        var candidatePlacementId = counterpart[..separator];
        var candidateFace = counterpart[(separator + 1)..];

        if (string.IsNullOrWhiteSpace(value: candidatePlacementId) || string.IsNullOrWhiteSpace(value: candidateFace)) {
            return false;
        }

        placementId = candidatePlacementId;
        face = candidateFace;

        return true;
    }

    /// <summary>Resolves a counterpart string against a (destination-side, at transfer time) definition — the
    /// placement + face whose transform is the arrival anchor.</summary>
    /// <param name="definition">The definition to resolve against (the destination's own delivered document).</param>
    /// <param name="counterpart">The authored counterpart string.</param>
    /// <param name="placement">The resolved placement, when resolution succeeds.</param>
    /// <param name="face">The resolved face row, when resolution succeeds.</param>
    /// <param name="reason">The named refusal reason, quoting exactly what was written, when resolution fails.</param>
    /// <returns><see langword="true"/> when <paramref name="counterpart"/> names a real placement/face pair in
    /// <paramref name="definition"/>.</returns>
    public static bool TryResolve(WorldDefinition definition, string? counterpart, out WorldPlacement? placement, out WorldPlacementFace? face, out string reason) {
        placement = null;
        face = null;

        if (!TryParse(counterpart: counterpart, placementId: out var placementId, face: out var faceName)) {
            reason = $"counterpart '{counterpart}' is malformed — expected '<placementId>/<face>'";

            return false;
        }

        var resolvedPlacement = WorldDefinitionRows.FindPlacement(placements: definition.Placements, id: placementId);

        if (resolvedPlacement is null) {
            reason = $"counterpart '{counterpart}' names no placement '{placementId}'";

            return false;
        }

        var resolvedFace = WorldDefinitionRows.FindPlacementFace(placement: resolvedPlacement, face: faceName);

        if (resolvedFace is null) {
            reason = $"counterpart '{counterpart}' names no face '{faceName}' on placement '{placementId}'";

            return false;
        }

        placement = resolvedPlacement;
        face = resolvedFace;
        reason = string.Empty;

        return true;
    }
}

/// <summary>The world-scope default a portal facet's absent <see cref="WorldPlacementPortal.Travel"/> resolves to —
/// the <c>portals</c> section's one authored fact today (see <see cref="WorldPortalsSection"/>).</summary>
/// <param name="Travel">The default travel scope (see <see cref="WorldPortalTravel"/>).</param>
public sealed record WorldPortalDefaults(WorldPortalTravel Travel);

/// <summary>
/// The <c>portals</c> section — the world-scope defaults a <see cref="WorldPlacementPortal"/> facet resolves
/// against when it does not author its own fact. Optional, for the same reason <see cref="WorldWaterSection"/> and
/// <see cref="WorldReference"/>'s own <c>references</c> section are: a world with no portals authors neither this
/// section nor any portal facet, and a required section would refuse every existing document at boot for declaring
/// nothing. Slotted immediately after <c>references</c> in <see cref="WorldDefinition"/>'s declaration order — the
/// two sections are the world-topology cluster a portal composes from (which world, and how travel through it
/// defaults).
/// </summary>
/// <param name="PortalDefaults">The travel default every portal facet in this document falls back to when it
/// authors no <see cref="WorldPlacementPortal.Travel"/> of its own.</param>
public sealed record WorldPortalsSection(WorldPortalDefaults PortalDefaults);
