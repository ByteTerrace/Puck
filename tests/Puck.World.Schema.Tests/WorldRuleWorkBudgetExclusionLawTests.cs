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

    private static WorldDefinition Document(params WorldRule[] rules) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [Slot("phase"), Slot("count"), Keyed("many", 64)]),
        Rules: rules
    );

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
