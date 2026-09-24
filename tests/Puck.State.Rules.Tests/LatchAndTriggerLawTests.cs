using Puck.Maths;

using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: Level fires every tick its gate holds and Edge only on the crossing; an iteration
/// binds the keys the row held when the sweep opened; the latch hashes in compiled order, round-trips through
/// <c>Flatten</c>/<c>Restore</c>, and closes a binding the sweep did not touch.</summary>
public sealed class LatchAndTriggerLawTests {
    [Fact]
    public void LatchHashDistinguishesEitherInstanceGenerationAndIgnoresInsertionOrder() {
        var (_, _, rules, _, _) = EvaluatorFixture.Arrange([Counting(mode: ActionTriggerMode.Edge, name: "edge")]);
        var original = new RuleLatch();

        original.Bindings(name: "edge").Add(key: new LatchKey(Left: 1, LeftGeneration: 7, Right: 2, RightGeneration: 9), value: true);
        original.Bindings(name: "edge").Add(key: new LatchKey(Left: 3, LeftGeneration: 0, Right: 4, RightGeneration: 0), value: false);
        var reversed = new RuleLatch();

        reversed.Bindings(name: "edge").Add(key: new LatchKey(Left: 3, LeftGeneration: 0, Right: 4, RightGeneration: 0), value: false);
        reversed.Bindings(name: "edge").Add(key: new LatchKey(Left: 1, LeftGeneration: 7, Right: 2, RightGeneration: 9), value: true);
        Assert.Equal(Hash(latch: original, rules: rules), Hash(latch: reversed, rules: rules));
        foreach (var key in new[] { new LatchKey(Left: 1, LeftGeneration: 8, Right: 2, RightGeneration: 9), new LatchKey(Left: 1, LeftGeneration: 7, Right: 2, RightGeneration: 10) }) {
            var changed = new RuleLatch();

            changed.Bindings(name: "edge").Add(key: key, value: true);
            changed.Bindings(name: "edge").Add(key: new LatchKey(Left: 3, LeftGeneration: 0, Right: 4, RightGeneration: 0), value: false);
            Assert.NotEqual(Hash(latch: original, rules: rules), Hash(latch: changed, rules: rules));
        }
    }

    private static ulong Hash(RuleLatch latch, CompiledRule[] rules) {
        var hash = Fnv1aHash.Create();

        latch.AppendStateHash(
            compiled: rules,
            hash: ref hash
        );

        return hash.Value;
    }
    private static Rule Counting(string name, ActionTriggerMode mode) => new(
        Name: RulesFixture.Name(value: name),
        Effects: [EvaluatorFixture.Add(
                row: "score",
                value: 1m
            )],
        Gate: EvaluatorFixture.Compare(
            comparison: ExpressionOp.Equal,
            row: "flag",
            value: 1m
        ),
        Mode: mode
    );

    [Fact]
    public void ALevelRuleFiresEveryTickItsGateHolds() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [Counting(
                mode: ActionTriggerMode.Level,
                name: "tick"
            )]);

        _ = host.Arena.TryWrite(
            key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            operand: 1L,
            reason: out _,
            rowOrdinal: EvaluatorFixture.Ordinal(
                host: host,
                row: "flag"
            ),
            write: StateWriteKind.Set
        );

        for (var tick = 0UL; (tick < 3UL); tick++) {
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

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 3L
        );
    }
    [Fact]
    public void AnEdgeRuleFiresOnTheCrossingAndRearmsWhenTheGateCloses() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [Counting(
                mode: ActionTriggerMode.Edge,
                name: "cross"
            )]);
        var arena = host.Arena;
        var slot = arena.Catalog.Keys.Intern(name: StateRow.SlotKey);
        var flag = EvaluatorFixture.Ordinal(
            host: host,
            row: "flag"
        );

        void Step(long value, ulong tick) {
            _ = arena.TryWrite(
                key: slot,
                operand: value,
                reason: out _,
                rowOrdinal: flag,
                write: StateWriteKind.Set
            );
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

        Step(
            tick: 0UL,
            value: 1L
        );
        Step(
            tick: 1UL,
            value: 1L
        );
        Step(
            tick: 2UL,
            value: 0L
        );
        Step(
            tick: 3UL,
            value: 1L
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 2L
        );
    }
    [Fact]
    public void AnIterationBindsTheKeysTheRowHeldWhenTheSweepOpened() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [new Rule(
                Name: RulesFixture.Name(value: "sweep"),
                Effects: [
                    EvaluatorFixture.Add(
                        row: "score",
                        value: 1m
                    ),
                    new ActionEffect.SetState(
                        Key: "c",
                        State: "hand",
                        Value: 5m
                    ),
                    new ActionEffect.SetState(
                        Key: "$each",
                        State: "hand",
                        Value: 0m
                    ),
                ],
                ForEach: "hand"
            )]);

        host.Advance(
            engineTick: 0UL,
            tick: 0UL
        );
        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 2L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                key: "a",
                row: "hand"
            ),
            expected: 0L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                key: "b",
                row: "hand"
            ),
            expected: 0L
        );
        // The cell the sweep minted is not one of the keys the sweep opened over, so no iteration binds it.
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                key: "c",
                row: "hand"
            ),
            expected: 5L
        );
    }
    [Fact]
    public void TheLatchHashesInCompiledOrderAndRoundTripsThroughFlattenAndRestore() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [
            Counting(
                mode: ActionTriggerMode.Edge,
                name: "cross"
            ),
            new Rule(
                Name: RulesFixture.Name(value: "sweep"),
                Effects: [EvaluatorFixture.Add(
                        row: "other",
                        value: 1m
                    )],
                ForEach: "hand"
            ),
        ]);

        host.Advance(
            engineTick: 0UL,
            tick: 0UL
        );
        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        var flattened = new List<(string Rule, LatchKey Binding, bool Held)>();

        latch.Flatten(into: flattened);
        Assert.NotEmpty(collection: flattened);

        var restored = new RuleLatch();

        foreach (var (rule, binding, held) in flattened) {
            restored.Restore(
                binding: binding,
                held: held,
                name: rule
            );
        }

        Assert.Equal(
            actual: Hash(
                latch: restored,
                rules: rules
            ),
            expected: Hash(
                latch: latch,
                rules: rules
            )
        );
        Assert.Equal(
            actual: restored.Count,
            expected: latch.Count
        );
    }
    [Fact]
    public void ABindingTheSweepDoesNotTouchIsClosed() {
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [new Rule(
                Name: RulesFixture.Name(value: "sweep"),
                Effects: [EvaluatorFixture.Add(
                        row: "score",
                        value: 1m
                    )],
                ForEach: "hand"
            )]);

        host.Advance(
            engineTick: 0UL,
            tick: 0UL
        );
        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );
        Assert.Equal(
            actual: latch.Count,
            expected: 2
        );

        _ = host.Arena.TryRemove(
            key: host.Arena.Catalog.Keys.Intern(name: RulesFixture.Name(value: "b")),
            reason: out _,
            rowOrdinal: EvaluatorFixture.Ordinal(
                host: host,
                row: "hand"
            )
        );
        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );
        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: latch.Count,
            expected: 1
        );
    }
    [Fact]
    public void SchedulingChangesNoObservableResult() {
        static long Run(bool scheduling) {
            var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [
                new Rule(
                    Name: RulesFixture.Name(value: "arm"),
                    Effects: [EvaluatorFixture.Set(
                            row: "flag",
                            value: 1m
                        )],
                    Gate: EvaluatorFixture.Compare(
                        comparison: ExpressionOp.GreaterOrEqual,
                        row: "other",
                        value: 2m
                    )
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "count"),
                    Effects: [EvaluatorFixture.Add(
                            row: "other",
                            value: 1m
                        )]
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "score"),
                    Effects: [EvaluatorFixture.Add(
                            row: "score",
                            value: 1m
                        )],
                    Gate: EvaluatorFixture.Compare(
                        comparison: ExpressionOp.Equal,
                        row: "flag",
                        value: 1m
                    )
                ),
            ]);

            evaluator.SchedulingEnabled = scheduling;

            for (var tick = 0UL; (tick < 5UL); tick++) {
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

            return EvaluatorFixture.Cell(
                host: host,
                row: "score"
            );
        }

        Assert.Equal(
            actual: Run(scheduling: false),
            expected: Run(scheduling: true)
        );
    }
}
