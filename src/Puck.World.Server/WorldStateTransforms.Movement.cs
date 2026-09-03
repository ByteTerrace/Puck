using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldStateTransforms {
    /// <summary>Checks phase generation, eligibility, readiness and deadline against the stamped actor.</summary>
    /// <param name="definition">The current definition.</param>
    /// <param name="guard">The submitted guard.</param>
    /// <param name="actor">The authenticated actor.</param>
    /// <param name="tick">The simulation tick; a deadline at this tick has already expired.</param>
    /// <returns>Whether this actor may perform a gameplay action.</returns>
    public static bool CanAct(WorldDefinition definition, WorldPhaseGuard guard, WorldPrincipal actor, ulong tick) {
        var phase = WorldDefinitionRows.FindStateRow(definition.State, guard.Row)?.Phase;
        if (phase is null || phase.Sequence != guard.Sequence || guard.Participant is not null && actor != WorldPrincipal.World) {
            return false;
        }
        var deadline = Deadline(phase, definition.SimulationRateHz);
        if (deadline > 0 && tick >= (ulong)deadline) {
            return false;
        }
        var mode = phase.Phases[phase.Current].Mode;
        if (mode == WorldPhaseMode.Resolution) {
            return actor == WorldPrincipal.World;
        }
        var principal = guard.Participant ?? actor.Describe();
        for (var index = 0; index < phase.Participants.Count; index++) {
            if (phase.Participants[index] == principal) {
                return mode == WorldPhaseMode.Sequential ? phase.Active == index : (phase.Ready & (1u << index)) == 0;
            }
        }
        return false;
    }

    private static void Move(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.MoveToken move) {
        var positionIndex = Find(rows, move.Positions);
        var allowanceIndex = Find(rows, move.Allowance);
        var positions = rows[positionIndex];
        var allowance = rows[allowanceIndex];
        var terrain = rows[Find(rows, move.Terrain)];
        if (positions.Kind != CellKind.Int || allowance.Kind != CellKind.Int || terrain.Kind != CellKind.Int ||
            positions.KeysFrom is null || allowance.KeysFrom != positions.KeysFrom ||
            terrain.Board is not { } board || positions.ValuesFrom != board.Topology || allowance.ValuesFrom is not null ||
            WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology ||
            (uint)move.Destination >= topology.CellCount || move.MaxVisits < 1 || move.MaxVisits > topology.CellCount ||
            !WorldCellName.TryParse(move.Token, out var token, out _)) {
            throw new InvalidOperationException("moveToken requires compatible position, allowance, and terrain rows and bounded addressing");
        }
        var positionCell = WorldDefinitionRows.FindCell(positions.Cells, token);
        var allowanceCell = WorldDefinitionRows.FindCell(allowance.Cells, token);
        if (positionCell is null || allowanceCell is null || (ulong)positionCell.Value >= (ulong)topology.CellCount || allowanceCell.Value < 0) {
            throw new InvalidOperationException("token has no valid position or movement allowance");
        }
        Span<long> costs = stackalloc long[topology.CellCount];
        WorldBoardQueries.Read(terrain, topology, costs);
        // Every position row on this topology contributes occupancy, independent of who can edit that row.
        foreach (var row in rows) {
            if (row.ValuesFrom != board.Topology) {
                continue;
            }
            foreach (var cell in row.Cells ?? []) {
                if ((row.Name != positions.Name || cell.Key != token) && (ulong)cell.Value < (ulong)topology.CellCount) {
                    costs[(int)cell.Value] = -1;
                }
            }
        }
        var query = new CompiledWorldBoardQuery(topology, WorldBoardQueryKind.PathCost,
            Target: move.Destination, MaxCost: allowanceCell.Value, MaxVisits: move.MaxVisits);
        var cost = WorldBoardQueries.Evaluate(query, costs, board.Empty, (int)positionCell.Value);
        if (cost < 0) {
            throw new InvalidOperationException(cost == -2 ? "moveToken search budget exhausted" : "moveToken destination is blocked, unreachable, or unaffordable");
        }
        rows[positionIndex] = positions with { Cells = (positions.Cells ?? []).Select(c => c.Key == token ? c with { Value = move.Destination } : c).ToArray() };
        rows[allowanceIndex] = allowance with { Cells = (allowance.Cells ?? []).Select(c => c.Key == token ? c with { Value = c.Value - cost } : c).ToArray() };
    }
}
