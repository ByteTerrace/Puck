using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldStateTransforms {
    private static bool TryObserve(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.Observe operation, WorldPrincipal actor, ulong tick, out string reason) {
        if (actor != WorldPrincipal.World) {
            return Refuse("only the authority may refresh knowledge", out reason);
        }
        if (!TryFind(rows, operation.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.Knowledge is not { } knowledge || row.EffectiveDomain is not WorldStateDomain.CellsOf board || WorldTopologyCompilation.Find(definition, board.Topology) is not { } topology) {
            return Refuse("observe requires a knowledge board", out reason);
        }
        if (!TryFind(rows, knowledge.Source, out var sourceIndex, out reason) || !TryFind(rows, knowledge.Mask, out var maskIndex, out reason)) {
            return false;
        }

        var source = rows[sourceIndex];
        var mask = rows[maskIndex];
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
        return true;
    }
}
