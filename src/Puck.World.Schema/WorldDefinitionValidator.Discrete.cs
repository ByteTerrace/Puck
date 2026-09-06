namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateDiscreteState(WorldDefinition definition, List<string> errors) {
        ValidateTokenAndPhaseRows(definition, errors);
        ValidateStateDisclosure(definition, errors);
        var topologies = definition.StateRaw?.Lattices ?? [];
        if (topologies.Count > TopologyCompilation.MaxTopologies) {
            errors.Add($"state.lattices exceeds {TopologyCompilation.MaxTopologies} topologies.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topology in topologies) {
            if (topology is null || !CellName.TryParse(topology.Name, out _, out _) || !names.Add(topology.Name)) {
                errors.Add("state.lattices requires unique valid topology names.");
                continue;
            }
            // A physical field's case type structurally has no wrap/radius/directions/elementAliases property to
            // author in the first place, so there is nothing left to check for it here.
            if (topology.Kind != TopologyKind.Field && !TopologyCompilation.TryValidate(topology, out var reason)) {
                errors.Add($"state.lattices '{topology.Name}': {reason}.");
            }
        }
        var totalCells = 0L;
        foreach (var row in definition.State ?? []) {
            if (row is null || row.EffectiveDomain is not StateDomain.CellsOf board || row.Field is not null) {
                continue;
            }
            if (row.Kind is not (CellKind.Int or CellKind.Bool) || row.Draw is not null ||
                row.Advance is not null || row.Cycle is not null || row.Dynamics is not null || row.Evicts || row.GatesDrive) {
                errors.Add($"state row '{row.Name}': board requires plain integer or boolean cells without other storage or time traits.");
            }
            if (row.ClampToEnvelope(board.Empty) != board.Empty ||
                (row.Kind == CellKind.Bool && board.Empty is not (0 or 1))) {
                errors.Add($"state row '{row.Name}': board.empty is outside the value domain.");
            }
            var compiled = WorldTopologyCompilation.Find(definition, board.Topology);
            if (compiled is null) {
                errors.Add($"state row '{row.Name}': domain.topology '{board.Topology}' names no valid discrete topology.");
                continue;
            }
            totalCells += compiled.CellCount;
            if (row.Capacity is { } capacity && capacity != compiled.CellCount) {
                errors.Add($"state row '{row.Name}': a board's capacity must equal its topology's {compiled.CellCount} cells or be omitted.");
            }
            foreach (var cell in row.Cells ?? []) {
                if (cell is null || !compiled.TryCell(cell.Key.Value, out _) || cell.Advance is not null || cell.Cycle is not null || cell.Dynamics is not null) {
                    errors.Add($"state row '{row.Name}': board cells require canonical topology keys and literal values.");
                }
            }
            if (row.Inverse is { } inverse) {
                ValidateDerivedBoard(definition, row, inverse, compiled, errors);
            }
        }
        if (totalCells > TopologyCompilation.MaxTotalCells) {
            errors.Add($"state board storage exceeds the {TopologyCompilation.MaxTotalCells}-cell world budget.");
        }
    }

    // A derived board's tokens/codes both resolve to keyed integer rows, codes carries exactly the tokens row's own
    // keys in the same order (so the two rows' cell lists correspond by index — see DerivedBoards.Compose), and the
    // board's own authored cells (if any) are exactly what the derivation would produce today — an authored
    // mismatch is refused rather than silently overwritten, since the mutation door and the live compose step both
    // refuse a direct write to a derived board's cells and this is the door a hand-authored or foreign document
    // passes through instead.
    private static void ValidateDerivedBoard(WorldDefinition definition, WorldStateRow row, StateInverse inverse, CompiledTopology topology, List<string> errors) {
        var tokens = WorldDefinitionRows.FindStateRow(definition.State, inverse.Tokens.Value);
        var codes = WorldDefinitionRows.FindStateRow(definition.State, inverse.Codes.Value);

        if (tokens is null || tokens.Kind != CellKind.Int || tokens.EffectiveDomain is StateDomain.Slot) {
            errors.Add($"state row '{row.Name}': inverse.tokens '{inverse.Tokens}' names no keyed integer row.");
        }
        if (codes is null || codes.Kind != CellKind.Int) {
            errors.Add($"state row '{row.Name}': inverse.codes '{inverse.Codes}' names no integer row.");
        }
        if (tokens is null || codes is null) {
            return;
        }

        var tokenCells = (tokens.Cells ?? []);
        var codeCells = (codes.Cells ?? []);
        var sameShape = (tokenCells.Count == codeCells.Count);

        for (var index = 0; sameShape && (index < tokenCells.Count); index++) {
            sameShape = (tokenCells[index]?.Key == codeCells[index]?.Key);
        }
        if (!sameShape) {
            errors.Add($"state row '{row.Name}': inverse.codes '{inverse.Codes}' must carry the same keys, in the same order, as inverse.tokens '{inverse.Tokens}'.");

            return;
        }

        var derived = DerivedBoards.Compose(definition.State, inverse, topology);
        var authored = (row.Cells ?? []);

        if ((authored.Count > 0) && !MatchesDerivation(authored, derived)) {
            errors.Add($"state row '{row.Name}': authored cells must be empty or match its inverse's derivation — the board is never authored, only derived.");
        }
    }
    // Set comparison, not order-sensitive: an authored board's cell order is whatever the author or a prior save
    // wrote, while the derivation always walks cell order — the same board, spelled either way, must match.
    private static bool MatchesDerivation(IReadOnlyList<StateCell> authored, IReadOnlyList<StateCell> derived) {
        if (authored.Count != derived.Count) {
            return false;
        }

        var byKey = new Dictionary<CellName, long>(derived.Count);

        foreach (var cell in derived) {
            byKey[cell.Key] = cell.Value;
        }

        foreach (var cell in authored) {
            if (cell is null || !byKey.TryGetValue(cell.Key, out var value) || (value != cell.Value)) {
                return false;
            }
        }

        return true;
    }

    private static void ValidateTokenAndPhaseRows(WorldDefinition definition, List<string> errors) {
        foreach (var row in definition.State ?? []) {
            if (row is null) {
                continue;
            }
            // A physical-field row (CellsOf domain + a Field trait) legitimately carries a draw fill inside its own
            // paint (validated separately in WorldDefinitionValidator.State.cs); every OTHER discrete domain (KeysOf,
            // or CellsOf with no Field trait — a plain board) admits none of these continuous or draw traits.
            var domainTraits = (row.Domain is StateDomain.CellsOf or StateDomain.KeysOf ? 1 : 0) + (row.Phase is null ? 0 : 1);
            var isPhysicalField = ((row.Domain is StateDomain.CellsOf) && (row.Field is not null));
            if (domainTraits > 1 || (domainTraits > 0 && !isPhysicalField && (row.Field is not null || row.Draw is not null || row.Advance is not null || row.Dynamics is not null || row.Cycle is not null || row.Evicts || row.GatesDrive))) {
                errors.Add($"state row '{row.Name}': discrete storage traits are mutually exclusive and cannot carry continuous or draw traits.");
            }
            if (row.Inverse is not null && ((row.EffectiveDomain is not StateDomain.CellsOf) || (row.Field is not null) || (row.Kind != CellKind.Int))) {
                errors.Add($"state row '{row.Name}': inverse requires an integer board — a cellsOf domain over int cells with no field trait.");
            }
            var domainName = (row.EffectiveDomain is StateDomain.KeysOf keysOf ? keysOf.Row.Value : null);
            if (row.ValuesFrom is { } topologyName) {
                var topology = WorldTopologyCompilation.Find(definition, topologyName);
                if (domainName is null || row.Kind != CellKind.Int || topology is null ||
                    (row.Cells ?? []).Any(c => c is not null && (ulong)c.Value >= (ulong)topology.CellCount)) {
                    errors.Add($"state row '{row.Name}': valuesFrom requires token-keyed integer positions inside a discrete topology.");
                }
            }
            if (domainName is not null) {
                var domain = WorldDefinitionRows.FindStateRow(definition.State, domainName);
                if (domain is null || domain.EffectiveDomain is not StateDomain.Keys) {
                    errors.Add($"state row '{row.Name}': '{domainName}' names no token domain.");
                } else {
                    var keys = new HashSet<CellName>((domain.Cells ?? []).Where(c => c is not null).Select(c => c.Key));
                    foreach (var cell in row.Cells ?? []) {
                        if (cell is not null && !keys.Contains(cell.Key)) {
                            errors.Add($"state row '{row.Name}': key '{cell.Key}' is outside token domain '{domainName}'.");
                        }
                    }
                }
            }
            if (row.EffectiveDomain is StateDomain.KeysOf { Ordered: true } && (row.Kind != CellKind.Bool || (row.Cells ?? []).Any(c => c is not null && c.Value != 1))) {
                errors.Add($"state row '{row.Name}': an ordered keysOf (pile/zone) row contains boolean membership cells whose value is true.");
            }
            if (row.Phase is { } phase) {
                ValidatePhase(row, phase, errors);
            }
        }
    }

    private static void ValidatePhase(WorldStateRow row, StatePhase phase, List<string> errors) {
        if (row.Kind != CellKind.Int || row.EffectiveDomain is StateDomain.Slot || row.Cells is { Count: > 0 } || row.Capacity is not null || phase.Sequence < 0) {
            errors.Add($"state row '{row.Name}': phase requires an integer row without cells/capacity and a nonnegative sequence.");
        }
    }
}
