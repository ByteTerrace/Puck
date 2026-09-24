using System.Runtime.CompilerServices;

using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: after warm-up an evaluation allocates nothing — a firing's scope, its gate, its
/// bindings and its effects all run over reused scratch, whether the gate holds or closes.</summary>
public sealed class EvaluatorAllocationLawTests {
    // Warms the evaluation twice, then reads the least allocation of a 64-evaluation window.
    private static long Measure(Action action, [CallerMemberName] string name = "") {
        action();
        action();

        return AllocationWindow.Least(
            name: name,
            window: () => {
                for (var repeat = 0; (repeat < 64); repeat++) {
                    action();
                }
            }
        );
    }

    [Fact]
    public void AFiringAllocatesNothingAfterWarmUp() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [
            new Rule(
                Name: RulesFixture.Name(value: "count"),
                Effects: [EvaluatorFixture.Add(
                        row: "score",
                        value: 1m
                    )],
                Locals: [new RuleLocal(
                        Expression: RulesFixture.Program(text: "1 + 2"),
                        Kind: CellKind.Int,
                        Name: RulesFixture.Name(value: "step")
                    )],
                Gate: EvaluatorFixture.Compare(
                    comparison: ExpressionOp.GreaterOrEqual,
                    row: "other",
                    value: 0m
                )
            ),
            new Rule(
                Name: RulesFixture.Name(value: "closed"),
                Effects: [EvaluatorFixture.Add(
                        row: "other",
                        value: 1m
                    )],
                Gate: EvaluatorFixture.Compare(
                    comparison: ExpressionOp.Equal,
                    row: "flag",
                    value: 1m
                )
            ),
        ]);
        var tick = 0UL;

        var allocated = Measure(action: () => {
            host.Advance(
                engineTick: tick,
                tick: tick
            );
            tick++;
            _ = evaluator.Evaluate(
                latch: latch,
                rules: rules,
                stepTicks: 1UL
            );
        });

        Assert.Equal(
            actual: allocated,
            expected: 0L
        );
    }
    [Fact]
    public void AnIterationAllocatesNothingAfterWarmUp() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [new Rule(
                Name: RulesFixture.Name(value: "sweep"),
                Effects: [new ActionEffect.AddState(
                        Key: "$each",
                        State: "hand",
                        Value: 0m
                    )],
                ForEach: "hand"
            )]);
        var tick = 0UL;

        var allocated = Measure(action: () => {
            host.Advance(
                engineTick: tick,
                tick: tick
            );
            tick++;
            _ = evaluator.Evaluate(
                latch: latch,
                rules: rules,
                stepTicks: 1UL
            );
        });

        Assert.Equal(
            actual: allocated,
            expected: 0L
        );
    }
}
