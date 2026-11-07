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

        original.Rule(name: "edge").Held.Add(key: new LatchKey(Left: 1, LeftGeneration: 7, Right: 2, RightGeneration: 9), value: true);
        original.Rule(name: "edge").Held.Add(key: new LatchKey(Left: 3, LeftGeneration: 0, Right: 4, RightGeneration: 0), value: false);
        var reversed = new RuleLatch();

        reversed.Rule(name: "edge").Held.Add(key: new LatchKey(Left: 3, LeftGeneration: 0, Right: 4, RightGeneration: 0), value: false);
        reversed.Rule(name: "edge").Held.Add(key: new LatchKey(Left: 1, LeftGeneration: 7, Right: 2, RightGeneration: 9), value: true);
        Assert.Equal(Hash(latch: original, rules: rules), Hash(latch: reversed, rules: rules));
        foreach (var key in new[] { new LatchKey(Left: 1, LeftGeneration: 8, Right: 2, RightGeneration: 9), new LatchKey(Left: 1, LeftGeneration: 7, Right: 2, RightGeneration: 10) }) {
            var changed = new RuleLatch();

            changed.Rule(name: "edge").Held.Add(key: key, value: true);
            changed.Rule(name: "edge").Held.Add(key: new LatchKey(Left: 3, LeftGeneration: 0, Right: 4, RightGeneration: 0), value: false);
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
    // A lifecycle write inside a scope is refused whole: it leaves the latch as it was, so the rewind that follows finds
    // every record it needs and restores the bit a firing wrote.
    [Fact]
    public void ALifecycleWriteInsideAScopeIsRefusedAndLeavesTheRewindIntact() {
        var (host, _, _, latch, _) = EvaluatorFixture.Arrange(rules: [Counting(
                mode: ActionTriggerMode.Edge,
                name: "cross"
            )]);
        var journal = host.Arena.Journal;
        var state = latch.Rule(name: "cross");
        var binding = new LatchKey(Left: -1, Right: -1);

        state.Held[binding] = true;

        var mark = host.Arena.BeginScope();

        latch.SetHeld(
            binding: binding,
            held: false,
            journal: journal,
            state: state
        );
        _ = Assert.Throws<InvalidOperationException>(testCode: () => latch.Restore(
            binding: binding,
            held: true,
            journal: journal,
            name: "cross"
        ));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => latch.Clear(journal: journal));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => latch.ClearRule(journal: journal, name: "cross"));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => latch.Prune(compiled: [], journal: journal));
        Assert.False(condition: state.Held[binding]);

        host.Arena.Rewind(mark: mark);

        Assert.Equal(expected: 0, actual: host.Arena.Journal.Scopes);
        Assert.True(condition: latch.Rule(name: "cross").Held[binding]);
    }
    // A pruned rule gives its ordinal back, so adding and dropping rules between ticks holds no state that nothing names.
    [Fact]
    public void PruningGivesBackTheOrdinalsOfDroppedRules() {
        var latch = new RuleLatch();
        var journal = new ArenaJournal();

        for (var cycle = 0; (cycle < 1000); cycle++) {
            _ = latch.Rule(name: "gone");
            latch.Prune(compiled: [], journal: journal);
        }

        Assert.True(condition: (latch.StateSlots <= 1));
    }
    // L1: an Edge firing that committed inside an enclosing scope is undone with that scope, latch bit and cells
    // together, so the crossing it consumed is still to be crossed.
    [Fact]
    public void ARewoundScopeRestoresTheEdgeCrossingAChildFiringConsumed() {
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

        void Step(ulong tick) {
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

        _ = arena.TryWrite(
            key: slot,
            operand: 1L,
            reason: out _,
            rowOrdinal: flag,
            write: StateWriteKind.Set
        );

        var parent = arena.BeginScope();

        Step(tick: 0UL);
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 1L
        );
        arena.Rewind(mark: parent);
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 0L
        );
        Step(tick: 1UL);
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 1L
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
        var journal = new ArenaJournal();

        foreach (var (rule, binding, held) in flattened) {
            restored.Restore(
                binding: binding,
                held: held,
                journal: journal,
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
    // The due list is the pull skip pushed: per tick, every cell and the latch hash must match the evaluation that
    // judges every rule in place, through Level and Edge rules, a sweep, a rule that never fires, and a key the host
    // removes between ticks.
    [Fact]
    public void TheDueListChangesNoObservableResult() {
        static List<string> Run(bool scheduling, bool due) {
            var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(rules: [
                new Rule(
                    Name: RulesFixture.Name(value: "arm"),
                    Effects: [EvaluatorFixture.Set(row: "flag", value: 1m)],
                    Gate: EvaluatorFixture.Compare(comparison: ExpressionOp.GreaterOrEqual, row: "other", value: 2m)
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "count"),
                    Effects: [EvaluatorFixture.Add(row: "other", value: 1m)],
                    Gate: EvaluatorFixture.Compare(comparison: ExpressionOp.Less, row: "other", value: 6m)
                ),
                Counting(mode: ActionTriggerMode.Level, name: "score"),
                new Rule(
                    Name: RulesFixture.Name(value: "edge"),
                    Effects: [EvaluatorFixture.Add(row: "third", value: 1m)],
                    Gate: EvaluatorFixture.Compare(comparison: ExpressionOp.GreaterOrEqual, row: "other", value: 3m),
                    Mode: ActionTriggerMode.Edge
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "sweep"),
                    Effects: [EvaluatorFixture.Add(row: "score", value: 10m)],
                    ForEach: "hand",
                    Gate: EvaluatorFixture.Compare(comparison: ExpressionOp.Equal, row: "third", value: 1m)
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "never"),
                    Effects: [EvaluatorFixture.Add(row: "score", value: 100m)],
                    Gate: EvaluatorFixture.Compare(comparison: ExpressionOp.Equal, row: "third", value: 99m)
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "deadline"),
                    Effects: [EvaluatorFixture.Add(row: "score", value: 1000m)],
                    Gate: new ActionPredicate.All(Predicates: [
                        EvaluatorFixture.Compare(comparison: ExpressionOp.GreaterOrEqual, row: RuleFacts.Tick, value: 4m),
                        EvaluatorFixture.Compare(comparison: ExpressionOp.Equal, row: "flag", value: 1m),
                    ])
                ),
            ]);
            var trace = new List<string>();
            var deadline = evaluator.ScheduleOf(latch: latch, rule: rules[^1]);

            Assert.NotNull(@object: deadline.Deadline);
            Assert.False(condition: deadline.Volatile);
            evaluator.SchedulingEnabled = scheduling;
            for (var tick = 0UL; (tick < 10UL); tick++) {
                if (tick == 7UL) {
                    _ = host.Arena.TryRemove(
                        key: host.Arena.Catalog.Keys.Intern(name: RulesFixture.Name(value: "b")),
                        reason: out _,
                        rowOrdinal: EvaluatorFixture.Ordinal(host: host, row: "hand")
                    );
                }
                host.Advance(engineTick: tick, tick: tick);
                _ = (due
                    ? evaluator.EvaluateDue(latch: latch, rules: rules, stepTicks: 1UL)
                    : evaluator.Evaluate(latch: latch, rules: rules, stepTicks: 1UL));
                trace.Add(item: $"{tick}: score={EvaluatorFixture.Cell(host: host, row: "score")} other={EvaluatorFixture.Cell(host: host, row: "other")} flag={EvaluatorFixture.Cell(host: host, row: "flag")} third={EvaluatorFixture.Cell(host: host, row: "third")} latch={Hash(latch: latch, rules: rules):x16} held={latch.Count}");
            }

            return trace;
        }

        var inPlace = Run(due: false, scheduling: false);

        Assert.Equal(actual: Run(due: false, scheduling: true), expected: inPlace);
        Assert.Equal(actual: Run(due: true, scheduling: true), expected: inPlace);
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
