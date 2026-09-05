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
                if (positions.EffectiveDomain is not WorldStateDomain.KeysOf posKeysOf || allowance.EffectiveDomain is not WorldStateDomain.KeysOf allowKeysOf || allowKeysOf.Row != posKeysOf.Row ||
                    terrain.EffectiveDomain is not WorldStateDomain.CellsOf terrainBoard ||
                    positions.ValuesFrom != terrainBoard.Topology || positions.Kind != CellKind.Int || allowance.Kind != CellKind.Int || terrain.Kind != CellKind.Int ||
                    WorldTopologyCompilation.Find(definition.StateRaw, terrainBoard.Topology) is not { } map ||
                    (uint)move.Destination >= map.CellCount || move.MaxVisits < 1 || move.MaxVisits > map.CellCount) {
                    throw Invalid("moveToken requires bounded addressing and compatible position, allowance, and terrain rows");
                }
                break;
            case WorldStateTransform.Transfer transfer:
                if (Row(transfer.From).EffectiveDomain is not WorldStateDomain.KeysOf { Ordered: true } source || Row(transfer.To).EffectiveDomain is not WorldStateDomain.KeysOf { Ordered: true } destination || source.Row != destination.Row ||
                    !Enum.IsDefined(transfer.Selector) || (transfer.Selector == WorldZoneSelector.Key) != (transfer.Key is not null) ||
                    (transfer.Selector == WorldZoneSelector.Random) != (transfer.Draw is not null) ||
                    transfer.Count < 1 || transfer.Count > WorldStateTransferCapacity.MaxTransferCount || (transfer.Selector == WorldZoneSelector.Key && transfer.Count != 1)) {
                    throw Invalid($"transfer requires compatible ordered token zones, selector arguments, and a count of 1..{WorldStateTransferCapacity.MaxTransferCount} (exactly 1 by key)");
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
                if (row.EffectiveDomain is not WorldStateDomain.CellsOf board || WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology ||
                    !topology.TryCell(ray.From, out _) || topology.Direction(ray.Direction) < 0 ||
                    definition.Patterns.FirstOrDefault(candidate => candidate.Name.Value == ray.Pattern) is not { } pattern || pattern.Kind != CellKind.Int ||
                    row.ClampToEnvelope(ray.Value) != ray.Value || (row.Kind == CellKind.Bool && ray.Value is not (0 or 1))) {
                    throw Invalid("setRay requires valid board addressing, a declared integer-kind pattern, and an admitted replacement");
                }
                break;
            case WorldStateTransform.SortZone sortZone:
                if (Row(sortZone.Row).EffectiveDomain is not WorldStateDomain.KeysOf { Ordered: true } zone ||
                    sortZone.By is not { Count: >= 1 and <= WorldStateCapacity.MaxSortKeys } ||
                    sortZone.By.Any(key => key is null || Row(key.Row) is not { IsKeyed: true, Kind: CellKind.Int or CellKind.Fixed } sortRow || sortRow.EffectiveDomain is not WorldStateDomain.KeysOf sortKeysOf || sortKeysOf.Row != zone.Row) ||
                    sortZone.By.Select(key => key!.Row).Distinct(StringComparer.Ordinal).Count() != sortZone.By.Count) {
                    throw Invalid($"sortZone requires an ordered zone with 1..{WorldStateCapacity.MaxSortKeys} distinct numeric attribute keys over the zone's token domain, each carrying its own direction");
                }
                break;
            case WorldStateTransform.SortKeyed sortKeyed:
                if (Row(sortKeyed.Row) is not { IsKeyed: true, Kind: CellKind.Int or CellKind.Fixed }) {
                    throw Invalid("sortKeyed requires a keyed numeric row");
                }
                break;
            case WorldStateTransform.Shuffle shuffle:
                if (Row(shuffle.Row) is not { IsKeyed: true } ||
                    Row(shuffle.Draw).Draw is not { Timing: not WorldDrawTiming.Boot } shuffleDraw ||
                    Row(shuffle.Draw).Kind != CellKind.Int ||
                    !WorldGeneratorEngine.TryResolveSource(generators: definition.Generators, draw: shuffleDraw, generator: out var shuffleSource, reason: out _) ||
                    shuffleSource.Source != WorldGeneratorSource.StreamDraw) {
                    throw Invalid("shuffle requires a keyed row, and a redrawable integer streamDraw site");
                }
                break;
            case WorldStateTransform.WriteSet writeSet:
                var written = Row(writeSet.Row);
                var setSource = Row(writeSet.Set);
                if (written.EffectiveDomain is not WorldStateDomain.CellsOf writtenBoard || WorldTopologyCompilation.Find(definition.StateRaw, writtenBoard.Topology) is not { } writtenTopology ||
                    writtenTopology.CellCount > WorldBoardMask.MaxCells || setSource.Kind != CellKind.Int ||
                    (writeSet.SetKey is null ? !setSource.IsSlot : (!setSource.IsKeyed || !WorldCellName.TryParse(writeSet.SetKey, out _, out _))) ||
                    written.ClampToEnvelope(writeSet.Value) != writeSet.Value || (written.Kind == CellKind.Bool && writeSet.Value is not (0 or 1))) {
                    throw Invalid($"writeSet requires a board of at most {WorldBoardMask.MaxCells} cells, an integer set cell, and an admitted value");
                }
                break;
            case WorldStateTransform.Push push:
                var ring = Row(push.Row);
                if (ring.EffectiveDomain is not WorldStateDomain.Ring || ring.ClampToEnvelope(push.Value) != push.Value) {
                    throw Invalid("push requires a history row and an admitted value");
                }
                break;
            default:
                throw Invalid("unknown or null state transform");
        }
        return new(WorldRuleEffectKind.TransformState, string.Empty, string.Empty, default, 0, null,
            $"transformState {transform.GetType().Name}", Transform: transform);
    }
}
