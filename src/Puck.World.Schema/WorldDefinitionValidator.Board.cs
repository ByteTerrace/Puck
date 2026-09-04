namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The BOARD facet's whole contract: Topology names a declared Grid discrete topology, at most one placement
    // carries it, Occupancy names a board row bound to that SAME topology, and every optional convenience binding
    // (Turn/Verdict/Move/Plan) names a declared row — existence only, since the engine never interprets them.
    private static void ValidateBoardFacet(WorldPlacementBoard? board, WorldDefinition definition, string path,
        HashSet<string> boardTopologies, List<string> errors) {
        if (board is null) {
            return;
        }

        if (WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology || topology.Kind != WorldTopologyKind.Grid) {
            errors.Add(item: $"{path}.board.topology '{board.Topology}' must name a declared Grid topology in state.lattices.");
        } else if (!boardTopologies.Add(item: board.Topology)) {
            errors.Add(item: $"{path}.board.topology '{board.Topology}' is already carried by another placement — a topology is anchored by at most one tabletop.");
        }

        var occupancy = WorldDefinitionRows.FindStateRow(definition.State, board.Occupancy);
        if (occupancy?.Board is not { } occupancyBoard || occupancyBoard.Topology != board.Topology) {
            errors.Add(item: $"{path}.board.occupancy '{board.Occupancy}' must name a board row over topology '{board.Topology}'.");
        }

        if (board.Turn is { } turn && WorldDefinitionRows.FindStateRow(definition.State, turn) is null) {
            errors.Add(item: $"{path}.board.turn '{turn}' names no declared state row.");
        }

        if (board.Verdict is { } verdict && WorldDefinitionRows.FindStateRow(definition.State, verdict) is null) {
            errors.Add(item: $"{path}.board.verdict '{verdict}' names no declared state row.");
        }

        if (board.Move is { } move && WorldDefinitionRows.FindStateRow(definition.State, move) is null) {
            errors.Add(item: $"{path}.board.move '{move}' names no declared state row.");
        }

        if (board.Plan is { } plan) {
            var planRow = WorldDefinitionRows.FindStateRow(definition.State, plan);
            if (planRow?.Board is not { } planBoard || planBoard.Topology != board.Topology) {
                errors.Add(item: $"{path}.board.plan '{plan}' must name a board row over topology '{board.Topology}'.");
            }
        }
    }
}
