using Xunit;
using Puck.Maths;

namespace Puck.State.Tests;

/// <summary>The evaluator runs authoritative rules over a bare row list: a host with no world, no bodies, and no
/// document — the card game or turn-based resolver the library exists for.</summary>
public sealed class RuleEvaluatorLawTests {
    // A host over an in-memory row list. Its door replaces one cell per upsert and refuses every write to a row named
    // "locked", so a transaction has something to fail on; a preflight scope is a snapshot of the (immutable) row
    // list.
    private sealed class HeadlessHost : IRuleHost, IStateSection {
        private readonly Stack<IReadOnlyList<StateRow>> m_scopes = new();
        private readonly long[] m_patternWord = new long[PatternCapacity.MaxWord];
        private long[] m_boardScratch = new long[64];

        public HeadlessHost(params StateRow[] rows) {
            Rows = rows;
            Catalog = StateCatalog.Compile(section: this);
        }

        public IReadOnlyList<StateRow> Rows { get; private set; }
        public List<RuleRuntimeDiagnostic> Narrated { get; } = [];
        public RuleEvaluator Evaluator { get; set; } = null!;
        public int Installs { get; private set; }

        IReadOnlyList<LatticeTopology>? IStateSection.Lattices => null;
        IReadOnlyList<IStateSlot>? IStateSection.ParticipantSlots => null;
        IReadOnlyList<IStateSlot>? IStateSection.IdentitySlots => null;

        public ulong Tick => Evaluator.Tick;
        public StateCatalog Catalog { get; }
        public CompiledPatterns Patterns => CompiledPatterns.Empty;
        public string? BoundEachKey => Evaluator.BoundEachKey;
        public string? BoundTokenKey { get; set; }
        public bool TableKeyMissing { get; set; }
        public Span<long> PatternWord => m_patternWord;
        public int BoundIndex(BoundKey key) => Evaluator.BoundIndex(key: key);
        public long BindingValue(int ordinal) => Evaluator.BindingValue(ordinal: ordinal);
        public CompiledTable Table(int ordinal) => throw new InvalidOperationException();
        public void ReportTableKeyMissing(string table, long key) => Evaluator.ReportTableKeyMissing(table: table, key: key);
        public Span<long> BoardScratch(int cells) {
            if (m_boardScratch.Length < cells) { m_boardScratch = new long[cells]; }
            return m_boardScratch.AsSpan(start: 0, length: cells);
        }

        public long Cell(string row, string? key = null) {
            Assert.True(StateReader.TryRead(rows: Rows, rowName: row, key: key, tick: Evaluator.Tick, row: out _, rawValue: out var raw, text: out _), row);
            return (raw ?? 0L);
        }

        public bool TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) {
            reason = string.Empty;
            switch (mutation) {
                case StateMutation.UpsertCell cell when (cell.Row == "locked"):
                    reason = "row 'locked' refuses every write";
                    return false;
                case StateMutation.UpsertCell cell: {
                    var row = StateRows.FindStateRow(rows: Rows, name: cell.Row)!;
                    var key = CellName.Parse(candidate: cell.Key);
                    var cells = new List<StateCell>(collection: (row.Cells ?? []));
                    var at = cells.FindIndex(match: c => c.Key == key);
                    var current = ((at >= 0) ? cells[at].Value : 0L);
                    var next = new StateCell(Key: key, Value: ((cell.Write == StateWriteKind.Add) ? (current + cell.Value) : cell.Value), Text: cell.Text);
                    if (at >= 0) { cells[at] = next; } else { cells.Add(item: next); }
                    Install(row: row, cells: cells, preflight: preflight);
                    return true;
                }
                case StateMutation.RemoveCell cell: {
                    var row = StateRows.FindStateRow(rows: Rows, name: cell.Row)!;
                    var key = CellName.Parse(candidate: cell.Key);
                    var cells = new List<StateCell>(collection: (row.Cells ?? []));
                    if (cells.RemoveAll(match: c => c.Key == key) == 0) {
                        reason = $"'{cell.Row}' holds no cell '{cell.Key}'";
                        return false;
                    }
                    Install(row: row, cells: cells, preflight: preflight);
                    return true;
                }
                default:
                    reason = $"{mutation.GetType().Name} is not a headless mutation";
                    return false;
            }
        }
        private void Install(StateRow row, List<StateCell> cells, bool preflight) {
            var rows = new List<StateRow>(collection: Rows);
            rows[rows.IndexOf(item: row)] = row with { Cells = cells };
            Rows = rows;
            if (!preflight) { Installs++; }
        }
        public void BeginPreflight() => m_scopes.Push(item: Rows);
        public void EndPreflight() => Rows = m_scopes.Pop();
        public EffectOutcome FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) => throw new InvalidOperationException(effect.Describe);
        public bool TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) {
            applied = false;
            return false;
        }
        public void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) => Narrated.Add(item: diagnostic);
    }

    private static StateRow Slot(string name, long value, CellKind kind = CellKind.Int) =>
        new(Name: CellName.Parse(candidate: name), Kind: kind, Cells: [new StateCell(Key: StateRow.SlotKey, Value: value)]);
    private static StateRow Keyed(string name, params (string Key, long Value)[] cells) =>
        new(Name: CellName.Parse(candidate: name), Kind: CellKind.Int, Capacity: 8, Cells: [.. cells.Select(static c => new StateCell(Key: CellName.Parse(candidate: c.Key), Value: c.Value))]);
    private static ValueExpression Expr(string text) {
        Assert.True(ExpressionSpelling.TryParse(text: text, tokens: out var tokens, error: out var error), error);
        return new ValueExpression(Tokens: tokens);
    }
    private static (HeadlessHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, RuleLatch Latch) Arrange(IReadOnlyList<Rule> rules, StateRow[] rows) {
        var host = new HeadlessHost(rows);
        var evaluator = new RuleEvaluator(host: host);
        host.Evaluator = evaluator;
        var context = new RuleCompileContext(section: host, catalog: host.Catalog, tables: null, patterns: null, generators: null, simulationRateHz: 240, vocabulary: RuleVocabulary.Core);
        return (host, evaluator, RuleCompiler.CompileAll(rules: rules, context: context), new RuleLatch());
    }
    private static Rule R(string name, ActionPredicate? gate, ActionTriggerMode mode = ActionTriggerMode.Level, string? forEach = null, params ActionEffect[] effects) =>
        new(Name: CellName.Parse(candidate: name), Effects: effects, Gate: gate, Mode: mode, ForEach: forEach);

    [Fact]
    public void ALevelRuleFiresEveryTickItsGateHoldsAndStopsAtTheBound() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(name: "count", gate: new ActionPredicate.CompareState(State: "counter", Comparison: ActionStateComparison.Less, Value: 3m), effects: new ActionEffect.AddState(State: "counter", Value: 1m))],
            rows: [Slot(name: "counter", value: 0L)]
        );

        var applied = 0;
        for (var tick = 1UL; tick <= 6UL; tick++) {
            applied += (evaluator.Evaluate(rules: rules, latch: latch, tick: tick, stepTicks: 1UL) ? 1 : 0);
        }

        Assert.Equal(3L, host.Cell(row: "counter"));
        Assert.Equal(3, applied);
        Assert.Equal(3, host.Installs);
        Assert.Empty(evaluator.Diagnostics());
        Assert.True(latch.Held(name: "count") == false);
    }

    [Fact]
    public void AnEdgeRuleFiresOnTheCrossingAloneAndReArmsWhenTheGateCloses() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(name: "strike", gate: new ActionPredicate.CompareState(State: "armed", Comparison: ActionStateComparison.Equal, Value: 1m), mode: ActionTriggerMode.Edge, effects: new ActionEffect.AddState(State: "hits", Value: 1m))],
            rows: [Slot(name: "armed", value: 1L), Slot(name: "hits", value: 0L)]
        );

        Assert.True(evaluator.Evaluate(rules: rules, latch: latch, tick: 1UL, stepTicks: 1UL));
        Assert.False(evaluator.Evaluate(rules: rules, latch: latch, tick: 2UL, stepTicks: 1UL));
        Assert.True(latch.Held(name: "strike"));
        Assert.Equal(1L, host.Cell(row: "hits"));

        Assert.True(host.TryApply(mutation: new StateMutation.UpsertCell(Row: "armed", Key: StateRow.SlotKey.Value, Value: 0L, Write: StateWriteKind.Set), tick: 3UL, preflight: false, reason: out _));
        Assert.False(evaluator.Evaluate(rules: rules, latch: latch, tick: 3UL, stepTicks: 1UL));
        Assert.False(latch.Held(name: "strike"));
        Assert.True(host.TryApply(mutation: new StateMutation.UpsertCell(Row: "armed", Key: StateRow.SlotKey.Value, Value: 1L, Write: StateWriteKind.Set), tick: 4UL, preflight: false, reason: out _));
        Assert.True(evaluator.Evaluate(rules: rules, latch: latch, tick: 4UL, stepTicks: 1UL));
        Assert.Equal(2L, host.Cell(row: "hits"));

        // The latch is simulation state: it flattens to a checkpoint spelling and restores from it.
        var flat = new List<(string, bool)>();
        latch.Flatten(into: flat);
        var restored = new RuleLatch();
        foreach (var (key, held) in flat) { restored.Restore(key: key, held: held); }
        var a = new Fnv1aHash(); var b = new Fnv1aHash();
        latch.AppendStateHash(hash: ref a, compiled: rules);
        restored.AppendStateHash(hash: ref b, compiled: rules);
        Assert.Equal(a.Value, b.Value);
    }

    [Fact]
    public void AForEachRuleBindsEachKeyAndLatchesPerBinding() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(name: "decay", gate: new ActionPredicate.CompareState(State: "hp", Comparison: ActionStateComparison.Greater, Value: 0m, Key: "$each"), forEach: "hp", effects: new ActionEffect.AddState(State: "hp", Value: -1m, Key: "$each"))],
            rows: [Keyed(name: "hp", cells: [("7", 2L), ("9", 1L)])]
        );

        Assert.True(evaluator.Evaluate(rules: rules, latch: latch, tick: 1UL, stepTicks: 1UL));
        Assert.Equal(1L, host.Cell(row: "hp", key: "7"));
        Assert.Equal(0L, host.Cell(row: "hp", key: "9"));
        Assert.Equal(2, latch.Count);
        Assert.True(evaluator.Evaluate(rules: rules, latch: latch, tick: 2UL, stepTicks: 1UL));
        Assert.Equal(0L, host.Cell(row: "hp", key: "7"));
        Assert.False(evaluator.Evaluate(rules: rules, latch: latch, tick: 3UL, stepTicks: 1UL));
        Assert.Equal(-1, evaluator.BoundEach);
        Assert.Null(evaluator.BoundEachKey);
    }

    [Fact]
    public void AGateWhoseExpressionFaultsIsACountedRefusalNotASilentClose() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(name: "phantom", gate: new ActionPredicate.CompareValue(Left: Expr(text: "prime(999)"), Comparison: ActionStateComparison.Greater, Right: Expr(text: "0"), Kind: CellKind.Int), effects: new ActionEffect.AddState(State: "hits", Value: 1m))],
            rows: [Slot(name: "hits", value: 0L)]
        );

        Assert.True(evaluator.ArmTrace(rule: "phantom", evaluations: 1));
        Assert.False(evaluator.Evaluate(rules: rules, latch: latch, tick: 1UL, stepTicks: 1UL));
        Assert.False(evaluator.Evaluate(rules: rules, latch: latch, tick: 2UL, stepTicks: 1UL));

        var diagnostic = Assert.Single(evaluator.Diagnostics());
        Assert.Equal<Enum>(RuleEffectRefusal.Arithmetic, diagnostic.Refusal);
        Assert.Equal(2UL, diagnostic.Count);
        Assert.Equal("gate", diagnostic.Effect);
        Assert.Equal("phantom", diagnostic.Rule);
        Assert.Single(host.Narrated);
        Assert.Equal(0L, host.Cell(row: "hits"));
        var trace = evaluator.DescribeTrace(verb: "trace")!;
        Assert.Contains("refused > refused -> false", trace, StringComparison.Ordinal);
        Assert.Contains("gate=closed", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedTransactionRollsBackItsEarlierStepsAndFiresOnFailure() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(name: "trade", gate: null, effects: new ActionEffect.Transaction(
                Effects: [new TransactionStep.AddCell(State: "gold", Value: 5m), new TransactionStep.SetCell(State: "locked", Value: 1m)],
                OnFailure: [new TransactionStep.AddCell(State: "refunds", Value: 1m)]
            ))],
            rows: [Slot(name: "gold", value: 0L), Slot(name: "locked", value: 0L), Slot(name: "refunds", value: 0L)]
        );

        Assert.True(evaluator.ArmTrace(rule: "trade", evaluations: 1));
        Assert.True(evaluator.Evaluate(rules: rules, latch: latch, tick: 1UL, stepTicks: 1UL));

        Assert.Equal(0L, host.Cell(row: "gold"));
        Assert.Equal(0L, host.Cell(row: "locked"));
        Assert.Equal(1L, host.Cell(row: "refunds"));
        Assert.Equal(1, host.Installs);
        var diagnostic = Assert.Single(evaluator.Diagnostics());
        Assert.Equal<Enum>(RuleEffectRefusal.MutationRejected, diagnostic.Refusal);
        Assert.Contains("refuses every write", diagnostic.Detail, StringComparison.Ordinal);
        Assert.Contains("refused (MutationRejected", evaluator.DescribeTrace(verb: "trace")!, StringComparison.Ordinal);
    }

    [Fact]
    public void ATopLevelWriteThatCannotMoveItsDestinationIsSkippedWithoutAJournalEntry() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(name: "pin", gate: null, effects: new ActionEffect.SetState(State: "flag", Value: 1m))],
            rows: [Slot(name: "flag", value: 1L)]
        );

        Assert.True(evaluator.ArmTrace(rule: "pin", evaluations: 1));
        Assert.False(evaluator.Evaluate(rules: rules, latch: latch, tick: 1UL, stepTicks: 1UL));
        Assert.Equal(0, host.Installs);
        Assert.Empty(evaluator.Diagnostics());
        Assert.Contains("skipped (could not move the destination)", evaluator.DescribeTrace(verb: "trace")!, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingsComputeBeforeTheGateAndReachTheEffects() {
        var rule = new Rule(
            Name: CellName.Parse(candidate: "score"),
            Effects: [new ActionEffect.SetState(State: "total", Expression: Expr(text: "$bind:doubled + 1"))],
            Gate: new ActionPredicate.CompareValue(Left: Expr(text: "$bind:doubled"), Comparison: ActionStateComparison.GreaterOrEqual, Right: Expr(text: "6"), Kind: CellKind.Int),
            Bindings: [new RuleBinding(Name: CellName.Parse(candidate: "doubled"), Kind: CellKind.Int, Expression: Expr(text: "base * 2"))]
        );
        var (host, evaluator, rules, latch) = Arrange(rules: [rule], rows: [Slot(name: "base", value: 3L), Slot(name: "total", value: 0L)]);

        Assert.True(evaluator.Evaluate(rules: rules, latch: latch, tick: 1UL, stepTicks: 1UL));
        Assert.Equal(7L, host.Cell(row: "total"));
    }
}
