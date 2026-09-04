namespace Puck.World;

public static partial class WorldRuleCompiler {
    // $match:<pattern>:<row>[:<direction>] — a board source walks the ray from the operand key's origin cell
    // (exclusive) in the named direction; an ordered zone reads the pattern's attribute row in pile order; a keyed row
    // reads its own cells in cell order. The word's kind must be the pattern's kind.
    private static ResolvedOperand ResolvePatternOperand(string name, string? key, string ruleName, WorldDefinition definition) {
        WorldRuleException Invalid(string detail) => new(WorldRuleRefusal.StateCellUnaddressable, ruleName, detail);
        var tokens = name.Split(':');
        if (tokens.Length is < 3 or > 4) {
            throw Invalid("pattern match requires $match:<pattern>:<row>[:<direction>]");
        }
        var pattern = definition.Patterns.FirstOrDefault(candidate => candidate.Name.Value == tokens[1]) ?? throw Invalid($"'{tokens[1]}' names no pattern");
        var row = WorldDefinitionRows.FindStateRow(definition.State, tokens[2]) ?? throw Invalid($"'{tokens[2]}' names no state row");
        CompiledWorldBoardQuery? board = null;
        CompiledCellRef? keyFrom = null;
        string? attribute = null;
        CellKind kind;
        if (row.Board is { } declaredBoard) {
            if (tokens.Length != 4) {
                throw Invalid("a board source requires a direction");
            }
            var topology = WorldTopologyCompilation.Find(definition.StateRaw, declaredBoard.Topology) ?? throw Invalid($"'{tokens[2]}' names no compiled topology");
            var direction = topology.Direction(tokens[3]);
            if (direction < 0) {
                throw Invalid($"'{tokens[3]}' is not a direction of '{declaredBoard.Topology}'");
            }
            if (TryResolveDynamicKey(definition: definition, key: key, ruleName: ruleName, verb: "match", keyFieldLabel: "key", cell: out var dynamicKey)) {
                keyFrom = dynamicKey;
            } else if (key is null || !topology.TryCell(key, out _)) {
                throw Invalid("a board source's key must name the origin cell or use a validated dynamic key");
            }
            board = new CompiledWorldBoardQuery(topology, WorldBoardQueryKind.RayCell, Direction: direction);
            kind = CellKind.Int;
        } else {
            if (tokens.Length != 3 || key is not null) {
                throw Invalid("a zone or keyed source takes neither a direction nor a key");
            }
            if (row.Zone is { } zone) {
                if (!zone.Ordered) {
                    throw Invalid($"zone '{row.Name}' must be ordered to read as a word");
                }
                attribute = pattern.Attribute ?? throw Invalid($"pattern '{pattern.Name}' reads a zone and so needs an attribute row");
                var attributeRow = WorldDefinitionRows.FindStateRow(definition.State, attribute) ?? throw Invalid($"attribute '{attribute}' names no state row");
                if (!attributeRow.IsKeyed || attributeRow.Kind is not (CellKind.Int or CellKind.Fixed) || attributeRow.KeysFrom != zone.Tokens) {
                    throw Invalid($"attribute '{attribute}' must be a numeric row keyed over token domain '{zone.Tokens}'");
                }
                kind = attributeRow.Kind;
            } else {
                if (!row.IsKeyed || pattern.Attribute is not null) {
                    throw Invalid($"'{row.Name}' must be a keyed row read without an attribute");
                }
                kind = row.Kind == CellKind.Bool ? CellKind.Int : row.Kind;
            }
        }
        if (kind != pattern.Kind) {
            throw Invalid($"pattern '{pattern.Name}' reads kind={pattern.Kind} but the source word is kind={kind}");
        }
        return new(new CompiledWorldOperand(WorldRuleFactKind.Pattern, tokens[2], key, KeyFrom: keyFrom, Board: board, FilterRow: attribute, Pattern: tokens[1], ValueKind: CellKind.Int,
            StateHandle: ResolveWorldStateHandle(definition: definition, name: tokens[2]),
            FilterHandle: (attribute is null) ? default : ResolveWorldStateHandle(definition: definition, name: attribute)), CellKind.Int, name);
    }
}
