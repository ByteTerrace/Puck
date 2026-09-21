using System.Globalization;

namespace Puck.State.Rules;

public static partial class RuleCompiler {
    /// <summary>Emits a bounded postfix Boolean program: leaf comparisons push, All/Any consume their child count,
    /// Not flips one result. A <see langword="null"/> predicate compiles to the empty program ("always").</summary>
    /// <param name="predicate">The authored gate.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The compiled gate.</returns>
    public static GateToken[] CompileGate(ActionPredicate? predicate, string ruleName, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var gate = new List<GateToken>();

        FlattenPredicate(
            context: context,
            gate: gate,
            predicate: predicate,
            ruleName: ruleName
        );

        if (gate.Count > RuleCapacity.MaxPredicateTokens) {
            throw new RuleException(
                detail: $"gate compiles to {gate.Count} tokens, exceeding the {RuleCapacity.MaxPredicateTokens}-token ceiling",
                refusal: RuleRefusal.PredicateKindInadmissible,
                ruleName: ruleName
            );
        }

        return [.. gate];
    }
    /// <summary>Compiles an expression key (<see cref="RuleFacts.ExpressionKeyPrefix"/>) into an implicit int
    /// binding appended to the rule's bindings and returns the key that reads it back. Repeated spellings in the
    /// same binding scope reuse that binding.</summary>
    /// <param name="text">The expression's canonical infix spelling.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="where">The field the key was spelled in, for refusal text.</param>
    /// <returns>The key that reads the binding back.</returns>
    public static CompiledCellRef CompileKeyExpression(string text, string ruleName, RuleCompileContext context, string where) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var cacheKey = (text, context.BindingScope);

        if (context.KeyExpressions.TryGetValue(
            key: cacheKey,
            value: out var cached
        )) {
            return cached;
        }
        if (!ExpressionSpelling.TryParse(
            error: out var error,
            program: out var parsed,
            text: text
        )) {
            throw new RuleException(
                detail: $"{where} key expression '{text}' does not parse: {error}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var bindings = (context.RuleLocals ??= []);
        var program = CompileExpression(
            context: context,
            expression: parsed,
            kind: CellKind.Int,
            ruleName: ruleName,
            verb: $"{where} key expression"
        );

        // Nested keys append their dependencies while compiling the expression; price the new binding after them.
        if (bindings.Count >= RuleCapacity.MaxLocalsPerRule) {
            throw new RuleException(
                detail: $"{where} key expression '{text}' would be binding {(bindings.Count + 1)}, exceeding the {RuleCapacity.MaxLocalsPerRule}-binding ceiling (declared and implicit key bindings together)",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        var ordinal = bindings.Count;

        bindings.Add(item: new CompiledRuleLocal(
            CarrierKind: CellKind.Int,
            Expression: program,
            Kind: CellKind.Int,
            Name: $"$key{ordinal}"
        ));

        var reference = new CompiledCellRef(
            Custom: new LocalKeyFact(
                ordinal: ordinal,
                source: program
            ),
            Key: default,
            RowOrdinal: -1
        );

        context.KeyExpressions.Add(
            key: cacheKey,
            value: reference
        );

        return reference;
    }
    /// <summary>Formats one comparison for the rules read-back.</summary>
    /// <param name="comparison">The comparison.</param>
    /// <returns>The infix spelling.</returns>
    public static string DescribeComparison(ActionStateComparison comparison) => (comparison switch {
        ActionStateComparison.Equal => "==",
        ActionStateComparison.NotEqual => "!=",
        ActionStateComparison.Less => "<",
        ActionStateComparison.LessOrEqual => "<=",
        ActionStateComparison.Greater => ">",
        _ => ">=",
    });
    /// <summary>Lowers a constant comparand against a cell exactly: an integral literal is the raw it names, and a
    /// fractional one against an Int or Bool cell becomes the equivalent integer comparison rather than a rounded
    /// literal that would move the gate. A Fixed cell keeps its exact fixed-point literal.</summary>
    /// <param name="kind">The cell kind the comparison reads in.</param>
    /// <param name="literal">The authored literal.</param>
    /// <param name="comparison">The authored comparison.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <returns>The raw comparand and the comparison it is applied with.</returns>
    /// <remarks>The conversion table, stated once for every spelling that compares a literal against a cell:
    /// <c>x &gt; 1.5</c> is <c>x &gt;= 2</c>, <c>x &gt;= 1.5</c> is <c>x &gt;= 2</c>, <c>x &lt; 1.5</c> is
    /// <c>x &lt;= 1</c>, <c>x &lt;= 1.5</c> is <c>x &lt;= 1</c>, <c>x == 1.5</c> never holds, and
    /// <c>x != 1.5</c> always holds.</remarks>
    public static (long Value, ActionStateComparison Comparison) LowerConstantComparison(CellKind kind, decimal literal, ActionStateComparison comparison, string ruleName) {
        if (
            (kind == CellKind.Fixed) ||
            (decimal.Truncate(d: literal) == literal)
        ) {
            return (LiteralToRaw(
                kind: kind,
                literal: literal,
                ruleName: ruleName,
                verb: "compareState"
            ), comparison);
        }

        var ceiling = LiteralToRaw(
            kind: kind,
            literal: decimal.Ceiling(d: literal),
            ruleName: ruleName,
            verb: "compareState"
        );
        var floor = LiteralToRaw(
            kind: kind,
            literal: decimal.Floor(d: literal),
            ruleName: ruleName,
            verb: "compareState"
        );

        return (comparison switch {
            ActionStateComparison.Greater or ActionStateComparison.GreaterOrEqual => (ceiling, ActionStateComparison.GreaterOrEqual),
            ActionStateComparison.Less or ActionStateComparison.LessOrEqual => (floor, ActionStateComparison.LessOrEqual),
            ActionStateComparison.Equal => (long.MaxValue, ActionStateComparison.Greater),
            _ => (long.MinValue, ActionStateComparison.GreaterOrEqual),
        });
    }
    /// <summary>Resolves a <c>$cell:&lt;row&gt;:&lt;key&gt;</c> indirection: the named cell must exist on a declared
    /// int or text row, since its value is read as a key every evaluation.</summary>
    /// <param name="row">The row name.</param>
    /// <param name="key">The inner key.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="channel">The authored spelling, for refusal text.</param>
    /// <returns>The compiled indirection.</returns>
    public static CompiledCellRef ResolveCellRef(string row, string key, string ruleName, RuleCompileContext context, string channel) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var declared = ResolveRequiredRow(
            channel: channel,
            context: context,
            malformed: RuleRefusal.StateCellUnaddressable,
            name: row,
            requireKeyed: false,
            requireNumeric: false,
            ruleName: ruleName
        );

        if (declared.Kind is not (CellKind.Int or CellKind.Text)) {
            throw new RuleException(
                detail: $"'{channel}' reads row '{row}' as a key, but it is kind={StateSpelling.Kind(kind: declared.Kind)} — a key cell is kind=Int or Text",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var rowOrdinal = ResolveRowOrdinal(
            context: context,
            name: row
        );

        if (
            (RuleBindingTokens.OfKeyToken(key: key) is var innerBinding) &&
            (innerBinding != BoundKey.None)
        ) {
            RequireBindingInScope(
                binding: innerBinding,
                context: context,
                ruleName: ruleName,
                spelled: key,
                where: $"'{channel}' inner key"
            );

            return new CompiledCellRef(
                InnerKeyBinding: innerBinding,
                Key: default,
                Kind: declared.Kind,
                RowOrdinal: rowOrdinal
            );
        }

        var resolvedKey = ResolveKey(
            key: key,
            keyFieldLabel: "key",
            row: declared,
            ruleName: ruleName,
            verb: channel
        );

        if (!declared.HasCell(key: resolvedKey)) {
            throw new RuleException(
                detail: $"'{channel}' reads cell '{row}'.'{resolvedKey}' as a key, which the row does not declare",
                refusal: RuleRefusal.StateCellUndeclared,
                ruleName: ruleName
            );
        }

        return new CompiledCellRef(
            Key: InternKey(
                context: context,
                name: resolvedKey
            ),
            Kind: declared.Kind,
            RowOrdinal: rowOrdinal
        );
    }
    /// <summary>Resolves a dynamic key spelling — a binding token, a registered key family's spelling, a
    /// <c>$zone:</c> endpoint, an <c>$expr:</c> expression, a <c>$local:</c> binding, or a <c>$cell:</c> indirection.
    /// A literal key returns <see langword="false"/>.</summary>
    /// <param name="reference">The authored key: a literal key, or a reserved channel as a call.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="keyFieldLabel">The field the key was spelled in, for refusal text.</param>
    /// <param name="cell">The compiled indirection, when dynamic.</param>
    /// <returns><see langword="true"/> when the key is dynamic.</returns>
    public static bool TryResolveDynamicKey(StateChannelRef? reference, string ruleName, RuleCompileContext context, string verb, string keyFieldLabel, out CompiledCellRef cell) {
        ArgumentNullException.ThrowIfNull(argument: context);

        // The call is what the walk dispatches on; the spelling is what a binding token and a refusal read.
        var call = reference?.Call;
        var key = reference?.Spelling;

        if (
            (RuleBindingTokens.OfKeyToken(key: key) is var bound) &&
            (bound != BoundKey.None)
        ) {
            RequireBindingInScope(
                binding: bound,
                context: context,
                ruleName: ruleName,
                spelled: key!,
                where: $"'{verb}' {keyFieldLabel}"
            );
            cell = new CompiledCellRef(
                Binding: bound,
                // A pattern word's first token has no token before it, and the authored contract is that $previous
                // reads the cell no keyed row holds there, not that it names nothing.
                Key: ((bound == BoundKey.Previous)
                    ? InternKey(
                        context: context,
                        name: StateRow.SlotKey.Value
                    )
                    : default
                ),
                RowOrdinal: -1
            );

            return true;
        }

        if (reference is not null) {
            foreach (var family in context.Vocabulary.Keys) {
                if (family.TryCompile(
                    cell: out cell,
                    context: context,
                    reference: reference,
                    keyFieldLabel: keyFieldLabel,
                    ruleName: ruleName,
                    verb: verb
                )) {
                    context.Needs.AddFact(fact: cell.Custom);

                    return true;
                }
            }
            if (call?.Channel == "zone") {
                var parts = call.Tokens();

                if (
                    (parts.Length != 3) ||
                    (parts[2] is not ("first" or "last"))
                ) {
                    throw new RuleException(
                        detail: $"'{verb}' {keyFieldLabel} '{key}' must spell '$zone:<ordered-zone>:<first|last>'",
                        refusal: RuleRefusal.StateCellUnaddressable,
                        ruleName: ruleName
                    );
                }

                var last = (parts[2] == "last");

                if (TryResolveLiveRow(
                    context: context,
                    name: parts[1],
                    ruleName: ruleName,
                    row: out var live,
                    where: $"'{verb}' {keyFieldLabel} '{key}' zone"
                )) {
                    cell = new CompiledCellRef(
                        Custom: new ZoneEndKey(
                            last: last,
                            rowFrom: live,
                            rowOrdinal: -1
                        ),
                        Key: default,
                        RowOrdinal: -1
                    );

                    return true;
                }
                if (context.FindRow(name: parts[1])?.EffectiveDomain is not StateDomain.KeysOf { Ordered: true }) {
                    throw new RuleException(
                        detail: $"'{verb}' {keyFieldLabel} '{key}' must spell '$zone:<ordered-zone>:<first|last>'",
                        refusal: RuleRefusal.StateCellUnaddressable,
                        ruleName: ruleName
                    );
                }

                cell = new CompiledCellRef(
                    Custom: new ZoneEndKey(
                        last: last,
                        rowFrom: null,
                        rowOrdinal: ResolveRowOrdinal(
                            context: context,
                            name: parts[1]
                        )
                    ),
                    Key: default,
                    RowOrdinal: -1
                );

                return true;
            }
            // A rule's own binding as a key — the same implicit-binding carrier an expression key reads back through.
            if (call?.Channel == "local") {
                var binding = ResolveLocalOperand(
                    context: context,
                    key: null,
                    keyFieldLabel: keyFieldLabel,
                    name: key!,
                    ruleName: ruleName
                );

                if (binding.ValueKind != CellKind.Int) {
                    throw new RuleException(
                        detail: $"'{verb}' {keyFieldLabel} '{key}' is a fixed binding — a key binding is int",
                        refusal: RuleRefusal.StateCellUnaddressable,
                        ruleName: ruleName
                    );
                }

                cell = new CompiledCellRef(
                    Custom: new LocalKeyFact(
                        ordinal: ((LocalOperand)binding.Operand).Ordinal,
                        source: ((LocalOperand)binding.Operand).Source?.Expression
                    ),
                    Key: default,
                    RowOrdinal: -1
                );

                return true;
            }
            if (call?.Channel == "expr") {
                cell = CompileKeyExpression(
                    context: context,
                    ruleName: ruleName,
                    text: key![RuleFacts.ExpressionKeyPrefix.Length..],
                    where: $"'{verb}' {keyFieldLabel}"
                );

                return true;
            }
        }

        if (call?.Channel != "cell") {
            cell = default;

            return false;
        }

        var segments = call.Texts();

        if (segments.Length != 2) {
            throw new RuleException(
                detail: $"'{verb}' {keyFieldLabel} '{key}' does not spell '{RuleFacts.CellKeyPrefix}<row>:<key>'",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        cell = ResolveCellRef(
            channel: $"{verb} {keyFieldLabel} '{key}'",
            context: context,
            key: segments[1],
            row: segments[0],
            ruleName: ruleName
        );

        return true;
    }
    /// <summary>Resolves a live row spelling: <c>$zones[&lt;index&gt;]</c> against the enclosing rule's zone table,
    /// or <c>&lt;family&gt;[&lt;index&gt;]</c> against a declared family. A literal row name returns
    /// <see langword="false"/>.</summary>
    /// <param name="name">The authored row position.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="where">Where the row is spelled, for refusal text.</param>
    /// <param name="row">The live row, when the spelling is one.</param>
    /// <returns><see langword="true"/> when the spelling selects a row live.</returns>
    /// <exception cref="RuleException">The spelling is malformed, the index is not live, or the rule declares no
    /// zone table.</exception>
    public static bool TryResolveLiveRow(string name, string ruleName, RuleCompileContext context, string where, out LiveRow? row) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: name);

        row = null;

        var bracket = name.IndexOf(value: '[');

        if (
            (bracket <= 0) ||
            (name[^1] != ']')
        ) {
            return false;
        }

        var head = name[..bracket];
        var overZones = string.Equals(
            a: $"{head}[",
            b: RuleFacts.LiveZonePrefix,
            comparisonType: StringComparison.Ordinal
        );
        var family = default(RowFamily);

        // A head that is not a well-formed cell name selects nothing live; the spelling falls through to the row
        // resolution that refuses it by name.
        if (
            !overZones &&
            (!CellName.TryParse(
            candidate: head,
            name: out var headName,
            reason: out _
        ) || !context.Catalog.TryGetFamily(
            family: out family,
            name: headName
        ))
        ) {
            return false;
        }

        RuleException Malformed(string detail) => new(
            detail: $"{where} '{name}' {detail}",
            refusal: RuleRefusal.StateCellUnaddressable,
            ruleName: ruleName
        );

        var inner = name[(bracket + 1)..^1];

        if (inner.Length == 0) {
            throw Malformed(detail: "carries an empty index");
        }
        if (inner.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: RuleFacts.ExpressionKeyPrefix
        )) {
            throw Malformed(detail: $"spells its index with '{RuleFacts.ExpressionKeyPrefix}' — the brackets already hold an expression, so write it bare");
        }
        if (!ExpressionSpelling.TryParseKey(
            error: out var error,
            key: out var key,
            text: inner
        )) {
            throw Malformed(detail: $"index '{inner}' does not parse: {error}");
        }
        if (!TryResolveDynamicKey(
            cell: out var index,
            context: context,
            reference: StateChannelRef.OfNullable(spelling: key),
            keyFieldLabel: "index",
            ruleName: ruleName,
            verb: where
        )) {
            throw Malformed(detail: $"index '{inner}' is not live — a live row is chosen by a cell, a bound token, a binding, or an expression; a fixed row is named by its row");
        }

        if (overZones) {
            var table = (context.Zones ?? throw Malformed(detail: "selects a zone live, but the rule declares no 'zones' table"));

            row = table.Reference(
                index: index,
                spelling: name
            );

            return true;
        }

        row = LiveRow.OverFamily(
            family: family,
            index: index,
            spelling: name
        );

        return true;
    }

    private static ActionStateComparison FlipComparison(ActionStateComparison comparison) => (comparison switch {
        ActionStateComparison.Less => ActionStateComparison.Greater,
        ActionStateComparison.LessOrEqual => ActionStateComparison.GreaterOrEqual,
        ActionStateComparison.Greater => ActionStateComparison.Less,
        ActionStateComparison.GreaterOrEqual => ActionStateComparison.LessOrEqual,
        _ => comparison,
    });
    private static CompiledExpressionToken Literal(long raw) => new(
        Constant: raw,
        Operation: ExpressionOp.Constant
    );
    private static bool TryFractionalLiteral(ExpressionProgram? program, out decimal literal) {
        if (
            (program is { Instructions: [{ Payload: InstructionPayload.Constant constant }] }) &&
            (decimal.Truncate(d: constant.Value) != constant.Value)
        ) {
            literal = constant.Value;

            return true;
        }

        literal = decimal.Zero;

        return false;
    }
    private static void FlattenPredicate(ActionPredicate? predicate, List<GateToken> gate, string ruleName, RuleCompileContext context, int depth = 0) {
        if (depth >= RuleCapacity.MaxPredicateNesting) {
            throw new RuleException(
                detail: $"a gate nests more than {RuleCapacity.MaxPredicateNesting} predicates deep; name the inner condition as a local and test that",
                refusal: RuleRefusal.PredicateKindInadmissible,
                ruleName: ruleName
            );
        }

        switch (predicate) {
            case null:
                break;
            case ActionPredicate.All all:
                if (all.Predicates is null) {
                    throw new RuleException(
                        detail: "an 'all' gate must carry a non-null predicate list",
                        refusal: RuleRefusal.PredicateKindInadmissible,
                        ruleName: ruleName
                    );
                }

                foreach (var inner in all.Predicates) {
                    if (inner is null) {
                        throw new RuleException(
                            detail: "an 'all' gate contains a null predicate row",
                            refusal: RuleRefusal.PredicateKindInadmissible,
                            ruleName: ruleName
                        );
                    }

                    FlattenPredicate(
                        context: context,
                        depth: (depth + 1),
                        gate: gate,
                        predicate: inner,
                        ruleName: ruleName
                    );
                }

                gate.Add(item: Logical(
                    arity: all.Predicates.Count,
                    describe: "all",
                    op: GateOp.All
                ));

                break;
            case ActionPredicate.Any any:
                if (any.Predicates is not { Count: > 0 }) {
                    throw new RuleException(
                        detail: "an 'any' gate must carry at least one predicate",
                        refusal: RuleRefusal.PredicateKindInadmissible,
                        ruleName: ruleName
                    );
                }

                foreach (var inner in any.Predicates) {
                    if (inner is null) {
                        throw new RuleException(
                            detail: "an 'any' gate contains a null predicate row",
                            refusal: RuleRefusal.PredicateKindInadmissible,
                            ruleName: ruleName
                        );
                    }

                    FlattenPredicate(
                        context: context,
                        depth: (depth + 1),
                        gate: gate,
                        predicate: inner,
                        ruleName: ruleName
                    );
                }

                gate.Add(item: Logical(
                    arity: any.Predicates.Count,
                    describe: "any",
                    op: GateOp.Any
                ));

                break;
            case ActionPredicate.Not not:
                if (not.Predicate is null) {
                    throw new RuleException(
                        detail: "a 'not' gate must carry one non-null predicate",
                        refusal: RuleRefusal.PredicateKindInadmissible,
                        ruleName: ruleName
                    );
                }

                FlattenPredicate(
                    context: context,
                    depth: (depth + 1),
                    gate: gate,
                    predicate: not.Predicate,
                    ruleName: ruleName
                );
                gate.Add(item: Logical(
                    arity: 1,
                    describe: "not",
                    op: GateOp.Not
                ));

                break;
            case ActionPredicate.CompareValue expression:
                if (
                    (expression.Kind is not (CellKind.Int or CellKind.Fixed)) ||
                    !Enum.IsDefined(value: expression.Comparison)
                ) {
                    throw new RuleException(
                        detail: "compareValue requires Int or Fixed and a defined comparison",
                        refusal: RuleRefusal.PredicateKindInadmissible,
                        ruleName: ruleName
                    );
                }

                var comparison = expression.Comparison;
                var left = CompileExpression(
                    context: context,
                    expression: expression.Left,
                    kind: expression.Kind,
                    ruleName: ruleName,
                    verb: "compareValue left"
                );
                var right = CompileExpression(
                    context: context,
                    expression: expression.Right,
                    kind: expression.Kind,
                    ruleName: ruleName,
                    verb: "compareValue right"
                );

                // The one conversion table, reached from this spelling too: a fractional literal on either side of an
                // Int comparison lowers to the exact integer comparison rather than to a rounded literal.
                if (expression.Kind == CellKind.Int) {
                    if (TryFractionalLiteral(
                        literal: out var rightLiteral,
                        program: expression.Right
                    )) {
                        var (raw, lowered) = LowerConstantComparison(
                            comparison: comparison,
                            kind: CellKind.Int,
                            literal: rightLiteral,
                            ruleName: ruleName
                        );

                        comparison = lowered;
                        right = [Literal(raw: raw)];
                    } else if (TryFractionalLiteral(
                        literal: out var leftLiteral,
                        program: expression.Left
                    )) {
                        var (raw, lowered) = LowerConstantComparison(
                            comparison: FlipComparison(comparison: comparison),
                            kind: CellKind.Int,
                            literal: leftLiteral,
                            ruleName: ruleName
                        );

                        comparison = FlipComparison(comparison: lowered);
                        left = [Literal(raw: raw)];
                    }
                }

                gate.Add(item: new GateToken(
                    Comparison: comparison,
                    Describe: $"compareValue {expression.Kind} {DescribeComparison(comparison: comparison)}",
                    LeftSource: CompiledValueSource.FromExpression(expression: left),
                    RightSource: CompiledValueSource.FromExpression(expression: right),
                    ValueKind: expression.Kind
                ));

                break;
            case ActionPredicate.CompareState compare:
                gate.Add(item: ResolvePredicate(
                    compare: compare,
                    context: context,
                    ruleName: ruleName
                ));

                break;
            default:
                if (context.Vocabulary.PredicateOf(predicate: predicate) is { } family) {
                    gate.Add(item: family.Compile(
                        context: context,
                        predicate: predicate,
                        ruleName: ruleName
                    ));

                    break;
                }

                throw new RuleException(
                    detail: $"'{predicate.GetType().Name}' has no rule-scope meaning — a gate admits 'compareState', 'compareValue', 'all', 'any', and 'not'",
                    refusal: RuleRefusal.PredicateKindInadmissible,
                    ruleName: ruleName
                );
        }

        static GateToken Logical(GateOp op, int arity, string describe) => new(
            Arity: arity,
            Comparison: ActionStateComparison.Equal,
            Describe: describe,
            LeftSource: default,
            Op: op,
            RightSource: default,
            ValueKind: CellKind.Bool
        );
    }
    private static GateToken ResolvePredicate(ActionPredicate.CompareState compare, string ruleName, RuleCompileContext context) {
        var comparison = compare.Comparison;
        var hasComparand = (compare.ComparandState is not null);
        var hasValue = (compare.Value is not null);
        var name = compare.State.Spelling;

        // 'comparandKey' is an appendage of 'comparandState'; on its own it is a parsed-and-discarded field, refused
        // by name rather than silently ignored under the constant spelling.
        if (
            (compare.ComparandKey is not null) &&
            (compare.ComparandState is null)
        ) {
            throw new RuleException(
                detail: "names 'comparandKey' without 'comparandState' — a comparand key addresses a cell inside a comparand row, which must be named",
                refusal: RuleRefusal.ComparandAmbiguous,
                ruleName: ruleName
            );
        }
        if (hasValue == hasComparand) {
            throw new RuleException(
                detail: (hasValue
                ? "names both 'value' and 'comparandState' — a compareState spells exactly one comparand, never both"
                : "names neither 'value' nor 'comparandState' — a compareState must spell exactly one comparand"),
                refusal: RuleRefusal.ComparandAmbiguous,
                ruleName: ruleName
            );
        }

        var lhs = ResolveOperand(
            context: context,
            cell: compare.Key,
            operand: compare.State,
            site: new OperandSite(
                FieldLabel: "state",
                KeyFieldLabel: "key",
                RuleName: ruleName,
                Verb: "compareState"
            )
        );

        if (hasValue) {
            var (value, lowered) = LowerConstantComparison(
                comparison: comparison,
                kind: lhs.ValueKind,
                literal: compare.Value!.Value,
                ruleName: ruleName
            );

            return new GateToken(
                Comparison: lowered,
                Describe: $"{lhs.Describe} {DescribeComparison(comparison: comparison)} {compare.Value.Value.ToString(provider: CultureInfo.InvariantCulture)}",
                LeftSource: CompiledValueSource.FromOperand(operand: lhs.Operand),
                RightSource: CompiledValueSource.Constant(rawValue: value),
                ValueKind: lhs.ValueKind
            );
        }

        var rhs = ResolveOperand(
            context: context,
            cell: compare.ComparandKey,
            operand: compare.ComparandState!,
            site: new OperandSite(
                FieldLabel: "comparandState",
                KeyFieldLabel: "comparandKey",
                RuleName: ruleName,
                Verb: "compareState"
            )
        );

        // Mixed kinds refuse by name for the comparand-row spelling alone; the constant spelling keeps its more
        // permissive lowering.
        if (lhs.ValueKind != rhs.ValueKind) {
            throw new RuleException(
                detail: $"'{name}' is kind={StateSpelling.Kind(kind: lhs.ValueKind)} but comparand '{compare.ComparandState}' is kind={StateSpelling.Kind(kind: rhs.ValueKind)} — mixed-kind comparisons are refused; author both sides the same kind",
                refusal: RuleRefusal.ComparandKindMismatch,
                ruleName: ruleName
            );
        }

        return new GateToken(
            Comparison: comparison,
            Describe: $"{lhs.Describe} {DescribeComparison(comparison: comparison)} {rhs.Describe}",
            LeftSource: CompiledValueSource.FromOperand(operand: lhs.Operand),
            RightSource: CompiledValueSource.FromOperand(operand: rhs.Operand),
            ValueKind: lhs.ValueKind
        );
    }
}
