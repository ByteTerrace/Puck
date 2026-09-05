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
            if (row?.Board is not { } board) {
                continue;
            }
            if (row.Kind is not (CellKind.Int or CellKind.Bool) || row.Lattice is not null || row.Draw is not null ||
                row.Advance is not null || row.Cycle is not null || row.Dynamics is not null || row.Evicts || row.GatesDrive) {
                errors.Add($"state row '{row.Name}': board requires plain integer or boolean cells without other storage or time traits.");
            }
            if (row.ClampToEnvelope(board.Empty) != board.Empty ||
                (row.Kind == CellKind.Bool && board.Empty is not (0 or 1))) {
                errors.Add($"state row '{row.Name}': board.empty is outside the value domain.");
            }
            var compiled = WorldTopologyCompilation.Find(definition.StateRaw, board.Topology);
            if (compiled is null) {
                errors.Add($"state row '{row.Name}': board.topology '{board.Topology}' names no valid discrete topology.");
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
            var traits = (row.Board is null ? 0 : 1) + (row.Tokens is null ? 0 : 1) + (row.Zone is null ? 0 : 1) + (row.Phase is null ? 0 : 1) + (row.KeysFrom is null ? 0 : 1);
            if (traits > 1 || (traits > 0 && (row.Lattice is not null || row.Draw is not null || row.Advance is not null || row.Dynamics is not null || row.Cycle is not null || row.Evicts || row.GatesDrive))) {
                errors.Add($"state row '{row.Name}': discrete storage traits are mutually exclusive and cannot carry continuous or draw traits.");
            }
            if (row.Tokens is { } tokens && (tokens.Capacity < 1 || tokens.Capacity > WorldTopologyCompilation.MaxCells)) {
                errors.Add($"state row '{row.Name}': tokens.capacity must be 1..{WorldTopologyCompilation.MaxCells}.");
            }
            var domainName = row.KeysFrom ?? row.Zone?.Tokens;
            if (row.ValuesFrom is { } topologyName) {
                var topology = WorldTopologyCompilation.Find(definition.StateRaw, topologyName);
                if (row.KeysFrom is null || row.Kind != CellKind.Int || topology is null ||
                    (row.Cells ?? []).Any(c => c is not null && (ulong)c.Value >= (ulong)topology.CellCount)) {
                    errors.Add($"state row '{row.Name}': valuesFrom requires token-keyed integer positions inside a discrete topology.");
                }
            }
            if (domainName is not null) {
                var domain = WorldDefinitionRows.FindStateRow(definition.State, domainName);
                if (domain?.Tokens is null) {
                    errors.Add($"state row '{row.Name}': '{domainName}' names no token domain.");
                } else {
                    var keys = new HashSet<WorldCellName>((domain.Cells ?? []).Where(c => c is not null).Select(c => c.Key));
                    foreach (var cell in row.Cells ?? []) {
                        if (cell is not null && !keys.Contains(cell.Key)) {
                            errors.Add($"state row '{row.Name}': key '{cell.Key}' is outside token domain '{domainName}'.");
                        }
                    }
                }
            }
            if (row.Zone is not null && (row.Kind != CellKind.Bool || (row.Cells ?? []).Any(c => c is not null && c.Value != 1))) {
                errors.Add($"state row '{row.Name}': zones contain boolean membership cells whose value is true.");
            }
            if (row.Phase is { } phase) {
                ValidatePhase(row, phase, errors);
            }
        }
        foreach (var domain in (definition.State ?? []).Where(r => r?.Tokens is not null)) {
            var zones = (definition.State ?? []).Where(r => r?.Zone?.Tokens == domain.Name.Value).ToArray();
            if (zones.Length == 0) {
                continue;
            }
            var members = new HashSet<WorldCellName>();
            foreach (var zone in zones) {
                foreach (var cell in zone.Cells ?? []) {
                    if (cell is not null && !members.Add(cell.Key)) {
                        errors.Add($"token '{domain.Name}.{cell.Key}' belongs to more than one zone.");
                    }
                }
            }
            foreach (var token in domain.Cells ?? []) {
                if (token is not null && !members.Contains(token.Key)) {
                    errors.Add($"token '{domain.Name}.{token.Key}' belongs to no zone.");
                }
            }
        }
    }

    private static void ValidatePhase(WorldStateRow row, WorldStatePhase phase, List<string> errors) {
        if (row.Kind != CellKind.Int || row.Cells is { Count: > 0 } || row.Capacity is not null || phase.Sequence < 0) {
            errors.Add($"state row '{row.Name}': phase requires an integer row without cells/capacity and a nonnegative sequence.");
        }
    }
}
