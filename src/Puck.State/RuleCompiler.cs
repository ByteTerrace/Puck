using System.Globalization;
using Puck.Maths;

namespace Puck.State;

/// <summary>Compiles authored <see cref="Rule"/> rows against a <see cref="RuleCompileContext"/>. Called twice by
/// design: once (wrapped, per rule) inside a document's validator so a malformed rule refuses by name instead of
/// throwing later, and once more (unwrapped — validation already proved success) inside the evaluator's install path
/// to obtain the live array the tick evaluates. Every piece a document project's own compile surface composes from
/// (<see cref="CompileGate"/>, <see cref="CompileEffects"/>, <see cref="CompileExpression"/>,
/// <see cref="CompileBindings"/>, <see cref="ResolveOperand"/>) is public on the same terms.</summary>
public static partial class RuleCompiler {
    /// <summary>Compiles one rule. Does not check name presence or uniqueness — that is <see cref="CompileAll"/>'s job,
    /// the one caller with a sibling list to check against. Sets and clears the context's per-compile scope.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="context">The compile context.</param>
    /// <exception cref="RuleException">The rule names something the section does not declare, or uses a
    /// predicate/effect kind rule scope has no meaning for.</exception>
    public static CompiledRule Compile(Rule rule, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: context);

        BeginScope(rule: rule, context: context);

        try {
            var bindings = CompileBindings(rule: rule, context: context);
            var gate = CompileGate(predicate: rule.Gate, ruleName: rule.Name, context: context);

            return new CompiledRule(
                Name: rule.Name,
                Mode: rule.Mode,
                Gate: gate,
                Effects: CompileEffects(effects: rule.Effects, ruleName: rule.Name, context: context, subject: "rule"),
                ForEach: rule.ForEach,
                Bindings: bindings
            );
        } finally {
            context.ClearScope();
        }
    }

    /// <summary>Opens a rule's per-compile scope on the context: the <c>$each</c> binding when the rule declares
    /// <see cref="Rule.ForEach"/> (proven to name a keyed numeric row), and the forEach row name. A document
    /// project's compile surface calls this before composing the pieces itself and <see cref="RuleCompileContext.ClearScope"/>
    /// after.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="context">The compile context.</param>
    public static void BeginScope(Rule rule, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: context);

        if (rule.ForEach is { } forEach) {
            _ = ResolveNumericRow(
                channel: "forEach",
                context: context,
                malformed: RuleRefusal.StateRowUnknown,
                name: forEach,
                requireKeyed: true,
                ruleName: rule.Name
            );
        }

        context.ClearScope();
        context.BindingScope = ((rule.ForEach is null) ? [] : [BoundKey.Each]);
        context.ForEachRow = rule.ForEach;
    }

    /// <summary>Compiles every rule in the list, in document order, checking that each carries a unique, unreserved
    /// name.</summary>
    /// <param name="rules">The authored rules.</param>
    /// <param name="context">The compile context.</param>
    /// <exception cref="RuleException">A rule's name is missing, reserved, or duplicated, or it fails to compile.</exception>
    public static CompiledRule[] CompileAll(IReadOnlyList<Rule>? rules, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (rules is not { Count: > 0 }) {
            return [];
        }

        var seen = new HashSet<string>(capacity: rules.Count, comparer: StringComparer.Ordinal);
        var compiled = new CompiledRule[rules.Count];

        for (var index = 0; (index < rules.Count); index++) {
            var rule = rules[index];

            RequireName(name: (rule?.Name.Value ?? string.Empty), seen: seen, subject: "rule");
            compiled[index] = Compile(rule: rule!, context: context);
        }

        return compiled;
    }

    /// <summary>Checks that an authored row's name is present, unreserved, and not yet seen — the same three refusals
    /// every authoring surface that compiles through the rule machinery applies to its own names.</summary>
    /// <param name="name">The authored name.</param>
    /// <param name="seen">The names already declared; the name is added on success.</param>
    /// <param name="subject">The row's noun, for refusal text.</param>
    public static void RequireName(string name, HashSet<string> seen, string subject) {
        ArgumentNullException.ThrowIfNull(argument: seen);

        // CellName already proved the shape (non-empty, dot-free, free of the reserved character set) at the JSON
        // converter; a default-valued struct from a programmatically built document is the one way an empty name
        // still reaches here.
        if (string.IsNullOrWhiteSpace(value: name)) {
            throw new RuleException(refusal: RuleRefusal.NameMissing, ruleName: "<unnamed>", detail: $"{Article(subject)} {subject} declares a name", subject: subject);
        }
        // The same reserved-prefix rule a state row name carries: '$' marks what the engine mints, and nothing mints a
        // rule.
        if (name.StartsWith(value: StateRow.ReservedNamePrefix, comparisonType: StringComparison.Ordinal)) {
            throw new RuleException(refusal: RuleRefusal.NameReserved, ruleName: name, detail: $"carries the reserved character '{StateRow.ReservedNamePrefix}' as its first character — that prefix marks what the ENGINE mints, and nothing mints {Article(subject)} {subject}", subject: subject);
        }
        if (!seen.Add(item: name)) {
            throw new RuleException(refusal: RuleRefusal.NameDuplicated, ruleName: name, detail: $"duplicates an earlier {subject}'s name", subject: subject);
        }
    }

    private static string Article(string subject) => ((subject.Length > 0) && (subject[0] is 'a' or 'e' or 'i' or 'o' or 'u')) ? "an" : "a";

    /// <summary>Compiles a rule's bindings in declared order, each visible to the ones after it; leaves the compiled
    /// list on the context so the gate and effects resolve <c>$bind:</c> reads against it.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="context">The compile context, with its scope open.</param>
    public static CompiledRuleBinding[] CompileBindings(Rule rule, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: context);

        if (rule.Bindings is not { Count: > 0 } authored) {
            return [];
        }
        if (authored.Count > RuleCapacity.MaxBindingsPerRule) {
            throw new RuleException(
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: rule.Name,
                detail: $"declares {authored.Count} bindings, exceeding the {RuleCapacity.MaxBindingsPerRule}-binding ceiling"
            );
        }
        var compiled = new List<CompiledRuleBinding>(capacity: authored.Count);
        context.RuleBindings = compiled;
        foreach (var binding in authored) {
            var name = (binding?.Name.Value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value: name)) {
                throw new RuleException(refusal: RuleRefusal.NameMissing, ruleName: rule.Name, detail: "a binding declares a name");
            }
            if (binding!.Kind is not (CellKind.Int or CellKind.Fixed)) {
                throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: rule.Name, detail: $"binding '{name}' is kind={DescribeCellKind(kind: binding.Kind)} — a bound value is int or fixed");
            }
            foreach (var earlier in compiled) {
                if (string.Equals(a: earlier.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                    throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: rule.Name, detail: $"binding '{name}' is declared twice");
                }
            }
            compiled.Add(item: new CompiledRuleBinding(
                Name: name,
                Kind: binding.Kind,
                Expression: CompileExpression(expression: binding.Expression, kind: binding.Kind, ruleName: rule.Name, verb: $"binding '{name}'", context: context)
            ));
        }
        return [.. compiled];
    }

    /// <summary>Emits a bounded postfix Boolean program: leaf comparisons push, All/Any consume their child count,
    /// Not flips one result. The evaluator therefore preserves arbitrary nesting without recursive evaluation or
    /// per-tick allocation. A <see langword="null"/> predicate compiles to the empty program ("always").</summary>
    /// <param name="predicate">The authored gate.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    public static GateToken[] CompileGate(ActionPredicate? predicate, string ruleName, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var gate = new List<GateToken>();

        FlattenPredicate(predicate: predicate, gate: gate, ruleName: ruleName, context: context);

        if (gate.Count > RuleCapacity.MaxPredicateTokens) {
            throw new RuleException(
                refusal: RuleRefusal.PredicateKindInadmissible,
                ruleName: ruleName,
                detail: $"gate compiles to {gate.Count} tokens, exceeding the {RuleCapacity.MaxPredicateTokens}-token ceiling"
            );
        }

        return [.. gate];
    }

    private static void FlattenPredicate(ActionPredicate? predicate, List<GateToken> gate, string ruleName, RuleCompileContext context, int depth = 0) {
        if (depth >= RuleCapacity.MaxPredicateTokens) {
            throw new RuleException(
                refusal: RuleRefusal.PredicateKindInadmissible,
                ruleName: ruleName,
                detail: $"a gate is nested past the {RuleCapacity.MaxPredicateTokens}-token ceiling"
            );
        }

        switch (predicate) {
            case null:
                break;
            case ActionPredicate.All all:
                if (all.Predicates is null) {
                    throw new RuleException(refusal: RuleRefusal.PredicateKindInadmissible, ruleName: ruleName, detail: "an 'all' gate must carry a non-null predicate list");
                }

                foreach (var inner in all.Predicates) {
                    if (inner is null) {
                        throw new RuleException(refusal: RuleRefusal.PredicateKindInadmissible, ruleName: ruleName, detail: "an 'all' gate contains a null predicate row");
                    }

                    FlattenPredicate(predicate: inner, gate: gate, ruleName: ruleName, context: context, depth: (depth + 1));
                }

                gate.Add(item: Logical(GateOp.All, all.Predicates.Count, "all"));

                break;
            case ActionPredicate.Any any:
                if (any.Predicates is not { Count: > 0 }) {
                    throw new RuleException(refusal: RuleRefusal.PredicateKindInadmissible, ruleName: ruleName, detail: "an 'any' gate must carry at least one predicate");
                }

                foreach (var inner in any.Predicates) {
                    if (inner is null) {
                        throw new RuleException(refusal: RuleRefusal.PredicateKindInadmissible, ruleName: ruleName, detail: "an 'any' gate contains a null predicate row");
                    }

                    FlattenPredicate(predicate: inner, gate: gate, ruleName: ruleName, context: context, depth: (depth + 1));
                }

                gate.Add(item: Logical(GateOp.Any, any.Predicates.Count, "any"));

                break;
            case ActionPredicate.Not not:
                if (not.Predicate is null) {
                    throw new RuleException(refusal: RuleRefusal.PredicateKindInadmissible, ruleName: ruleName, detail: "a 'not' gate must carry one non-null predicate");
                }

                FlattenPredicate(predicate: not.Predicate, gate: gate, ruleName: ruleName, context: context, depth: (depth + 1));
                gate.Add(item: Logical(GateOp.Not, 1, "not"));

                break;
            case ActionPredicate.CompareValue expression:
                if (expression.Kind is not (CellKind.Int or CellKind.Fixed) || !Enum.IsDefined(expression.Comparison)) {
                    throw new RuleException(RuleRefusal.PredicateKindInadmissible, ruleName, "compareValue requires Int or Fixed and a defined comparison");
                }
                gate.Add(new(null, expression.Comparison, 0, expression.Kind, null, $"compareValue {expression.Kind} {expression.Comparison}",
                    LeftExpression: CompileExpression(expression.Left, expression.Kind, ruleName, "compareValue left", context),
                    RightExpression: CompileExpression(expression.Right, expression.Kind, ruleName, "compareValue right", context)));
                break;
            case ActionPredicate.CompareState compare:
                gate.Add(item: ResolvePredicate(compare: compare, ruleName: ruleName, context: context));

                break;
            default:
                if (context.Vocabulary.PredicateOf(predicate: predicate) is { } family) {
                    gate.Add(item: family.Compile(predicate: predicate, ruleName: ruleName, context: context));
                    break;
                }

                throw new RuleException(
                    refusal: RuleRefusal.PredicateKindInadmissible,
                    ruleName: ruleName,
                    detail: $"'{predicate.GetType().Name}' has no world-scope meaning — world gates admit 'compareState', 'compareValue', 'all', 'any', and 'not'"
                );
        }

        static GateToken Logical(GateOp op, int arity, string describe) => new(
            Left: null,
            Comparison: default,
            Value: 0L,
            ValueKind: default,
            Comparand: null,
            Describe: describe,
            Op: op,
            Arity: arity
        );
    }

    private static GateToken ResolvePredicate(ActionPredicate.CompareState compare, string ruleName, RuleCompileContext context) {
        var name = (compare.State ?? string.Empty);
        var comparison = compare.Comparison;
        var hasValue = (compare.Value is not null);
        var hasComparand = (compare.ComparandState is not null);

        // 'comparandKey' is an appendage of 'comparandState'; on its own it is a parsed-and-discarded field, refused
        // by name rather than silently ignored under the constant spelling.
        if ((compare.ComparandKey is not null) && (compare.ComparandState is null)) {
            throw new RuleException(
                refusal: RuleRefusal.ComparandAmbiguous,
                ruleName: ruleName,
                detail: "names 'comparandKey' without 'comparandState' — a comparand key addresses a cell inside a comparand row, which must be named"
            );
        }

        if (hasValue == hasComparand) {
            throw new RuleException(
                refusal: RuleRefusal.ComparandAmbiguous,
                ruleName: ruleName,
                detail: (hasValue
                ? "names both 'value' and 'comparandState' — a compareState spells exactly one comparand, never both"
                : "names neither 'value' nor 'comparandState' — a compareState must spell exactly one comparand")
            );
        }

        var lhs = ResolveOperand(name: name, key: compare.Key, site: new OperandSite(RuleName: ruleName, Verb: "compareState", FieldLabel: "state", KeyFieldLabel: "key"), context: context);

        if (hasValue) {
            var describe = $"{lhs.Describe} {RuleEvaluation.DescribeComparison(comparison: comparison)} {compare.Value!.Value.ToString(provider: CultureInfo.InvariantCulture)}";
            var (value, lowered) = LowerConstantComparison(comparison: comparison, kind: lhs.ValueKind, literal: compare.Value.Value, ruleName: ruleName);

            return new GateToken(
                Left: lhs.Operand,
                Comparison: lowered,
                Value: value,
                ValueKind: lhs.ValueKind,
                Comparand: null,
                Describe: describe
            );
        }

        var rhs = ResolveOperand(name: compare.ComparandState!, key: compare.ComparandKey, site: new OperandSite(RuleName: ruleName, Verb: "compareState", FieldLabel: "comparandState", KeyFieldLabel: "comparandKey"), context: context);

        // Mixed kinds refuse by name for the comparand-row spelling alone; the constant spelling keeps its more
        // permissive lowering.
        if (lhs.ValueKind != rhs.ValueKind) {
            throw new RuleException(
                refusal: RuleRefusal.ComparandKindMismatch,
                ruleName: ruleName,
                detail: $"'{name}' is kind={DescribeCellKind(kind: lhs.ValueKind)} but comparand '{compare.ComparandState}' is kind={DescribeCellKind(kind: rhs.ValueKind)} — mixed-kind comparisons are refused; author both sides the same kind"
            );
        }

        return new GateToken(
            Left: lhs.Operand,
            Comparison: comparison,
            Value: default,
            ValueKind: lhs.ValueKind,
            Comparand: rhs.Operand,
            Describe: $"{lhs.Describe} {RuleEvaluation.DescribeComparison(comparison: comparison)} {rhs.Describe}"
        );
    }

    // A constant comparand against an int or bool operand is lowered exactly: an integral literal is the raw it
    // names, and a fractional one becomes the equivalent integer comparison (x > 1.5 is x >= 2, x <= 1.5 is x <= 1,
    // x == 1.5 never holds, x != 1.5 always holds) rather than a rounded literal that would move the gate. A fixed
    // operand keeps its exact fixed-point literal.
    private static (long Value, ActionStateComparison Comparison) LowerConstantComparison(CellKind kind, decimal literal, ActionStateComparison comparison, string ruleName) {
        if ((kind == CellKind.Fixed) || (decimal.Truncate(d: literal) == literal)) {
            return (LiteralToRaw(kind: kind, literal: literal, ruleName: ruleName, verb: "compareState"), comparison);
        }

        var floor = LiteralToRaw(kind: kind, literal: decimal.Floor(d: literal), ruleName: ruleName, verb: "compareState");
        var ceiling = LiteralToRaw(kind: kind, literal: decimal.Ceiling(d: literal), ruleName: ruleName, verb: "compareState");

        return comparison switch {
            ActionStateComparison.Greater or ActionStateComparison.GreaterOrEqual => (ceiling, ActionStateComparison.GreaterOrEqual),
            ActionStateComparison.Less or ActionStateComparison.LessOrEqual => (floor, ActionStateComparison.LessOrEqual),
            ActionStateComparison.Equal => (long.MaxValue, ActionStateComparison.Greater),
            _ => (long.MinValue, ActionStateComparison.GreaterOrEqual),
        };
    }

    /// <summary>Converts an authored decimal literal to a cell kind's raw encoding, refusing one outside the kind's range.</summary>
    /// <param name="kind">The destination kind.</param>
    /// <param name="literal">The authored literal.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    public static long LiteralToRaw(CellKind kind, decimal literal, string ruleName, string verb) {
        try {
            var raw = kind switch {
                CellKind.Int => checked((long)decimal.Round(d: literal, decimals: 0, mode: MidpointRounding.ToEven)),
                CellKind.Fixed => NumericLiteral.ToFixed(value: literal).Value,
                _ => ((literal != decimal.Zero) ? 1L : 0L), // Bool — Text is refused before numeric lowering.
            };

            return raw;
        } catch (OverflowException) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{verb}' literal {literal.ToString(provider: CultureInfo.InvariantCulture)} is outside the representable {DescribeCellKind(kind: kind)} state range"
            );
        }
    }

    /// <summary>Converts an authored decimal to fixed point, refusing one outside the Q48.16 range.</summary>
    /// <param name="value">The authored value.</param>
    /// <param name="field">The field it was spelled in, for refusal text.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    public static FixedQ4816 ResolveFixedLiteral(decimal value, string field, string verb, string ruleName) {
        if (NumericLiteral.TryToFixed(value: value, result: out var result)) {
            return result;
        }

        throw new RuleException(
            refusal: RuleRefusal.EffectKindInadmissible,
            ruleName: ruleName,
            detail: $"'{verb}' {field} '{value.ToString(provider: CultureInfo.InvariantCulture)}' is outside the Q48.16 range"
        );
    }

    /// <summary>Formats a cell kind as its lower-case spelling.</summary>
    /// <param name="kind">The kind.</param>
    public static string DescribeCellKind(CellKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>Resolves the compiled handle of a declared row — a row the compiler already proved present.</summary>
    /// <param name="context">The compile context.</param>
    /// <param name="name">The row name.</param>
    public static StateHandle ResolveHandle(RuleCompileContext context, string name) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (context.Catalog.TryResolve(lane: StateLane.Document, name: name, handle: out var handle)) {
            return handle;
        }

        throw new InvalidOperationException(message: $"Validated state row '{name}' is absent from its compiled catalog.");
    }

    /// <summary>Refuses a binding token that is not live in the current compile scope.</summary>
    /// <param name="context">The compile context.</param>
    /// <param name="binding">The binding spelled.</param>
    /// <param name="spelled">The authored token, for refusal text.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="where">Where the token was spelled, for refusal text.</param>
    public static void RequireBindingInScope(RuleCompileContext context, BoundKey binding, string spelled, string ruleName, string where) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (Array.IndexOf(array: (context.BindingScope ?? []), value: binding) < 0) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"{where} names '{spelled}', which is not bound here — {s_bindingScopes}"
            );
        }
    }

    private static readonly string s_bindingScopes = string.Join(
        separator: ", ",
        values: RuleBindingTokens.Bindings.Select(selector: static (entry, index) => $"'{entry.KeyToken}' {((index == 0) ? "binds inside " : "inside ")}{entry.Scope}")
    );

    /// <summary>Refuses a key on a reserved channel, which is a single quantity and carries no cells.</summary>
    /// <param name="key">The authored key.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="name">The channel name.</param>
    /// <param name="keyFieldLabel">The field the key was spelled in.</param>
    public static void RefuseKeyOnReservedChannel(string? key, string ruleName, string name, string keyFieldLabel) {
        if (key is not null) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"reserved channel '{name}' is a single quantity and carries no cells — drop the '{keyFieldLabel}'"
            );
        }
    }

    /// <summary>Resolves a <c>$cell:&lt;row&gt;:&lt;key&gt;</c> indirection: the named cell must exist on a declared int
    /// row, since its value is read as a key every evaluation. An inner key spelling a binding token is admitted
    /// too — the row is fixed, its key is bound.</summary>
    /// <param name="row">The row name.</param>
    /// <param name="key">The inner key.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="channel">The authored spelling, for refusal text.</param>
    public static CompiledCellRef ResolveCellRef(string row, string key, string ruleName, RuleCompileContext context, string channel) {
        var declared = ResolveNumericRow(channel: channel, context: context, malformed: RuleRefusal.StateCellUnaddressable, name: row, requireKeyed: false, ruleName: ruleName);

        if (declared.Kind != CellKind.Int) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{channel}' reads row '{row}' as a body index, but it is kind={DescribeCellKind(kind: declared.Kind)} — an index cell is kind=int"
            );
        }

        if ((RuleBindingTokens.OfKeyToken(key: key) is var innerBinding) && (innerBinding != BoundKey.None)) {
            RequireBindingInScope(context: context, binding: innerBinding, spelled: key, ruleName: ruleName, where: $"'{channel}' inner key");

            return new CompiledCellRef(
                Handle: ResolveHandle(context: context, name: row),
                InnerKeyBinding: innerBinding,
                Key: string.Empty,
                Row: row
            );
        }

        var resolvedKey = ResolveKey(key: key, keyFieldLabel: "key", row: declared, ruleName: ruleName, verb: channel);

        if (!declared.HasCell(key: resolvedKey)) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUndeclared,
                ruleName: ruleName,
                detail: $"'{channel}' reads cell '{row}'.'{resolvedKey}' as a key, which the row does not declare"
            );
        }

        return new CompiledCellRef(Key: resolvedKey, Row: row, Handle: ResolveHandle(context: context, name: row));
    }

    /// <summary>Resolves a dynamic key spelling — a binding token, a registered key family's spelling, or a
    /// <c>$cell:</c> indirection. A literal key returns <see langword="false"/>.</summary>
    /// <param name="key">The authored key.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="keyFieldLabel">The field the key was spelled in, for refusal text.</param>
    /// <param name="cell">The compiled indirection, when dynamic.</param>
    public static bool TryResolveDynamicKey(string? key, string ruleName, RuleCompileContext context, string verb, string keyFieldLabel, out CompiledCellRef cell) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if ((RuleBindingTokens.OfKeyToken(key: key) is var bound) && (bound != BoundKey.None)) {
            RequireBindingInScope(context: context, binding: bound, spelled: key!, ruleName: ruleName, where: $"'{verb}' {keyFieldLabel}");
            cell = new CompiledCellRef(Binding: bound, Key: string.Empty, Row: string.Empty);

            return true;
        }

        if (key is not null) {
            foreach (var family in context.Vocabulary.Keys) {
                if (family.TryCompile(key: key, ruleName: ruleName, verb: verb, keyFieldLabel: keyFieldLabel, context: context, cell: out cell)) {
                    return true;
                }
            }
        }

        if ((key is null) || !key.StartsWith(value: RuleFacts.CellKeyPrefix, comparisonType: StringComparison.Ordinal)) {
            cell = default;

            return false;
        }

        var tokens = key[RuleFacts.CellKeyPrefix.Length..].Split(separator: ':');

        if (tokens.Length != 2) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{verb}' {keyFieldLabel} '{key}' does not spell '{RuleFacts.CellKeyPrefix}<row>:<key>'"
            );
        }

        cell = ResolveCellRef(channel: $"{verb} {keyFieldLabel} '{key}'", context: context, key: tokens[1], row: tokens[0], ruleName: ruleName);

        return true;
    }

    /// <summary>Resolves a literal key against a row under the (row, key) pair rule: a null key means the row's slot
    /// cell, which a keyed row does not have.</summary>
    /// <param name="row">The declared row.</param>
    /// <param name="key">The authored key, or <see langword="null"/>.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="keyFieldLabel">The field the key was spelled in, for refusal text.</param>
    public static string ResolveKey(StateRow row, string? key, string ruleName, string verb, string keyFieldLabel) {
        ArgumentNullException.ThrowIfNull(argument: row);

        if (key is { } authored) {
            return (CellName.TryParse(candidate: authored, name: out var parsed, reason: out var reason)
                ? parsed.Value
                : throw new RuleException(
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"'{verb}' {keyFieldLabel} '{authored}' {reason}"
                )
            );
        }

        return (row.IsKeyed
            ? throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{verb}' names keyed row '{row.Name}' without a '{keyFieldLabel}' — a keyed row has no single cell, so name the one you mean"
            )
            : StateRow.SlotKey.Value
        );
    }

    /// <summary>Resolves a declared numeric row: it must exist and must not be kind=text. <paramref name="requireKeyed"/>
    /// additionally demands the row be keyed — a read that yields a cell key (an extremum, a filter, a forEach) has no
    /// key to yield from a slot row.</summary>
    /// <param name="name">The row name.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="requireKeyed">Whether the row must be keyed.</param>
    /// <param name="malformed">The refusal category for an undeclared or text row.</param>
    /// <param name="channel">The authored spelling, for refusal text.</param>
    public static StateRow ResolveNumericRow(string name, string ruleName, RuleCompileContext context, bool requireKeyed, Enum malformed, string channel) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var row = (context.FindRow(name: name)
            ?? throw new RuleException(refusal: malformed, ruleName: ruleName, detail: $"'{channel}' names row '{name}', which the document does not declare"));

        if (row.Kind == CellKind.Text) {
            throw new RuleException(refusal: malformed, ruleName: ruleName, detail: $"'{channel}' names row '{name}', which is kind=text — a reduction/extremum is numeric, never text");
        }

        if (requireKeyed && !row.IsKeyed) {
            throw new RuleException(
                refusal: RuleRefusal.ArgRowNotKeyed,
                ruleName: ruleName,
                detail: $"'{channel}' names row '{name}', which is not keyed — an argmax/argmin yields a body, and a slot row's cell carries no body-index key; author a keyed row whose cell keys ARE body indices"
            );
        }

        return row;
    }

    /// <summary>Converts a non-negative duration in seconds to whole simulation ticks, rounding up.</summary>
    /// <param name="seconds">The authored duration.</param>
    /// <param name="ratePerSecond">The simulation rate.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    public static long DurationSimulationTicks(decimal seconds, int ratePerSecond, string ruleName, string verb) {
        if ((seconds < decimal.Zero) || (ratePerSecond <= 0)) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'{verb}' requires a non-negative delay and a positive simulation rate");
        }

        var maximumSeconds = (((decimal)long.MaxValue) / ratePerSecond);
        if (seconds > maximumSeconds) {
            throw new RuleException(refusal: RuleRefusal.DurationEngineTicksOutOfRange, ruleName: ruleName, detail: $"'{verb}' delay exceeds the signed 64-bit simulation-tick carrier");
        }

        var ticks = decimal.Ceiling(d: (seconds * ratePerSecond));
        return decimal.ToInt64(d: ticks);
    }

    /// <summary>Converts a non-negative duration in seconds to an exact whole engine-tick count, refusing one that
    /// has none.</summary>
    /// <param name="seconds">The authored duration.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    public static ulong DurationTicksExact(decimal seconds, string ruleName, string verb) {
        if ((seconds < decimal.Zero) || !FixedTickConversion.TryDurationEngineTicksExact(seconds: seconds, ticks: out var ticks)) {
            throw new RuleException(
                refusal: RuleRefusal.DurationNotExactEngineTicks,
                ruleName: ruleName,
                detail: $"'{verb}' delay {seconds.ToString(provider: CultureInfo.InvariantCulture)} seconds is not a non-negative exact whole-engine-tick duration"
            );
        }

        return ticks;
    }

    /// <summary>One resolved operand: its compiled fact, the cell kind it reads in, and its read-back spelling.</summary>
    /// <param name="Operand">The compiled operand.</param>
    /// <param name="ValueKind">The kind the operand reads in — always the operand's own <see cref="OperandFact.ValueKind"/>.</param>
    /// <param name="Describe">The authored spelling, for the rules read-back.</param>
    public readonly record struct ResolvedOperand(OperandFact Operand, CellKind ValueKind, string Describe) {
        /// <summary>Creates a resolved operand over a fact, in the fact's own kind.</summary>
        /// <param name="operand">The compiled operand.</param>
        /// <param name="describe">The authored spelling.</param>
        public ResolvedOperand(OperandFact operand, string describe) : this(Operand: operand, ValueKind: operand.ValueKind, Describe: describe) { }
    }
}
