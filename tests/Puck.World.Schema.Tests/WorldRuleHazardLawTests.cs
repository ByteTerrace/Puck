using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The hazard read-back names the pairs the rules' document order decides: an earlier read of a later
/// write, and two writes of one cell with a set among them — never a pair whose gates make it impossible on one
/// tick, and never a pair of plain adds.</summary>
public sealed class WorldRuleHazardLawTests {
    private static WorldStateRow Slot(string name) =>
        new(WorldCellName.Parse(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 5L)]);
    private static ActionPredicate PhaseIs(long value) =>
        new ActionPredicate.CompareState(State: "phase", Comparison: ActionStateComparison.Equal, Value: value);
    private static ActionPredicate HpAtMost(long value) =>
        new ActionPredicate.CompareState(State: "hp", Comparison: ActionStateComparison.LessOrEqual, Value: value);
    private static WorldRule Rule(string name, ActionEffect effect, ActionPredicate? gate = null) =>
        new(WorldCellName.Parse(name), [effect], Gate: gate);
    private static WorldDefinition Document(params WorldRule[] rules) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [Slot("phase"), Slot("hp"), Slot("fainted"), Slot("armor")]),
        Rules: rules
    );

    [Fact]
    public void AnEarlierReadOfALaterWriteIsAWriteAfterReadHazard() {
        var hazards = WorldRuleHazards.Analyze(Document(
            Rule("faint", new ActionEffect.SetState(State: "fainted", Value: 1m), HpAtMost(0)),
            Rule("damage", new ActionEffect.AddState(State: "hp", Value: -3m))
        ));
        var hazard = Assert.Single(hazards);
        Assert.Equal(WorldRuleHazardKind.WriteAfterRead, hazard.Kind);
        Assert.Equal(("faint", "damage", $"hp.{WorldStateRow.SlotKey}"), (hazard.First, hazard.Second, hazard.Cell));
        Assert.Contains("previous tick", hazard.Detail, StringComparison.Ordinal);

        // Declared the other way round, the check sees the new value and nothing is decided silently.
        Assert.Empty(WorldRuleHazards.Analyze(Document(
            Rule("damage", new ActionEffect.AddState(State: "hp", Value: -3m)),
            Rule("faint", new ActionEffect.SetState(State: "fainted", Value: 1m), HpAtMost(0))
        )));
    }

    [Fact]
    public void TwoWritesWithASetAmongThemAreAWriteAfterWriteHazardAndTwoAddsAreNot() {
        var sets = WorldRuleHazards.Analyze(Document(
            Rule("poison", new ActionEffect.SetState(State: "hp", Value: 1m)),
            Rule("regen", new ActionEffect.SetState(State: "hp", Value: 9m))
        ));
        var hazard = Assert.Single(sets);
        Assert.Equal(WorldRuleHazardKind.WriteAfterWrite, hazard.Kind);
        Assert.Contains("'regen' wins", hazard.Detail, StringComparison.Ordinal);

        var setAfterAdd = Assert.Single(WorldRuleHazards.Analyze(Document(
            Rule("poison", new ActionEffect.AddState(State: "hp", Value: -1m)),
            Rule("reset", new ActionEffect.SetState(State: "hp", Value: 9m))
        )));
        Assert.Contains("the add is discarded", setAfterAdd.Detail, StringComparison.Ordinal);

        Assert.Empty(WorldRuleHazards.Analyze(Document(
            Rule("poison", new ActionEffect.AddState(State: "hp", Value: -1m)),
            Rule("regen", new ActionEffect.AddState(State: "hp", Value: 2m))
        )));
    }

    [Fact]
    public void APairThatCannotFireOnOneTickIsNotAHazard() {
        Assert.Empty(WorldRuleHazards.Analyze(Document(
            Rule("faint", new ActionEffect.SetState(State: "fainted", Value: 1m), new ActionPredicate.All([PhaseIs(0), HpAtMost(0)])),
            Rule("damage", new ActionEffect.AddState(State: "hp", Value: -3m), PhaseIs(1))
        )));
        Assert.Single(WorldRuleHazards.Analyze(Document(
            Rule("faint", new ActionEffect.SetState(State: "fainted", Value: 1m), new ActionPredicate.All([PhaseIs(0), HpAtMost(0)])),
            Rule("damage", new ActionEffect.AddState(State: "hp", Value: -3m), PhaseIs(0))
        )));
    }

    [Fact]
    public void ReadAndWriteSetsFollowIndirectionsAndBranches() {
        var rule = WorldRuleCompiler.CompileAll(Document(
            Rule("r", new ActionEffect.Transaction(Effects: [
                new WorldTransactionStep.SetCell(State: "armor", Value: 1m),
            ]), HpAtMost(0))
        ))[0];
        Assert.Contains(new WorldRuleAccess("hp", WorldStateRow.SlotKey), WorldRuleDataflow.Reads(rule));
        Assert.Contains(new WorldRuleAccess("armor", WorldStateRow.SlotKey, IsSet: true), WorldRuleDataflow.Writes(rule));
    }
}
