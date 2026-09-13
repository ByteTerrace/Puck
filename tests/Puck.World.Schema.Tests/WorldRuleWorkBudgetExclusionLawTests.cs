using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>All rule checks sum; distinct discriminator values may share firing costs only.</summary>
public sealed class WorldRuleWorkBudgetExclusionLawTests {
    private static WorldRule Advance(string name, ActionPredicate? gate) =>
        new(
            CellName.Parse(candidate: name),
            [new ActionEffect.SetState(
                    State: "phase",
                    Value: 2m
                )],
            Gate: gate
        );
    private static ActionPredicate Both(long phase, long sub) => new ActionPredicate.All(Predicates: [PhaseIs(value: phase), SubIs(value: sub)]);
    private static WorldDefinition Document(params WorldRule[] rules) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [Slot(name: "phase"), Slot(name: "sub"), Slot(name: "count"), Keyed(
                capacity: 64,
                name: "many"
            )]),
        Rules: rules
    );
    private static ActionPredicate Hp(ActionStateComparison comparison, long value) =>
        new ActionPredicate.CompareState(
            State: "sub",
            Comparison: comparison,
            Value: value
        );
    private static WorldStateRow Keyed(string name, int capacity) =>
        new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Capacity: capacity,
            Cells: [new StateCell(
                    CellName.Parse(candidate: "0"),
                    0L
                )]
        );
    private static ActionPredicate PhaseIs(long value) =>
        new ActionPredicate.CompareState(
            State: "phase",
            Comparison: ActionStateComparison.Equal,
            Value: value
        );
    private static WorldRule Rule(string name, ActionPredicate? gate, string forEach = "many") =>
        new(
            CellName.Parse(candidate: name),
            [new ActionEffect.AddState(
                    State: "count",
                    Value: 1m
                )],
            Gate: gate,
            ForEach: forEach
        );
    private static WorldStateRow Slot(string name) =>
        new(
            CellName.Parse(candidate: name),
            CellKind.Int,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    0L
                )]
        );
    private static ActionPredicate SubIs(long value) =>
        new ActionPredicate.CompareState(
            State: "sub",
            Comparison: ActionStateComparison.Equal,
            Value: value
        );
    private static long Work(params WorldRule[] rules) => WorldRuleWorkBudget.Measure(definition: Document(rules)).WorkUnitsPerTick;

    [Fact]
    public void AFurtherPinnedCellNestsUnderTheFirstSoSubphasesAreExclusiveToo() {
        var ruleA = Rule(
            "a",
            Both(
                phase: 1,
                sub: 0
            )
        );
        var ruleB = Rule(
            "b",
            Both(
                phase: 1,
                sub: 1
            )
        );
        var lineA = WorldRuleWorkBudget.Contributors(definition: Document(ruleA)).Single();
        var checkA = lineA.CheckUnits;
        var firingA = lineA.FiringUnits;

        var rulePhaseOnly = Rule(
            "phase",
            PhaseIs(value: 1)
        );
        var linePhaseOnly = WorldRuleWorkBudget.Contributors(definition: Document(rulePhaseOnly)).Single();
        var checkPhaseOnly = linePhaseOnly.CheckUnits;
        var firingPhaseOnly = linePhaseOnly.FiringUnits;

        // Same phase, distinct subphases: checks sum, firing takes the costliest subphase.
        Assert.Equal(
            ((2 * checkA) + firingA),
            Work(
                ruleA,
                ruleB
            )
        );
        // Spelling order does not matter: "sub then phase" shares the node.
        Assert.Equal(
            ((2 * checkA) + firingA),
            Work(
                ruleA,
                Rule(
                    "b",
                    new ActionPredicate.All(Predicates: [SubIs(value: 1), PhaseIs(value: 1)])
                )
            )
        );
        // A rule pinning only the phase sums with the deeper ones under it:
        // checks sum across all 3 rules; firing carries phaseOnly plus the costliest subphase.
        Assert.Equal(
            (((checkPhaseOnly + (2 * checkA)) + firingPhaseOnly) + firingA),
            Work(
                rulePhaseOnly,
                ruleA,
                ruleB
            )
        );
        // Distinct phases stay exclusive across their whole subtrees:
        // checks sum across all 4 rules (phaseOnly + 3 two-predicate rules).
        // Phase 1 carries firingPhaseOnly + firingA; phase 2 carries firingA. Their maximum is phase 1.
        var ruleC = Rule(
            "c",
            Both(
                phase: 2,
                sub: 0
            )
        );
        var ruleD = Rule(
            "d",
            Both(
                phase: 2,
                sub: 1
            )
        );

        Assert.Equal(
            (((checkPhaseOnly + (3 * checkA)) + firingPhaseOnly) + firingA),
            Work(
                rulePhaseOnly,
                ruleA,
                ruleC,
                ruleD
            )
        );
        // Different cells are not exclusive of each other.
        Assert.Equal(
            ((2 * checkPhaseOnly) + (2 * firingPhaseOnly)),
            Work(
                rulePhaseOnly,
                Rule(
                    "b",
                    SubIs(value: 0)
                )
            )
        );
    }
    [Fact]
    public void AGateThatCanNeverHoldIsRefusedByName() {
        var never = Document(Rule(
            "stuck",
            new ActionPredicate.All(Predicates: [Hp(
                    comparison: ActionStateComparison.Less,
                    value: 0
                ), Hp(
                    comparison: ActionStateComparison.Greater,
                    value: 0
                )])
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: never,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: $"rule 'stuck' gate can never hold: its comparisons pin sub.{WorldStateRow.SlotKey} to an empty range"
        );
        Assert.Equal(
            [("stuck", $"sub.{WorldStateRow.SlotKey}")],
            WorldRuleWorkBudget.ContradictoryGates(definition: never)
        );
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(Rule(
                "fine",
                new ActionPredicate.All(Predicates: [Hp(
                        comparison: ActionStateComparison.GreaterOrEqual,
                        value: 0
                    ), Hp(
                        comparison: ActionStateComparison.LessOrEqual,
                        value: 0
                    )])
            )),
            reason: out _
        ));
    }
    [Fact]
    public void ARuleThatWritesTheDiscriminatorAdmitsOneMoreValuePerWrite() {
        var ruleB = Rule(
            "b",
            PhaseIs(value: 1)
        );
        var lineB = WorldRuleWorkBudget.Contributors(definition: Document(ruleB)).Single();
        var checkB = lineB.CheckUnits;
        var firingB = lineB.FiringUnits;

        var advanceRule = Advance(
            gate: null,
            name: "go"
        );
        var lineAdvance = WorldRuleWorkBudget.Contributors(definition: Document(advanceRule)).Single();
        var checkAdvance = lineAdvance.CheckUnits;
        var firingAdvance = lineAdvance.FiringUnits;
        var advanceCost = lineAdvance.WorkUnits;

        // Effects apply immediately: a rule advancing the phase lets the next phase's rules fire in the same tick, so
        // the group prices its two costliest values, never one. All checks sum.
        var ruleC = Rule(
            "c",
            PhaseIs(value: 2)
        );
        var ruleD = Rule(
            "d",
            PhaseIs(value: 3)
        );

        Assert.Equal(
            (((checkAdvance + (3 * checkB)) + firingAdvance) + (2 * firingB)),
            Work(
                advanceRule,
                ruleB,
                ruleC,
                ruleD
            )
        );
        // With fewer values than admitted, everything sums.
        Assert.Equal(
            ((advanceCost + checkB) + firingB),
            Work(
                advanceRule,
                ruleB
            )
        );
        // A writer inside the group is one of its values: the two costliest are kept, and the cheap advance is not.
        var advanceInGroup = Advance(
            "go",
            PhaseIs(value: 0)
        );
        var lineAdvanceInGroup = WorldRuleWorkBudget.Contributors(definition: Document(advanceInGroup)).Single();

        Assert.Equal(
            ((lineAdvanceInGroup.CheckUnits + (2 * checkB)) + (2 * firingB)),
            Work(
                advanceInGroup,
                ruleB,
                ruleC
            )
        );
    }
    [Fact]
    public void DisjointRangesOnOneCellAreExclusiveAndOverlappingOnesSum() {
        var ruleA = Rule(
            "a",
            Hp(
                comparison: ActionStateComparison.LessOrEqual,
                value: 0
            )
        );
        var ruleB = Rule(
            "b",
            Hp(
                comparison: ActionStateComparison.Greater,
                value: 0
            )
        );
        var ruleC = Rule(
            "c",
            Hp(
                comparison: ActionStateComparison.Equal,
                value: 0
            )
        );
        var linesA = WorldRuleWorkBudget.Contributors(definition: Document(ruleA)).Single();
        var check = linesA.CheckUnits;
        var firing = linesA.FiringUnits;

        // Disjoint ranges: checks sum, but exclusive firing takes the maximum rather than summing.
        Assert.Equal(
            ((2 * check) + firing),
            Work(
                ruleA,
                ruleB
            )
        );
        Assert.Equal(
            ((3 * check) + firing),
            Work(
                Rule(
                    "a",
                    Hp(
                        comparison: ActionStateComparison.Less,
                        value: 0
                    )
                ),
                ruleC,
                ruleB
            )
        );
        // Overlapping ranges: firing sums as well.
        Assert.Equal(
            ((2 * check) + (2 * firing)),
            Work(
                Rule(
                    "a",
                    Hp(
                        comparison: ActionStateComparison.LessOrEqual,
                        value: 5
                    )
                ),
                Rule(
                    "b",
                    Hp(
                        comparison: ActionStateComparison.GreaterOrEqual,
                        value: 3
                    )
                )
            )
        );
        // A range and a point inside it fire together; a point outside it does not.
        Assert.Equal(
            ((2 * check) + (2 * firing)),
            Work(
                Rule(
                    "a",
                    Hp(
                        comparison: ActionStateComparison.GreaterOrEqual,
                        value: 1
                    )
                ),
                Rule(
                    "b",
                    Hp(
                        comparison: ActionStateComparison.Equal,
                        value: 7
                    )
                )
            )
        );
        Assert.Equal(
            ((2 * check) + firing),
            Work(
                Rule(
                    "a",
                    Hp(
                        comparison: ActionStateComparison.GreaterOrEqual,
                        value: 1
                    )
                ),
                ruleC
            )
        );
        Assert.Equal(
            $"sub.{WorldStateRow.SlotKey}<=0",
            linesA.Discriminators.Single().Describe()
        );
    }
    [Fact]
    public void DistinctEqualityGatesOnOneCellChargeTheirMaximumNotTheirSum() {
        var a = Rule(
            "a",
            PhaseIs(value: 0)
        );
        var b = Rule(
            "b",
            PhaseIs(value: 1)
        );
        var c = Rule(
            "c",
            PhaseIs(value: 2)
        );
        var lineA = WorldRuleWorkBudget.Contributors(definition: Document(a)).Single();
        var checkA = lineA.CheckUnits;
        var firingA = lineA.FiringUnits;
        var one = WorldRuleWorkBudget.Measure(definition: Document(a)).WorkUnitsPerTick;

        var exclusive = WorldRuleWorkBudget.Measure(definition: Document(
            a,
            b,
            c
        )).WorkUnitsPerTick;
        // All checks sum, but exclusive firing takes the maximum rather than summing.
        Assert.Equal(
            actual: exclusive,
            expected: ((3 * checkA) + firingA)
        );

        var shared = WorldRuleWorkBudget.Measure(definition: Document(
            a,
            Rule(
                "b",
                PhaseIs(value: 0)
            )
        )).WorkUnitsPerTick;

        Assert.Equal(
            actual: shared,
            expected: (2 * one)
        );

        var alone = WorldRuleWorkBudget.Measure(definition: Document(Rule(
            "b",
            null
        ))).WorkUnitsPerTick;
        var ungated = WorldRuleWorkBudget.Measure(definition: Document(
            a,
            Rule(
                "b",
                null
            )
        )).WorkUnitsPerTick;

        Assert.Equal(
            actual: ungated,
            expected: (one + alone)
        );

        var conjoined = WorldRuleWorkBudget.Measure(definition: Document(
            Rule(
                "a",
                new ActionPredicate.All(Predicates: [PhaseIs(value: 0), new ActionPredicate.CompareState(
                        State: "count",
                        Comparison: ActionStateComparison.Less,
                        Value: 9m
                    )])
            ),
            Rule(
                "b",
                new ActionPredicate.All(Predicates: [PhaseIs(value: 1), new ActionPredicate.CompareState(
                        State: "count",
                        Comparison: ActionStateComparison.Less,
                        Value: 9m
                    )])
            )
        )).WorkUnitsPerTick;

        Assert.True(condition: (conjoined < (2 * one)));
    }
    [Fact]
    public void ThreeExclusiveRulesPayThreeChecksAndOneFiring() {
        var lines = Enumerable.Range(
            count: 3,
            start: 0
        ).Select(selector: value => new RuleWorkContributor(
            Name: $"phase{value}",
            IsInteraction: false,
            Multiplier: 1,
            Cost: new RuleCost(
                Check: 10,
                Effects: 100,
                Setup: 0
            ),
            CheckUnits: 10,
            FiringUnits: 100,
            WorkUnits: 110,
            Discriminators: [new RulePinnedCell(
                    Cell: "phase.value",
                    High: value,
                    Low: value
                )]
        )).ToArray();

        Assert.Equal(
            (3L, 130L),
            RuleWorkBudget.Tally(
                contributors: lines,
                writers: new Dictionary<string, long>()
            )
        );
        // A preceding writer can admit another phase; its own cost belongs to its own contributor.
        Assert.Equal(
            (3L, 230L),
            RuleWorkBudget.Tally(
                contributors: lines,
                writers: new Dictionary<string, long> { ["phase.value"] = 1 }
            )
        );
    }
}
