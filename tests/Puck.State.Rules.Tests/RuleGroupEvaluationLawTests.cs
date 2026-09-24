using Puck.Maths;

using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a fixpoint group runs one pass per tick, closes when no row of its write set moved
/// during the pass, and breaches its ceiling into one named refusal that stops it until its trigger re-arms it; a
/// staged group advances its cursor on a committed step, stalls on a refused one, and skips past a refusal when the
/// step declares it. Progress hashes, flattens and restores beside the latch.</summary>
public sealed class RuleGroupEvaluationLawTests {
    private static (ArenaEffectHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, CompiledRuleGroup[] Groups, RuleGroupState State, RuleLatch Latch) Arrange(IReadOnlyList<Rule> rules, IReadOnlyList<RuleGroupDeclaration> groups) {
        var section = EvaluatorFixture.Section();
        var context = EvaluatorFixture.Context(section: section);
        var compiled = RuleCompiler.CompileAll(
            context: context,
            rules: rules
        );
        var compiledGroups = RuleCompiler.CompileGroups(
            context: context,
            groups: groups,
            rules: compiled
        );
        var host = EvaluatorFixture.Host(
            context: context,
            section: section
        );

        return (host, new RuleEvaluator(host: host), compiled, compiledGroups, new RuleGroupState(), new RuleLatch());
    }
    private static ulong Hash(RuleGroupState state, CompiledRuleGroup[] groups) {
        var hash = Fnv1aHash.Create();

        state.AppendStateHash(
            compiled: groups,
            hash: ref hash
        );

        return hash.Value;
    }
    // Every pass leaves `score` different from what it held when the pass began, so the group never settles.
    private static Rule Toggle() => new(
        Name: RulesFixture.Name(value: "toggle"),
        Effects: [new ActionEffect.SetState(
                Expression: RulesFixture.Program(text: "1 - score"),
                State: "score"
            )]
    );

    [Fact]
    public void AFixpointGroupClosesWhenAPassLeavesItsWriteSetUnchanged() {
        var (host, evaluator, rules, groups, state, latch) = Arrange(
            groups: [new RuleGroupDeclaration(
                    Name: RulesFixture.Name(value: "settle"),
                    Shape: RuleGroupShape.Fixpoint,
                    Steps: [new RuleGroupStep(Rule: RulesFixture.Name(value: "raise"))]
                )],
            rules: [new Rule(
                    Name: RulesFixture.Name(value: "raise"),
                    Effects: [EvaluatorFixture.Add(
                            row: "score",
                            value: 1m
                        )],
                    Gate: EvaluatorFixture.Compare(
                        comparison: ExpressionOp.Less,
                        row: "score",
                        value: 3m
                    )
                )]
        );

        for (var tick = 0UL; (tick < 5UL); tick++) {
            host.Advance(
                engineTick: tick,
                tick: tick
            );
            _ = evaluator.EvaluateGroups(
                groups: groups,
                latch: latch,
                rules: rules,
                state: state,
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
        Assert.Empty(collection: evaluator.Diagnostics());
        Assert.Equal(
            actual: state.Progress(name: "settle"),
            expected: default
        );
    }
    // One member clears the row and the next rebuilds it: every pass moves the row's version twice, and leaves it as
    // it found it, which is no progress.
    [Fact]
    public void AFixpointGroupWhoseMembersClearAndRebuildARowSettlesOnTheSecondPass() {
        var (host, evaluator, rules, groups, state, latch) = Arrange(
            groups: [new RuleGroupDeclaration(
                    Name: RulesFixture.Name(value: "rebuild"),
                    Passes: 2,
                    Shape: RuleGroupShape.Fixpoint,
                    Steps: [
                        new RuleGroupStep(Rule: RulesFixture.Name(value: "clear")),
                        new RuleGroupStep(Rule: RulesFixture.Name(value: "fill")),
                    ]
                )],
            rules: [
                new Rule(Name: RulesFixture.Name(value: "clear"), Effects: [EvaluatorFixture.Set(row: "score", value: 0m)]),
                new Rule(Name: RulesFixture.Name(value: "fill"), Effects: [EvaluatorFixture.Set(row: "score", value: 1m)]),
            ]
        );

        for (var tick = 0UL; (tick < 6UL); tick++) {
            host.Advance(engineTick: tick, tick: tick);
            _ = evaluator.EvaluateGroups(groups: groups, latch: latch, rules: rules, state: state, stepTicks: 1UL);
        }

        Assert.Empty(collection: evaluator.Diagnostics());
        Assert.Equal(actual: EvaluatorFixture.Cell(host: host, row: "score"), expected: 1L);
        Assert.Equal(actual: state.Progress(name: "rebuild"), expected: default);
    }
    [Fact]
    public void AnOscillatingTriggerlessFixpointGroupReArmsAfterEveryCeilingBreach() {
        var (host, evaluator, rules, groups, state, latch) = Arrange(
            groups: [new RuleGroupDeclaration(
                    Name: RulesFixture.Name(value: "flip"),
                    Passes: 2,
                    Shape: RuleGroupShape.Fixpoint,
                    Steps: [new RuleGroupStep(Rule: RulesFixture.Name(value: "toggle"))]
                )],
            rules: [Toggle()]
        );

        for (var tick = 0UL; (tick < 6UL); tick++) {
            host.Advance(
                engineTick: tick,
                tick: tick
            );
            _ = evaluator.EvaluateGroups(
                groups: groups,
                latch: latch,
                rules: rules,
                state: state,
                stepTicks: 1UL
            );
        }

        var refusals = evaluator.Diagnostics();

        Assert.Single(collection: refusals);
        Assert.Equal(
            actual: refusals[0].Refusal,
            expected: RuleEffectRefusal.GroupPassCeiling
        );

        // Nothing can lift a latched breach on a group with no trigger, so the breach is reported and the group
        // starts a fresh set of passes on the next tick: three breaches over six ticks at a ceiling of two.
        Assert.Equal(
            actual: refusals[0].Count,
            expected: 3UL
        );
        Assert.False(condition: state.Progress(name: "flip").Breached);
    }
    [Fact]
    public void ABreachedFixpointGroupRunsAgainOnlyAfterItsTriggerGoesFalseAndHoldsOnceMore() {
        var (host, evaluator, rules, groups, state, latch) = Arrange(
            groups: [new RuleGroupDeclaration(
                    Name: RulesFixture.Name(value: "flip"),
                    Passes: 2,
                    Shape: RuleGroupShape.Fixpoint,
                    Steps: [new RuleGroupStep(Rule: RulesFixture.Name(value: "toggle"))],
                    Trigger: EvaluatorFixture.Compare(
                        comparison: ExpressionOp.Equal,
                        row: "flag",
                        value: 0m
                    )
                )],
            rules: [Toggle()]
        );

        void Flag(long value) => Assert.True(condition: host.Arena.TryWrite(
            key: host.Arena.Catalog.Keys.Intern(name: StateRow.SlotKey),
            operand: value,
            reason: out _,
            rowOrdinal: EvaluatorFixture.Ordinal(
                host: host,
                row: "flag"
            ),
            write: StateWriteKind.Set
        ));
        void Tick(ulong tick) {
            host.Advance(
                engineTick: tick,
                tick: tick
            );
            _ = evaluator.EvaluateGroups(
                groups: groups,
                latch: latch,
                rules: rules,
                state: state,
                stepTicks: 1UL
            );
        }

        for (var tick = 0UL; (tick < 4UL); tick++) {
            Tick(tick: tick);
        }

        Assert.True(condition: state.Progress(name: "flip").Breached);
        Assert.Equal(
            actual: evaluator.Diagnostics()[0].Count,
            expected: 1UL
        );

        // A breach stops the group, so further ticks under the same armed trigger add nothing.
        Tick(tick: 4UL);
        Assert.Equal(
            actual: evaluator.Diagnostics()[0].Count,
            expected: 1UL
        );

        Flag(value: 1L);
        Tick(tick: 5UL);
        Assert.False(condition: state.Progress(name: "flip").Breached);

        Flag(value: 0L);
        for (var tick = 6UL; (tick < 10UL); tick++) {
            Tick(tick: tick);
        }

        Assert.True(condition: state.Progress(name: "flip").Breached);
        Assert.Equal(
            actual: evaluator.Diagnostics()[0].Count,
            expected: 2UL
        );
    }
    [Fact]
    public void AStagedGroupStallsOnARefusedStepAndResumesWhenItCommits() {
        var (host, evaluator, rules, groups, state, latch) = Arrange(
            groups: [new RuleGroupDeclaration(
                    Name: RulesFixture.Name(value: "flow"),
                    Shape: RuleGroupShape.Staged,
                    Steps: [
                        new RuleGroupStep(Rule: RulesFixture.Name(value: "first")),
                        new RuleGroupStep(Rule: RulesFixture.Name(value: "second")),
                        new RuleGroupStep(Rule: RulesFixture.Name(value: "third")),
                    ]
                )],
            rules: [
                new Rule(
                    Name: RulesFixture.Name(value: "first"),
                    Effects: [EvaluatorFixture.Set(
                            row: "score",
                            value: 1m
                        )]
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "second"),
                    Effects: [
                        EvaluatorFixture.Add(
                            row: "other",
                            value: 1m
                        ),
                        EvaluatorFixture.Set(
                            row: "locked",
                            value: 1m
                        ),
                    ],
                    Gate: EvaluatorFixture.Compare(
                        comparison: ExpressionOp.Equal,
                        row: "flag",
                        value: 0m
                    )
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "third"),
                    Effects: [EvaluatorFixture.Set(
                            row: "third",
                            value: 7m
                        )]
                ),
            ]
        );

        void Tick(ulong tick) {
            host.Advance(
                engineTick: tick,
                tick: tick
            );
            _ = evaluator.EvaluateGroups(
                groups: groups,
                latch: latch,
                rules: rules,
                state: state,
                stepTicks: 1UL
            );
        }

        Tick(tick: 0UL);
        Assert.Equal(
            actual: state.Progress(name: "flow").Step,
            expected: 1
        );

        Tick(tick: 1UL);
        Tick(tick: 2UL);
        // The refused step rewound its own sibling write and the cursor never moved.
        Assert.Equal(
            actual: state.Progress(name: "flow").Step,
            expected: 1
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "other"
            ),
            expected: 0L
        );

        // Closing the step's gate is not progress either: only a commit advances the cursor.
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
        Tick(tick: 3UL);
        Assert.Equal(
            actual: state.Progress(name: "flow").Step,
            expected: 1
        );
    }
    [Fact]
    public void AStagedStepDeclaringSkipAdvancesPastItsOwnRefusal() {
        var (host, evaluator, rules, groups, state, latch) = Arrange(
            groups: [new RuleGroupDeclaration(
                    Name: RulesFixture.Name(value: "flow"),
                    Shape: RuleGroupShape.Staged,
                    Steps: [
                        new RuleGroupStep(
                            OnRefusal: RuleGroupStepPolicy.Skip,
                            Rule: RulesFixture.Name(value: "blocked")
                        ),
                        new RuleGroupStep(Rule: RulesFixture.Name(value: "last")),
                    ]
                )],
            rules: [
                new Rule(
                    Name: RulesFixture.Name(value: "blocked"),
                    Effects: [EvaluatorFixture.Set(
                            row: "locked",
                            value: 1m
                        )]
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "last"),
                    Effects: [EvaluatorFixture.Set(
                            row: "score",
                            value: 5m
                        )]
                ),
            ]
        );

        for (var tick = 0UL; (tick < 2UL); tick++) {
            host.Advance(
                engineTick: tick,
                tick: tick
            );
            _ = evaluator.EvaluateGroups(
                groups: groups,
                latch: latch,
                rules: rules,
                state: state,
                stepTicks: 1UL
            );
        }

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 5L
        );
        Assert.False(condition: state.Progress(name: "flow").Running);
    }
    [Fact]
    public void GroupProgressHashesAndRoundTripsThroughFlattenAndRestore() {
        var (host, evaluator, rules, groups, state, latch) = Arrange(
            groups: [new RuleGroupDeclaration(
                    Name: RulesFixture.Name(value: "flow"),
                    Shape: RuleGroupShape.Staged,
                    Steps: [
                        new RuleGroupStep(Rule: RulesFixture.Name(value: "first")),
                        new RuleGroupStep(Rule: RulesFixture.Name(value: "second")),
                    ]
                )],
            rules: [
                new Rule(
                    Name: RulesFixture.Name(value: "first"),
                    Effects: [EvaluatorFixture.Set(
                            row: "score",
                            value: 1m
                        )]
                ),
                new Rule(
                    Name: RulesFixture.Name(value: "second"),
                    Effects: [EvaluatorFixture.Set(
                            row: "other",
                            value: 1m
                        )]
                ),
            ]
        );

        host.Advance(
            engineTick: 0UL,
            tick: 0UL
        );
        _ = evaluator.EvaluateGroups(
            groups: groups,
            latch: latch,
            rules: rules,
            state: state,
            stepTicks: 1UL
        );

        var flattened = new List<(string, RuleGroupProgress)>();

        state.Flatten(into: flattened);
        Assert.NotEmpty(collection: flattened);

        var restored = new RuleGroupState();

        foreach (var (name, progress) in flattened) {
            restored.Restore(
                name: name,
                progress: progress
            );
        }

        Assert.Equal(
            actual: Hash(
                groups: groups,
                state: restored
            ),
            expected: Hash(
                groups: groups,
                state: state
            )
        );
    }
}
