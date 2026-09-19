using System.Globalization;
using Puck.Maths;

namespace Puck.State.Rules;

public static partial class RuleCompiler {
    /// <summary>Compiles one authored effect: the library's own arms directly, a registered arm through its family,
    /// and any other by-name refusal.</summary>
    /// <param name="effect">The authored effect.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The compiled effect.</returns>
    public static IRuleEffect CompileEffect(ActionEffect? effect, string ruleName, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        switch (effect) {
            case null:
                throw new RuleException(
                    detail: "an effect row is null",
                    refusal: RuleRefusal.EffectKindInadmissible,
                    ruleName: ruleName
                );
            case ActionEffect.TransformState transform:
                return ResolveStateTransform(
                    context: context,
                    ruleName: ruleName,
                    transform: transform.Transform
                );
            case ActionEffect.SetState set:
                return ResolveWrite(
                    context: context,
                    expression: set.Expression,
                    fromKey: set.FromKey,
                    fromState: set.FromState,
                    key: set.Key,
                    rowName: set.State,
                    ruleName: ruleName,
                    target: set.Target,
                    text: set.Text,
                    value: set.Value,
                    valueSeconds: set.ValueSeconds,
                    vector: set.Vector,
                    verb: "setState",
                    write: StateWriteKind.Set
                );
            case ActionEffect.PushState push:
                return ResolvePush(
                    context: context,
                    push: push,
                    ruleName: ruleName
                );
            case ActionEffect.AddState add:
                return ResolveWrite(
                    context: context,
                    expression: add.Expression,
                    fromKey: add.FromKey,
                    fromState: add.FromState,
                    key: add.Key,
                    rowName: add.State,
                    ruleName: ruleName,
                    target: add.Target,
                    text: null,
                    value: add.Value,
                    valueSeconds: add.ValueSeconds,
                    verb: "addState",
                    write: StateWriteKind.Add
                );
            case ActionEffect.CountdownState countdown:
                return ResolveCountdown(
                    context: context,
                    effect: countdown,
                    ruleName: ruleName
                );
            case ActionEffect.Generate generate:
                return ResolveGenerate(
                    context: context,
                    generate: generate,
                    ruleName: ruleName
                );
            case ActionEffect.RemoveStateCell remove:
                return ResolveRemoveStateCell(
                    context: context,
                    effect: remove,
                    ruleName: ruleName
                );
            case ActionEffect.ScheduleState schedule:
                return ResolveScheduleState(
                    context: context,
                    effect: schedule,
                    ruleName: ruleName
                );
            case ActionEffect.Transaction transaction:
                return ResolveTransaction(
                    context: context,
                    effect: transaction,
                    ruleName: ruleName
                );
            case ActionEffect.If branch:
                return ResolveIf(
                    context: context,
                    effect: branch,
                    ruleName: ruleName
                );
            default:
                if (context.Vocabulary.EffectOf(effect: effect) is { } family) {
                    var compiled = family.Compile(
                        context: context,
                        effect: effect,
                        ruleName: ruleName
                    );

                    context.Needs.AddFact(fact: compiled);

                    return compiled;
                }

                throw new RuleException(
                    detail: $"'{effect.GetType().Name}' has no rule-scope meaning",
                    refusal: RuleRefusal.EffectKindInadmissible,
                    ruleName: ruleName
                );
        }
    }
    /// <summary>Compiles an effect list, refusing an empty or absent one in the subject's own noun.</summary>
    /// <param name="effects">The authored effects.</param>
    /// <param name="ruleName">The rule being compiled.</param>
    /// <param name="context">The compile context.</param>
    /// <param name="subject">The authored-row noun, for refusal text.</param>
    /// <param name="allowTransaction">Whether a savepoint is admissible here; savepoints never nest.</param>
    /// <param name="allowEmpty">Whether an empty or absent list compiles to no effects instead of refusing.</param>
    /// <param name="member">The document member this list is, as the document spells it, so a located refusal
    /// names the branch it came from rather than the enclosing effect's own list.</param>
    /// <returns>The compiled effects, in authored order.</returns>
    public static IRuleEffect[] CompileEffects(IReadOnlyList<ActionEffect>? effects, string ruleName, RuleCompileContext context, string subject, bool allowTransaction = true, bool allowEmpty = false, string member = "effects") {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (effects is not { Count: > 0 }) {
            if (allowEmpty) {
                return [];
            }

            throw new RuleException(
                detail: $"{Article(subject: subject)} {subject} must carry a non-empty effect list",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName,
                subject: subject
            );
        }
        if (effects.Count > RuleCapacity.MaxEffectsPerRule) {
            throw new RuleException(
                detail: $"{Article(subject: subject)} {subject} carries {effects.Count} effects, exceeding the {RuleCapacity.MaxEffectsPerRule}-effect ceiling",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName,
                subject: subject
            );
        }

        var compiled = new IRuleEffect[effects.Count];

        for (var index = 0; (index < compiled.Length); index++) {
            if (
                !allowTransaction &&
                (effects[index] is ActionEffect.Transaction)
            ) {
                throw new RuleException(
                    detail: "a transaction cannot contain another transaction",
                    refusal: RuleRefusal.EffectKindInadmissible,
                    ruleName: ruleName
                );
            }

            try {
                compiled[index] = CompileEffect(
                    context: context,
                    effect: effects[index],
                    ruleName: ruleName
                );
            } catch (RuleException error) { throw error.Within(segment: $"{member}[{index}]"); }
        }

        return compiled;
    }

    // Builds the refusal detail for a 'valueSeconds' that is not an exact whole engine-tick count — names the
    // authored value, the arithmetic that proves it inexact, and the nearest exact durations on either side. One
    // engine tick is 1/50400 s, which itself has no terminating decimal spelling, so the seconds gloss is
    // approximate by construction.
    private static string DescribeInexactDuration(string verb, string rowName, decimal literalSeconds) {
        var secondsText = literalSeconds.ToString(provider: CultureInfo.InvariantCulture);

        if (literalSeconds < decimal.Zero) {
            return $"'{verb}' authors {rowName} 'valueSeconds' {secondsText} — a duration must be non-negative.";
        }

        var scaledTicks = (literalSeconds * FixedTickConversion.TicksPerSecond);
        var lowerTicks = decimal.Floor(d: scaledTicks);
        var upperTicks = (lowerTicks + 1m);
        var lowerSeconds = (lowerTicks / FixedTickConversion.TicksPerSecond);
        var upperSeconds = (upperTicks / FixedTickConversion.TicksPerSecond);

        return string.Concat(
            str0: $"'{verb}' authors {rowName} 'valueSeconds' {secondsText} — {secondsText}s * {FixedTickConversion.TicksPerSecond} engine ticks/s = {scaledTicks.ToString(provider: CultureInfo.InvariantCulture)} ticks, not a whole number, so no exact engine-tick duration exists for it; ",
            str1: $"the nearest exact durations are {lowerTicks.ToString(provider: CultureInfo.InvariantCulture)} engine ticks (~{lowerSeconds.ToString(provider: CultureInfo.InvariantCulture)}s) ",
            str2: $"and {upperTicks.ToString(provider: CultureInfo.InvariantCulture)} engine ticks (~{upperSeconds.ToString(provider: CultureInfo.InvariantCulture)}s) — author one of those as 'valueSeconds', ",
            str3: "or (when no terminating decimal spells the intended duration exactly) author the raw whole engine-tick count directly via 'value' on the row and its companion decrement rule."
        );
    }
    // A live selection's first candidate row: every member of a zone table or a family shares its kind and shape,
    // so one of them answers every compile-time question about the destination.
    private static int FirstCandidate(LiveRow row) {
        var candidates = new List<CellAccess>();

        row.CollectRows(
            into: candidates,
            isSet: false
        );

        return ((candidates.Count > 0)
            ? candidates[0].RowOrdinal
            : -1);
    }
    private static (CellKey Key, CompiledCellRef? KeyFrom, string Spelling) ResolveDestination(StateRow row, string? key, string verb, string ruleName, RuleCompileContext context) {
        if (TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: context,
            key: key,
            keyFieldLabel: "key",
            ruleName: ruleName,
            verb: verb
        )) {
            if (!row.IsKeyed) {
                throw new RuleException(
                    detail: $"'{verb}' key '{key}' addresses a cell by indirection, but row '{row.Name}' is not keyed",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                );
            }

            return (default, dynamicKey, key!);
        }

        var resolved = ResolveKey(
            key: key,
            keyFieldLabel: "key",
            row: row,
            ruleName: ruleName,
            verb: verb
        );

        return (InternKey(
            context: context,
            name: resolved
        ), null, resolved);
    }
    private static IRuleEffect ResolveCountdown(ActionEffect.CountdownState effect, string ruleName, RuleCompileContext context) {
        var row = (context.FindRow(name: effect.State)
            ?? throw new RuleException(
            detail: $"'countdownState' names no state row '{effect.State}'",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (
            (row.Kind != CellKind.Int) ||
            (row.Min != 0L)
        ) {
            throw new RuleException(
                detail: $"state row '{effect.State}' is kind={StateSpelling.Kind(kind: row.Kind)} min={(row.Min?.ToString(provider: CultureInfo.InvariantCulture) ?? "none")} — 'countdownState' requires kind=Int min=0 so its computed final partial step can saturate at zero",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var destination = ResolveDestination(
            context: context,
            key: effect.Key,
            row: row,
            ruleName: ruleName,
            verb: "countdownState"
        );

        return new CountdownEffect(
            describe: $"countdownState {effect.State}.{destination.Spelling} by runtime step",
            key: destination.Key,
            keyFrom: destination.KeyFrom,
            rowOrdinal: ResolveRowOrdinal(
                context: context,
                name: effect.State
            )
        );
    }
    // A 'generate' effect names one thing: the site to redraw. The source is the site's own facet, so there is no
    // second row to resolve and no key to address — a draw site is a scalar slot by construction.
    private static IRuleEffect ResolveGenerate(ActionEffect.Generate generate, string ruleName, RuleCompileContext context) {
        var row = (context.FindRow(name: generate.Row)
            ?? throw new RuleException(
            detail: $"'generate' names no state row '{generate.Row}'",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));
        var draw = context.FindDraw(row: row);

        if (draw is null) {
            throw new RuleException(
                detail: $"state row '{generate.Row}' declares no draw — 'generate' redraws a draw site or a field row painted by a draw fill",
                refusal: RuleRefusal.GeneratorUnknown,
                ruleName: ruleName
            );
        }
        if (draw.Timing == DrawTiming.Boot) {
            throw new RuleException(
                detail: $"state row '{generate.Row}' declares timing=boot — it draws once at first fill and is never redrawn",
                refusal: RuleRefusal.GeneratorUnknown,
                ruleName: ruleName
            );
        }
        if (!GeneratorEngine.TryResolveSource(
            draw: draw,
            generator: out var generator,
            generators: context.Generators,
            reason: out var resolveReason
        )) {
            throw new RuleException(
                detail: $"state row '{generate.Row}' {resolveReason}",
                refusal: RuleRefusal.GeneratorUnknown,
                ruleName: ruleName
            );
        }
        // The one kind predicate, asked here at compile time so an author sees a mismatch before the effect ever
        // fires — the same call the fire-time door makes.
        if (!GeneratorEngine.TryCheckTargetKind(
            reason: out var kindReason,
            source: generator.Source,
            targetKind: row.Kind
        )) {
            throw new RuleException(
                detail: $"state row '{generate.Row}': {kindReason}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        return new GenerateEffect(
            describe: $"generate {generate.Row}",
            key: InternKey(
                context: context,
                name: StateRow.SlotKey.Value
            ),
            rowOrdinal: ResolveRowOrdinal(
                context: context,
                name: generate.Row
            )
        );
    }
    // The condition compiles as an ordinary gate. Each branch compiles as an ordinary effect list, with a savepoint
    // admitted in the branch only when this 'if' is not itself inside one: savepoints never nest, whether directly
    // or through an intervening 'if'.
    private static IRuleEffect ResolveIf(ActionEffect.If effect, string ruleName, RuleCompileContext context) {
        if (effect.Condition is null) {
            throw new RuleException(
                detail: "'if' must carry a 'condition'",
                refusal: RuleRefusal.PredicateKindInadmissible,
                ruleName: ruleName
            );
        }

        var condition = CompileGate(
            context: context,
            predicate: effect.Condition,
            ruleName: ruleName
        );
        var allowTransaction = !context.Scope.ContainsKey(key: SavepointScope);
        var then = CompileEffects(
            allowTransaction: allowTransaction,
            context: context,
            effects: effect.Then,
            member: "then",
            ruleName: ruleName,
            subject: "if"
        );
        var elseEffects = ((effect.Else is { Count: > 0 })
            ? CompileEffects(
                allowTransaction: allowTransaction,
                context: context,
                effects: effect.Else,
                member: "else",
                ruleName: ruleName,
                subject: "if"
            )
            : []
        );

        return new IfEffect(
            condition: condition,
            describe: $"if {then.Length} then-effect(s), else {elseEffects.Length}",
            elseEffects: elseEffects,
            then: then
        );
    }
    // pushState is a write whose destination is the ring's next slot rather than a named cell: it borrows the write
    // resolver for its one source spelling and its kind proof, then carries the effect as its own kind.
    private static IRuleEffect ResolvePush(ActionEffect.PushState push, string ruleName, RuleCompileContext context) {
        var row = (context.FindRow(name: push.State)
            ?? throw new RuleException(
            detail: $"'pushState' names no state row '{push.State}'",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (row.EffectiveDomain is not StateDomain.Ring) {
            throw new RuleException(
                detail: $"'pushState' requires a history row; '{push.State}' has no history trait",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var write = ((WriteEffect)ResolveWrite(
            context: context,
            expression: push.Expression,
            fromKey: push.FromKey,
            fromState: push.FromState,
            key: "0",
            rowName: push.State,
            ruleName: ruleName,
            target: ActionTarget.Self,
            text: null,
            value: push.Value,
            valueSeconds: null,
            verb: "pushState",
            write: StateWriteKind.Set
        ));

        return new PushStateEffect(
            describe: $"pushState {push.State}",
            rowOrdinal: write.RowOrdinal,
            source: write.Source
        );
    }
    private static IRuleEffect ResolveRemoveStateCell(ActionEffect.RemoveStateCell effect, string ruleName, RuleCompileContext context) {
        var row = (context.FindRow(name: effect.State)
            ?? throw new RuleException(
            detail: $"'removeStateCell' names no state row '{effect.State}'",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));
        var destination = ResolveDestination(
            context: context,
            key: effect.Key,
            row: row,
            ruleName: ruleName,
            verb: "removeStateCell"
        );

        return new RemoveStateCellEffect(
            describe: $"removeStateCell {effect.State}.{destination.Spelling}",
            key: destination.Key,
            keyFrom: destination.KeyFrom,
            rowOrdinal: ResolveRowOrdinal(
                context: context,
                name: effect.State
            )
        );
    }
    private static IRuleEffect ResolveScheduleState(ActionEffect.ScheduleState effect, string ruleName, RuleCompileContext context) {
        var row = (context.FindRow(name: effect.State)
            ?? throw new RuleException(
            detail: $"'scheduleState' names no state row '{effect.State}'",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (row.Kind != CellKind.Int) {
            throw new RuleException(
                detail: $"'scheduleState' requires a kind=Int row; '{effect.State}' is {StateSpelling.Kind(kind: row.Kind)}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var ticks = DurationSimulationTicks(
            ratePerSecond: context.SimulationRateHz,
            ruleName: ruleName,
            seconds: effect.DelaySeconds,
            verb: "scheduleState"
        );
        var destination = ResolveDestination(
            context: context,
            key: effect.Key,
            row: row,
            ruleName: ruleName,
            verb: "scheduleState"
        );

        return new ScheduleStateEffect(
            delayTicks: ticks,
            describe: $"scheduleState {effect.State}.{destination.Spelling} after {effect.DelaySeconds.ToString(provider: CultureInfo.InvariantCulture)}s",
            key: destination.Key,
            keyFrom: destination.KeyFrom,
            rowOrdinal: ResolveRowOrdinal(
                context: context,
                name: effect.State
            )
        );
    }
    private static IRuleEffect ResolveTransaction(ActionEffect.Transaction effect, string ruleName, RuleCompileContext context) {
        if (
            (effect.Effects is not { Count: > 0 }) ||
            (effect.Effects.Count > RuleCapacity.MaxTransactionEffects)
        ) {
            throw new RuleException(
                detail: $"a transaction must carry 1..{RuleCapacity.MaxTransactionEffects} effects",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }
        if ((effect.OnFailure?.Count ?? 0) > RuleCapacity.MaxTransactionEffects) {
            throw new RuleException(
                detail: $"a transaction failure branch exceeds {RuleCapacity.MaxTransactionEffects} effects",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        var reentered = context.Scope.ContainsKey(key: SavepointScope);

        context.Scope[SavepointScope] = SavepointScope;
        try {
            var effects = CompileEffects(
                allowTransaction: false,
                context: context,
                effects: effect.Effects,
                ruleName: ruleName,
                subject: "transaction"
            );
            var failure = ((effect.OnFailure is { Count: > 0 })
                ? CompileEffects(
                    allowTransaction: false,
                    context: context,
                    effects: effect.OnFailure,
                    member: "onFailure",
                    ruleName: ruleName,
                    subject: "transaction"
                )
                : []
            );

            return new TransactionEffect(
                describe: $"transaction {effects.Length} effect(s), failure {failure.Length}",
                effects: effects,
                onFailure: failure
            );
        } finally {
            if (!reentered) {
                context.Scope.Remove(key: SavepointScope);
            }
        }
    }
    // value XOR valueSeconds XOR (fromState, fromKey) XOR expression XOR text: the same duality ResolvePredicate
    // enforces for compareState's comparand, applied to the write side.
    private static IRuleEffect ResolveWrite(string rowName, string? key, ActionTarget target, StateWriteKind write, decimal? value, string? fromState, string? fromKey, decimal? valueSeconds, string? text, ExpressionProgram? expression, string ruleName, RuleCompileContext context, string verb, string? vector = null) {
        if (target != ActionTarget.Self) {
            throw new RuleException(
                detail: $"'{verb}' carries target '{target}' — a rule has no entity to address, so a target is refused rather than parsed and discarded",
                refusal: RuleRefusal.TargetInadmissible,
                ruleName: ruleName
            );
        }

        // A live destination selects one row of a zone table or a declared family per firing; the selection's first
        // candidate stands for the whole set at compile time, because every member shares its kind and shape.
        _ = TryResolveLiveRow(
            context: context,
            name: rowName,
            row: out var liveRow,
            ruleName: ruleName,
            where: verb
        );

        var row = ((liveRow is { } selection)
            ? (context.FindRowAt(rowOrdinal: FirstCandidate(row: selection)) ?? throw new RuleException(
                detail: $"'{verb}' selects a row live through '{rowName}', which names no row at all",
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName
            ))
            : (context.FindRow(name: rowName)
            ?? throw new RuleException(
            detail: $"'{verb}' names no state row '{rowName}'",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        )));
        var hasText = (text is not null);
        var isTextRow = (row.Kind == CellKind.Text);

        if (
            (hasText && !isTextRow) ||
            (isTextRow && !hasText && (fromState is null)) ||
            (isTextRow && (expression is not null)) ||
            (isTextRow && (write == StateWriteKind.Add))
        ) {
            throw new RuleException(
                detail: (hasText
                ? $"state row '{rowName}' is kind={StateSpelling.Kind(kind: row.Kind)} — '{verb}' 'text' writes a kind=Text row"
                : $"state row '{rowName}' is kind=Text — '{verb}' writes it through 'text' or a text 'fromState' copy, never arithmetic"),
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var destination = ResolveDestination(
            context: context,
            key: key,
            row: row,
            ruleName: ruleName,
            verb: verb
        );
        var rowOrdinal = ((liveRow is null)
            ? ResolveRowOrdinal(
                context: context,
                name: rowName
            )
            : -1);

        if (row.Kind == CellKind.Vector) {
            return ResolveVectorWrite(
                context: context,
                destination: destination,
                expression: expression,
                fromKey: fromKey,
                fromState: fromState,
                row: row,
                rowOrdinal: rowOrdinal,
                ruleName: ruleName,
                text: text,
                value: value,
                valueSeconds: valueSeconds,
                vector: vector,
                verb: verb,
                write: write
            );
        }
        if (vector is not null) {
            throw new RuleException(
                detail: $"state row '{rowName}' is kind={StateSpelling.Kind(kind: row.Kind)} — 'vector' can only write to a kind=Vector row",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }

        var hasExpression = (expression is not null);
        var hasFrom = (fromState is not null);
        var hasValue = (value is not null);
        var hasValueSeconds = (valueSeconds is not null);

        if (hasText) {
            if (
                hasValue ||
                hasFrom ||
                hasValueSeconds ||
                hasExpression ||
                (fromKey is not null)
            ) {
                throw new RuleException(
                    detail: $"'{verb}' names 'text' beside 'value'/'valueSeconds'/'fromState' — a text write has exactly one spelling",
                    refusal: RuleRefusal.EffectSourceAmbiguous,
                    ruleName: ruleName
                );
            }

            return new WriteEffect(
                describe: $"{verb} {rowName}.{destination.Spelling} = \"{text}\"",
                key: destination.Key,
                keyFrom: destination.KeyFrom,
                rowFrom: liveRow,
                rowOrdinal: rowOrdinal,
                source: default,
                text: text,
                write: write
            );
        }
        if (
            (fromKey is not null) &&
            (fromState is null)
        ) {
            throw new RuleException(
                detail: $"'{verb}' names 'fromKey' without 'fromState' — a copy source key addresses a cell inside a source row, which must be named",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }

        var spellingCount = ((((hasValue
            ? 1
            : 0) + (hasFrom
            ? 1
            : 0)) + (hasValueSeconds
            ? 1
            : 0)) + (hasExpression
            ? 1
            : 0));

        if (spellingCount != 1) {
            throw new RuleException(
                detail: $"'{verb}' must name exactly one of 'value', 'valueSeconds', 'fromState', or 'expression' — named {spellingCount}",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }
        if (hasValueSeconds) {
            if (row.Kind != CellKind.Int) {
                throw new RuleException(
                    detail: $"state row '{rowName}' is kind={StateSpelling.Kind(kind: row.Kind)} — '{verb}' 'valueSeconds' authors a whole engine-tick countdown, meaningful only against a kind=Int row",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                );
            }

            var literalSeconds = valueSeconds!.Value;
            var maximumSeconds = (((decimal)long.MaxValue) / FixedTickConversion.TicksPerSecond);

            if (literalSeconds > maximumSeconds) {
                throw new RuleException(
                    detail: $"'{verb}' authors {rowName} 'valueSeconds' {literalSeconds.ToString(provider: CultureInfo.InvariantCulture)} — the duration exceeds the signed 64-bit state carrier's maximum of {long.MaxValue} engine ticks (approximately {maximumSeconds.ToString(provider: CultureInfo.InvariantCulture)} seconds)",
                    refusal: RuleRefusal.DurationEngineTicksOutOfRange,
                    ruleName: ruleName
                );
            }
            if (!FixedTickConversion.TryDurationEngineTicksExact(
                seconds: literalSeconds,
                ticks: out var ticks
            )) {
                throw new RuleException(
                    detail: DescribeInexactDuration(
                        literalSeconds: literalSeconds,
                        rowName: rowName,
                        verb: verb
                    ),
                    refusal: RuleRefusal.DurationNotExactEngineTicks,
                    ruleName: ruleName
                );
            }

            return new WriteEffect(
                describe: $"{verb} {rowName}.{destination.Spelling} = {literalSeconds.ToString(provider: CultureInfo.InvariantCulture)}s ({ticks} engine ticks)",
                key: destination.Key,
                keyFrom: destination.KeyFrom,
                rowFrom: liveRow,
                rowOrdinal: rowOrdinal,
                source: CompiledValueSource.Constant(rawValue: checked((long)ticks)),
                text: null,
                write: write
            );
        }
        if (hasValue) {
            var literal = value!.Value;

            return new WriteEffect(
                describe: $"{verb} {rowName}.{destination.Spelling} = {literal.ToString(provider: CultureInfo.InvariantCulture)}",
                key: destination.Key,
                keyFrom: destination.KeyFrom,
                rowFrom: liveRow,
                rowOrdinal: rowOrdinal,
                source: CompiledValueSource.Constant(rawValue: LiteralToRaw(
                    kind: row.Kind,
                    literal: literal,
                    ruleName: ruleName,
                    verb: verb
                )),
                text: null,
                write: write
            );
        }
        if (hasExpression) {
            // A kind=Text destination was refused above. A kind=Bool one computes in Int — the kind a comparison
            // leaves — and lands as 0 for zero and 1 for anything else, which the row's own write door then admits.
            var program = CompileExpression(
                context: context,
                expression: expression,
                kind: ((row.Kind == CellKind.Bool)
                ? CellKind.Int
                : row.Kind),
                ruleName: ruleName,
                verb: verb
            );

            return new WriteEffect(
                describe: $"{verb} {rowName}.{destination.Spelling} := expression[{program.Length}]",
                key: destination.Key,
                keyFrom: destination.KeyFrom,
                rowFrom: liveRow,
                rowOrdinal: rowOrdinal,
                source: CompiledValueSource.FromExpression(expression: program),
                text: null,
                write: write
            );
        }

        var source = ResolveOperand(
            context: context,
            key: fromKey,
            name: fromState!,
            site: new OperandSite(
                AllowText: isTextRow,
                FieldLabel: "fromState",
                KeyFieldLabel: "fromKey",
                RuleName: ruleName,
                Verb: verb
            )
        );

        if (source.ValueKind != row.Kind) {
            throw new RuleException(
                detail: $"state row '{rowName}' is kind={StateSpelling.Kind(kind: row.Kind)} but 'fromState' '{fromState}' is kind={StateSpelling.Kind(kind: source.ValueKind)} — mixed-kind copies are refused; author both sides the same kind",
                refusal: RuleRefusal.EffectSourceKindMismatch,
                ruleName: ruleName
            );
        }

        return new WriteEffect(
            describe: $"{verb} {rowName}.{destination.Spelling} := {source.Describe}",
            key: destination.Key,
            keyFrom: destination.KeyFrom,
            rowOrdinal: rowOrdinal,
            source: CompiledValueSource.FromOperand(operand: source.Operand),
            text: null,
            write: write
        );
    }
    private static IRuleEffect ResolveVectorWrite(StateRow row, int rowOrdinal, (CellKey Key, CompiledCellRef? KeyFrom, string Spelling) destination, string? vector, string? fromState, string? fromKey, decimal? value, decimal? valueSeconds, string? text, ExpressionProgram? expression, StateWriteKind write, string ruleName, RuleCompileContext context, string verb) {
        var rowName = row.Name.Value;

        if (write != StateWriteKind.Set) {
            throw new RuleException(
                detail: $"state row '{rowName}' is kind=Vector — '{verb}' cannot write to a vector row; only setState or transforms are permitted",
                refusal: RuleRefusal.VectorEffectNotAdmitted,
                ruleName: ruleName
            );
        }

        var hasBareCellExpression = (expression is not null);
        var hasFromVector = (fromState is not null);
        var hasVector = (vector is not null);
        var sources = (((hasVector
            ? 1
            : 0) + (hasFromVector
            ? 1
            : 0)) + (hasBareCellExpression
            ? 1
            : 0));

        if (
            (sources != 1) ||
            (value is not null) ||
            (valueSeconds is not null) ||
            (text is not null)
        ) {
            throw new RuleException(
                detail: $"'{verb}' on vector row '{rowName}' must name exactly one vector source: 'vector', 'fromState', or a bare cell 'expression'",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }
        if (expression is { Instructions: [{ Payload: InstructionPayload.State stateToken }] }) {
            fromKey = stateToken.Key;
            fromState = stateToken.Name;
            hasFromVector = true;
            hasVector = false;
        } else if (hasBareCellExpression) {
            throw new RuleException(
                detail: $"'{verb}' on vector row '{rowName}': 'expression' must be a bare cell reference; arithmetic does not produce a vector",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }

        var space = (context.FindSpace(name: row.Space)
            ?? throw new RuleException(
            detail: $"Row '{rowName}' names undeclared vector space '{row.Space}'",
            refusal: RuleRefusal.VectorSpaceMismatch,
            ruleName: ruleName
        ));
        var source = (hasVector
            ? ResolveVectorLiteral(
                context: context,
                expectedSpace: space,
                ruleName: ruleName,
                spelling: vector!.Trim(),
                where: $"{verb} {rowName}"
            )
            : ResolveVectorCellOperand(
                context: context,
                expectedSpace: space,
                key: fromKey,
                rowName: fromState!,
                ruleName: ruleName,
                where: $"{verb} {rowName}"
            )
        );

        return new VectorCopyEffect(
            describe: $"{verb} {rowName}.{destination.Spelling} := {source.Describe}",
            key: destination.Key,
            keyFrom: destination.KeyFrom,
            rowOrdinal: rowOrdinal,
            source: source
        );
    }

    private const string SavepointScope = "$savepoint";
}
