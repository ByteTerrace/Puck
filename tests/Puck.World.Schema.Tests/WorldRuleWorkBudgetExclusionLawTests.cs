using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Rules gated on distinct equality values of one literal cell are mutually exclusive, so the work sheet
/// charges the group its most expensive value; rules sharing a value, or gated on anything else, sum.</summary>
public sealed class WorldRuleWorkBudgetExclusionLawTests {
    private static WorldStateRow Slot(string name) =>
        new(WorldCellName.Parse(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
    private static WorldStateRow Keyed(string name, int capacity) =>
        new(WorldCellName.Parse(name), CellKind.Int, Capacity: capacity, Cells: [new WorldStateCell(WorldCellName.Parse("0"), 0L)]);
    private static WorldRule Rule(string name, ActionPredicate? gate, string forEach = "many") =>
        new(WorldCellName.Parse(name), [new ActionEffect.AddState(State: "count", Value: 1m)], Gate: gate, ForEach: forEach);
    private static ActionPredicate PhaseIs(long value) =>
        new ActionPredicate.CompareState(State: "phase", Comparison: ActionStateComparison.Equal, Value: value);

    private static ActionPredicate SubIs(long value) =>
        new ActionPredicate.CompareState(State: "sub", Comparison: ActionStateComparison.Equal, Value: value);
    private static ActionPredicate Both(long phase, long sub) => new ActionPredicate.All([PhaseIs(phase), SubIs(sub)]);
    private static WorldRule Advance(string name, ActionPredicate? gate) =>
        new(WorldCellName.Parse(name), [new ActionEffect.SetState(State: "phase", Value: 2m)], Gate: gate);

    private static WorldDefinition Document(params WorldRule[] rules) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [Slot("phase"), Slot("sub"), Slot("count"), Keyed("many", 64)]),
        Rules: rules
    );

    private static long Work(params WorldRule[] rules) => WorldRuleWorkBudget.Measure(Document(rules)).WorkUnitsPerTick;

    [Fact]
    public void AFurtherPinnedCellNestsUnderTheFirstSoSubphasesAreExclusiveToo() {
        var one = Work(Rule("a", Both(1, 0)));
        var phaseOnly = Work(Rule("a", PhaseIs(1)));
        // Same phase, distinct subphases: the trie takes the costliest subphase, not both.
        Assert.Equal(one, Work(Rule("a", Both(1, 0)), Rule("b", Both(1, 1))));
        // Spelling order does not matter: "sub then phase" shares the node.
        Assert.Equal(one, Work(Rule("a", Both(1, 0)), Rule("b", new ActionPredicate.All([SubIs(1), PhaseIs(1)]))));
        // A rule pinning only the phase sums with the deeper ones under it.
        Assert.Equal(phaseOnly + one, Work(Rule("a", PhaseIs(1)), Rule("b", Both(1, 0)), Rule("c", Both(1, 1))));
        // Distinct phases stay exclusive across their whole subtrees.
        Assert.Equal(phaseOnly + one, Work(Rule("a", PhaseIs(1)), Rule("b", Both(1, 0)), Rule("c", Both(2, 0)), Rule("d", Both(2, 1))));
        // Different cells are not exclusive of each other.
        Assert.Equal(2 * phaseOnly, Work(Rule("a", PhaseIs(1)), Rule("b", SubIs(0))));
    }

    [Fact]
    public void ARuleThatWritesTheDiscriminatorAdmitsOneMoreValuePerWrite() {
        var one = Work(Rule("a", PhaseIs(0)));
        var advance = WorldRuleWorkBudget.Contributors(Document(Advance("go", null))).Single().WorkUnits;
        // Effects apply immediately: a rule advancing the phase lets the next phase's rules fire in the same tick, so
        // the group prices its two costliest values, never one.
        Assert.Equal(advance + 2 * one, Work(Advance("go", null), Rule("b", PhaseIs(1)), Rule("c", PhaseIs(2)), Rule("d", PhaseIs(3))));
        // With fewer values than admitted, everything sums.
        Assert.Equal(advance + one, Work(Advance("go", null), Rule("b", PhaseIs(1))));
        // A writer inside the group is one of its values: the two costliest are kept, and the cheap advance is not.
        Assert.Equal(2 * one, Work(Advance("go", PhaseIs(0)), Rule("b", PhaseIs(1)), Rule("c", PhaseIs(2))));
    }

    [Fact]
    public void DistinctEqualityGatesOnOneCellChargeTheirMaximumNotTheirSum() {
        var one = WorldRuleWorkBudget.Measure(Document(Rule("a", PhaseIs(0)))).WorkUnitsPerTick;
        var exclusive = WorldRuleWorkBudget.Measure(Document(Rule("a", PhaseIs(0)), Rule("b", PhaseIs(1)), Rule("c", PhaseIs(2)))).WorkUnitsPerTick;
        Assert.Equal(one, exclusive);

        var shared = WorldRuleWorkBudget.Measure(Document(Rule("a", PhaseIs(0)), Rule("b", PhaseIs(0)))).WorkUnitsPerTick;
        Assert.Equal(2 * one, shared);

        var alone = WorldRuleWorkBudget.Measure(Document(Rule("b", null))).WorkUnitsPerTick;
        var ungated = WorldRuleWorkBudget.Measure(Document(Rule("a", PhaseIs(0)), Rule("b", null))).WorkUnitsPerTick;
        Assert.Equal(one + alone, ungated);

        var conjoined = WorldRuleWorkBudget.Measure(Document(
            Rule("a", new ActionPredicate.All([PhaseIs(0), new ActionPredicate.CompareState(State: "count", Comparison: ActionStateComparison.Less, Value: 9m)])),
            Rule("b", new ActionPredicate.All([PhaseIs(1), new ActionPredicate.CompareState(State: "count", Comparison: ActionStateComparison.Less, Value: 9m)]))
        )).WorkUnitsPerTick;
        Assert.True(conjoined < 2 * one);
    }
}
