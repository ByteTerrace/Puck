using Puck.Maths;

namespace Puck.World.Testing;

/// <summary>The bound state row a value-domain law writes.</summary>
internal static class ValueDomainFixtures {
    // The state row the bound value reads.
    internal const string Row = "bound";

    // The document with the bound row holding a value: the state a load reads, or a live write leaves.
    internal static WorldDefinition WithRow(WorldDefinition definition, double value, string row = Row) => definition.WithWorldState(rows: [
        .. definition.State.Where(predicate: candidate => !string.Equals(a: candidate.Name.Value, b: row, comparisonType: StringComparison.Ordinal)),
        new WorldStateRow(
            Name: CellName.Parse(candidate: row),
            Kind: CellKind.Fixed,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: value).Value))]
        ),
    ]);
}
