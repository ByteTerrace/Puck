using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldStateTransforms {
    /// <summary>Checks a submitted guard's generation against its phase row.</summary>
    /// <param name="definition">The current definition.</param>
    /// <param name="guard">The submitted guard.</param>
    /// <param name="actor">The authenticated actor.</param>
    /// <returns>Whether the guard matches: the sole condition a mutation's guard checks.</returns>
    public static bool CanAct(WorldDefinition definition, WorldPhaseGuard guard, WorldPrincipal actor) {
        var phase = WorldDefinitionRows.FindStateRow(definition.State, guard.Row)?.Phase;

        return phase is not null && phase.Sequence == guard.Sequence && (guard.Participant is null || actor == WorldPrincipal.World);
    }

    private static bool TryMove(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.MoveToken move, out string reason) {
        if (!TryFind(rows, move.Positions, out var positionIndex, out reason) ||
            !TryFind(rows, move.Allowance, out var allowanceIndex, out reason) ||
            !TryFind(rows, move.Terrain, out var terrainIndex, out reason)) {
            return false;
        }
        var positions = rows[positionIndex];
        var allowance = rows[allowanceIndex];
        var terrain = rows[terrainIndex];
        if (positions.Kind != CellKind.Int || allowance.Kind != CellKind.Int || terrain.Kind != CellKind.Int ||
            positions.EffectiveDomain is not WorldStateDomain.KeysOf positionsKeysOf || allowance.EffectiveDomain is not WorldStateDomain.KeysOf allowanceKeysOf || allowanceKeysOf.Row != positionsKeysOf.Row ||
            terrain.EffectiveDomain is not WorldStateDomain.CellsOf board || positions.ValuesFrom != board.Topology || allowance.ValuesFrom is not null ||
            WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology ||
            (uint)move.Destination >= topology.CellCount || move.MaxVisits < 1 || move.MaxVisits > topology.CellCount ||
            !WorldCellName.TryParse(move.Token, out var token, out _)) {
            return Refuse("moveToken requires compatible position, allowance, and terrain rows and bounded addressing", out reason);
        }
        var positionCell = WorldDefinitionRows.FindCell(positions.Cells, token);
        var allowanceCell = WorldDefinitionRows.FindCell(allowance.Cells, token);
        if (positionCell is null || allowanceCell is null || (ulong)positionCell.Value >= (ulong)topology.CellCount || allowanceCell.Value < 0) {
            return Refuse("token has no valid position or movement allowance", out reason);
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
            return Refuse(cost == -2 ? "moveToken search budget exhausted" : "moveToken destination is blocked, unreachable, or unaffordable", out reason);
        }
        rows[positionIndex] = positions with { Cells = WithCell(positions.Cells, token, move.Destination) };
        rows[allowanceIndex] = allowance with { Cells = WithCell(allowance.Cells, token, allowanceCell.Value - cost) };
        return true;
    }

    // One copy of the cell list with the keyed cell's value replaced in place.
    private static WorldStateCell[] WithCell(IReadOnlyList<WorldStateCell>? cells, WorldCellName key, long value) {
        var copy = (cells ?? []).ToArray();
        for (var index = 0; index < copy.Length; index++) {
            if (copy[index].Key == key) {
                copy[index] = copy[index] with { Value = value };
            }
        }
        return copy;
    }
}
