using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a sweep bounded by <c>maxFirings</c> stops once that many bindings have fired, leaves
/// every binding after it exactly as it stood (an open edge stays held, a closed one waits), and is priced at that
/// many firings; only an iterating rule may declare it.</summary>
public sealed class MaxFiringsLawTests {
    private static Rule Sweep(ActionTriggerMode mode, int? maxFirings) => new(
        Name: RulesFixture.Name(value: "sweep"),
        Effects: [EvaluatorFixture.Add(row: "score", value: 1m)],
        Gate: new ActionPredicate.CompareState(Comparison: ExpressionOp.GreaterOrEqual, Key: "$each", State: "hand", Value: 1m),
        Mode: mode,
        ForEach: "hand",
        MaxFirings: maxFirings
    );

    // Keys a and b both open: a capped sweep fires a, then b on the next tick, and an edge held on b survives a
    // tick whose firing of a ends the sweep before b is reached.
    [Fact]
    public void ASweepStopsAtItsFiringsAndLeavesTheRestAsTheyStood() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [Sweep(mode: ActionTriggerMode.Edge, maxFirings: 1)]);
        var hand = EvaluatorFixture.Ordinal(host: host, row: "hand");
        var a = host.Arena.Catalog.Keys.Intern(name: RulesFixture.Name(value: "a"));

        void Step(ulong tick, long? first = null) {
            if (first is { } value) {
                Assert.True(condition: host.Arena.TryWrite(key: a, operand: value, reason: out var reason, rowOrdinal: hand, write: StateWriteKind.Set), userMessage: reason);
            }
            host.Advance(engineTick: tick, tick: tick);
            _ = evaluator.Evaluate(latch: latch, rules: rules, stepTicks: 1UL);
        }

        Step(tick: 0UL);
        Assert.Equal(expected: 1L, actual: EvaluatorFixture.Cell(host: host, row: "score"));
        Step(tick: 1UL);
        Assert.Equal(expected: 2L, actual: EvaluatorFixture.Cell(host: host, row: "score"));
        Step(tick: 2UL, first: 0L);
        Step(tick: 3UL, first: 1L);
        Assert.Equal(expected: 3L, actual: EvaluatorFixture.Cell(host: host, row: "score"));
        // b's edge stayed held through the tick that never reached it, so it does not fire again.
        Step(tick: 4UL);
        Assert.Equal(expected: 3L, actual: EvaluatorFixture.Cell(host: host, row: "score"));
    }
    [Fact]
    public void ACappedSweepIsPricedAtItsFiringsAndCountsThatManyWriters() {
        var (_, _, rules, _, context) = EvaluatorFixture.Arrange(rules: [Sweep(mode: ActionTriggerMode.Level, maxFirings: 1)]);
        var (_, _, uncapped, _, _) = EvaluatorFixture.Arrange(rules: [Sweep(mode: ActionTriggerMode.Level, maxFirings: null)]);
        var line = RuleWorkBudget.Contributor(context: context, isInteraction: false, multiplier: 8L, rule: rules[0]);
        var full = RuleWorkBudget.Contributor(context: context, isInteraction: false, multiplier: 8L, rule: uncapped[0]);

        Assert.Equal(expected: 8L, actual: line.Multiplier);
        Assert.Equal(expected: 1L, actual: line.Firings);
        Assert.Equal(expected: line.CheckUnits, actual: full.CheckUnits);
        Assert.Equal(expected: (1L * line.Cost.Effects), actual: line.FiringUnits);
        Assert.Equal(expected: (8L * full.Cost.Effects), actual: full.FiringUnits);
        Assert.Equal(expected: 1L, actual: Assert.Single(collection: RuleWorkBudget.CountWriters(rules: [(rules[0], 8L)])).Value);
    }
    // A pool sweep is priced at the pool's capacity, and its bound defers the instances past it to the next tick.
    [Fact]
    public void APoolSweepIsPricedAtItsCapacityAndStopsAtItsFirings() {
        var section = new StateSection(
            Rows: [EvaluatorFixture.Slot(name: "score", value: 0L)],
            Records: [new StateRecord(Name: RulesFixture.Name(value: "piece"), Fields: [
                new StatePoolField(Name: RulesFixture.Name(value: "count"), Kind: CellKind.Int, Default: CellValue.Int(value: 0L)),
            ])],
            Pools: [new StatePool(Name: RulesFixture.Name(value: "pieces"), Record: RulesFixture.Name(value: "piece"), Capacity: 4)]
        );
        var (host, evaluator, rules, latch, context) = EvaluatorFixture.Arrange(section: section, rules: [new Rule(
            Name: RulesFixture.Name(value: "visit"),
            Effects: [EvaluatorFixture.Add(row: "score", value: 1m)],
            Mode: ActionTriggerMode.Edge,
            PoolForEach: new RulePoolIteration(Pool: "pieces", Binding: RulesFixture.Name(value: "piece")),
            MaxFirings: 2
        )]);

        for (var index = 0; (index < 3); index++) {
            Assert.True(condition: host.Arena.TryClaim(poolOrdinal: host.Arena.Catalog.Pools[0].Ordinal, handle: out _, reason: out var reason), userMessage: reason);
        }
        Assert.Equal(expected: 4L, actual: RuleWorkBudget.ForEachCount(context: context, rule: rules[0]));
        for (var tick = 0UL; (tick < 3UL); tick++) {
            host.Advance(engineTick: tick, tick: tick);
            _ = evaluator.Evaluate(latch: latch, rules: rules, stepTicks: 1UL);
            Assert.Equal(expected: ((tick == 0UL) ? 2L : 3L), actual: EvaluatorFixture.Cell(host: host, row: "score"));
        }
    }
    [Fact]
    public void OnlyAnIteratingRuleTakesAPositiveBound() {
        var once = new Rule(Name: RulesFixture.Name(value: "once"), Effects: [EvaluatorFixture.Add(row: "score", value: 1m)], MaxFirings: 1);

        Assert.Contains(expectedSubstring: "maxFirings", actualString: Assert.Throws<RuleException>(testCode: () => EvaluatorFixture.Arrange(rules: [once])).Message);
        Assert.Contains(expectedSubstring: "at least one firing", actualString: Assert.Throws<RuleException>(testCode: () => EvaluatorFixture.Arrange(rules: [Sweep(mode: ActionTriggerMode.Level, maxFirings: 0)])).Message);
    }
}
