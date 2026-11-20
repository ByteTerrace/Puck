namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Deals and tenure are readings of gameplay state. Installing their projection never edits declarations.
    internal void InstallPlacementProjection(IReadOnlyList<(string Id, WorldPlacement? Placement)> changes) {
        if (changes.Count == 0) { return; }
        var previous = m_definition;
        var held = previous.Placements;
        var byId = WorldPlacementIndex.ById(placements: held);
        // The installed rows in place (null where removed), then the added rows in the order they were first added:
        // a change replaces its id's row where it stands, a removal drops it, and an id not held appends.
        var rows = new WorldPlacement?[held.Count];
        List<WorldPlacement?>? added = null;
        Dictionary<string, int>? addedAt = null;
        var solidsMoved = false;
        var kept = 0;
        for (var index = 0; (index < held.Count); index++) { kept += (((rows[index] = held[index]) is null) ? 0 : 1); }
        foreach (var (id, placement) in changes) {
            var position = ((byId.TryGetPosition(key: id, position: out var found) && (rows[found] is not null)) ? found : -1);
            var at = (((position < 0) && (addedAt?.TryGetValue(key: id, value: out var appended) == true) && (added![appended] is not null)) ? appended : -1);
            var existing = ((position >= 0) ? rows[position] : ((at >= 0) ? added![at] : null));
            solidsMoved |= ((placement?.Solid is not null) || (existing?.Solid is not null));
            if (position >= 0) {
                rows[position] = placement;
                kept -= ((placement is null) ? 1 : 0);
            } else if (at >= 0) {
                added![at] = placement;
                kept -= ((placement is null) ? 1 : 0);
            } else if (placement is not null) {
                added ??= [];
                addedAt ??= new Dictionary<string, int>(comparer: StringComparer.Ordinal);
                addedAt[id] = added.Count;
                added.Add(item: placement);
                kept++;
            }
        }
        var installed = new WorldPlacement[kept];
        var next = 0;
        foreach (var row in rows) { if (row is not null) { installed[next++] = row; } }
        foreach (var row in (added ?? [])) { if (row is not null) { installed[next++] = row; } }
        var definition = previous with { PlacementRowsRaw = installed };
        AdoptDefinition(definition);
        m_pendingStateDelivery = true;
        if (!solidsMoved) { return; }
        if (RequiresSolidField(definition: definition)) {
            if (!TryBuildSolids(definition, out var solids, out var reason)) { throw new InvalidOperationException(reason); }
            AdoptSolidField(solids);
        }
        Host.Population.InstallProjectedSolids(definition, m_solids);
    }
}
