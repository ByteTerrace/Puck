using Xunit;

namespace Puck.State.Tests;

/// <summary>Two schedule-identity laws the row-version scheduler must hold beyond the general equivalence law: a
/// binding chained to an earlier binding through <c>$bind:</c> must recompute when what the chain ultimately reads
/// changes, and a rule recompiled under the same name must never trust a cache the prior compiled instance left
/// behind.</summary>
public sealed class BindingChainScheduleLawTests {
    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<StateRow> Rows => rows;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
    }

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateRow Slot(string name, long value) =>
        new(Name: Name(name), Kind: CellKind.Int, Cells: [new StateCell(Key: StateRow.SlotKey, Value: value)]);
    private static ValueExpression Expr(string text) {
        Assert.True(condition: ExpressionSpelling.TryParse(text: text, tokens: out var tokens, error: out var error), userMessage: error);

        return new ValueExpression(Tokens: tokens);
    }
    private static ActionPredicate.CompareState CS(string state, ActionStateComparison comparison, decimal value) =>
        new(State: state, Comparison: comparison, Value: value);
    private static ActionEffect.AddState Add(string state, decimal value) => new(State: state, Value: value);
    private static RuleCompileContext NewContext(StateRow[] rows, StateCatalog catalog) =>
        new(section: new Section(rows: rows), catalog: catalog, tables: null, patterns: null, generators: null, simulationRateHz: 240, vocabulary: RuleVocabulary.Core);
    private static long Read(FrameHost host, string row) {
        Assert.True(condition: host.Frame.TryStored(row: host.Frame.Find(name: row)!, key: StateRow.SlotKey, value: out var value, text: out _));

        return value;
    }
    private static void Set(FrameHost host, string row, long value) {
        var target = host.Frame.Find(name: row)!;

        Assert.True(condition: host.Frame.TryWrite(row: target, key: StateRow.SlotKey, value: value, write: StateWriteKind.Set, reason: out var reason), userMessage: reason);
    }

    [Fact]
    public void ChainedBindingRecomputesWhenTheChainsUnderlyingRowChanges() {
        StateRow[] rows = [Slot(name: "src", value: 1L), Slot(name: "total", value: 0L)];
        var catalog = StateCatalog.Compile(section: new Section(rows: rows));
        var host = new FrameHost(new FrameLayout(rows: rows, topology: static _ => null), rows, catalog, CompiledPatterns.Empty, []);

        host.Frame.Load(source: new RowStore(rows: rows));
        host.Evaluator.SchedulingEnabled = true;

        Rule[] rules = [
            new Rule(
                Name: Name(value: "chain"),
                Effects: [new ActionEffect.SetState(State: "total", Expression: Expr(text: "$bind:doubled"))],
                Bindings: [
                    new RuleBinding(Name: Name(value: "first"), Kind: CellKind.Int, Expression: Expr(text: "src")),
                    new RuleBinding(Name: Name(value: "doubled"), Kind: CellKind.Int, Expression: Expr(text: "$bind:first * 2")),
                ]
            ),
        ];
        var compiled = RuleCompiler.CompileAll(rules: rules, context: NewContext(rows: rows, catalog: catalog));
        var latch = new RuleLatch();

        host.Evaluator.Evaluate(rules: compiled, latch: latch, tick: 1UL, stepTicks: 1UL);
        Assert.Equal(expected: 2L, actual: Read(host: host, row: "total"));

        Set(host: host, row: "src", value: 10L);
        host.Evaluator.Evaluate(rules: compiled, latch: latch, tick: 2UL, stepTicks: 1UL);
        Assert.Equal(expected: 20L, actual: Read(host: host, row: "total"));
    }

    [Fact]
    public void RecompiledRuleUnderTheSameNameIgnoresThePriorInstancesCache() {
        StateRow[] rows = [Slot(name: "rowA", value: 0L), Slot(name: "rowB", value: 5L), Slot(name: "fired", value: 0L)];
        var catalog = StateCatalog.Compile(section: new Section(rows: rows));
        var host = new FrameHost(new FrameLayout(rows: rows, topology: static _ => null), rows, catalog, CompiledPatterns.Empty, []);

        host.Frame.Load(source: new RowStore(rows: rows));
        host.Evaluator.SchedulingEnabled = true;
        var latch = new RuleLatch();

        // v1 gates on rowA (0, closed); its closed verdict caches rowA's row version.
        Rule[] v1 = [new Rule(Name: Name(value: "R"), Effects: [Add(state: "fired", value: 1m)], Gate: CS(state: "rowA", comparison: ActionStateComparison.Greater, value: 0m))];
        host.Evaluator.Evaluate(rules: RuleCompiler.CompileAll(rules: v1, context: NewContext(rows: rows, catalog: catalog)), latch: latch, tick: 1UL, stepTicks: 1UL);
        Assert.Equal(expected: 0L, actual: Read(host: host, row: "fired"));

        // v2 recompiles the same name to gate on rowB instead (already 5, so it should open and fire). rowA and
        // rowB carry the same row version (both bumped once by the same Load), so a cache keyed by name and binding
        // alone would wrongly accept v1's cached rowA version as proof v2's rowB read is unchanged.
        Rule[] v2 = [new Rule(Name: Name(value: "R"), Effects: [Add(state: "fired", value: 1m)], Gate: CS(state: "rowB", comparison: ActionStateComparison.Greater, value: 0m))];
        host.Evaluator.Evaluate(rules: RuleCompiler.CompileAll(rules: v2, context: NewContext(rows: rows, catalog: catalog)), latch: latch, tick: 2UL, stepTicks: 1UL);

        Assert.Equal(expected: 1L, actual: Read(host: host, row: "fired"));
    }
}
