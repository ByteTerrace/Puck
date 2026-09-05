namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static void ValidateDiscreteState(WorldDefinition definition, List<string> errors) {
        ValidateTokenAndPhaseRows(definition, errors);
        ValidateStateDisclosure(definition, errors);
        var topologies = definition.StateRaw?.Lattices ?? [];
        if (topologies.Count > WorldTopologyCompilation.MaxTopologies) {
            errors.Add($"state.lattices exceeds {WorldTopologyCompilation.MaxTopologies} topologies.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topology in topologies) {
            if (topology is null || !WorldCellName.TryParse(topology.Name, out _, out _) || !names.Add(topology.Name)) {
                errors.Add("state.lattices requires unique valid topology names.");
                continue;
            }
            if (topology.Kind != WorldTopologyKind.Field && !WorldTopologyCompilation.TryValidate(topology, out var reason)) {
                errors.Add($"state.lattices '{topology.Name}': {reason}.");
            }
            if (topology.Kind == WorldTopologyKind.Field && (topology.Wrap != WorldTopologyWrap.None || topology.Radius != 0 || topology.Directions is not null || topology.ElementAliases is not null)) {
                errors.Add($"state.lattices '{topology.Name}': physical fields do not admit discrete wrapping, radius, a direction vocabulary, or element aliases.");
            }
        }
        var totalCells = 0L;
        foreach (var row in definition.State ?? []) {
            if (row is null || row.EffectiveDomain is not WorldStateDomain.CellsOf board || row.Field is not null) {
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
            var compiled = WorldTopologyCompilation.Find(definition.StateRaw, board.Topology);
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
        }
        if (totalCells > WorldTopologyCompilation.MaxTotalCells) {
            errors.Add($"state board storage exceeds the {WorldTopologyCompilation.MaxTotalCells}-cell world budget.");
        }
    }

    private static void ValidateTokenAndPhaseRows(WorldDefinition definition, List<string> errors) {
        foreach (var row in definition.State ?? []) {
            if (row is null) {
                continue;
            }
            // A physical-field row (CellsOf domain + a Field trait) legitimately carries a draw fill inside its own
            // paint (validated separately in WorldDefinitionValidator.State.cs); every OTHER discrete domain (KeysOf,
            // or CellsOf with no Field trait — a plain board) admits none of these continuous or draw traits.
            var domainTraits = (row.Domain is WorldStateDomain.CellsOf or WorldStateDomain.KeysOf ? 1 : 0) + (row.Phase is null ? 0 : 1);
            var isPhysicalField = ((row.Domain is WorldStateDomain.CellsOf) && (row.Field is not null));
            if (domainTraits > 1 || (domainTraits > 0 && !isPhysicalField && (row.Field is not null || row.Draw is not null || row.Advance is not null || row.Dynamics is not null || row.Cycle is not null || row.Evicts || row.GatesDrive))) {
                errors.Add($"state row '{row.Name}': discrete storage traits are mutually exclusive and cannot carry continuous or draw traits.");
            }
            if (row.EffectiveDomain is WorldStateDomain.Keys && row.Capacity is { } tokensCapacity && (tokensCapacity < 1 || tokensCapacity > WorldTopologyCompilation.MaxCells)) {
                errors.Add($"state row '{row.Name}': capacity must be 1..{WorldTopologyCompilation.MaxCells}.");
            }
            var domainName = (row.EffectiveDomain is WorldStateDomain.KeysOf keysOf ? keysOf.Row.Value : null);
            if (row.ValuesFrom is { } topologyName) {
                var topology = WorldTopologyCompilation.Find(definition.StateRaw, topologyName);
                if (domainName is null || row.Kind != CellKind.Int || topology is null ||
                    (row.Cells ?? []).Any(c => c is not null && (ulong)c.Value >= (ulong)topology.CellCount)) {
                    errors.Add($"state row '{row.Name}': valuesFrom requires token-keyed integer positions inside a discrete topology.");
                }
            }
            if (domainName is not null) {
                var domain = WorldDefinitionRows.FindStateRow(definition.State, domainName);
                if (domain is null || domain.EffectiveDomain is not (WorldStateDomain.Keys or WorldStateDomain.CellsOf)) {
                    errors.Add($"state row '{row.Name}': '{domainName}' names no token domain.");
                } else {
                    var keys = new HashSet<WorldCellName>((domain.Cells ?? []).Where(c => c is not null).Select(c => c.Key));
                    foreach (var cell in row.Cells ?? []) {
                        if (cell is not null && keys.Count > 0 && !keys.Contains(cell.Key)) {
                            errors.Add($"state row '{row.Name}': key '{cell.Key}' is outside token domain '{domainName}'.");
                        }
                    }
                }
            }
            if (row.EffectiveDomain is WorldStateDomain.KeysOf { Ordered: true } && (row.Kind != CellKind.Bool || (row.Cells ?? []).Any(c => c is not null && c.Value != 1))) {
                errors.Add($"state row '{row.Name}': an ordered keysOf (pile/zone) row contains boolean membership cells whose value is true.");
            }
            if (row.Phase is { } phase) {
                ValidatePhase(row, phase, errors);
            }
        }
    }

    private static void ValidatePhase(WorldStateRow row, WorldStatePhase phase, List<string> errors) {
        if (row.Kind != CellKind.Int || row.Cells is { Count: > 0 } || row.Capacity is not null || phase.Sequence < 0) {
            errors.Add($"state row '{row.Name}': phase requires an integer row without cells/capacity and a nonnegative sequence.");
        }
    }
}
