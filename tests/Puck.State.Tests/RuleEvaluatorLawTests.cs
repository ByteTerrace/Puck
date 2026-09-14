using Xunit;
using Puck.Maths;

namespace Puck.State.Tests;

/// <summary>The evaluator runs authoritative rules over a bare row list: a host with no world, no bodies, and no
/// document — the card game or turn-based resolver the library exists for.</summary>
public sealed class RuleEvaluatorLawTests {
    private sealed class CountingOperand(RuleFact fact) : OperandFact(CellKind.Int) {
        public int Reads { get; private set; }

        public override long Cost(RuleCompileContext context) => 1;
        public override RuleFact Read(IRuleReader reader) { Reads++; return fact; }
    }

    private static (HeadlessHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, RuleLatch Latch) Arrange(IReadOnlyList<Rule> rules, StateRow[] rows) {
        var host = new HeadlessHost(rows);
        var evaluator = new RuleEvaluator(host: host);

        host.Evaluator = evaluator;
        var context = new RuleCompileContext(
            section: host,
            catalog: host.Catalog,
            tables: null,
            patterns: null,
            generators: null,
            simulationRateHz: 240,
            vocabulary: RuleVocabulary.Core
        );

        return (host, evaluator, RuleCompiler.CompileAll(
            context: context,
            rules: rules
        ), new RuleLatch());
    }
    private static (HeadlessHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, RuleLatch Latch) ArrangeWithPatterns(IReadOnlyList<Rule> rules, IReadOnlyList<PatternRow> patterns, params StateRow[] rows) {
        var host = new HeadlessHost(rows);
        var errors = new List<string>();

        Assert.True(
            condition: CompiledPatterns.TryCompileAll(
                errors: errors,
                patterns: out var compiled,
                rows: patterns
            ),
            userMessage: string.Join(
                separator: "; ",
                values: errors
            )
        );
        host.Patterns = compiled;
        var evaluator = new RuleEvaluator(host: host);

        host.Evaluator = evaluator;
        var context = new RuleCompileContext(
            section: host,
            catalog: host.Catalog,
            tables: null,
            patterns: patterns,
            generators: null,
            simulationRateHz: 240,
            vocabulary: RuleVocabulary.Core
        );

        return (host, evaluator, RuleCompiler.CompileAll(
            context: context,
            rules: rules
        ), new RuleLatch());
    }
    private static ValueExpression Expr(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                text: text,
                tokens: out var tokens
            ),
            userMessage: error
        );
        return new ValueExpression(Tokens: tokens);
    }
    private static StateRow Keyed(string name, params (string Key, long Value)[] cells) =>
        new(
            Name: CellName.Parse(candidate: name),
            Kind: CellKind.Int,
            Capacity: 8,
            Cells: [.. cells.Select(selector: static c => new StateCell(
                    Key: CellName.Parse(candidate: c.Key),
                    Value: c.Value
                ))]
        );
    private static Rule R(string name, ActionPredicate? gate, ActionTriggerMode mode = ActionTriggerMode.Level, string? forEach = null, params ActionEffect[] effects) =>
        new(
            Name: CellName.Parse(candidate: name),
            Effects: effects,
            Gate: gate,
            Mode: mode,
            ForEach: forEach
        );
    private static StateRow Slot(string name, long value, CellKind kind = CellKind.Int) =>
        new(
            Name: CellName.Parse(candidate: name),
            Kind: kind,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: value
                )]
        );

    [Fact]
    public void ACommittedTransactionInstallsOnce() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "deal",
                    gate: null,
                    effects: new ActionEffect.Transaction(Effects: [new ActionEffect.AddState(
                            State: "gold",
                            Value: 5m
                        ), new ActionEffect.AddState(
                            State: "cards",
                            Value: 2m
                        ), new ActionEffect.SetState(
                            State: "turn",
                            Value: 1m
                        )])
                )],
            rows: [Slot(
                    name: "gold",
                    value: 0L
                ), Slot(
                    name: "cards",
                    value: 0L
                ), Slot(
                    name: "turn",
                    value: 0L
                )]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            5L,
            host.Cell(row: "gold")
        );
        Assert.Equal(
            2L,
            host.Cell(row: "cards")
        );
        Assert.Equal(
            1L,
            host.Cell(row: "turn")
        );
        Assert.Equal(
            1,
            host.Installs
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    [InlineData(StateWriteKind.Set, false)]
    [InlineData(StateWriteKind.Add, false)]
    [InlineData(StateWriteKind.Set, true)]
    [InlineData(StateWriteKind.Add, true)]
    [Theory]
    public void ACopyReadsItsSourceOncePerPassAndStillSkipsAbsentAndForever(StateWriteKind write, bool transaction) {
        foreach (var fact in new[] { RuleFact.Finite(
            kind: CellKind.Int,
            value: 7
        ), RuleFact.Absent(kind: CellKind.Int), RuleFact.Forever(kind: CellKind.Int) }) {
            var (host, evaluator, _, _) = Arrange(
                [],
                [Slot(
                        "target",
                        2
                    )]
            );
            var source = new CountingOperand(fact: fact);
            var effect = new WriteEffect(
                "target",
                StateRow.SlotKey.Value,
                null,
                write,
                0,
                source,
                null,
                null,
                "copy"
            );
            EffectFact submitted = (transaction
                ? new TransactionEffect(
                    describe: "atomic copy",
                    effects: [effect],
                    onFailure: []
                )
                : effect
            );

            Assert.Equal(
                (!fact.IsAbsent && !fact.IsForever),
                evaluator.FireEffects(
                    effects: [submitted],
                    ruleName: "copy",
                    stepTicks: 1,
                    tick: 1
                )
            );
            Assert.Equal(
                (transaction
                ? 1
                : 2),
                source.Reads
            );
            Assert.Equal(
                ((fact.IsAbsent || fact.IsForever)
                ? 2
                : ((write == StateWriteKind.Set)
                    ? 7
                    : 9)),
                host.Cell("target")
            );
            Assert.Empty(collection: evaluator.Diagnostics());
        }
    }
    [Fact]
    public void AForEachRuleBindsEachKeyAndLatchesPerBinding() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "decay",
                    gate: new ActionPredicate.CompareState(
                        State: "hp",
                        Comparison: ActionStateComparison.Greater,
                        Value: 0m,
                        Key: "$each"
                    ),
                    forEach: "hp",
                    effects: new ActionEffect.AddState(
                        State: "hp",
                        Value: -1m,
                        Key: "$each"
                    )
                )],
            rows: [Keyed(
                    name: "hp",
                    cells: [("7", 2L), ("9", 1L)]
                )]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            1L,
            host.Cell(
                key: "7",
                row: "hp"
            )
        );
        Assert.Equal(
            0L,
            host.Cell(
                key: "9",
                row: "hp"
            )
        );
        Assert.Equal(
            2,
            latch.Count
        );
        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 2UL,
            engineTick: 2UL
        ));
        Assert.Equal(
            0L,
            host.Cell(
                key: "7",
                row: "hp"
            )
        );
        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 3UL,
            engineTick: 3UL
        ));
        Assert.Equal(
            -1,
            evaluator.BoundEach
        );
        Assert.Null(@object: evaluator.BoundEachKey);
    }
    [Fact]
    public void AGateWhoseExpressionFaultsIsACountedRefusalNotASilentClose() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "phantom",
                    gate: new ActionPredicate.CompareValue(
                        Left: Expr(text: "primeAt(999)"),
                        Comparison: ActionStateComparison.Greater,
                        Right: Expr(text: "0"),
                        Kind: CellKind.Int
                    ),
                    effects: new ActionEffect.AddState(
                        State: "hits",
                        Value: 1m
                    )
                )],
            rows: [Slot(
                    name: "hits",
                    value: 0L
                )]
        );

        Assert.True(condition: evaluator.ArmTrace(
            evaluations: 1,
            rule: "phantom"
        ));
        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 2UL,
            engineTick: 2UL
        ));

        var diagnostic = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal<Enum>(
            RuleEffectRefusal.Arithmetic,
            diagnostic.Refusal
        );
        Assert.Equal(
            2UL,
            diagnostic.Count
        );
        Assert.Equal(
            "gate",
            diagnostic.Effect
        );
        Assert.Equal(
            "phantom",
            diagnostic.Rule
        );
        Assert.Single(collection: host.Narrated);
        Assert.Equal(
            0L,
            host.Cell(row: "hits")
        );
        var trace = evaluator.DescribeTrace(verb: "trace")!;

        Assert.Contains(
            actualString: trace,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "refused > refused -> false"
        );
        Assert.Contains(
            actualString: trace,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gate=closed"
        );
    }
    [Fact]
    public void ALevelRuleFiresEveryTickItsGateHoldsAndStopsAtTheBound() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "count",
                    gate: new ActionPredicate.CompareState(
                        State: "counter",
                        Comparison: ActionStateComparison.Less,
                        Value: 3m
                    ),
                    effects: new ActionEffect.AddState(
                        State: "counter",
                        Value: 1m
                    )
                )],
            rows: [Slot(
                    name: "counter",
                    value: 0L
                )]
        );

        var applied = 0;

        for (var tick = 1UL; (tick <= 6UL); tick++) {
            applied += (evaluator.Evaluate(
                latch: latch,
                rules: rules,
                stepTicks: 1UL,
                tick: tick,
                engineTick: tick
            )
                ? 1
                : 0
            );
        }

        Assert.Equal(
            3L,
            host.Cell(row: "counter")
        );
        Assert.Equal(
            actual: applied,
            expected: 3
        );
        Assert.Equal(
            3,
            host.Installs
        );
        Assert.Empty(collection: evaluator.Diagnostics());
        Assert.True(condition: (latch.Held(name: "count") == false));
    }
    [Fact]
    public void ARefusedTransactionRollsBackItsEarlierStepsAndFiresOnFailure() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "trade",
                    gate: null,
                    effects: new ActionEffect.Transaction(
                        Effects: [new ActionEffect.AddState(
                                State: "gold",
                                Value: 5m
                            ), new ActionEffect.SetState(
                                State: "locked",
                                Value: 1m
                            )],
                        OnFailure: [new ActionEffect.AddState(
                                State: "refunds",
                                Value: 1m
                            )]
                    )
                )],
            rows: [Slot(
                    name: "gold",
                    value: 0L
                ), Slot(
                    name: "locked",
                    value: 0L
                ), Slot(
                    name: "refunds",
                    value: 0L
                )]
        );

        Assert.True(condition: evaluator.ArmTrace(
            evaluations: 1,
            rule: "trade"
        ));
        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));

        Assert.Equal(
            0L,
            host.Cell(row: "gold")
        );
        Assert.Equal(
            0L,
            host.Cell(row: "locked")
        );
        Assert.Equal(
            1L,
            host.Cell(row: "refunds")
        );
        Assert.Equal(
            1,
            host.Installs
        );
        var diagnostic = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal<Enum>(
            RuleEffectRefusal.MutationRejected,
            diagnostic.Refusal
        );
        Assert.Contains(
            "refuses every write",
            diagnostic.Detail,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "refused (MutationRejected",
            evaluator.DescribeTrace(verb: "trace")!,
            StringComparison.Ordinal
        );
    }
    [Fact]
    public void ATopLevelWriteThatCannotMoveItsDestinationIsSkippedWithoutAJournalEntry() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "pin",
                    gate: null,
                    effects: new ActionEffect.SetState(
                        State: "flag",
                        Value: 1m
                    )
                )],
            rows: [Slot(
                    name: "flag",
                    value: 1L
                )]
        );

        Assert.True(condition: evaluator.ArmTrace(
            evaluations: 1,
            rule: "pin"
        ));
        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            0,
            host.Installs
        );
        Assert.Empty(collection: evaluator.Diagnostics());
        Assert.Contains(
            "skipped (could not move the destination)",
            evaluator.DescribeTrace(verb: "trace")!,
            StringComparison.Ordinal
        );
    }
    // A write whose exact result would leave the row's envelope (or overflow 64-bit storage on an unbounded row) is
    // refused by name, whether the cell already sits on the crossed bound or the write crosses it from inside.
    [Theory]
    [InlineData(0L, -1L, StateWriteKind.Add)]
    [InlineData(10L, 50L, StateWriteKind.Set)]
    [InlineData(2L, -5L, StateWriteKind.Add)]
    [InlineData(long.MaxValue, 1L, StateWriteKind.Add)]
    public void AWriteThatWouldLeaveTheEnvelopeIsRefusedByNameRatherThanSkippedOrSilentlyApplied(long current, long operand, StateWriteKind write) {
        var bounded = (current != long.MaxValue);
        var row = new StateRow(
            Name: CellName.Parse(candidate: "meter"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: current
                )],
            Min: (bounded ? 0L : null),
            Max: (bounded ? 10L : null)
        );
        ActionEffect effect = ((write == StateWriteKind.Add)
            ? new ActionEffect.AddState(
                State: "meter",
                Value: operand
            )
            : new ActionEffect.SetState(
                State: "meter",
                Value: operand
            )
        );
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "push",
                    gate: null,
                    effects: effect
                )],
            rows: [row]
        );

        Assert.True(condition: evaluator.ArmTrace(
            evaluations: 1,
            rule: "push"
        ));
        evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        );

        Assert.Equal(
            current,
            host.Cell(row: "meter")
        );
        Assert.Equal(
            0,
            host.Installs
        );

        var diagnostic = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal<Enum>(
            RuleEffectRefusal.MutationRejected,
            diagnostic.Refusal
        );
        Assert.Contains(
            "refused (MutationRejected",
            evaluator.DescribeTrace(verb: "trace")!,
            StringComparison.Ordinal
        );
    }
    // The mirror control: a write that stays inside the declared envelope is neither skipped nor refused — it
    // applies normally.
    [Fact]
    public void AWriteThatStaysInsideTheEnvelopeAppliesNormally() {
        var row = new StateRow(
            Name: CellName.Parse(candidate: "meter"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: 5L
                )],
            Min: 0L,
            Max: 10L
        );
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "push",
                    gate: null,
                    effects: new ActionEffect.AddState(
                        State: "meter",
                        Value: 2m
                    )
                )],
            rows: [row]
        );

        evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        );

        Assert.Equal(
            7L,
            host.Cell(row: "meter")
        );
        Assert.Equal(
            1,
            host.Installs
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    // A solitaire column: a run is legal from a card when every later card is one rank lower in the same suit. The
    // pattern's value expression reads the current token and the one before it; the word starts at the keyed card.
    [Fact]
    public void AZoneWordStartsAtTheKeyedTokenAndItsValueExpressionSeesThePreviousToken() {
        var run = new PatternRow(
            Name: CellName.Parse(candidate: "run"),
            Kind: CellKind.Int,
            Symbols: [new PatternSymbol(
                    Name: CellName.Parse(candidate: "ok"),
                    Min: 1m,
                    Max: 1m
                )],
            Pattern: new PatternNode.Sequence(Items: [new PatternNode.AnySymbol(), new PatternNode.Star(Item: new PatternNode.Symbol(Name: "ok"))]),
            Value: Expr(text: "(rank[$token] == rank[$previous] - 1) * (suit[$token] == suit[$previous])")
        );
        var rules = new Rule[] {
            R(
            name: "fromB",
            gate: new ActionPredicate.CompareState(
                State: "$match:run:column:prefix",
                Comparison: ActionStateComparison.Equal,
                Value: 2m,
                Key: "b"
            ),
            effects: new ActionEffect.SetState(
                State: "flagB",
                Value: 1m
            )
        ),
            R(
            name: "fromA",
            gate: new ActionPredicate.CompareState(
                State: "$match:run:column:prefix",
                Comparison: ActionStateComparison.Equal,
                Value: 3m,
                Key: "a"
            ),
            effects: new ActionEffect.SetState(
                State: "flagA",
                Value: 1m
            )
        ),
            R(
            name: "fromD",
            gate: new ActionPredicate.CompareState(
                State: "$match:run:column",
                Comparison: ActionStateComparison.Equal,
                Value: 1m,
                Key: "d"
            ),
            effects: new ActionEffect.SetState(
                State: "flagD",
                Value: 1m
            )
        ),
            R(
            name: "fromZ",
            gate: new ActionPredicate.CompareState(
                State: "$match:run:column",
                Comparison: ActionStateComparison.Equal,
                Value: 0m,
                Key: "zz"
            ),
            effects: new ActionEffect.SetState(
                State: "flagZ",
                Value: 1m
            )
        ),
        };

        var (host, evaluator, compiled, latch) = ArrangeWithPatterns(
            rules: rules,
            patterns: [run],
            rows: [
                Keyed(
                    name: "cards",
                    cells: [("a", 0L), ("b", 0L), ("c", 0L), ("d", 0L), ("e", 0L)]
                ),
                Keyed(
                    name: "rank",
                    cells: [("a", 9L), ("b", 8L), ("c", 7L), ("d", 6L), ("e", 5L)]
                ) with { Domain = new StateDomain.KeysOf(CellName.Parse(candidate: "cards")) },
                Keyed(
                    name: "suit",
                    cells: [("a", 0L), ("b", 0L), ("c", 0L), ("d", 1L), ("e", 1L)]
                ) with { Domain = new StateDomain.KeysOf(CellName.Parse(candidate: "cards")) },
                new StateRow(
                    Name: CellName.Parse(candidate: "column"),
                    Kind: CellKind.Bool,
                    Capacity: 8,
                    Cells: [.. "abcde".Select(selector: static c => new StateCell(Key: CellName.Parse(candidate: c.ToString())))],
                    Domain: new StateDomain.KeysOf(
                        CellName.Parse(candidate: "cards"),
                        Ordered: true
                    )
                ),
                Slot(
                    name: "flagA",
                    value: 0L
                ), Slot(
                    name: "flagB",
                    value: 0L
                ), Slot(
                    name: "flagD",
                    value: 0L
                ), Slot(
                    name: "flagZ",
                    value: 0L
                ),
            ]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: compiled,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            1L,
            host.Cell(row: "flagB")
        );
        Assert.Equal(
            1L,
            host.Cell(row: "flagA")
        );
        Assert.Equal(
            1L,
            host.Cell(row: "flagD")
        );
        Assert.Equal(
            1L,
            host.Cell(row: "flagZ")
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    [Fact]
    public void AnEdgeRuleFiresOnTheCrossingAloneAndReArmsWhenTheGateCloses() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "strike",
                    gate: new ActionPredicate.CompareState(
                        State: "armed",
                        Comparison: ActionStateComparison.Equal,
                        Value: 1m
                    ),
                    mode: ActionTriggerMode.Edge,
                    effects: new ActionEffect.AddState(
                        State: "hits",
                        Value: 1m
                    )
                )],
            rows: [Slot(
                    name: "armed",
                    value: 1L
                ), Slot(
                    name: "hits",
                    value: 0L
                )]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 2UL,
            engineTick: 2UL
        ));
        Assert.True(condition: latch.Held(name: "strike"));
        Assert.Equal(
            1L,
            host.Cell(row: "hits")
        );

        Assert.True(condition: host.TryApply(
            mutation: new StateMutation.UpsertCell(
                Row: "armed",
                Key: StateRow.SlotKey.Value,
                Value: 0L,
                Write: StateWriteKind.Set
            ),
            tick: 3UL,
            preflight: false,
            reason: out _
        ));
        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 3UL,
            engineTick: 3UL
        ));
        Assert.False(condition: latch.Held(name: "strike"));
        Assert.True(condition: host.TryApply(
            mutation: new StateMutation.UpsertCell(
                Row: "armed",
                Key: StateRow.SlotKey.Value,
                Value: 1L,
                Write: StateWriteKind.Set
            ),
            tick: 4UL,
            preflight: false,
            reason: out _
        ));
        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 4UL,
            engineTick: 4UL
        ));
        Assert.Equal(
            2L,
            host.Cell(row: "hits")
        );

        // The latch is simulation state: it flattens to a checkpoint spelling and restores from it.
        var flat = new List<(string, bool)>();

        latch.Flatten(into: flat);
        var restored = new RuleLatch();

        foreach (var (key, held) in flat) { restored.Restore(
            held: held,
            key: key
        ); }
        var a = new Fnv1aHash(); var b = new Fnv1aHash();

        latch.AppendStateHash(
            compiled: rules,
            hash: ref a
        );
        restored.AppendStateHash(
            compiled: rules,
            hash: ref b
        );
        Assert.Equal(
            a.Value,
            b.Value
        );
    }
    [Fact]
    public void AnExpressionKeyCompilesToAnImplicitBindingEvaluatedBeforeTheGate() {
        var rule = new Rule(
            Name: CellName.Parse(candidate: "step"),
            Effects: [new ActionEffect.SetState(
                    State: "next",
                    Expression: Expr(text: "board[from + 1] + board[board[(from)] - 5]")
                )],
            Gate: new ActionPredicate.CompareValue(
                Left: Expr(text: "board[from + 1]"),
                Comparison: ActionStateComparison.Equal,
                Right: Expr(text: "7"),
                Kind: CellKind.Int
            )
        );

        var (host, evaluator, rules, latch) = Arrange(
            rules: [rule],
            rows: [Keyed(
                    name: "board",
                    cells: [("2", 6L), ("3", 7L), ("1", 40L)]
                ), Slot(
                    name: "from",
                    value: 2L
                ), Slot(
                    name: "next",
                    value: 0L
                )]
        );

        Assert.Equal(
            3,
            rules[0].Bindings!.Length
        );
        Assert.Equal(
            ["$key0", "$key1", "$key2"],
            rules[0].Bindings!.Select(selector: static b => b.Name)
        );
        Assert.True(condition: evaluator.ArmTrace(
            evaluations: 1,
            rule: "step"
        ));
        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        // board[3] + board[board[2] - 5] = 47; gate and effect share their key, followed by the nested dependency and its reader.
        Assert.Equal(
            47L,
            host.Cell(row: "next")
        );
        Assert.Contains(
            "$key0=3",
            evaluator.DescribeTrace(verb: "trace")!,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "$key1=2",
            evaluator.DescribeTrace(verb: "trace")!,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "$key2=1",
            evaluator.DescribeTrace(verb: "trace")!,
            StringComparison.Ordinal
        );

        // The infix spelling round-trips: a key expression prints back as it was written.
        Assert.True(condition: ExpressionSpelling.TryParse(
            error: out _,
            text: "board[from + 1]",
            tokens: out var tokens
        ));
        Assert.Equal(
            "board[from + 1]",
            ExpressionSpelling.Print(tokens: tokens)
        );
        Assert.True(condition: ExpressionSpelling.TryParse(
            error: out _,
            text: "board[other[k]]",
            tokens: out var nested
        ));
        Assert.Equal(
            "board[other[k]]",
            ExpressionSpelling.Print(tokens: nested)
        );
        Assert.True(condition: ExpressionSpelling.TryParse(
            error: out _,
            text: "board[(from)]",
            tokens: out var read
        ));
        Assert.Equal(
            "board[(from)]",
            ExpressionSpelling.Print(tokens: read)
        );
        Assert.True(condition: ExpressionSpelling.TryParse(
            error: out _,
            text: "board[other[(k)] * 2]",
            tokens: out var deep
        ));
        Assert.Equal(
            "board[other[(k)] * 2]",
            ExpressionSpelling.Print(tokens: deep)
        );
    }
    [Fact]
    public void BindingsComputeBeforeTheGateAndReachTheEffects() {
        var rule = new Rule(
            Name: CellName.Parse(candidate: "score"),
            Effects: [new ActionEffect.SetState(
                    State: "total",
                    Expression: Expr(text: "$bind:doubled + 1")
                )],
            Gate: new ActionPredicate.CompareValue(
                Left: Expr(text: "$bind:doubled"),
                Comparison: ActionStateComparison.GreaterOrEqual,
                Right: Expr(text: "6"),
                Kind: CellKind.Int
            ),
            Bindings: [new RuleBinding(
                    Name: CellName.Parse(candidate: "doubled"),
                    Kind: CellKind.Int,
                    Expression: Expr(text: "base * 2")
                )]
        );

        var (host, evaluator, rules, latch) = Arrange(
            rules: [rule],
            rows: [Slot(
                    name: "base",
                    value: 3L
                ), Slot(
                    name: "total",
                    value: 0L
                )]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            7L,
            host.Cell(row: "total")
        );
    }
    [Fact]
    public void ClosedRulesStillEvaluateBindingsAndGatesBeforeFailing() {
        var bindingExpr = Expr(text: "counter + 5");
        var rule = new Rule(
            Name: CellName.Parse(candidate: "closed"),
            Effects: [new ActionEffect.AddState(
                    State: "target",
                    Value: 1m
                )],
            Gate: new ActionPredicate.CompareState(
                State: "counter",
                Comparison: ActionStateComparison.Equal,
                Value: 999m
            ),
            Bindings: [new RuleBinding(
                    Name: CellName.Parse(candidate: "computed"),
                    Kind: CellKind.Int,
                    Expression: bindingExpr
                )]
        );

        var (host, evaluator, rules, latch) = Arrange(
            rules: [rule],
            rows: [Slot(
                    "counter",
                    10L
                ), Slot(
                    "target",
                    0L
                )]
        );

        var applied = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        );

        // The gate evaluated to false, so the rule did not fire its effects.
        Assert.False(condition: applied);
        Assert.Equal(
            0L,
            host.Cell("target")
        );

        // However, the rule's binding expression was still evaluated during the check phase.
        Assert.Equal(
            15L,
            evaluator.BindingValue(ordinal: 0)
        );
    }
    [Fact]
    public void ForEachVisitsTextKeysWithoutReadingTheirValuesAndTracksMembership() {
        var names = new StateRow(
            CellName.Parse(candidate: "names"),
            CellKind.Text,
            Capacity: 8,
            Cells: [
            new StateCell(
                    CellName.Parse(candidate: "west"),
                    0,
                    Text: "West granary"
                ),
            new StateCell(
                    CellName.Parse(candidate: "east"),
                    0,
                    Text: "East granary"
                )]
        );
        var rule = R(
            "visit",
            null,
            forEach: "names",
            effects: new ActionEffect.AddState(
                "visits",
                Value: 1,
                Key: "$each"
            )
        );

        var (host, evaluator, rules, latch) = Arrange(
            [rule],
            [names, Keyed("visits")]
        );
        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1,
            tick: 1,
            engineTick: 1
        ));
        Assert.Equal(
            1,
            host.Cell(
                key: "west",
                row: "visits"
            )
        );
        Assert.Equal(
            1,
            host.Cell(
                key: "east",
                row: "visits"
            )
        );
        Assert.True(condition: host.TryApply(
            new StateMutation.RemoveCell(
                Key: "west",
                Row: "names"
            ),
            2,
            false,
            out _
        ));
        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1,
            tick: 2,
            engineTick: 2
        ));
        Assert.Equal(
            1,
            host.Cell(
                key: "west",
                row: "visits"
            )
        );
        Assert.Equal(
            2,
            host.Cell(
                key: "east",
                row: "visits"
            )
        );
        Assert.Empty(collection: evaluator.Diagnostics());

        var scalar = names with { Capacity = null, Cells = [new StateCell(
                StateRow.SlotKey,
                0,
                Text: "scalar"
            )] };

        Assert.Throws<RuleException>(testCode: () => Arrange(
            [rule],
            [scalar, Keyed("visits")]
        ));
        var reduction = R(
            "sum",
            null,
            effects: new ActionEffect.SetState(
                "total",
                Expression: Expr(text: "$reduce:sum:names")
            )
        );

        Assert.Throws<RuleException>(testCode: () => Arrange(
            [reduction],
            [names, Slot(
                    "total",
                    0
                )]
        ));
    }

    // A host over an in-memory row list. Its door replaces one cell per upsert and refuses every write to a row named
    // "locked", so a transaction has something to fail on; a preflight scope is a snapshot of the (immutable) row
    // list.
    private sealed class HeadlessHost : IRuleHost, IStateSection {
        private readonly Stack<(IReadOnlyList<StateRow> Rows, int Composed)> m_scopes = new();
        private readonly long[] m_patternWord = new long[PatternCapacity.MaxWord];
        private long[] m_boardScratch = new long[64];

        public List<RuleRuntimeDiagnostic> Narrated { get; } = [];
        public RuleEvaluator Evaluator { get; set; } = null!;
        public CompiledPatterns Patterns { get; set; } = CompiledPatterns.Empty;

        private int m_composed;
        private RowStore? m_store;

        public HeadlessHost(params StateRow[] rows) {
            Rows = rows;
            Catalog = StateCatalog.Compile(section: this);
        }

        IReadOnlyList<IStateSlot>? IStateSection.IdentitySlots => null;
        IReadOnlyList<LatticeTopology>? IStateSection.Lattices => null;
        IReadOnlyList<IStateSlot>? IStateSection.ParticipantSlots => null;

        public string? BoundEachKey => Evaluator.BoundEachKey;
        public string? BoundPreviousKey { get; set; }
        public string? BoundTokenKey { get; set; }
        public StateCatalog Catalog { get; }
        public int Installs { get; private set; }
        public Span<long> PatternWord => m_patternWord;
        public IReadOnlyList<StateRow> Rows { get; private set; }
        public StateStore Store => (m_store ??= new RowStore(rows: () => Rows));
        public bool TableKeyMissing { get; set; }
        public ulong Tick => Evaluator.Tick;
        public ulong EngineTick => Evaluator.EngineTick;

        private void Install(StateRow row, List<StateCell> cells, bool preflight) {
            var rows = new List<StateRow>(collection: Rows);

            rows[rows.IndexOf(item: row)] = row with { Cells = cells };
            Rows = rows;
            if (preflight) { m_composed++; } else { Installs++; }
        }

        public void BeginPreflight() => m_scopes.Push(item: (Rows, m_composed));
        public long BindingValue(int ordinal) => Evaluator.BindingValue(ordinal: ordinal);
        public Span<long> BoardScratch(int cells) {
            if (m_boardScratch.Length < cells) { m_boardScratch = new long[cells]; }
            return m_boardScratch.AsSpan(
                length: cells,
                start: 0
            );
        }
        public int BoundIndex(BoundKey key) => Evaluator.BoundIndex(key: key);
        public long Cell(string row, string? key = null) {
            Assert.True(
                condition: StateReader.TryRead(
                    rows: Rows,
                    rowName: row,
                    key: key,
                    tick: Evaluator.Tick,
                    engineTick: Evaluator.Tick,
                    row: out _,
                    rawValue: out var raw,
                    text: out _
                ),
                userMessage: row
            );
            return (raw ?? 0L);
        }
        public void EndPreflight() => (Rows, m_composed) = m_scopes.Pop();
        public EffectOutcome FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) => throw new InvalidOperationException(message: effect.Describe);
        public void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) => Narrated.Add(item: diagnostic);
        public void ReportTableKeyMissing(string table, long key) => Evaluator.ReportTableKeyMissing(
            key: key,
            table: table
        );
        public CompiledTable Table(int ordinal) => throw new InvalidOperationException();
        public bool TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) {
            reason = string.Empty;
            switch (mutation) {
                case StateMutation.UpsertCell cell when (cell.Row == "locked"):
                    reason = "row 'locked' refuses every write";
                    return false;
                case StateMutation.UpsertCell cell: {
                        var row = StateRows.FindStateRow(
                            rows: Rows,
                            name: cell.Row
                        )!;
                        var key = CellName.Parse(candidate: cell.Key);
                        var cells = new List<StateCell>(collection: (row.Cells ?? []));
                        var at = cells.FindIndex(match: c => (c.Key == key));
                        var current = ((at >= 0)
                            ? cells[at].Value
                            : 0L
                        );

                        // Mirrors the real StateFrame's admission door (StateRow.TryAdmitWrite) rather than
                        // installing the raw operand unconditionally, so a rule law exercising an out-of-range
                        // write sees the same refuse/saturate verdict production code would.
                        var admitted = current;

                        if (cell.Text is null) {
                            if (!row.TryAdmitWrite(
                                current: current,
                                operand: cell.Value,
                                reason: out reason,
                                stored: out admitted,
                                write: cell.Write
                            )) {
                                return false;
                            }
                        }

                        var next = new StateCell(
                            Key: key,
                            Value: admitted,
                            Text: cell.Text
                        );

                        if (at >= 0) { cells[at] = next; } else { cells.Add(item: next); }
                        Install(
                            cells: cells,
                            preflight: preflight,
                            row: row
                        );
                        return true;
                    }
                case StateMutation.RemoveCell cell: {
                        var row = StateRows.FindStateRow(
                            rows: Rows,
                            name: cell.Row
                        )!;
                        var key = CellName.Parse(candidate: cell.Key);
                        var cells = new List<StateCell>(collection: (row.Cells ?? []));

                        if (cells.RemoveAll(match: c => (c.Key == key)) == 0) {
                            reason = $"'{cell.Row}' holds no cell '{cell.Key}'";
                            return false;
                        }
                        Install(
                            cells: cells,
                            preflight: preflight,
                            row: row
                        );
                        return true;
                    }
                default:
                    reason = $"{mutation.GetType().Name} is not a headless mutation";
                    return false;
            }
        }
        public bool TryCommitPreflight(ulong tick, out string reason) {
            reason = string.Empty;
            var (_, before) = m_scopes.Pop();
            var installed = (m_composed > before);

            m_composed = before;
            if (installed) { Installs++; }
            return installed;
        }
        public bool TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) {
            applied = false;
            return false;
        }
    }
}
