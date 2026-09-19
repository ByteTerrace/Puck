using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: the age of a <c>$history:</c> read is a whole number bounded by the ring when the
/// rule compiles, or an int expression read fresh at every evaluation whose result outside the ring reads the ring's
/// empty value rather than refusing.</summary>
public sealed class HistoryAgeLawTests {
    private static long Recalled(decimal age) {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [new Rule(
                Name: RulesFixture.Name(value: "recall"),
                Effects: [
                    EvaluatorFixture.Set(
                        row: "other",
                        value: age
                    ),
                    new ActionEffect.PushState(
                        State: "log",
                        Value: 5m
                    ),
                    new ActionEffect.PushState(
                        State: "log",
                        Value: 6m
                    ),
                    new ActionEffect.PushState(
                        State: "log",
                        Value: 7m
                    ),
                    new ActionEffect.SetState(
                        FromState: "$history:log:other",
                        State: "score"
                    ),
                ],
                Mode: ActionTriggerMode.Level
            )]);

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));

        return EvaluatorFixture.Cell(
            host: host,
            row: "score"
        );
    }

    [Fact]
    public void AHistoryAgeSpelledAsAnExpressionIsReadFreshAtEveryEvaluation() {
        Assert.Equal(
            actual: Recalled(age: 0m),
            expected: 7L
        );
        Assert.Equal(
            actual: Recalled(age: 2m),
            expected: 5L
        );
    }
    [Fact]
    public void AHistoryAgeExpressionOutsideTheRingReadsItsEmptyValue() => Assert.Equal(
        actual: Recalled(age: 9m),
        expected: -1L
    );
}
