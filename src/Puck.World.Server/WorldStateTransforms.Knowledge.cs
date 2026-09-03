using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldStateTransforms {
    private static void Observe(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.Observe operation, WorldPrincipal actor, ulong tick) {
        if (actor != WorldPrincipal.World) {
            throw new InvalidOperationException("only the authority may refresh knowledge");
        }

        var index = Find(rows, operation.Row);
        var row = rows[index];
        if (row.Knowledge is not { } knowledge || row.Board is not { } board || WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology) {
            throw new InvalidOperationException("observe requires a knowledge board");
        }

        var source = rows[Find(rows, knowledge.Source)];
        var mask = rows[Find(rows, knowledge.Mask)];
        Span<long> values = stackalloc long[topology.CellCount];
        Span<long> visible = stackalloc long[topology.CellCount];
        WorldBoardQueries.Read(source, topology, values);
        WorldBoardQueries.Read(mask, topology, visible);
        var cells = (row.Cells ?? []).Select(c => c with { Observation = c.Observation is { } previous ? previous with { Visible = false } : null }).ToList();
        for (var cell = 0; cell < topology.CellCount; cell++) {
            if (visible[cell] == 0) {
                continue;
            }

            var key = topology.Key(cell);
            var found = cells.FindIndex(c => c.Key.Value == key);
            var observed = new WorldStateCell(WorldCellName.Parse(key), values[cell], Observation: new(checked((long)tick), true));
            if (found < 0) {
                cells.Add(observed);
            } else {
                cells[found] = observed;
            }
        }
        rows[index] = row with { Cells = cells };
    }
}
