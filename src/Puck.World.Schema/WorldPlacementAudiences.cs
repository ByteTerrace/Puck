namespace Puck.World;

/// <summary>
/// Which placements a world's disclosure can show differently to different readers, answered from the document's
/// declarations alone, for every reader at once. <see cref="WorldStateDisclosure"/> changes a reader's placements in
/// exactly three ways: it re-deals or masks the dealt children of a template whose dealt or variant row the reader does
/// not read whole (and drops a placement authored under a child it does not show), and it clears a responding row's
/// holding bits over cells the reader may not read. A placement that <see cref="Varies"/> is one any of those can touch
/// for some principal; every other placement is the same row for every reader that reads the scene's other inputs whole.
/// </summary>
/// <remarks>The presentation draws a varying placement only for the audience it was disclosed to (a rigid copy tagged
/// with that audience, <c>WorldRigidAssets</c>), so one residency of a world can serve readers of several disclosures
/// without a merged mesh or a far product carrying what one of them is withheld.</remarks>
public static class WorldPlacementAudiences {
    /// <summary>Returns whether a row's declarations let it withhold a member from some principal: a non-public policy of
    /// its own, a cell policy its storage admits, or a containing ordered zone's. The declaration-only answer, for every
    /// recipient at once, of the question <c>WorldStateDisclosure.Observer.ReadsWhole</c> answers for one.</summary>
    /// <param name="definition">The document declaring the row.</param>
    /// <param name="row">The row.</param>
    /// <returns>Whether some principal can be withheld a member of the row.</returns>
    public static bool CanWithhold(WorldDefinition definition, WorldStateRow row) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: row);

        return (((row.Visibility is { } policy) && !policy.IsPublic) || ArenaLayout.DeclaresCellVisibility(row: row) || RestrictedByZone(definition: definition, row: row));
    }
    /// <summary>Returns whether a row lies in an ordered zone whose own policy can withhold from some principal.</summary>
    /// <param name="definition">The document declaring the row.</param>
    /// <param name="row">The row.</param>
    /// <returns>Whether a containing ordered zone restricts the row.</returns>
    public static bool RestrictedByZone(WorldDefinition definition, WorldStateRow row) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: row);

        var domain = (row.EffectiveDomain is StateDomain.KeysOf keys) ? keys.Row : row.Name;

        foreach (var zone in definition.State) {
            if ((zone?.EffectiveDomain is StateDomain.KeysOf { Ordered: true } ordered) && (ordered.Row == domain) &&
                (((zone.Visibility is { } policy) && !policy.IsPublic) || ArenaLayout.DeclaresCellVisibility(row: zone))) {
                return true;
            }
        }
        return false;
    }
    /// <summary>Returns the dealt or variant row through which a deal template can withhold from some principal, or
    /// <see langword="null"/> when neither can, or the placement deals nothing.</summary>
    /// <param name="definition">The document declaring the rows.</param>
    /// <param name="template">The template placement.</param>
    /// <returns>The withholding row's name, or <see langword="null"/>.</returns>
    public static string? WithholdingRow(WorldDefinition definition, WorldPlacement template) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: template);

        if (template.Deal is not { } deal) { return null; }
        if (Withholds(definition: definition, name: deal.Row)) { return deal.Row; }
        return ((deal.Variants?.Row is { } variants) && Withholds(definition: definition, name: variants)) ? variants : null;
    }
    /// <summary>Returns the deal template whose dealt child lies on a placement's parent chain (the placement itself
    /// included), or <see langword="null"/>. Frame validation admits a distributed parent only as a template over its own
    /// dealt children, so this is the one template whose hidden slots a placement's frame can follow. The walk is bounded
    /// by the row count, so a parent cycle, which frame validation refuses by name, ends it.</summary>
    /// <param name="placement">The placement.</param>
    /// <param name="placementsById">The document's placements by id.</param>
    /// <returns>The template, or <see langword="null"/>.</returns>
    public static WorldPlacement? DealtAncestor(WorldPlacement placement, IReadOnlyDictionary<string, WorldPlacement> placementsById) {
        ArgumentNullException.ThrowIfNull(argument: placement);
        ArgumentNullException.ThrowIfNull(argument: placementsById);

        var current = placement;

        for (var steps = 0; (steps <= placementsById.Count) && (current.Parent is { } parentId) && placementsById.TryGetValue(key: parentId, value: out var parent); steps++) {
            if (WorldPlacementDeal.IsChild(parent: parent, placement: current)) { return parent; }
            current = parent;
        }
        return null;
    }
    /// <summary>Returns whether the disclosure can show a placement differently to some principal: a row under a deal
    /// template that can withhold (<see cref="DealtAncestor"/>, <see cref="WithholdingRow"/>), a card back a reader's view
    /// deals in a withheld cell's place, or a responding row one of whose conditions reads a row that can withhold.</summary>
    /// <param name="definition">The document the placement belongs to.</param>
    /// <param name="placement">The placement.</param>
    /// <param name="placementsById">The document's placements by id (<see cref="WorldPlacementIndex.ById"/>).</param>
    /// <returns>Whether some reader can be shown the placement otherwise than another.</returns>
    public static bool Varies(WorldDefinition definition, WorldPlacement placement, IReadOnlyDictionary<string, WorldPlacement> placementsById) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: placement);

        if (WorldPlacementDeal.IsPlaceholderId(id: placement.Id, template: out _)) { return true; }
        if ((DealtAncestor(placement: placement, placementsById: placementsById) is { } template) && (WithholdingRow(definition: definition, template: template) is not null)) { return true; }
        foreach (var response in (placement.Respond ?? [])) {
            var withholds = response?.When switch {
                WorldPlacementResponseCondition.StateCondition state => (Withholds(definition: definition, name: state.State) || ((state.ComparandState is { } comparand) && Withholds(definition: definition, name: comparand))),
                WorldPlacementResponseCondition.FieldCondition { Value.Row: { } row } => Withholds(definition: definition, name: row),
                _ => false,
            };
            if (withholds) { return true; }
        }
        return false;
    }
    // Whether the row a name declares can withhold from some principal.
    private static bool Withholds(WorldDefinition definition, string name) =>
        ((WorldDefinitionRows.FindStateRow(definition.State, name) is { } row) && CanWithhold(definition: definition, row: row));
}
