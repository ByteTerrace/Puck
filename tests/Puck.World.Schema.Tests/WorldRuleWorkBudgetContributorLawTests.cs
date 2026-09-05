using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The work sheet's lines account for its total exactly, costliest first, and an over-budget refusal names
/// the lines that put it there.</summary>
public sealed class WorldRuleWorkBudgetContributorLawTests {
    private static WorldStateRow Slot(string name) =>
        new(WorldCellName.Parse(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
    private static WorldStateRow Keyed(string name, int capacity) =>
        new(WorldCellName.Parse(name), CellKind.Int, Capacity: capacity, Cells: [new WorldStateCell(WorldCellName.Parse("0"), 0L)]);
    private static ActionPredicate PhaseIs(long value) =>
        new ActionPredicate.CompareState(State: "phase", Comparison: ActionStateComparison.Equal, Value: value);
    private static WorldRule Rule(string name, string? forEach, ActionPredicate? gate = null) =>
        new(WorldCellName.Parse(name), [new ActionEffect.AddState(State: "count", Value: 1m)], Gate: gate, ForEach: forEach);

    private static WorldDefinition Document(params WorldRule[] rules) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [Slot("phase"), Slot("count"), Keyed("many", 64), Keyed("more", 4096)]),
        Rules: rules
    );

    [Fact]
    public void LinesAccountForTheTotalAndSortCostliestFirst() {
        var document = Document(Rule("plain", null), Rule("wide", "more"), Rule("narrow", "many"), Rule("p0", "many", PhaseIs(0)), Rule("p1", "many", PhaseIs(1)));
        var lines = WorldRuleWorkBudget.Contributors(document);
        Assert.Equal(["wide", "p0", "p1", "narrow", "plain"], lines.Select(line => line.Name));
        Assert.Equal(4096L, lines[0].Multiplier);
        Assert.Equal(lines[0].Multiplier * lines[0].UnitCost, lines[0].WorkUnits);
        Assert.Equal([$"phase.{WorldStateRow.SlotKey}=0"], lines[1].Discriminators.Select(pinned => pinned.Describe()));
        Assert.Equal([$"phase.{WorldStateRow.SlotKey}=1"], lines[2].Discriminators.Select(pinned => pinned.Describe()));
        Assert.Empty(lines[0].Discriminators);

        // p0 and p1 are exclusive on phase, so the total carries one of them, not both.
        var unconditional = lines.Where(line => line.Discriminators.Count == 0).Sum(line => line.WorkUnits);
        var exclusive = lines.Where(line => line.Discriminators.Count > 0).Max(line => line.WorkUnits);
        Assert.Equal(unconditional + exclusive, WorldRuleWorkBudget.Measure(document).WorkUnitsPerTick);
    }

    [Fact]
    public void AnOverBudgetRefusalNamesTheCostliestLines() {
        var document = Document(Rule("heavy", "more"), Rule("light", null));
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition: document, reason: out var reason));
        Assert.Contains("costliest: 'heavy' x4096 = ", reason, StringComparison.Ordinal);
        Assert.Contains("'light' x1 = ", reason, StringComparison.Ordinal);
    }
}
