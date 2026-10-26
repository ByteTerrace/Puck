namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Deals and tenure are readings of gameplay state. Installing their projection never edits declarations.
    internal void InstallPlacementProjection(IReadOnlyList<(string Id, WorldPlacement? Placement)> changes) {
        if (changes.Count == 0) { return; }
        var previous = m_definition;
        var rows = previous.Placements.ToList();
        var solidsMoved = false;
        foreach (var (id, placement) in changes) {
            var index = rows.FindIndex(row => row.Id == id);
            solidsMoved |= placement?.Solid is not null || (index >= 0 && rows[index].Solid is not null);
            if (placement is null) { if (index >= 0) { rows.RemoveAt(index); } }
            else if (index < 0) { rows.Add(placement); }
            else { rows[index] = placement; }
        }
        var definition = previous with { PlacementRowsRaw = rows.ToArray() };
        AdoptDefinition(definition);
        m_pendingStateDelivery = true;
        if (!solidsMoved) { return; }
        if (WorldContactSelection.RequiresField(definition.Collision) || WorldTargetSelection.RequiresLineOfSight(definition)) {
            if (!TryBuildSolids(definition, out var solids, out var reason)) { throw new InvalidOperationException(reason); }
            AdoptSolidField(solids);
        }
        Host.Population.InstallProjectedSolids(definition, m_solids);
    }
}
