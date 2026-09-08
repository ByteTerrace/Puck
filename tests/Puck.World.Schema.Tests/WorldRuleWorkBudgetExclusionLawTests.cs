using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>All rule checks sum; distinct discriminator values may share firing costs only.</summary>
public sealed class WorldRuleWorkBudgetExclusionLawTests {
    private static WorldStateRow Slot(string name) =>
        new(CellName.Parse(name), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, 0L)]);
    private static WorldStateRow Keyed(string name, int capacity) =>
        new(CellName.Parse(name), CellKind.Int, Capacity: capacity, Cells: [new StateCell(CellName.Parse("0"), 0L)]);
    private static WorldRule Rule(string name, ActionPredicate? gate, string forEach = "many") =>
        new(CellName.Parse(name), [new ActionEffect.AddState(State: "count", Value: 1m)], Gate: gate, ForEach: forEach);
    private static ActionPredicate PhaseIs(long value) =>
        new ActionPredicate.CompareState(State: "phase", Comparison: ActionStateComparison.Equal, Value: value);

    private static ActionPredicate SubIs(long value) =>
        new ActionPredicate.CompareState(State: "sub", Comparison: ActionStateComparison.Equal, Value: value);
    private static ActionPredicate Both(long phase, long sub) => new ActionPredicate.All([PhaseIs(phase), SubIs(sub)]);
    private static WorldRule Advance(string name, ActionPredicate? gate) =>
        new(CellName.Parse(name), [new ActionEffect.SetState(State: "phase", Value: 2m)], Gate: gate);

    private static WorldDefinition Document(params WorldRule[] rules) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [Slot("phase"), Slot("sub"), Slot("count"), Keyed("many", 64)]),
        Rules: rules
    );

    private static long Work(params WorldRule[] rules) => WorldRuleWorkBudget.Measure(Document(rules)).WorkUnitsPerTick;
    private static ActionPredicate Hp(ActionStateComparison comparison, long value) =>
        new ActionPredicate.CompareState(State: "sub", Comparison: comparison, Value: value);

    [Fact]
    public void ThreeExclusiveRulesPayThreeChecksAndOneFiring() {
        var lines = Enumerable.Range(0, 3).Select(value => new RuleWorkContributor(
            Name: $"phase{value}", IsInteraction: false, Multiplier: 1,
            Cost: new RuleCost(0, 10, 100), CheckUnits: 10, FiringUnits: 100, WorkUnits: 110,
            Discriminators: [new RulePinnedCell("phase.value", value, value)]
        )).ToArray();
        Assert.Equal((3L, 130L), RuleWorkBudget.Tally(lines, new Dictionary<string, long>()));
        // A preceding writer can admit another phase; its own cost belongs to its own contributor.
        Assert.Equal((3L, 230L), RuleWorkBudget.Tally(lines, new Dictionary<string, long> { ["phase.value"] = 1 }));
    }

    [Fact]
    public void DisjointRangesOnOneCellAreExclusiveAndOverlappingOnesSum() {
        var ruleA = Rule("a", Hp(ActionStateComparison.LessOrEqual, 0));
        var ruleB = Rule("b", Hp(ActionStateComparison.Greater, 0));
        var ruleC = Rule("c", Hp(ActionStateComparison.Equal, 0));
        var linesA = WorldRuleWorkBudget.Contributors(Document(ruleA)).Single();
        var check = linesA.CheckUnits;
        var firing = linesA.FiringUnits;

        // Disjoint ranges: checks sum, but exclusive firing takes the maximum rather than summing.
        Assert.Equal(2 * check + firing, Work(ruleA, ruleB));
        Assert.Equal(3 * check + firing, Work(Rule("a", Hp(ActionStateComparison.Less, 0)), ruleC, ruleB));
        // Overlapping ranges: firing sums as well.
        Assert.Equal(2 * check + 2 * firing, Work(Rule("a", Hp(ActionStateComparison.LessOrEqual, 5)), Rule("b", Hp(ActionStateComparison.GreaterOrEqual, 3))));
        // A range and a point inside it fire together; a point outside it does not.
        Assert.Equal(2 * check + 2 * firing, Work(Rule("a", Hp(ActionStateComparison.GreaterOrEqual, 1)), Rule("b", Hp(ActionStateComparison.Equal, 7))));
        Assert.Equal(2 * check + firing, Work(Rule("a", Hp(ActionStateComparison.GreaterOrEqual, 1)), ruleC));
        Assert.Equal($"sub.{WorldStateRow.SlotKey}<=0", linesA.Discriminators.Single().Describe());
    }

    [Fact]
    public void AGateThatCanNeverHoldIsRefusedByName() {
        var never = Document(Rule("stuck", new ActionPredicate.All([Hp(ActionStateComparison.Less, 0), Hp(ActionStateComparison.Greater, 0)])));
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: never, reason: out var reason));
        Assert.Contains($"rule 'stuck' gate can never hold: its comparisons pin sub.{WorldStateRow.SlotKey} to an empty range", reason, StringComparison.Ordinal);
        Assert.Equal([("stuck", $"sub.{WorldStateRow.SlotKey}")], WorldRuleWorkBudget.ContradictoryGates(never));
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition: Document(Rule("fine", new ActionPredicate.All([Hp(ActionStateComparison.GreaterOrEqual, 0), Hp(ActionStateComparison.LessOrEqual, 0)]))), reason: out _));
    }

    [Fact]
    public void AFurtherPinnedCellNestsUnderTheFirstSoSubphasesAreExclusiveToo() {
        var ruleA = Rule("a", Both(1, 0));
        var ruleB = Rule("b", Both(1, 1));
        var lineA = WorldRuleWorkBudget.Contributors(Document(ruleA)).Single();
        var checkA = lineA.CheckUnits;
        var firingA = lineA.FiringUnits;

        var rulePhaseOnly = Rule("phase", PhaseIs(1));
        var linePhaseOnly = WorldRuleWorkBudget.Contributors(Document(rulePhaseOnly)).Single();
        var checkPhaseOnly = linePhaseOnly.CheckUnits;
        var firingPhaseOnly = linePhaseOnly.FiringUnits;

        // Same phase, distinct subphases: checks sum, firing takes the costliest subphase.
        Assert.Equal(2 * checkA + firingA, Work(ruleA, ruleB));
        // Spelling order does not matter: "sub then phase" shares the node.
        Assert.Equal(2 * checkA + firingA, Work(ruleA, Rule("b", new ActionPredicate.All([SubIs(1), PhaseIs(1)]))));
        // A rule pinning only the phase sums with the deeper ones under it:
        // checks sum across all 3 rules; firing carries phaseOnly plus the costliest subphase.
        Assert.Equal(checkPhaseOnly + 2 * checkA + firingPhaseOnly + firingA, Work(rulePhaseOnly, ruleA, ruleB));
        // Distinct phases stay exclusive across their whole subtrees:
        // checks sum across all 4 rules (phaseOnly + 3 two-predicate rules).
        // Phase 1 carries firingPhaseOnly + firingA; phase 2 carries firingA. Their maximum is phase 1.
        var ruleC = Rule("c", Both(2, 0));
        var ruleD = Rule("d", Both(2, 1));
        Assert.Equal(checkPhaseOnly + 3 * checkA + firingPhaseOnly + firingA, Work(rulePhaseOnly, ruleA, ruleC, ruleD));
        // Different cells are not exclusive of each other.
        Assert.Equal(2 * checkPhaseOnly + 2 * firingPhaseOnly, Work(rulePhaseOnly, Rule("b", SubIs(0))));
    }

    [Fact]
    public void ARuleThatWritesTheDiscriminatorAdmitsOneMoreValuePerWrite() {
        var ruleB = Rule("b", PhaseIs(1));
        var lineB = WorldRuleWorkBudget.Contributors(Document(ruleB)).Single();
        var checkB = lineB.CheckUnits;
        var firingB = lineB.FiringUnits;

        var advanceRule = Advance("go", null);
        var lineAdvance = WorldRuleWorkBudget.Contributors(Document(advanceRule)).Single();
        var checkAdvance = lineAdvance.CheckUnits;
        var firingAdvance = lineAdvance.FiringUnits;
        var advanceCost = lineAdvance.WorkUnits;

        // Effects apply immediately: a rule advancing the phase lets the next phase's rules fire in the same tick, so
        // the group prices its two costliest values, never one. All checks sum.
        var ruleC = Rule("c", PhaseIs(2));
        var ruleD = Rule("d", PhaseIs(3));
        Assert.Equal(checkAdvance + 3 * checkB + firingAdvance + 2 * firingB, Work(advanceRule, ruleB, ruleC, ruleD));
        // With fewer values than admitted, everything sums.
        Assert.Equal(advanceCost + checkB + firingB, Work(advanceRule, ruleB));
        // A writer inside the group is one of its values: the two costliest are kept, and the cheap advance is not.
        var advanceInGroup = Advance("go", PhaseIs(0));
        var lineAdvanceInGroup = WorldRuleWorkBudget.Contributors(Document(advanceInGroup)).Single();
        Assert.Equal(lineAdvanceInGroup.CheckUnits + 2 * checkB + 2 * firingB, Work(advanceInGroup, ruleB, ruleC));
    }

    [Fact]
    public void DistinctEqualityGatesOnOneCellChargeTheirMaximumNotTheirSum() {
        var a = Rule("a", PhaseIs(0));
        var b = Rule("b", PhaseIs(1));
        var c = Rule("c", PhaseIs(2));
        var lineA = WorldRuleWorkBudget.Contributors(Document(a)).Single();
        var checkA = lineA.CheckUnits;
        var firingA = lineA.FiringUnits;
        var one = WorldRuleWorkBudget.Measure(Document(a)).WorkUnitsPerTick;

        var exclusive = WorldRuleWorkBudget.Measure(Document(a, b, c)).WorkUnitsPerTick;
        // All checks sum, but exclusive firing takes the maximum rather than summing.
        Assert.Equal(3 * checkA + firingA, exclusive);

        var shared = WorldRuleWorkBudget.Measure(Document(a, Rule("b", PhaseIs(0)))).WorkUnitsPerTick;
        Assert.Equal(2 * one, shared);

        var alone = WorldRuleWorkBudget.Measure(Document(Rule("b", null))).WorkUnitsPerTick;
        var ungated = WorldRuleWorkBudget.Measure(Document(a, Rule("b", null))).WorkUnitsPerTick;
        Assert.Equal(one + alone, ungated);

        var conjoined = WorldRuleWorkBudget.Measure(Document(
            Rule("a", new ActionPredicate.All([PhaseIs(0), new ActionPredicate.CompareState(State: "count", Comparison: ActionStateComparison.Less, Value: 9m)])),
            Rule("b", new ActionPredicate.All([PhaseIs(1), new ActionPredicate.CompareState(State: "count", Comparison: ActionStateComparison.Less, Value: 9m)]))
        )).WorkUnitsPerTick;
        Assert.True(conjoined < 2 * one);
    }
}
