using System.Globalization;
using Puck.Maths;

namespace Puck.State;

public static partial class RuleCompiler {
    /// <summary>Compiles an effect list, refusing an empty or absent one in the subject's own noun.</summary>
    /// <param name="effects">The authored effects.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="subject">The authored-row noun, for refusal text.</param>
    /// <param name="allowTransaction">Whether a transaction is admissible here (never inside another).</param>
    public static EffectFact[] CompileEffects(IReadOnlyList<ActionEffect>? effects, string ruleName, RuleCompileContext context, string subject, bool allowTransaction = true) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (effects is not { Count: > 0 }) {
            throw new RuleException(
                detail: $"{Article(subject)} {subject} must carry a non-empty effect list",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName,
                subject: subject
            );
        }
        if (effects.Count > RuleCapacity.MaxEffectsPerRule) {
            throw new RuleException(
                detail: $"a {subject} carries {effects.Count} effects, exceeding the {RuleCapacity.MaxEffectsPerRule}-effect ceiling",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName,
                subject: subject
            );
        }

        var compiled = new EffectFact[effects.Count];

        for (var index = 0; (index < compiled.Length); index++) {
            if (!allowTransaction && effects[index] is ActionEffect.Transaction) {
                throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "a transaction cannot contain another transaction");
            }

            compiled[index] = CompileEffect(effect: effects[index], ruleName: ruleName, context: context);
        }

        return compiled;
    }

    /// <summary>Compiles one authored effect: the library's own arms directly, a registered arm through its family,
    /// and any other by-name refusal.</summary>
    /// <param name="effect">The authored effect.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    public static EffectFact CompileEffect(ActionEffect? effect, string ruleName, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        switch (effect) {
            case null:
                throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "an effect row is null");
            case ActionEffect.TransformState transform:
                return ResolveStateTransform(transform.Transform, ruleName, context);
            case ActionEffect.SetState set:
                return ResolveWrite(rowName: set.State, key: set.Key, target: set.Target, write: StateWriteKind.Set, value: set.Value, fromState: set.FromState, fromKey: set.FromKey, valueSeconds: set.ValueSeconds, text: set.Text, expression: set.Expression, ruleName: ruleName, context: context, verb: "setState");
            case ActionEffect.PushState push:
                return ResolvePush(push, ruleName, context);
            case ActionEffect.AddState add:
                return ResolveWrite(rowName: add.State, key: add.Key, target: add.Target, write: StateWriteKind.Add, value: add.Value, fromState: add.FromState, fromKey: add.FromKey, valueSeconds: add.ValueSeconds, text: null, expression: add.Expression, ruleName: ruleName, context: context, verb: "addState");
            case ActionEffect.CountdownState countdown:
                return ResolveCountdown(effect: countdown, ruleName: ruleName, context: context);
            case ActionEffect.Generate generate:
                return ResolveGenerate(generate: generate, ruleName: ruleName, context: context);
            case ActionEffect.RemoveStateCell remove:
                return ResolveRemoveStateCell(effect: remove, ruleName: ruleName, context: context);
            case ActionEffect.ScheduleState schedule:
                return ResolveScheduleState(effect: schedule, ruleName: ruleName, context: context);
            case ActionEffect.Transaction transaction:
                return ResolveTransaction(effect: transaction, ruleName: ruleName, context: context);
            default:
                if (context.Vocabulary.EffectOf(effect: effect) is { } family) {
                    return family.Compile(effect: effect, ruleName: ruleName, context: context);
                }

                throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"'{effect.GetType().Name}' has no world-scope meaning");
        }
    }

    private static EffectFact ResolveRemoveStateCell(ActionEffect.RemoveStateCell effect, string ruleName, RuleCompileContext context) {
        var row = (context.FindRow(name: effect.State)
            ?? throw new RuleException(refusal: RuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'removeStateCell' names no state row '{effect.State}'"));
        var keyFrom = default(CompiledCellRef?);
        string key;

        if (TryResolveDynamicKey(context: context, key: effect.Key, ruleName: ruleName, verb: "removeStateCell", keyFieldLabel: "key", cell: out var dynamicKey)) {
            if (!row.IsKeyed) {
                throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'removeStateCell' uses a dynamic key against non-keyed row '{effect.State}'");
            }
            key = effect.Key!;
            keyFrom = dynamicKey;
        } else {
            key = ResolveKey(key: effect.Key, keyFieldLabel: "key", row: row, ruleName: ruleName, verb: "removeStateCell");
        }

        return new RemoveStateCellEffect(row: effect.State, key: key, keyFrom: keyFrom, describe: $"removeStateCell {effect.State}.{key}");
    }

    private static EffectFact ResolveScheduleState(ActionEffect.ScheduleState effect, string ruleName, RuleCompileContext context) {
        var row = (context.FindRow(name: effect.State)
            ?? throw new RuleException(refusal: RuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'scheduleState' names no state row '{effect.State}'"));

        if (row.Kind != CellKind.Int) {
            throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'scheduleState' requires a kind=int row; '{effect.State}' is {DescribeCellKind(kind: row.Kind)}");
        }

        var ticks = DurationSimulationTicks(seconds: effect.DelaySeconds, ratePerSecond: context.SimulationRateHz, ruleName: ruleName, verb: "scheduleState");
        var keyFrom = default(CompiledCellRef?);
        string key;

        if (TryResolveDynamicKey(context: context, key: effect.Key, ruleName: ruleName, verb: "scheduleState", keyFieldLabel: "key", cell: out var dynamicKey)) {
            if (!row.IsKeyed) {
                throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'scheduleState' uses a dynamic key against non-keyed row '{effect.State}'");
            }
            key = effect.Key!;
            keyFrom = dynamicKey;
        } else {
            key = ResolveKey(key: effect.Key, keyFieldLabel: "key", row: row, ruleName: ruleName, verb: "scheduleState");
        }

        return new ScheduleStateEffect(
            row: effect.State,
            key: key,
            keyFrom: keyFrom,
            delayTicks: checked((long)ticks),
            describe: $"scheduleState {effect.State}.{key} after {effect.DelaySeconds.ToString(provider: CultureInfo.InvariantCulture)}s"
        );
    }

    private static EffectFact ResolveTransaction(ActionEffect.Transaction effect, string ruleName, RuleCompileContext context) {
        if (effect.Effects is not { Count: > 0 } || effect.Effects.Count > RuleCapacity.MaxTransactionEffects) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"a transaction must carry 1..{RuleCapacity.MaxTransactionEffects} effects");
        }
        if ((effect.OnFailure?.Count ?? 0) > RuleCapacity.MaxTransactionEffects) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"a transaction failure branch exceeds {RuleCapacity.MaxTransactionEffects} effects");
        }

        var effects = CompileTransactionSteps(steps: effect.Effects, ruleName: ruleName, context: context);
        var failure = ((effect.OnFailure is { Count: > 0 })
            ? CompileTransactionSteps(steps: effect.OnFailure, ruleName: ruleName, context: context)
            : []
        );

        return new TransactionEffect(effects: effects, onFailure: failure, describe: $"transaction {effects.Length} effect(s), failure {failure.Length}");
    }

    private static EffectFact[] CompileTransactionSteps(IReadOnlyList<TransactionStep> steps, string ruleName, RuleCompileContext context) {
        var compiled = new EffectFact[steps.Count];
        var closingSuffix = false;

        for (var index = 0; index < steps.Count; index++) {
            compiled[index] = CompileTransactionStep(step: steps[index], ruleName: ruleName, context: context);
            var closes = compiled[index].ClosesTransaction;
            if (closingSuffix && !closes) {
                throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "placement steps must form the transaction's final suffix because they can rebuild the active population");
            }
            closingSuffix |= closes;
        }

        return compiled;
    }

    private static EffectFact CompileTransactionStep(TransactionStep? step, string ruleName, RuleCompileContext context) {
        var effect = step switch {
            TransactionStep.TransformStateStep transform => new ActionEffect.TransformState(transform.Transform),
            TransactionStep.SetCell set => new ActionEffect.SetState(State: set.State, Key: set.Key, Value: set.Value, FromState: set.FromState, FromKey: set.FromKey, ValueSeconds: set.ValueSeconds, Expression: set.Expression),
            TransactionStep.AddCell add => new ActionEffect.AddState(State: add.State, Key: add.Key, Value: add.Value, FromState: add.FromState, FromKey: add.FromKey, ValueSeconds: add.ValueSeconds, Expression: add.Expression),
            TransactionStep.CountdownCell countdown => new ActionEffect.CountdownState(State: countdown.State, Key: countdown.Key),
            TransactionStep.RemoveCell remove => new ActionEffect.RemoveStateCell(State: remove.State, Key: remove.Key),
            TransactionStep.ScheduleCell schedule => new ActionEffect.ScheduleState(State: schedule.State, DelaySeconds: schedule.DelaySeconds, Key: schedule.Key),
            TransactionStep.GenerateStep generate => new ActionEffect.Generate(Row: generate.Row),
            null => throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "transaction contains a null step"),
            _ => (context.Vocabulary.StepOf(step: step)?.Lift(step: step)
                ?? throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: $"transaction step kind '{step.GetType().Name}' is not supported")),
        };

        return CompileEffect(effect: effect, ruleName: ruleName, context: context);
    }

    private static EffectFact ResolveCountdown(ActionEffect.CountdownState effect, string ruleName, RuleCompileContext context) {
        var row = (context.FindRow(name: effect.State)
            ?? throw new RuleException(refusal: RuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'countdownState' names no state row '{effect.State}' — declare it with world.row.set state <json> first"));

        if ((row.Kind != CellKind.Int) || !row.NonNegative) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"state row '{effect.State}' is kind={DescribeCellKind(kind: row.Kind)} nonNegative={row.NonNegative.ToString().ToLowerInvariant()} — 'countdownState' requires kind=int nonNegative=true so its computed final partial step can saturate at zero"
            );
        }

        CompiledCellRef? keyFrom = null;
        string resolvedKey;

        if (TryResolveDynamicKey(context: context, key: effect.Key, ruleName: ruleName, verb: "countdownState", keyFieldLabel: "key", cell: out var dynamicKey)) {
            keyFrom = dynamicKey;
            resolvedKey = effect.Key!;
        } else {
            resolvedKey = ResolveKey(row: row, key: effect.Key, ruleName: ruleName, verb: "countdownState", keyFieldLabel: "key");
        }

        return new CountdownEffect(row: effect.State, key: resolvedKey, keyFrom: keyFrom, describe: $"countdownState {effect.State}.{resolvedKey} by runtime step");
    }

    // A 'generate' effect names one thing: the site to redraw. The source is the site's own facet (named or
    // inlined), so there is no second row to resolve and no key to address — a draw site is a scalar slot by
    // construction. A boot-timed site draws once at first fill and can never be redrawn, so that refuses here.
    private static EffectFact ResolveGenerate(ActionEffect.Generate generate, string ruleName, RuleCompileContext context) {
        var row = (context.FindRow(name: generate.Row)
            ?? throw new RuleException(refusal: RuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'generate' names no state row '{generate.Row}'"));
        var draw = context.FindDraw(row: row);

        if (draw is null) {
            throw new RuleException(
                refusal: RuleRefusal.GeneratorUnknown,
                ruleName: ruleName,
                detail: $"state row '{generate.Row}' declares no draw — 'generate' redraws a draw site or a field row painted by a draw fill"
            );
        }

        if (draw.Timing == DrawTiming.Boot) {
            throw new RuleException(refusal: RuleRefusal.GeneratorUnknown, ruleName: ruleName, detail: $"state row '{generate.Row}' declares timing=boot — it draws once at first fill and is never redrawn");
        }

        if (!GeneratorEngine.TryResolveSource(generators: context.Generators, draw: draw, generator: out var generator, reason: out var resolveReason)) {
            throw new RuleException(refusal: RuleRefusal.GeneratorUnknown, ruleName: ruleName, detail: $"state row '{generate.Row}' {resolveReason}");
        }

        // The one kind predicate, asked here at compile time so an author sees a mismatch before the effect ever
        // fires — the same call the fire-time door makes.
        if (!GeneratorEngine.TryCheckTargetKind(source: generator.Source, targetKind: row.Kind, reason: out var kindReason)) {
            throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"state row '{generate.Row}': {kindReason}");
        }

        return new GenerateEffect(row: generate.Row, generator: generate.Row, describe: $"generate {generate.Row}");
    }

    // pushState is a write whose destination is the ring's next slot rather than a named cell: it borrows the
    // write resolver for its one source spelling and its kind proof, then carries the effect as its own kind.
    private static EffectFact ResolvePush(ActionEffect.PushState push, string ruleName, RuleCompileContext context) {
        var row = context.FindRow(name: push.State)
            ?? throw new RuleException(RuleRefusal.StateRowUnknown, ruleName, $"'pushState' names no state row '{push.State}'");
        if (row.EffectiveDomain is not StateDomain.Ring) {
            throw new RuleException(RuleRefusal.StateCellUnaddressable, ruleName, $"'pushState' requires a history row; '{push.State}' has no history trait");
        }
        var write = ResolveWrite(
            rowName: push.State,
            key: "0",
            target: ActionTarget.Self,
            write: StateWriteKind.Set,
            value: push.Value,
            fromState: push.FromState,
            fromKey: push.FromKey,
            valueSeconds: null,
            text: null,
            expression: push.Expression,
            ruleName: ruleName,
            context: context,
            verb: "pushState"
        );
        return PushStateEffect.FromWrite((WriteEffect)write, $"pushState {push.State}");
    }

    // Builds the refusal detail for a 'valueSeconds' that is not an exact whole engine-tick count — names the
    // authored value, the arithmetic that proves it inexact, and the nearest exact durations on either side (as
    // engine-tick counts, which are always exact integers, plus an approximate seconds gloss for orientation — 1
    // engine tick is 1/50400 s, which itself has no terminating decimal spelling, so the gloss is never claimed exact).
    private static string DescribeInexactDuration(string verb, string rowName, decimal literalSeconds) {
        var secondsText = literalSeconds.ToString(provider: CultureInfo.InvariantCulture);

        if (literalSeconds < 0m) {
            return $"'{verb}' authors {rowName} 'valueSeconds' {secondsText} — a duration must be non-negative.";
        }

        var scaledTicks = (literalSeconds * FixedTickConversion.TicksPerSecond);
        var lowerTicks = decimal.Floor(d: scaledTicks);
        var upperTicks = (lowerTicks + 1m);
        var lowerSeconds = (lowerTicks / FixedTickConversion.TicksPerSecond);
        var upperSeconds = (upperTicks / FixedTickConversion.TicksPerSecond);

        return ((((((string)$"'{verb}' authors {rowName} 'valueSeconds' {secondsText} — {secondsText}s * {FixedTickConversion.TicksPerSecond} engine ticks/s = {scaledTicks.ToString(provider: CultureInfo.InvariantCulture)} ticks, not a whole number, so no exact engine-tick duration exists for it; ")
            + $"the nearest EXACT durations are {lowerTicks.ToString(provider: CultureInfo.InvariantCulture)} engine ticks ")
            + $"(≈{lowerSeconds.ToString(provider: CultureInfo.InvariantCulture)}s) and {upperTicks.ToString(provider: CultureInfo.InvariantCulture)} engine ticks ")
            + $"(≈{upperSeconds.ToString(provider: CultureInfo.InvariantCulture)}s) — author one of those as 'valueSeconds', or (when no terminating decimal spells the ")
            + "intended duration exactly) author the raw whole engine-tick count directly via 'value' on the row and its companion decrement rule.");
    }

    // value XOR valueSeconds XOR (fromState, fromKey) XOR expression XOR text: the same duality ResolvePredicate
    // enforces for compareState's comparand, applied to the write side. 'fromKey' is an appendage of 'fromState' on
    // the same terms 'comparandKey' is.
    private static EffectFact ResolveWrite(string rowName, string? key, ActionTarget target, StateWriteKind write, decimal? value, string? fromState, string? fromKey, decimal? valueSeconds, string? text, ValueExpression? expression, string ruleName, RuleCompileContext context, string verb) {
        if (target != ActionTarget.Self) {
            throw new RuleException(
                refusal: RuleRefusal.TargetInadmissible,
                ruleName: ruleName,
                detail: $"'{verb}' carries target '{target}' — a world rule has no entity to address, so a target is refused rather than parsed and discarded"
            );
        }

        var row = (context.FindRow(name: rowName)
            ?? throw new RuleException(refusal: RuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'{verb}' names no state row '{rowName}' — declare it with world.row.set state <json> first"));

        var hasText = (text is not null);
        var isTextRow = (row.Kind == CellKind.Text);

        if (
            (hasText && !isTextRow) ||
            (isTextRow && !hasText && (fromState is null)) ||
            (isTextRow && (expression is not null)) ||
            (isTextRow && (write == StateWriteKind.Add))
        ) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: (hasText
                    ? $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} — '{verb}' 'text' writes a kind=text row"
                    : $"state row '{rowName}' is kind=text — '{verb}' writes it through 'text' or a text 'fromState' copy, never arithmetic"
                )
            );
        }

        CompiledCellRef? destinationKeyFrom = null;
        string resolvedKey;

        if (TryResolveDynamicKey(cell: out var dynamicDestination, context: context, key: key, keyFieldLabel: "key", ruleName: ruleName, verb: verb)) {
            if (!row.IsKeyed) {
                throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{verb}' key '{key}' addresses a cell by indirection, but row '{rowName}' is not keyed");
            }

            destinationKeyFrom = dynamicDestination;
            resolvedKey = key!;
        } else {
            resolvedKey = ResolveKey(key: key, keyFieldLabel: "key", row: row, ruleName: ruleName, verb: verb);
        }

        var hasValue = (value is not null);
        var hasFrom = (fromState is not null);
        var hasValueSeconds = (valueSeconds is not null);
        var hasExpression = (expression is not null);

        if (hasText) {
            if (hasValue || hasFrom || hasValueSeconds || hasExpression || (fromKey is not null)) {
                throw new RuleException(refusal: RuleRefusal.EffectSourceAmbiguous, ruleName: ruleName, detail: $"'{verb}' names 'text' beside 'value'/'valueSeconds'/'fromState' — a text write has exactly one spelling");
            }

            return new WriteEffect(row: rowName, key: resolvedKey, keyFrom: destinationKeyFrom, write: write, rawValue: 0L, from: null, text: text, expression: null, describe: $"{verb} {rowName}.{resolvedKey} = \"{text}\"");
        }

        if ((fromKey is not null) && (fromState is null)) {
            throw new RuleException(refusal: RuleRefusal.EffectSourceAmbiguous, ruleName: ruleName, detail: $"'{verb}' names 'fromKey' without 'fromState' — a copy source key addresses a cell inside a source row, which must be named");
        }

        var spellingCount = ((hasValue ? 1 : 0) + (hasFrom ? 1 : 0) + (hasValueSeconds ? 1 : 0) + (hasExpression ? 1 : 0));

        if (spellingCount != 1) {
            throw new RuleException(refusal: RuleRefusal.EffectSourceAmbiguous, ruleName: ruleName, detail: $"'{verb}' must name EXACTLY ONE of 'value', 'valueSeconds', 'fromState', or 'expression' — named {spellingCount}");
        }

        if (hasValueSeconds) {
            if (row.Kind != CellKind.Int) {
                throw new RuleException(
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} — '{verb}' 'valueSeconds' authors a whole engine-tick countdown, meaningful only against a kind=int row"
                );
            }

            var literalSeconds = valueSeconds!.Value;
            var maximumSeconds = (((decimal)long.MaxValue) / FixedTickConversion.TicksPerSecond);

            if (literalSeconds > maximumSeconds) {
                throw new RuleException(
                    refusal: RuleRefusal.DurationEngineTicksOutOfRange,
                    ruleName: ruleName,
                    detail: $"'{verb}' authors {rowName} 'valueSeconds' {literalSeconds.ToString(provider: CultureInfo.InvariantCulture)} — the duration exceeds the signed 64-bit state carrier's maximum of {long.MaxValue} engine ticks (approximately {maximumSeconds.ToString(provider: CultureInfo.InvariantCulture)} seconds)"
                );
            }

            if (!FixedTickConversion.TryDurationEngineTicksExact(seconds: literalSeconds, ticks: out var ticks)) {
                throw new RuleException(refusal: RuleRefusal.DurationNotExactEngineTicks, ruleName: ruleName, detail: DescribeInexactDuration(literalSeconds: literalSeconds, rowName: rowName, verb: verb));
            }

            return new WriteEffect(row: rowName, key: resolvedKey, keyFrom: destinationKeyFrom, write: write, rawValue: checked((long)ticks), from: null, text: null, expression: null, describe: $"{verb} {rowName}.{resolvedKey} = {literalSeconds.ToString(provider: CultureInfo.InvariantCulture)}s ({ticks} engine ticks)");
        }

        if (hasValue) {
            var literal = value!.Value;
            var raw = LiteralToRaw(kind: row.Kind, literal: literal, ruleName: ruleName, verb: verb);

            return new WriteEffect(row: rowName, key: resolvedKey, keyFrom: destinationKeyFrom, write: write, rawValue: raw, from: null, text: null, expression: null, describe: $"{verb} {rowName}.{resolvedKey} = {literal.ToString(provider: CultureInfo.InvariantCulture)}");
        }

        if (hasExpression) {
            if (row.Kind is CellKind.Bool or CellKind.Text) {
                throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} — numeric expressions require kind=int or kind=fixed");
            }

            var program = CompileExpression(context: context, expression: expression, kind: row.Kind, ruleName: ruleName, verb: verb);

            return new WriteEffect(row: rowName, key: resolvedKey, keyFrom: destinationKeyFrom, write: write, rawValue: 0L, from: null, text: null, expression: program, describe: $"{verb} {rowName}.{resolvedKey} := expression[{program.Length}]");
        }

        var source = ResolveOperand(name: fromState!, key: fromKey, site: new OperandSite(RuleName: ruleName, Verb: verb, FieldLabel: "fromState", KeyFieldLabel: "fromKey", AllowText: isTextRow), context: context);

        if (source.ValueKind != row.Kind) {
            throw new RuleException(
                refusal: RuleRefusal.EffectSourceKindMismatch,
                ruleName: ruleName,
                detail: $"state row '{rowName}' is kind={DescribeCellKind(kind: row.Kind)} but 'fromState' '{fromState}' is kind={DescribeCellKind(kind: source.ValueKind)} — mixed-kind copies are refused; author both sides the same kind"
            );
        }

        return new WriteEffect(row: rowName, key: resolvedKey, keyFrom: destinationKeyFrom, write: write, rawValue: 0L, from: source.Operand, text: null, expression: null, describe: $"{verb} {rowName}.{resolvedKey} := {source.Describe}");
    }

    private static EffectFact ResolveStateTransform(StateTransform transform, string ruleName, RuleCompileContext context) {
        RuleException Invalid(string message) => new(RuleRefusal.EffectKindInadmissible, ruleName, message);
        StateRow Row(string name) => context.FindRow(name: name) ?? throw Invalid($"unknown state row '{name}'");
        switch (transform) {
            case StateTransform.Observe observe:
                if (Row(observe.Row).Knowledge is null) {
                    throw Invalid("observe requires a knowledge board");
                }

                break;
            case StateTransform.Transfer transfer:
                // A live end indexes the rule's zone table; a literal end must be an ordered zone over the same
                // token domain as the other end (the table's, when that end is live).
                _ = TryResolveLiveZone(name: transfer.From, ruleName: ruleName, context: context, where: "transfer 'from'", zone: out var fromZone);
                _ = TryResolveLiveZone(name: transfer.To, ruleName: ruleName, context: context, where: "transfer 'to'", zone: out var toZone);
                var tokenDomain = (fromZone ?? toZone)?.Table.TokenDomain;
                void RequireZone(string name, string label) {
                    if (Row(name).EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zone) {
                        throw Invalid($"transfer {label} '{name}' is not an ordered token zone");
                    }
                    if ((tokenDomain is { } domain) && !string.Equals(a: domain, b: zone.Row.Value, comparisonType: StringComparison.Ordinal)) {
                        throw Invalid($"transfer {label} '{name}' is a zone over '{zone.Row}', not the token domain '{domain}' the transfer's other end shares");
                    }
                    tokenDomain = zone.Row.Value;
                }
                if (fromZone is null) { RequireZone(transfer.From, "'from'"); }
                if (toZone is null) { RequireZone(transfer.To, "'to'"); }
                if (!Enum.IsDefined(transfer.Selector) || (transfer.Selector is ZoneSelector.Key or ZoneSelector.Slice) != (transfer.Key is not null) ||
                    (transfer.Selector == ZoneSelector.Random) != (transfer.Draw is not null) ||
                    transfer.Count < 1 || transfer.Count > StateTransferCapacity.MaxTransferCount || (transfer.Selector is ZoneSelector.Key or ZoneSelector.Slice && transfer.Count != 1)) {
                    throw Invalid($"transfer requires selector arguments matching the selector and a count of 1..{StateTransferCapacity.MaxTransferCount} (exactly 1 by key or slice)");
                }
                if (transfer.Draw is { } drawName) {
                    var drawRow = Row(drawName);
                    if (drawRow.Draw is not { Timing: not DrawTiming.Boot } draw || drawRow.Kind != CellKind.Int ||
                        !GeneratorEngine.TryResolveSource(generators: context.Generators, draw: draw, generator: out var generator, reason: out _) || generator.Source != GeneratorSource.StreamDraw) {
                        throw Invalid("random transfer requires a redrawable integer streamDraw site");
                    }
                }
                CompiledCellRef? keyRef = null;
                if (transfer.Key is { } key) {
                    if (TryResolveDynamicKey(key, ruleName, context, "transfer", "key", out var selectedKey)) {
                        keyRef = selectedKey;
                    } else if (!CellName.TryParse(key, out _, out _)) {
                        throw Invalid($"transfer 'key' '{key}' spells neither a token name nor a dynamic key");
                    }
                }
                if (keyRef is not null || fromZone is not null || toZone is not null) {
                    var spelledKey = ((keyRef is not null) ? $" key {transfer.Key}" : string.Empty);
                    return new TransformStateEffect(transform, $"transformState Transfer {transfer.From} to {transfer.To} {transfer.Selector}{spelledKey}", keyRef: keyRef, fromZone: fromZone, toZone: toZone);
                }
                break;
            case StateTransform.SetRay ray:
                var row = Row(ray.Row);
                if (row.EffectiveDomain is not StateDomain.CellsOf board || context.FindTopology(name: board.Topology) is not { } topology ||
                    !topology.TryCell(ray.From, out _) || topology.Direction(ray.Direction) < 0 ||
                    FindPattern(context: context, name: ray.Pattern) is not { } pattern || pattern.Kind != CellKind.Int ||
                    row.ClampToEnvelope(ray.Value) != ray.Value || (row.Kind == CellKind.Bool && ray.Value is not (0 or 1))) {
                    throw Invalid("setRay requires valid board addressing, a declared integer-kind pattern, and an admitted replacement");
                }
                break;
            case StateTransform.SortZone sortZone:
                if (Row(sortZone.Row).EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zone ||
                    sortZone.By is not { Count: >= 1 and <= StateCapacity.MaxSortKeys } ||
                    sortZone.By.Any(key => key is null || Row(key.Row) is not { IsKeyed: true, Kind: CellKind.Int or CellKind.Fixed } sortRow || sortRow.EffectiveDomain is not StateDomain.KeysOf sortKeysOf || sortKeysOf.Row != zone.Row) ||
                    sortZone.By.Select(key => key!.Row).Distinct(StringComparer.Ordinal).Count() != sortZone.By.Count) {
                    throw Invalid($"sortZone requires an ordered zone with 1..{StateCapacity.MaxSortKeys} distinct numeric attribute keys over the zone's token domain, each carrying its own direction");
                }
                break;
            case StateTransform.SortKeyed sortKeyed:
                if (Row(sortKeyed.Row) is not { IsKeyed: true, Kind: CellKind.Int or CellKind.Fixed }) {
                    throw Invalid("sortKeyed requires a keyed numeric row");
                }
                break;
            case StateTransform.Shuffle shuffle:
                if (Row(shuffle.Row) is not { IsKeyed: true } ||
                    Row(shuffle.Draw).Draw is not { Timing: not DrawTiming.Boot } shuffleDraw ||
                    Row(shuffle.Draw).Kind != CellKind.Int ||
                    !GeneratorEngine.TryResolveSource(generators: context.Generators, draw: shuffleDraw, generator: out var shuffleSource, reason: out _) ||
                    shuffleSource.Source != GeneratorSource.StreamDraw) {
                    throw Invalid("shuffle requires a keyed row, and a redrawable integer streamDraw site");
                }
                break;
            case StateTransform.WriteSet writeSet: {
                var written = Row(writeSet.Row);
                var setSource = Row(writeSet.Set);
                if (written.EffectiveDomain is not StateDomain.CellsOf writtenBoard || context.FindTopology(name: writtenBoard.Topology) is not { } writtenTopology ||
                    writtenTopology.CellCount > BoardMask.MaxCells || setSource.Kind != CellKind.Int ||
                    written.ClampToEnvelope(writeSet.Value) != writeSet.Value || (written.Kind == CellKind.Bool && writeSet.Value is not (0 or 1))) {
                    throw Invalid($"writeSet requires a board of at most {BoardMask.MaxCells} cells, an integer set row, and an admitted value");
                }
                if (RuleCompiler.TryResolveDynamicKey(key: writeSet.SetKey, ruleName: ruleName, context: context, verb: "writeSet", keyFieldLabel: "setKey", cell: out var setKeyRef)) {
                    if (!setSource.IsKeyed) {
                        throw Invalid("writeSet 'setKey' addresses a cell by indirection, but the set row is not keyed");
                    }
                    return new TransformStateEffect(transform: transform, describe: $"transformState WriteSet {writeSet.Row} from {writeSet.Set}[{writeSet.SetKey}]", keyRef: setKeyRef);
                }
                if (writeSet.SetKey is null ? !setSource.IsSlot : (!setSource.IsKeyed || !CellName.TryParse(writeSet.SetKey, out _, out _))) {
                    throw Invalid($"writeSet reads its cell set from an integer cell '{writeSet.SetKey ?? StateRow.SlotKey.Value}' of '{writeSet.Set}'");
                }
                break;
            }
            case StateTransform.BoardCombine combine: {
                var target = Row(combine.Row);
                if (target.EffectiveDomain is not StateDomain.CellsOf targetBoard || context.FindTopology(name: targetBoard.Topology) is not { } targetTopology) {
                    throw Invalid("boardCombine writes a board row");
                }
                var needsLeft = combine.Operation is not (BoardCombineOp.Fill or BoardCombineOp.Clear);
                var needsRight = combine.Operation is BoardCombineOp.And or BoardCombineOp.Or or BoardCombineOp.Xor or BoardCombineOp.AndNot;
                if (!Enum.IsDefined(combine.Operation) || needsLeft != (combine.Left is not null) || needsRight != (combine.Right is not null) ||
                    (combine.Operation == BoardCombineOp.Shift) != (combine.Direction is not null) || (combine.Operation == BoardCombineOp.Image) != (combine.Element is not null)) {
                    throw Invalid("boardCombine takes left for every operation but fill and clear, right for and/or/xor/andNot, direction for shift alone, and element for image alone");
                }
                foreach (var sourceName in new[] { combine.Left, combine.Right }) {
                    if (sourceName is not null && (Row(sourceName).EffectiveDomain is not StateDomain.CellsOf sourceBoard || sourceBoard.Topology != targetBoard.Topology)) {
                        throw Invalid($"boardCombine source '{sourceName}' must be a board over '{targetBoard.Topology}'");
                    }
                }
                if ((combine.Direction is { } direction && targetTopology.Direction(direction) < 0) || (combine.Element is { } element && targetTopology.Element(element) < 0)) {
                    throw Invalid("boardCombine names a direction or point-group element its topology does not declare");
                }
                if (target.ClampToEnvelope(combine.Value) != combine.Value || (target.Kind == CellKind.Bool && combine.Value is not (0 or 1)) || combine.Value == targetBoard.Empty) {
                    throw Invalid("boardCombine writes a member value the board admits and that is not the board's own empty value");
                }
                break;
            }
            case StateTransform.Arrange arrange: {
                var arrangedZone = Row(arrange.Row);
                var rank = Row(arrange.From);
                if (arrangedZone.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } || rank.Kind != CellKind.Int ||
                    (arrange.FromKey is null ? !rank.IsSlot : (!rank.IsKeyed || !CellName.TryParse(arrange.FromKey, out _, out _)))) {
                    throw Invalid("arrange requires an ordered zone and an integer rank cell");
                }
                break;
            }
            case StateTransform.Push push:
                var ring = Row(push.Row);
                if (ring.EffectiveDomain is not StateDomain.Ring || ring.ClampToEnvelope(push.Value) != push.Value) {
                    throw Invalid("push requires a history row and an admitted value");
                }
                break;
            case StateTransform.ClearEnclosed enclosed: {
                var enclosedRow = Row(enclosed.Row);
                if (enclosedRow.EffectiveDomain is not StateDomain.CellsOf enclosedBoard || context.FindTopology(name: enclosedBoard.Topology) is not { } enclosedTopology ||
                    enclosedRow.Kind != CellKind.Int || enclosed.Lower > enclosed.Upper || (enclosedBoard.Empty >= enclosed.Lower && enclosedBoard.Empty <= enclosed.Upper)) {
                    throw Invalid("clearEnclosed requires an integer board and an enclosed range that excludes the board's empty value");
                }
                if (RuleCompiler.TryResolveDynamicKey(key: enclosed.From, ruleName: ruleName, context: context, verb: "clearEnclosed", keyFieldLabel: "from", cell: out var origin)) {
                    return new TransformStateEffect(transform: transform, describe: $"transformState ClearEnclosed {enclosed.Row} from {enclosed.From}", keyRef: origin);
                }
                if (!enclosedTopology.TryCell(enclosed.From, out _)) {
                    throw Invalid($"clearEnclosed 'from' names no cell of '{enclosedBoard.Topology}' and spells no dynamic key");
                }
                break;
            }
            default:
                throw Invalid("unknown or null state transform");
        }
        return new TransformStateEffect(transform: transform, describe: $"transformState {transform.GetType().Name}");
    }
}
