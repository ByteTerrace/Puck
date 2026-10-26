namespace Puck.World;

/// <summary>The gameplay state behind presence-tenure placement projections.</summary>
public static class WorldContributionState {
    /// <summary>The deadline row: -1 means reachable, -2 means retracted, otherwise a simulation deadline.</summary>
    public const string Row = "contributionTenure";

    /// <summary>Declares deadline seeds at a document edit boundary.</summary>
    public static WorldDefinition Declare(WorldDefinition definition) {
        var slots = definition.Placements.Where(static placement => placement.Contribution?.Tenure == WorldContributionTenure.Presence).ToArray();
        if (slots.Length == 0) { return definition; }
        var previous = WorldDefinitionRows.FindStateRow(definition.State, Row);
        var cells = new List<StateCell>();
        var changed = previous is null;
        foreach (var slot in slots) {
            var key = CellName.Parse(slot.Id);
            var cell = StateRows.FindCell(previous?.Cells, key);
            if (cell is null || (cell.Value.Raw == -2L && slot.Contribution!.Contributor is not null)) {
                cell = new StateCell(key, CellValue.Int(slot.Contribution!.RetractDeadlineTick ?? -1L)); changed = true;
            }
            cells.Add(cell);
        }
        if (!changed && previous!.Cells?.Count == cells.Count) { return definition; }
        var row = new WorldStateRow(Name: CellName.Parse(Row), Kind: CellKind.Int, Min: -2L, Max: long.MaxValue,
            Capacity: Math.Max(1, slots.Length), Domain: StateDomain.Keys.Instance, Cells: cells);
        return definition.WithWorldState(rows: definition.AuthoredState.Where(static candidate => candidate.Name.Value != Row).Append(row).ToArray());
    }
}
