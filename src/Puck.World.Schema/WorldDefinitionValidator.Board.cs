namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The BOARD facet's whole contract: Topology names a declared Grid, Hex, Graph, or Tiling discrete topology, at most one placement
    // carries it, Occupancy names a board row bound to that SAME topology, and every optional convenience binding
    // (Turn/Verdict/Move/Plan) names a declared row — existence only, since the engine never interprets them.
    private static void ValidateBoardFacet(WorldPlacementBoard? board, WorldDefinition definition, string path,
        HashSet<string> boardTopologies, List<string> errors) {
        if (board is null) {
            return;
        }

        if (
            (WorldTopologyCompilation.Find(
            definition: definition,
            name: board.Topology
        ) is not { } topology) ||
            (topology.Kind is not (TopologyKind.Grid or TopologyKind.Hex or TopologyKind.Graph or TopologyKind.Tiling))
        ) {
            errors.Add(item: $"{path}.board.topology '{board.Topology}' must name a declared Grid, Hex, Graph, or Tiling topology in state.lattices.");
        } else if (!boardTopologies.Add(item: board.Topology)) {
            errors.Add(item: $"{path}.board.topology '{board.Topology}' is already carried by another placement — a topology is anchored by at most one tabletop.");
        }

        var occupancy = WorldDefinitionRows.FindStateRow(
            definition.State,
            board.Occupancy
        );

        if (
            (occupancy?.EffectiveDomain is not StateDomain.CellsOf occupancyDomain) ||
            (occupancyDomain.Topology != board.Topology)
        ) {
            errors.Add(item: $"{path}.board.occupancy '{board.Occupancy}' must name a board row over topology '{board.Topology}'.");
        }

        if (
            (board.Turn is { } turn) &&
            (WorldDefinitionRows.FindStateRow(
            definition.State,
            turn
        ) is null)
        ) {
            errors.Add(item: $"{path}.board.turn '{turn}' names no declared state row.");
        }

        if (
            (board.Verdict is { } verdict) &&
            (WorldDefinitionRows.FindStateRow(
            definition.State,
            verdict
        ) is null)
        ) {
            errors.Add(item: $"{path}.board.verdict '{verdict}' names no declared state row.");
        }

        if (
            (board.Move is { } move) &&
            (WorldDefinitionRows.FindStateRow(
            definition.State,
            move
        ) is null)
        ) {
            errors.Add(item: $"{path}.board.move '{move}' names no declared state row.");
        }

        if (board.Plan is { } plan) {
            var planRow = WorldDefinitionRows.FindStateRow(
                definition.State,
                plan
            );

            if (
                (planRow?.EffectiveDomain is not StateDomain.CellsOf planDomain) ||
                (planDomain.Topology != board.Topology)
            ) {
                errors.Add(item: $"{path}.board.plan '{plan}' must name a board row over topology '{board.Topology}'.");
            }
        }

        if (
            (board.Enforcement == WorldBoardEnforcement.Return) &&
            ((board.Move is null) || (board.Verdict is null))
        ) {
            errors.Add(item: $"{path}.board.enforcement 'Return' requires both board.move and board.verdict.");
        }

        if (board.Engage is { } engage) {
            ValidateTabletopEngage(
                definition: definition,
                engage: engage,
                errors: errors,
                path: $"{path}.board.engage"
            );
        }
    }
    // The engage channel is consumed by the context-button probe exactly like a screen route's, so it is held to the
    // same "names a declared channel" bar; the view and pointer numbers are refused where no camera or ray can use them.
    private static void ValidateTabletopEngage(WorldTabletopEngage engage, WorldDefinition definition, string path, List<string> errors) {
        var declared = false;

        foreach (var channel in definition.Channels) {
            if (string.Equals(
                a: channel.Name,
                b: engage.Channel,
                comparisonType: StringComparison.Ordinal
            )) {
                declared = true;

                break;
            }
        }

        if (!declared) {
            errors.Add(item: $"{path}.channel '{engage.Channel}' names no declared channel.");
        }

        if (!(double.IsFinite(d: engage.Radius) && (engage.Radius > 0d))) {
            errors.Add(item: $"{path}.radius must be a finite positive distance, not {engage.Radius.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        if (!(double.IsFinite(d: engage.Pitch) && (engage.Pitch > 0d) && (engage.Pitch <= 90d))) {
            errors.Add(item: $"{path}.pitch must be in (0, 90] degrees, not {engage.Pitch.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        if (!(double.IsFinite(d: engage.Margin) && (engage.Margin >= 0d))) {
            errors.Add(item: $"{path}.margin must be a finite non-negative distance, not {engage.Margin.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        if (!(double.IsFinite(d: engage.Surface) && (engage.Surface >= 0d))) {
            errors.Add(item: $"{path}.surface must be a finite non-negative height, not {engage.Surface.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}.");
        }
    }
}
