using Puck.Abstractions.Counting;

using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a rule evaluator reports its work through <see cref="IWorkCounterSource"/> under the
/// <see cref="RuleWorkKinds"/> kinds: one evaluation per rule it is asked to judge, one skip per closed verdict the
/// schedule keeps without running the gate, and one firing per committed firing that moved the arena.</summary>
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
            expected: ["state.rules.evaluations", "state.rules.skips", "state.rules.firings"]
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
}
