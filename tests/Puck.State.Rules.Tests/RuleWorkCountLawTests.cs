using Puck.Abstractions.Counting;

using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a rule evaluator reports its work through <see cref="IWorkCounterSource"/> under the
/// <see cref="RuleWorkKinds"/> kinds: one evaluation per rule it is asked to judge, one skip per closed verdict the
/// schedule keeps without running the gate, and one firing per committed firing that ran. A Level firing that changed
/// nothing is not fired again until a row it reads or writes moves: it counts as a skip that settled, answers what
/// its firing answered, and fires again the tick another writer moves its row.</summary>
public sealed class RuleWorkCountLawTests {
    private static long Read(IWorkCounterSource source, WorkKind kind) {
        Assert.True(condition: source.TryRead(kind: kind, value: out var value), userMessage: kind.Name);

        return value;
    }

    [Fact]
    public void TheEvaluatorDeclaresItsKindsInReportOrder() {
        var (_, evaluator, _, _, _) = EvaluatorFixture.Arrange(rules: []);
        IWorkCounterSource source = evaluator;

        Assert.Equal(
            actual: source.WorkKinds.ToArray().Select(selector: static kind => kind.Name),
            expected: ["state.rules.evaluations", "state.rules.skips", "state.rules.settled", "state.rules.firings", "state.rules.armed", "state.rules.due", "state.rules.elided"]
        );
        Assert.False(condition: source.TryRead(kind: new WorkKind(name: "state.rules.other", unit: "count", workClass: WorkClass.Deterministic), value: out _));
    }
    // A rule that fires while flag reads 0 and sets it: the first tick evaluates and fires; the second finds the gate
    // closed; the third finds nothing it reads has moved and keeps the closed verdict without running the gate.
    [Fact]
    public void EvaluationsSkipsAndFiringsCountWhatTheEvaluatorDid() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [RulesFixture.Rule(
                effects: [EvaluatorFixture.Set(row: "flag", value: 1m)],
                gate: new ActionPredicate.CompareState(
                    Comparison: ExpressionOp.Equal,
                    State: "flag",
                    Value: 0m
                ),
                name: "raise"
            )]
        );
        IWorkCounterSource source = evaluator;

        void Tick(ulong tick) {
            host.Advance(
                engineTick: tick,
                tick: tick
            );
            _ = evaluator.Evaluate(
                latch: latch,
                rules: rules,
                stepTicks: 1UL
            );
        }

        Tick(tick: 1UL);
        Assert.Equal(expected: 1L, actual: Read(kind: RuleWorkKinds.Evaluations, source: source));
        Assert.Equal(expected: 1L, actual: Read(kind: RuleWorkKinds.Firings, source: source));
        Assert.Equal(expected: 0L, actual: Read(kind: RuleWorkKinds.Skips, source: source));

        Tick(tick: 2UL);
        Tick(tick: 3UL);
        Assert.Equal(expected: 3L, actual: Read(kind: RuleWorkKinds.Evaluations, source: source));
        Assert.Equal(expected: 1L, actual: Read(kind: RuleWorkKinds.Firings, source: source));
        Assert.Equal(expected: 1L, actual: Read(kind: RuleWorkKinds.Skips, source: source));
    }
    // An ungated Level rule that sets flag to 1: the first firing changes the row and records the open bit, the second
    // changes nothing and settles, and the next two are not fired. A write of flag by another writer moves a row the
    // firing writes, so the rule fires again and puts the value back, then settles again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALevelFiringThatChangedNothingIsNotFiredAgainUntilItsRowMoves(bool due) {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Name: RulesFixture.Name(value: "hold"),
                Effects: [EvaluatorFixture.Set(row: "flag", value: 1m)],
                Mode: ActionTriggerMode.Level
            )]
        );
        IWorkCounterSource source = evaluator;
        var tick = 0UL;

        RuleOutcome Tick() {
            tick++;
            host.Advance(engineTick: tick, tick: tick);
            if (due) {
                _ = evaluator.EvaluateDue(latch: latch, rules: rules, stepTicks: 1UL);

                return RuleOutcome.Fired;
            }

            return evaluator.Evaluate(applied: out _, latch: latch, rules: rules, stepTicks: 1UL);
        }

        _ = Tick();
        _ = Tick();
        Assert.Equal(expected: 2L, actual: Read(kind: RuleWorkKinds.Firings, source: source));
        Assert.Equal(expected: RuleOutcome.Fired, actual: Tick());
        _ = Tick();
        Assert.Equal(expected: 2L, actual: Read(kind: RuleWorkKinds.Firings, source: source));
        // A due list leaves a settled rule unvisited; an in-place evaluation answers for it without firing.
        Assert.Equal(expected: (due ? 0L : 2L), actual: Read(kind: RuleWorkKinds.Settled, source: source));
        Assert.Equal(expected: 1L, actual: EvaluatorFixture.Cell(host: host, row: "flag"));

        var mark = host.Arena.BeginScope();

        Assert.True(condition: host.Arena.TryWrite(
            key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            operand: 5L,
            reason: out var reason,
            rowOrdinal: EvaluatorFixture.Ordinal(host: host, row: "flag"),
            write: StateWriteKind.Set
        ), userMessage: reason);
        host.Arena.Commit(mark: mark);

        _ = Tick();
        Assert.Equal(expected: 1L, actual: EvaluatorFixture.Cell(host: host, row: "flag"));
        Assert.Equal(expected: 3L, actual: Read(kind: RuleWorkKinds.Firings, source: source));

        // The check fires every settled firing again and holds it to changing nothing.
        evaluator.QuiescenceChecked = true;
        _ = Tick();
        _ = Tick();
        _ = Tick();
        Assert.Null(@object: evaluator.StaleQuiescence);
        Assert.Equal(expected: 1L, actual: EvaluatorFixture.Cell(host: host, row: "flag"));
    }
}
