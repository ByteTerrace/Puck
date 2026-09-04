namespace Puck.World;

public static partial class WorldRuleCompiler {
    private static CompiledWorldEffect ResolveStateTransform(WorldStateTransform transform, string ruleName, WorldDefinition definition) {
        WorldRuleException Invalid(string message) => new(WorldRuleRefusal.EffectKindInadmissible, ruleName, message);
        WorldStateRow Row(string name) => WorldDefinitionRows.FindStateRow(definition.State, name) ?? throw Invalid($"unknown state row '{name}'");
        switch (transform) {
            case WorldStateTransform.Observe observe:
                if (Row(observe.Row).Knowledge is null) {
                    throw Invalid("observe requires a knowledge board");
                }

                break;
            case WorldStateTransform.MoveToken move:
                var positions = Row(move.Positions);
                var allowance = Row(move.Allowance);
                var terrain = Row(move.Terrain);
                if (positions.KeysFrom is null || allowance.KeysFrom != positions.KeysFrom || terrain.Board is not { } terrainBoard ||
                    positions.ValuesFrom != terrainBoard.Topology || positions.Kind != CellKind.Int || allowance.Kind != CellKind.Int || terrain.Kind != CellKind.Int ||
                    WorldTopologyCompilation.Find(definition.StateRaw, terrainBoard.Topology) is not { } map ||
                    (uint)move.Destination >= map.CellCount || move.MaxVisits < 1 || move.MaxVisits > map.CellCount) {
                    throw Invalid("moveToken requires bounded addressing and compatible position, allowance, and terrain rows");
                }
                break;
            case WorldStateTransform.Transfer transfer:
                if (Row(transfer.From).Zone is not { } source || Row(transfer.To).Zone is not { } destination || source.Tokens != destination.Tokens ||
                    !Enum.IsDefined(transfer.Selector) || (transfer.Selector == WorldZoneSelector.Key) != (transfer.Key is not null) ||
                    (transfer.Selector == WorldZoneSelector.Random) != (transfer.Draw is not null) ||
                    ((transfer.Selector is WorldZoneSelector.First or WorldZoneSelector.Last) && !source.Ordered) || transfer.InsertFirst && !destination.Ordered ||
                    transfer.Count < 1 || transfer.Count > WorldStateTokens.MaxTransferCount || (transfer.Selector == WorldZoneSelector.Key && transfer.Count != 1)) {
                    throw Invalid($"transfer requires compatible token zones, selector arguments, ordered positional operations, and a count of 1..{WorldStateTokens.MaxTransferCount} (exactly 1 by key)");
                }
                if (transfer.Draw is { } drawName) {
                    var drawRow = Row(drawName);
                    if (drawRow.Draw is not { Timing: not WorldDrawTiming.Boot } draw || drawRow.Kind != CellKind.Int ||
                        !WorldGeneratorEngine.TryResolveSource(generators: definition.Generators, draw: draw, generator: out var generator, reason: out _) || generator.Source != WorldGeneratorSource.StreamDraw) {
                        throw Invalid("random transfer requires a redrawable integer streamDraw site");
                    }
                }
                break;
            case WorldStateTransform.SetRay ray:
                var row = Row(ray.Row);
                if (row.Board is not { } board || WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology ||
                    !topology.TryCell(ray.From, out _) || topology.Direction(ray.Direction) < 0 || ray.Through == ray.Until ||
                    row.ClampToEnvelope(ray.Value) != ray.Value || (row.Kind == CellKind.Bool && ray.Value is not (0 or 1))) {
                    throw Invalid("setRay requires valid board addressing, distinct through/until values, and an admitted replacement");
                }
                break;
            case WorldStateTransform.CompletePhase complete:
                if (Row(complete.Row).Phase is not { } phase || complete.ExpectedSequence < 0 ||
                    (complete.Participant is not null && !(phase.Participants ?? []).Contains(complete.Participant, StringComparer.Ordinal)) ||
                    (complete.Next is not null && !(phase.Phases ?? []).Any(candidate => candidate.Name == complete.Next))) {
                    throw Invalid("completePhase requires a phase row, valid sequence, declared participant, and a declared next phase");
                }
                break;
            case WorldStateTransform.Sort sort:
                if (Row(sort.Row) is not { } sortedRow ||
                    (sortedRow.Zone is not null
                        ? (!sortedRow.Zone.Ordered || sort.Descending || sort.By is not { Count: >= 1 and <= WorldStateCapacity.MaxSortKeys } ||
                            sort.By.Any(key => key is null || Row(key.Row) is not { IsKeyed: true, Kind: CellKind.Int or CellKind.Fixed } || Row(key.Row).KeysFrom != sortedRow.Zone.Tokens) ||
                            sort.By.Select(key => key!.Row).Distinct(StringComparer.Ordinal).Count() != sort.By.Count)
                        : (!sortedRow.IsKeyed || sort.By is not null || sortedRow.Kind is not (CellKind.Int or CellKind.Fixed)))) {
                    throw Invalid($"sort requires an ordered zone with 1..{WorldStateCapacity.MaxSortKeys} distinct numeric attribute keys over the zone's token domain (direction per key), or a keyed numeric row alone");
                }
                break;
            case WorldStateTransform.Shuffle shuffle:
                if (Row(shuffle.Row).Zone is not { Ordered: true } || Row(shuffle.Draw).Draw is not { Timing: not WorldDrawTiming.Boot } shuffleDraw ||
                    Row(shuffle.Draw).Kind != CellKind.Int ||
                    !WorldGeneratorEngine.TryResolveSource(generators: definition.Generators, draw: shuffleDraw, generator: out var shuffleSource, reason: out _) ||
                    shuffleSource.Source != WorldGeneratorSource.StreamDraw) {
                    throw Invalid("shuffle requires an ordered zone and a redrawable integer streamDraw site");
                }
                break;
            case WorldStateTransform.TurnOrder order:
                if (Row(order.Row).Phase is not { } ordered || order.Direction is not (null or 1 or -1) ||
                    (order.Skip ?? []).Concat(order.Unskip ?? []).Concat(order.Active is null ? [] : [order.Active])
                        .Any(token => !(ordered.Participants ?? []).Contains(token, StringComparer.Ordinal))) {
                    throw Invalid("turnOrder requires a phase row, a direction of 1 or -1, and declared participants");
                }
                break;
            case WorldStateTransform.SetMask setMask:
                var masked = Row(setMask.Row);
                var maskSource = Row(setMask.Mask);
                if (masked.Board is not { } maskedBoard || WorldTopologyCompilation.Find(definition.StateRaw, maskedBoard.Topology) is not { } maskedTopology ||
                    maskedTopology.CellCount > WorldBoardMask.MaxCells || maskSource.Kind != CellKind.Int ||
                    (setMask.MaskKey is null ? !maskSource.IsSlot : (!maskSource.IsKeyed || !WorldCellName.TryParse(setMask.MaskKey, out _, out _))) ||
                    masked.ClampToEnvelope(setMask.Value) != setMask.Value || (masked.Kind == CellKind.Bool && setMask.Value is not (0 or 1))) {
                    throw Invalid($"setMask requires a board of at most {WorldBoardMask.MaxCells} cells, an integer mask cell, and an admitted value");
                }
                break;
            case WorldStateTransform.MapBoard mapped:
                var mappedTarget = Row(mapped.Target);
                var mappedSource = Row(mapped.Source);
                if (mappedTarget.Board is not { } mappedBoard || WorldTopologyCompilation.Find(definition.StateRaw, mappedBoard.Topology) is not { } mappedTopology ||
                    mappedSource.Board?.Topology != mappedBoard.Topology || mappedSource.Kind != mappedTarget.Kind || mappedTopology.Element(mapped.Element ?? string.Empty) < 0) {
                    throw Invalid("mapBoard requires source and target boards of one kind over one topology and a symmetry element of that topology");
                }
                break;
            case WorldStateTransform.Push push:
                var ring = Row(push.Row);
                if (ring.History is null || ring.ClampToEnvelope(push.Value) != push.Value) {
                    throw Invalid("push requires a history row and an admitted value");
                }
                break;
            case WorldStateTransform.Combine combine:
                var combined = Row(combine.Target);
                var leftBoard = Row(combine.Left);
                if (combined.Board is not { } combinedBoard || combined.Kind is not (CellKind.Int or CellKind.Bool) || leftBoard.Board?.Topology != combinedBoard.Topology ||
                    !Enum.IsDefined(combine.Operation) ||
                    (combine.Operation == WorldBoardCombine.Not ? combine.Right is not null : (combine.Right is null || Row(combine.Right).Board?.Topology != combinedBoard.Topology))) {
                    throw Invalid("combine requires boards over one topology, an integer or boolean target, and a right board for every operation but not");
                }
                break;
            default:
                throw Invalid("unknown or null state transform");
        }
        return new(WorldRuleEffectKind.TransformState, string.Empty, string.Empty, default, 0, null,
            $"transformState {transform.GetType().Name}", Transform: transform);
    }
}
