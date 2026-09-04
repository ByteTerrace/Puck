namespace Puck.World;

public static partial class WorldRuleCompiler {
    // $match:<pattern>:<row>[:<direction>|:any][:<facet>] — a board source walks the ray from the operand key's
    // origin cell (exclusive) in the named direction, or every direction under `any`; an ordered zone reads the
    // pattern's attribute row in pile order; a keyed row reads its own cells in cell order. The word's kind must be
    // the pattern's kind. The facet is `prefix` (longest accepted prefix length) on one word, or `mask`/`count`
    // over `any`; absent, the operand answers acceptance.
    private static ResolvedOperand ResolvePatternOperand(string name, string? key, string ruleName, WorldDefinition definition) {
        WorldRuleException Invalid(string detail) => new(WorldRuleRefusal.StateCellUnaddressable, ruleName, detail);
        var tokens = name.Split(':');
        if (tokens.Length is < 3 or > 5) {
            throw Invalid("pattern match requires $match:<pattern>:<row>[:<direction>|:any][:prefix|:mask|:count]");
        }
        var facet = WorldMatchFacet.Accept;
        var pattern = definition.Patterns.FirstOrDefault(candidate => candidate.Name.Value == tokens[1]) ?? throw Invalid($"'{tokens[1]}' names no pattern");
        var row = WorldDefinitionRows.FindStateRow(definition.State, tokens[2]) ?? throw Invalid($"'{tokens[2]}' names no state row");
        CompiledWorldBoardQuery? board = null;
        CompiledCellRef? keyFrom = null;
        string? attribute = null;
        CompiledWorldExpressionToken[]? tokenExpression = null;
        CellKind kind;
        if (row.Board is { } declaredBoard) {
            if (tokens.Length < 4) {
                throw Invalid("a board source requires a direction or any");
            }
            var topology = WorldTopologyCompilation.Find(definition.StateRaw, declaredBoard.Topology) ?? throw Invalid($"'{tokens[2]}' names no compiled topology");
            var every = tokens[3] == "any";
            var direction = every ? -1 : topology.Direction(tokens[3]);
            if (direction < 0 && !every) {
                throw Invalid($"'{tokens[3]}' is not a direction of '{declaredBoard.Topology}'");
            }
            if (tokens.Length == 5) {
                facet = tokens[4] switch {
                    "prefix" when !every => WorldMatchFacet.Prefix,
                    "mask" when every => WorldMatchFacet.DirectionMask,
                    "count" when every => WorldMatchFacet.DirectionCount,
                    _ => throw Invalid($"'{tokens[4]}' is not a facet for this source: prefix on one direction, mask or count over any"),
                };
            }
            if (TryResolveDynamicKey(definition: definition, key: key, ruleName: ruleName, verb: "match", keyFieldLabel: "key", cell: out var dynamicKey)) {
                keyFrom = dynamicKey;
            } else if (key is null || !topology.TryCell(key, out _)) {
                throw Invalid("a board source's key must name the origin cell or use a validated dynamic key");
            }
            board = new CompiledWorldBoardQuery(topology, WorldBoardQueryKind.RayCell, Direction: direction);
            kind = CellKind.Int;
        } else {
            if (tokens.Length > 4 || key is not null) {
                throw Invalid("a zone or keyed source takes neither a direction nor a key");
            }
            if (tokens.Length == 4) {
                facet = (tokens[3] == "prefix") ? WorldMatchFacet.Prefix : throw Invalid($"'{tokens[3]}' is not a facet for a word source; prefix is");
            }
            if (row.Zone is { } zone) {
                if (!zone.Ordered) {
                    throw Invalid($"zone '{row.Name}' must be ordered to read as a word");
                }
                if (pattern.Value is not null) {
                    if (!TryCompilePatternValue(definition: definition, pattern: pattern, tokenDomain: zone.Tokens, ruleName: ruleName, tokens: out tokenExpression, reason: out var valueReason)) {
                        throw Invalid(valueReason);
                    }
                    kind = pattern.Kind;
                    return new(new CompiledWorldOperand(WorldRuleFactKind.Pattern, tokens[2], key, Board: null, Pattern: tokens[1], ValueKind: CellKind.Int, MatchFacet: facet,
                        StateHandle: ResolveWorldStateHandle(definition: definition, name: tokens[2]), TokenExpression: tokenExpression), CellKind.Int, name);
                }
                attribute = pattern.Attribute ?? throw Invalid($"pattern '{pattern.Name}' reads a zone and so needs an attribute row or a value expression");
                var attributeRow = WorldDefinitionRows.FindStateRow(definition.State, attribute) ?? throw Invalid($"attribute '{attribute}' names no state row");
                if (!attributeRow.IsKeyed || attributeRow.Kind is not (CellKind.Int or CellKind.Fixed) || attributeRow.KeysFrom != zone.Tokens) {
                    throw Invalid($"attribute '{attribute}' must be a numeric row keyed over token domain '{zone.Tokens}'");
                }
                kind = attributeRow.Kind;
            } else {
                if ((!row.IsKeyed && row.History is null) || pattern.Attribute is not null) {
                    throw Invalid($"'{row.Name}' must be a keyed or history row read without an attribute");
                }
                kind = row.Kind == CellKind.Bool ? CellKind.Int : row.Kind;
            }
        }
        if (kind != pattern.Kind) {
            throw Invalid($"pattern '{pattern.Name}' reads kind={pattern.Kind} but the source word is kind={kind}");
        }
        return new(new CompiledWorldOperand(WorldRuleFactKind.Pattern, tokens[2], key, KeyFrom: keyFrom, Board: board, FilterRow: attribute, Pattern: tokens[1], ValueKind: CellKind.Int, MatchFacet: facet,
            StateHandle: ResolveWorldStateHandle(definition: definition, name: tokens[2]),
            FilterHandle: (attribute is null) ? default : ResolveWorldStateHandle(definition: definition, name: attribute)), CellKind.Int, name);
    }

    /// <summary>Compiles a pattern row's value expression for one zone: inside it, a state token keyed
    /// <c>$token</c> reads the current token's cell of a row keyed over <paramref name="tokenDomain"/>.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="pattern">The pattern row carrying the expression.</param>
    /// <param name="tokenDomain">The zone's token domain.</param>
    /// <param name="ruleName">The rule or console verb the compile answers for.</param>
    /// <param name="tokens">The compiled postfix program, on success.</param>
    /// <param name="reason">The refusal, on failure.</param>
    /// <returns><see langword="true"/> when the expression compiles in the pattern's kind.</returns>
    public static bool TryCompilePatternValue(WorldDefinition definition, WorldPatternRow pattern, string tokenDomain, string ruleName, out CompiledWorldExpressionToken[]? tokens, out string reason) {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(pattern);
        var scope = s_bindingScope;
        s_bindingScope = [RuleBinding.Token];
        try {
            tokens = CompileExpression(expression: pattern.Value, kind: pattern.Kind, ruleName: ruleName, verb: $"pattern '{pattern.Name}' value", definition: definition);
            foreach (var token in tokens) {
                if (token.Operand is { KeyFrom: { Binding: RuleBinding.Token } } operand &&
                    (WorldDefinitionRows.FindStateRow(definition.State, operand.Row ?? string.Empty) is not { } row || row.KeysFrom != tokenDomain)) {
                    tokens = null;
                    reason = $"pattern '{pattern.Name}' value reads '{operand.Row}' by $token, which must be a row keyed over token domain '{tokenDomain}'";
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        } catch (WorldRuleException exception) {
            tokens = null;
            reason = exception.Message;
            return false;
        } finally {
            s_bindingScope = scope;
        }
    }
}
