using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldStateTransforms {
    private static bool TryObserve(WorldDefinition definition, WorldStateRow[] rows, StateTransform.Observe operation, WorldPrincipal actor, ulong tick, out string reason) {
        if (actor != WorldPrincipal.World) {
            return Refuse(
                message: "only the authority may refresh knowledge",
                reason: out reason
            );
        }
        if (!TryFind(
            rows,
            operation.Row,
            out var index,
            out reason
        )) {
            return false;
        }
        var row = rows[index];

        if (
            (row.Knowledge is not { } knowledge) ||
            (row.EffectiveDomain is not StateDomain.CellsOf board) ||
            (WorldTopologyCompilation.Find(
            definition: definition,
            name: board.Topology
        ) is not { } topology)
        ) {
            return Refuse(
                message: "observe requires a knowledge board",
                reason: out reason
            );
        }
        if (
            !TryFind(
            rows,
            knowledge.Source,
            out var sourceIndex,
            out reason
        ) ||
            !TryFind(
            rows,
            knowledge.Mask,
            out var maskIndex,
            out reason
        )
        ) {
            return false;
        }

        var source = rows[sourceIndex];
        var mask = rows[maskIndex];
        Span<long> values = stackalloc long[topology.CellCount];
        Span<long> visible = stackalloc long[topology.CellCount];

        BoardQueries.Read(
            row: source,
            topology: topology,
            values: values
        );
        BoardQueries.Read(
            row: mask,
            topology: topology,
            values: visible
        );
        var cells = (row.Cells ?? []).Select(selector: c => c with { Observation = ((c.Observation is { } previous)
            ? previous with { Visible = false }
            : null) }).ToList();

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            if (visible[cell] == 0) {
                continue;
            }

            var key = topology.Key(cell: cell);
            var found = cells.FindIndex(match: c => (c.Key.Value == key));
            var observed = new StateCell(
                CellName.Parse(candidate: key),
                values[cell],
                Observation: new(
                    Tick: checked((long)tick),
                    Visible: true
                )
            );

            if (found < 0) {
                cells.Add(item: observed);
            } else {
                cells[found] = observed;
            }
        }
        rows[index] = row with { Cells = cells };
        return true;
    }
}
