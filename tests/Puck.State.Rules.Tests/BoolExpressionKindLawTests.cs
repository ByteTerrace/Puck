using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a Bool cell and an Int expression meet in both directions — an expression computed
/// in Int lands in a Bool cell as 0 for zero and 1 for anything else, and a Bool cell read inside an Int expression
/// yields the 0 or 1 it stores.</summary>
public sealed class BoolExpressionKindLawTests {
    private static bool Armed(decimal score) {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [new Rule(
                Name: RulesFixture.Name(value: "arm"),
                Effects: [
                    EvaluatorFixture.Set(
                        row: "score",
                        value: score
                    ),
                    new ActionEffect.SetState(
                        Expression: RulesFixture.Program(text: "score > 1"),
                        State: "ready"
                    ),
                ],
                Mode: ActionTriggerMode.Level
            )]);
        var arena = host.Arena;

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Empty(collection: evaluator.Diagnostics());
        Assert.True(condition: arena.TryRead(
            key: arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            rowOrdinal: EvaluatorFixture.Ordinal(
                host: host,
                row: "ready"
            ),
            value: out var value
        ));

        return value.AsBool;
    }
    private static long Counted(decimal ready) {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [new Rule(
                Name: RulesFixture.Name(value: "count"),
                Effects: [
                    new ActionEffect.SetState(
                        State: "ready",
                        Value: ready
                    ),
                    new ActionEffect.SetState(
                        Expression: RulesFixture.Program(text: "ready + 1"),
                        State: "score"
                    ),
                ],
                Mode: ActionTriggerMode.Level
            )]);

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Empty(collection: evaluator.Diagnostics());

        return EvaluatorFixture.Cell(
            host: host,
            row: "score"
        );
    }

    [Fact]
    public void AComparisonsIntLandsInABoolCellAsZeroOrOne() {
        Assert.True(condition: Armed(score: 5m));
        Assert.False(condition: Armed(score: 0m));
    }
    [Fact]
    public void ABoolCellReadIntoAnIntExpressionYieldsZeroOrOne() {
        Assert.Equal(
            actual: Counted(ready: 1m),
            expected: 2L
        );
        Assert.Equal(
            actual: Counted(ready: 0m),
            expected: 1L
        );
    }
}
