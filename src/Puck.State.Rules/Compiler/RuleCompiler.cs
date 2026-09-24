using System.Globalization;
using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>Compiles authored <see cref="Rule"/> rows against a <see cref="RuleCompileContext"/> into programs
/// addressed by catalog ordinal and interned cell key. Called twice by design: once inside a document's validator so
/// a malformed rule refuses by name instead of throwing later, and once more inside the evaluator's install path to
/// obtain the live array the tick evaluates.</summary>
public static partial class RuleCompiler {
    private static readonly string BindingScopes = string.Join(
        separator: ", ",
        values: RuleBindingTokens.Bindings.Select(selector: static (entry, index) => $"'{entry.KeyToken}' {((index == 0)
        ? "binds inside "
        : "inside ")}{entry.Scope}")
    );
    private static readonly string[] CoreSpellings = [RuleFacts.Tick, $"{RuleFacts.ReducePrefix}<op>:<row>"];

    /// <summary>Compiles one rule. Does not check name presence or uniqueness — that is <see cref="CompileAll"/>'s
    /// job, the one caller with a sibling list to check against. Sets and clears the context's per-compile scope.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="extend">A document project's own compile, run inside the rule's scope on the library's compiled
    /// rule; it returns the record the document project carries in its place. <see langword="null"/> leaves the
    /// library's rule as the result.</param>
    /// <param name="requireEffects">Whether an empty effect list refuses. A document project whose rule carries its
    /// effects in a branch of its own passes <see langword="false"/> and refuses an empty rule itself.</param>
    /// <returns>The compiled rule.</returns>
    /// <exception cref="RuleException">The rule names something the section does not declare, or uses a
    /// predicate/effect kind rule scope has no meaning for.</exception>
    public static CompiledRule Compile(Rule rule, RuleCompileContext context, Func<CompiledRule, CompiledRule>? extend = null, bool requireEffects = true) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: rule);

        BeginScope(
            context: context,
            rule: rule
        );

        try {
            var locals = CompileLocals(
                context: context,
                rule: rule
            );
            GateToken[] gate;

            // Each phase locates its own refusals, so a caller that knows the rule's index can name the authored
            // line rather than the whole rule.
            try {
                gate = CompileGate(
                    context: context,
                    predicate: rule.Gate,
                    ruleName: rule.Name
                );
            } catch (RuleException error) { throw error.Within(segment: "gate"); }

            var effects = CompileEffects(
                allowEmpty: !requireEffects,
                context: context,
                effects: rule.Effects,
                ruleName: rule.Name,
                subject: "rule"
            );
            var forEachZones = IteratesZones(forEach: rule.ForEach);
            var forEachOrdinal = -1;

            if (
                (rule.ForEach is { } forEachName) &&
                !forEachZones
            ) {
                forEachOrdinal = ResolveRowOrdinal(
                    context: context,
                    name: forEachName.Spelling
                );
            }
            var poolForEach = (((rule.PoolForEach is { } poolIteration) && context.Catalog.TryGetPool(name: CellName.Parse(candidate: poolIteration.Pool.Spelling), pool: out var resolvedPool))
                ? resolvedPool
                : null);
            var poolBindingSlot = -1;

            if ((poolForEach is not null) && context.TryInstanceBinding(name: rule.PoolForEach!.Binding.Value, binding: out var poolBinding)) {
                poolBindingSlot = poolBinding.Slot;
            }

            RuleDataflow.CollectGateFacts(
                gate: gate,
                into: context.Needs
            );
            RuleDataflow.CollectEffectFacts(
                effects: effects,
                into: context.Needs
            );

            var compiledLocals = AllLocals(
                context: context,
                declared: locals
            );

            foreach (var local in compiledLocals) {
                RuleDataflow.CollectExpressionFacts(
                    into: context.Needs,
                    tokens: local.Expression
                );
            }

            RefuseIrreversibleResultRead(
                effects: effects,
                ruleName: rule.Name
            );

            var compiled = new CompiledRule(
                Locals: compiledLocals,
                Describe: rule.Name.Value,
                Effects: effects,
                ForEachOrdinal: forEachOrdinal,
                ForEachZones: forEachZones,
                Gate: gate,
                Mode: rule.Mode,
                Name: rule.Name.Value,
                Needs: context.Needs.Build(),
                PoolBindingSlot: poolBindingSlot,
                PoolForEach: poolForEach,
                Zones: context.Zones
            );

            return ((extend is null)
                ? compiled
                : extend(arg: compiled)
            );
        } finally {
            context.ClearScope();
        }
    }
    /// <summary>Returns a rule's locals after its gate and effects compiled: the declared ones plus every implicit
    /// key local an expression key added, in evaluation order.</summary>
    /// <param name="declared">The declared locals <see cref="CompileLocals"/> returned.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The locals in evaluation order.</returns>
    public static CompiledRuleLocal[] AllLocals(CompiledRuleLocal[] declared, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: declared);

        return (((context.RuleLocals is { } all) && (all.Count != declared.Length))
            ? [.. all]
            : declared
        );
    }
    /// <summary>Opens a rule's per-compile scope on the context: the compiled <see cref="Rule.Zones"/> table, the
    /// <c>$each</c> binding when the rule declares <see cref="Rule.ForEach"/>, and the forEach row name.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="context">The compile context.</param>
    public static void BeginScope(Rule rule, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: rule);

        context.ClearScope();

        var zones = CompileZones(
            context: context,
            rule: rule
        );

        if (rule.ForEach is { } forEach) {
            if (IteratesZones(forEach: forEach)) {
                if (zones is null) {
                    throw new RuleException(
                        detail: $"iterates 'forEach' over '{RuleFacts.ForEachZones}' but declares no 'zones' table",
                        refusal: RuleRefusal.ZoneTableMalformed,
                        ruleName: rule.Name
                    );
                }
            } else {
                _ = ResolveRequiredRow(
                    channel: "forEach",
                    context: context,
                    malformed: RuleRefusal.StateRowUnknown,
                    name: forEach.Spelling,
                    requireKeyed: true,
                    ruleName: rule.Name
                );
            }
        }
        if (rule.PoolForEach is { } poolIteration) {
            if (rule.ForEach is not null) {
                throw new RuleException(detail: "a rule may declare either 'forEach' or 'poolForEach', not both", refusal: RuleRefusal.EffectKindInadmissible, ruleName: rule.Name);
            }
            if (!CellName.TryParse(candidate: poolIteration.Pool.Spelling, name: out var poolName, reason: out _) || !context.Catalog.TryGetPool(name: poolName, pool: out var pool) || (pool is null)) {
                throw new RuleException(detail: $"'poolForEach' names no declared pool '{poolIteration.Pool}'", refusal: RuleRefusal.StateRowUnknown, ruleName: rule.Name);
            }
            try {
                _ = context.PushInstanceBinding(name: poolIteration.Binding, pool: pool);
            } catch (InvalidOperationException error) {
                throw new RuleException(detail: error.Message, refusal: RuleRefusal.EffectKindInadmissible, ruleName: rule.Name);
            }
        }

        context.BindingScope = ((rule.ForEach is null)
            ? []
            : [BoundKey.Each]
        );
        context.ForEachRow = rule.ForEach?.Spelling;
        context.Zones = zones;
    }
    /// <summary>Compiles every rule in the list, in document order, checking that each carries a unique, unreserved
    /// name.</summary>
    /// <param name="rules">The authored rules.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The compiled rules, in document order.</returns>
    /// <exception cref="RuleException">A rule's name is missing, reserved, or duplicated, or it fails to compile.</exception>
    public static CompiledRule[] CompileAll(IReadOnlyList<Rule>? rules, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (rules is not { Count: > 0 }) {
            return [];
        }

        var compiled = new CompiledRule[rules.Count];
        var seen = new HashSet<string>(
            capacity: rules.Count,
            comparer: StringComparer.Ordinal
        );

        for (var index = 0; (index < rules.Count); index++) {
            var rule = rules[index];

            RequireName(
                name: (rule?.Name.Value ?? string.Empty),
                seen: seen,
                subject: "rule"
            );
            compiled[index] = Compile(
                context: context,
                rule: rule!
            );
        }

        return compiled;
    }
    /// <summary>Compiles a rule's locals in declared order, each visible to the ones after it; leaves the compiled
    /// list on the context so the gate and effects resolve <c>$local:</c> reads against it.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="context">The compile context, with its scope open.</param>
    /// <returns>The declared locals.</returns>
    public static CompiledRuleLocal[] CompileLocals(Rule rule, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: rule);

        if (rule.Locals is not { Count: > 0 } authored) {
            return [];
        }
        if (authored.Count > RuleCapacity.MaxLocalsPerRule) {
            throw new RuleException(
                detail: $"declares {authored.Count} locals, exceeding the {RuleCapacity.MaxLocalsPerRule}-local ceiling",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: rule.Name
            );
        }

        var compiled = new List<CompiledRuleLocal>(capacity: authored.Count);

        context.RuleLocals = compiled;
        for (var index = 0; (index < authored.Count); index++) {
            try {
                CompileLocal(
                    compiled: compiled,
                    context: context,
                    local: authored[index],
                    rule: rule
                );
            } catch (RuleException error) { throw error.Within(segment: $"locals[{index}]"); }
        }

        return [.. compiled];
    }

    // A rule iterates its own zone table when its `forEach` is the reserved word alone (RuleFacts.ForEachZones): the
    // zones channel called with no argument.
    private static bool IteratesZones(StateChannelRef? forEach) => (forEach?.Call is { Channel: "zones", Count: 0 });
    private static bool IsFractionalConstantExpression(ExpressionProgram? expression) {
        if (expression is null) {
            return false;
        }

        var instructions = expression.Instructions.Concat(second: expression.Subprograms.SelectMany(selector: static subprogram => subprogram.Instructions));

        return (
            !instructions.Any(predicate: static instruction => (instruction.Payload is InstructionPayload.State)) &&
            instructions.Any(predicate: static instruction => (
                (instruction.Payload is InstructionPayload.Constant constant) &&
                (decimal.Truncate(d: constant.Value) != constant.Value)
            ))
        );
    }
    private static void CompileLocal(List<CompiledRuleLocal> compiled, RuleCompileContext context, RuleLocal? local, Rule rule) {
        var name = (local?.Name.Value ?? string.Empty);

        if (string.IsNullOrWhiteSpace(value: name)) {
            throw new RuleException(
                detail: "a local declares a name",
                refusal: RuleRefusal.NameMissing,
                ruleName: rule.Name
            );
        }
        // The engine mints the implicit binding a computed key needs onto this same list as `$key<n>`, so an
        // authored local under the reserved prefix could shadow one.
        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: StateRow.ReservedNamePrefix
        )) {
            throw new RuleException(
                detail: $"local '{name}' carries the reserved '{StateRow.ReservedNamePrefix}' prefix — the engine names the key bindings it mints '{StateRow.ReservedNamePrefix}key<n>'",
                refusal: RuleRefusal.NameReserved,
                ruleName: rule.Name
            );
        }
        if (local!.Kind is { } declared and not (CellKind.Int or CellKind.Fixed)) {
            throw new RuleException(
                detail: $"local '{name}' is kind={StateSpelling.Kind(kind: declared)} — a local value is int or fixed",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: rule.Name
            );
        }
        foreach (var earlier in compiled) {
            if (string.Equals(
                a: earlier.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                throw new RuleException(
                    detail: $"local '{name}' is declared twice",
                    refusal: RuleRefusal.EffectKindInadmissible,
                    ruleName: rule.Name
                );
            }
        }

        // A computed key inside the expression mints its own local on this same list before the declared one
        // lands, so the ceiling is priced here, after them, over declared and implicit locals together.
        // Every numeric operand shares one carrier kind, so a row or local selects it. Constants alone compile under
        // either, where an Int reading rounds a fraction away, so a fraction there starts the binding under Fixed.
        // The expression can still *leave* Int: comparisons and Sign consume exact Fixed operands but return a
        // Boolean-like or sign value. The binding therefore records the compiler's result kind, not its carrier.
        // An expression that compiles under neither is refused for the reason its first reading gave. A key binding
        // the first attempt minted stays: a key expression is Int under either kind, and the second attempt reads it
        // back through the key-expression cache by the ordinal it holds.
        var kind = (local.Kind ?? (IsFractionalConstantExpression(expression: local.Expression)
            ? CellKind.Fixed
            : CellKind.Int
        ));
        CompiledExpressionToken[] expression;
        CellKind result;

        try {
            expression = CompileExpression(
                context: context,
                expression: local.Expression,
                kind: kind,
                result: out result,
                resultKind: local.Kind,
                ruleName: rule.Name,
                verb: $"local '{name}'"
            );
        } catch (RuleException first) when ((local.Kind is null)) {
            kind = ((kind == CellKind.Int)
                ? CellKind.Fixed
                : CellKind.Int
            );

            try {
                expression = CompileExpression(
                    context: context,
                    expression: local.Expression,
                    kind: kind,
                    result: out result,
                    resultKind: local.Kind,
                    ruleName: rule.Name,
                    verb: $"local '{name}'"
                );
            } catch (RuleException) {
                throw first;
            }
        }

        if (compiled.Count >= RuleCapacity.MaxLocalsPerRule) {
            throw new RuleException(
                detail: $"local '{name}' would be local {(compiled.Count + 1)}, exceeding the {RuleCapacity.MaxLocalsPerRule}-local ceiling (declared and implicit key locals together)",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: rule.Name
            );
        }

        compiled.Add(item: new CompiledRuleLocal(
            CarrierKind: kind,
            Expression: expression,
            Kind: result,
            Name: name
        ));
    }

    /// <summary>Compiles a rule's <see cref="Rule.Zones"/> table: every non-empty entry a declared ordered zone, all
    /// over one token domain and of one kind, none twice; an empty entry a gap.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The compiled table, or <see langword="null"/> when the rule declares none.</returns>
    public static ZoneTable? CompileZones(Rule rule, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: rule);

        if (rule.Zones is not { } authored) {
            return null;
        }

        RuleException Malformed(string detail) => new(
            detail: detail,
            refusal: RuleRefusal.ZoneTableMalformed,
            ruleName: rule.Name
        );

        if (authored.Count == 0) {
            throw Malformed(detail: "declares an empty 'zones' table — list at least one ordered zone, or drop the table");
        }

        var capacity = 0L;
        var indices = new List<CellKey>(capacity: authored.Count);
        var kind = CellKind.Int;
        var names = new string[authored.Count];
        var ordinals = new int[authored.Count];
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        string? tokenDomain = null;

        for (var index = 0; (index < authored.Count); index++) {
            var name = (authored[index] ?? string.Empty);

            names[index] = name;
            ordinals[index] = -1;
            if (name.Length == 0) {
                continue;
            }

            var row = (context.FindRow(name: name) ?? throw Malformed(detail: $"'zones' entry {index} names row '{name}', which the document does not declare"));

            if (row.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zone) {
                throw Malformed(detail: $"'zones' entry {index} '{name}' is not an ordered token zone");
            }
            if (tokenDomain is null) {
                kind = row.Kind;
                tokenDomain = zone.Row.Value;
            } else if (!string.Equals(
                a: tokenDomain,
                b: zone.Row.Value,
                comparisonType: StringComparison.Ordinal
            )) {
                throw Malformed(detail: $"'zones' entry {index} '{name}' is a zone over '{zone.Row}', not the token domain '{tokenDomain}' the table's other zones share");
            } else if (kind != row.Kind) {
                throw Malformed(detail: $"'zones' entry {index} '{name}' is kind={StateSpelling.Kind(kind: row.Kind)}, not the kind={StateSpelling.Kind(kind: kind)} the table's other zones share");
            }
            if (!seen.Add(item: name)) {
                throw Malformed(detail: $"'zones' names '{name}' twice");
            }

            capacity = Math.Max(
                val1: capacity,
                val2: context.RowCapacity(name: name)
            );
            indices.Add(item: InternKey(
                context: context,
                name: IndexKeyCache.Get(index: index)
            ));
            ordinals[index] = ResolveRowOrdinal(
                context: context,
                name: name
            );
        }

        if (tokenDomain is null) {
            throw Malformed(detail: "declares a 'zones' table with no zone in it — every entry is empty");
        }

        return new ZoneTable(
            capacity: ((int)capacity),
            indices: [.. indices],
            kind: kind,
            names: names,
            ordinals: ordinals,
            tokenDomain: tokenDomain
        );
    }
    /// <summary>Converts a non-negative duration in seconds to whole simulation ticks, rounding up.</summary>
    /// <param name="seconds">The authored duration.</param>
    /// <param name="ratePerSecond">The simulation rate.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <returns>The tick count.</returns>
    public static long DurationSimulationTicks(decimal seconds, int ratePerSecond, string ruleName, string verb) {
        if (
            (seconds < decimal.Zero) ||
            (ratePerSecond <= 0)
        ) {
            throw new RuleException(
                detail: $"'{verb}' requires a non-negative delay and a positive simulation rate",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        var maximumSeconds = (((decimal)long.MaxValue) / ratePerSecond);

        if (seconds > maximumSeconds) {
            throw new RuleException(
                detail: $"'{verb}' delay exceeds the signed 64-bit simulation-tick carrier",
                refusal: RuleRefusal.DurationEngineTicksOutOfRange,
                ruleName: ruleName
            );
        }

        return decimal.ToInt64(d: decimal.Ceiling(d: (seconds * ratePerSecond)));
    }
    /// <summary>Converts a non-negative duration in seconds to an exact whole engine-tick count, refusing one that
    /// has none.</summary>
    /// <param name="seconds">The authored duration.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <returns>The engine-tick count.</returns>
    public static ulong DurationTicksExact(decimal seconds, string ruleName, string verb) {
        if (
            (seconds < decimal.Zero) ||
            !FixedTickConversion.TryDurationEngineTicksExact(
            seconds: seconds,
            ticks: out var ticks
        )
        ) {
            throw new RuleException(
                detail: $"'{verb}' delay {seconds.ToString(provider: CultureInfo.InvariantCulture)} seconds is not a non-negative exact whole-engine-tick duration",
                refusal: RuleRefusal.DurationNotExactEngineTicks,
                ruleName: ruleName
            );
        }

        return ticks;
    }
    /// <summary>Interns a cell key against the compile catalog's key table.</summary>
    /// <param name="context">The compile context.</param>
    /// <param name="name">The key name.</param>
    /// <returns>The interned key.</returns>
    public static CellKey InternKey(RuleCompileContext context, string name) {
        ArgumentNullException.ThrowIfNull(argument: context);

        return context.Catalog.Keys.Intern(name: CellName.Parse(candidate: name));
    }
    /// <summary>Converts an authored decimal literal to a cell kind's raw encoding, refusing one outside the kind's
    /// range.</summary>
    /// <param name="kind">The destination kind.</param>
    /// <param name="literal">The authored literal.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <returns>The raw value.</returns>
    public static long LiteralToRaw(CellKind kind, decimal literal, string ruleName, string verb) {
        try {
            return (kind switch {
                CellKind.Int => checked((long)decimal.Round(
                d: literal,
                decimals: 0,
                mode: MidpointRounding.ToEven
            )),
                CellKind.Fixed => NumericLiteral.ToFixed(value: literal).Value,
                // Bool — Text is refused before numeric lowering.
                _ => ((literal != decimal.Zero)
                ? 1L
                : 0L),
            });
        } catch (OverflowException) {
            throw new RuleException(
                detail: $"'{verb}' literal {literal.ToString(provider: CultureInfo.InvariantCulture)} is outside the representable {StateSpelling.Kind(kind: kind)} state range",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }
    }
    /// <summary>Refuses a key on a reserved channel, which is a single quantity and carries no cells.</summary>
    /// <param name="key">The authored key.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="name">The channel name.</param>
    /// <param name="keyFieldLabel">The field the key was spelled in.</param>
    public static void RefuseKeyOnReservedChannel(StateChannelRef? key, string ruleName, string name, string keyFieldLabel) {
        if (key is not null) {
            throw new RuleException(
                detail: $"reserved channel '{name}' is a single quantity and carries no cells — drop the '{keyFieldLabel}'",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }
    }
    /// <summary>Refuses a binding token that is not live in the current compile scope.</summary>
    /// <param name="context">The compile context.</param>
    /// <param name="binding">The binding spelled.</param>
    /// <param name="spelled">The authored token, for refusal text.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="where">Where the token was spelled, for refusal text.</param>
    public static void RequireBindingInScope(RuleCompileContext context, BoundKey binding, string spelled, string ruleName, string where) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (Array.IndexOf(
            array: (context.BindingScope ?? []),
            value: binding
        ) < 0) {
            throw new RuleException(
                detail: $"{where} names '{spelled}', which is not bound here — {BindingScopes}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }
    }
    /// <summary>Checks that an authored row's name is present, unreserved, and not yet seen.</summary>
    /// <param name="name">The authored name.</param>
    /// <param name="seen">The names already declared; the name is added on success.</param>
    /// <param name="subject">The row's noun, for refusal text.</param>
    public static void RequireName(string name, HashSet<string> seen, string subject) {
        ArgumentNullException.ThrowIfNull(argument: seen);

        // CellName already proved the shape at the JSON converter; a default-valued struct from a programmatically
        // built document is the one way an empty name still reaches here.
        if (string.IsNullOrWhiteSpace(value: name)) {
            throw new RuleException(
                detail: $"{Article(subject: subject)} {subject} declares a name",
                refusal: RuleRefusal.NameMissing,
                ruleName: "<unnamed>",
                subject: subject
            );
        }
        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: StateRow.ReservedNamePrefix
        )) {
            throw new RuleException(
                detail: $"carries the reserved character '{StateRow.ReservedNamePrefix}' as its first character — that prefix marks what the engine mints, and the engine mints no {subject} under it",
                refusal: RuleRefusal.NameReserved,
                ruleName: name,
                subject: subject
            );
        }
        if (!seen.Add(item: name)) {
            throw new RuleException(
                detail: $"duplicates an earlier {subject}'s name",
                refusal: RuleRefusal.NameDuplicated,
                ruleName: name,
                subject: subject
            );
        }
    }
    /// <summary>Converts an authored decimal to fixed point, refusing one outside the Q48.16 range.</summary>
    /// <param name="value">The authored value.</param>
    /// <param name="field">The field it was spelled in, for refusal text.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <returns>The fixed-point value.</returns>
    public static FixedQ4816 ResolveFixedLiteral(decimal value, string field, string verb, string ruleName) {
        if (NumericLiteral.TryToFixed(
            result: out var result,
            value: value
        )) {
            return result;
        }

        throw new RuleException(
            detail: $"'{verb}' {field} '{value.ToString(provider: CultureInfo.InvariantCulture)}' is outside the Q48.16 range",
            refusal: RuleRefusal.EffectKindInadmissible,
            ruleName: ruleName
        );
    }
    /// <summary>Resolves a literal key against a row under the (row, key) pair rule: a null key means the row's slot
    /// cell, which a keyed row does not have.</summary>
    /// <param name="row">The declared row.</param>
    /// <param name="key">The authored key, or <see langword="null"/>.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="verb">The authored verb, for refusal text.</param>
    /// <param name="keyFieldLabel">The field the key was spelled in, for refusal text.</param>
    /// <returns>The resolved key name.</returns>
    public static string ResolveKey(StateRow row, string? key, string ruleName, string verb, string keyFieldLabel) {
        ArgumentNullException.ThrowIfNull(argument: row);

        if (key is { } authored) {
            return (CellName.TryParse(
                candidate: authored,
                name: out var parsed,
                reason: out var reason
            )
                ? parsed.Value
                : throw new RuleException(
                    detail: $"'{verb}' {keyFieldLabel} '{authored}' {reason}",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                )
            );
        }

        return (row.IsKeyed
            ? throw new RuleException(
                detail: $"'{verb}' names keyed row '{row.Name}' without a '{keyFieldLabel}' — a keyed row has no single cell, so name the one you mean",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            )
            : StateRow.SlotKey.Value
        );
    }
    /// <summary>Resolves a declared numeric row: it must exist and must not be kind=Text.</summary>
    /// <param name="name">The row name.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="requireKeyed">Whether the row must be keyed.</param>
    /// <param name="malformed">The refusal category for an undeclared or text row.</param>
    /// <param name="channel">The authored spelling, for refusal text.</param>
    /// <returns>The declared row.</returns>
    public static StateRow ResolveNumericRow(string name, string ruleName, RuleCompileContext context, bool requireKeyed, Enum malformed, string channel) => ResolveRequiredRow(
        channel: channel,
        context: context,
        malformed: malformed,
        name: name,
        requireKeyed: requireKeyed,
        requireNumeric: true,
        ruleName: ruleName
    );
    /// <summary>Resolves the catalog ordinal of a declared row, recording a host-owned row on the rule's needs.</summary>
    /// <param name="context">The compile context.</param>
    /// <param name="name">The row name.</param>
    /// <returns>The catalog ordinal.</returns>
    /// <exception cref="InvalidOperationException">The row is absent from the compiled catalog.</exception>
    public static int ResolveRowOrdinal(RuleCompileContext context, string name) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (!context.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: name
        )) {
            throw new InvalidOperationException(message: $"Validated state row '{name}' is absent from its compiled catalog.");
        }

        var ordinal = handle.Ordinal;

        if (
            context.Catalog.TryGetDescriptor(
            descriptor: out var descriptor,
            handle: handle
        ) &&
            descriptor.HostOwned
        ) {
            context.Needs.AddHostOwnedRow(rowOrdinal: ordinal);
        }

        return ordinal;
    }

    private static string Article(string subject) => (((subject.Length > 0) && (subject[0] is 'a' or 'e' or 'i' or 'o' or 'u'))
        ? "an"
        : "a"
    );
    // The cells one effect writes only after the firing commits: its own writes when the effect itself cannot be
    // rewound, and otherwise the same question asked of each arm it holds.
    private static void DeferredWrites(IRuleEffect effect, List<CellAccess> into) {
        if (effect.Needs.HasFlag(flag: EffectNeeds.Irreversible)) {
            effect.CollectWrites(into: into);

            return;
        }
        foreach (var arm in effect.Arms) {
            foreach (var nested in arm) {
                DeferredWrites(
                    effect: nested,
                    into: into
                );
            }
        }
    }

    /// <summary>Refuses a later effect of the same firing that reads what an effect the evaluator will defer past
    /// the commit writes.</summary>
    /// <remarks>Deferral is the arm's own, never its enclosing branch's: a branch stays inside the firing's scope and
    /// the evaluator defers the irreversible arms it reaches, at whatever depth they sit. This scan follows those
    /// arms, so a branch holding one still refuses a later sibling that reads its result.</remarks>
    /// <param name="effects">The compiled effects, in authored order.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <exception cref="RuleException">A later effect reads a cell a deferred arm writes.</exception>
    public static void RefuseIrreversibleResultRead(IRuleEffect[] effects, string ruleName) {
        ArgumentNullException.ThrowIfNull(argument: effects);

        for (var index = 0; (index < effects.Length); index++) {
            var writes = new List<CellAccess>();

            DeferredWrites(
                effect: effects[index],
                into: writes
            );
            if (writes.Count != 0) {
                for (var later = (index + 1); (later < effects.Length); later++) {
                    var reads = new List<CellAccess>();

                    effects[later].CollectReads(into: reads);
                    foreach (var read in reads) {
                        foreach (var write in writes) {
                            if (read.Overlaps(other: write)) {
                                throw new RuleException(
                                    detail: $"effect {later} ('{effects[later].Describe}') reads a cell effect {index} ('{effects[index].Describe}') writes, but that arm cannot be rewound and so fires only after the firing commits",
                                    refusal: RuleRefusal.IrreversibleResultRead,
                                    ruleName: ruleName
                                );
                            }
                        }
                    }
                }
            }
            foreach (var arm in effects[index].Arms) {
                RefuseIrreversibleResultRead(
                    effects: arm,
                    ruleName: ruleName
                );
            }
        }
    }

    private static string ReservedChannels(RuleCompileContext context) {
        var spellings = new List<string>(collection: CoreSpellings);

        foreach (var family in context.Vocabulary.Operands) {
            spellings.AddRange(collection: family.Spellings);
        }

        return string.Join(
            separator: ", ",
            values: spellings.Select(selector: static spelling => $"'{spelling}'")
        );
    }
    private static StateRow ResolveRequiredRow(string name, string ruleName, RuleCompileContext context, bool requireKeyed, Enum malformed, string channel, bool requireNumeric = false) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var row = (context.FindRow(name: name)
            ?? throw new RuleException(
            detail: $"'{channel}' names row '{name}', which the document does not declare",
            refusal: malformed,
            ruleName: ruleName
        ));

        if (
            requireNumeric &&
            (row.Kind == CellKind.Text)
        ) {
            throw new RuleException(
                detail: $"'{channel}' names row '{name}', which is kind=Text — a reduction/extremum is numeric, never text",
                refusal: malformed,
                ruleName: ruleName
            );
        }
        if (
            requireKeyed &&
            !row.IsKeyed
        ) {
            throw new RuleException(
                detail: $"'{channel}' names row '{name}', which is not keyed — author a keyed row to select or iterate its cell keys",
                refusal: RuleRefusal.ArgRowNotKeyed,
                ruleName: ruleName
            );
        }

        return row;
    }

    /// <summary>One resolved operand: its compiled fact, the cell kind it reads in, and its read-back spelling.</summary>
    /// <param name="Operand">The compiled operand.</param>
    /// <param name="ValueKind">The kind the operand reads in.</param>
    /// <param name="Describe">The authored spelling, for the rules read-back.</param>
    public readonly record struct ResolvedOperand(IRuleOperand Operand, CellKind ValueKind, string Describe) {
        /// <summary>Creates a resolved operand over a fact, in the fact's own kind.</summary>
        /// <param name="operand">The compiled operand.</param>
        /// <param name="describe">The authored spelling.</param>
        public ResolvedOperand(IRuleOperand operand, string describe) : this(
            Describe: describe,
            Operand: operand,
            ValueKind: operand.ValueKind
        ) { }
    }
}
