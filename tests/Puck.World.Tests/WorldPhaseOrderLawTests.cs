using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the phase machine's authorable order: direction, skipped participants, the active hand-off, and the
/// world program's transition branch.</summary>
public sealed class WorldPhaseOrderLawTests {
    private static readonly string[] Three = ["seat1", "seat2", "seat3"];

    [Fact]
    public void SkippedParticipantsArePassedOverAndTheTurnEndsPastTheLastOne() {
        var definition = Document(Phase(Three, [new("bet", WorldPhaseMode.Sequential, "showdown"), new("showdown", WorldPhaseMode.Resolution, "bet")]));

        var folded = Apply(definition, new WorldStateTransform.TurnOrder("turn", Skip: ["seat2"]), WorldPrincipal.World);
        Assert.Equal(0b010u, Read(folded).Skipped);
        Assert.Equal(0, Read(folded).Active);

        var afterFirst = Apply(folded, new WorldStateTransform.CompletePhase("turn", Read(folded).Sequence), WorldPrincipal.Seat(0));
        Assert.Equal(2, Read(afterFirst).Active);
        Assert.Equal(0, Read(afterFirst).Current);

        Assert.False(WorldStateTransforms.TryApply(afterFirst, new WorldStateTransform.CompletePhase("turn", Read(afterFirst).Sequence), WorldPrincipal.Seat(1), 1, "test", out _, out _));

        var afterLast = Apply(afterFirst, new WorldStateTransform.CompletePhase("turn", Read(afterFirst).Sequence), WorldPrincipal.Seat(2));
        Assert.Equal(1, Read(afterLast).Current);
        Assert.Equal(0b010u, Read(afterLast).Skipped);
    }

    [Fact]
    public void ReversedDirectionWalksBackwardAndSeatsAFreshPhaseFromTheTrailingEnd() {
        var definition = Document(Phase(Three, [new("play", WorldPhaseMode.Sequential, "play")]));

        var second = Apply(definition, new WorldStateTransform.CompletePhase("turn", 0), WorldPrincipal.Seat(0));
        Assert.Equal(1, Read(second).Active);

        var reversed = Apply(second, new WorldStateTransform.TurnOrder("turn", Direction: -1), WorldPrincipal.World);
        Assert.Equal(-1, Read(reversed).Direction);
        Assert.Equal(1, Read(reversed).Active);

        var back = Apply(reversed, new WorldStateTransform.CompletePhase("turn", Read(reversed).Sequence), WorldPrincipal.Seat(1));
        Assert.Equal(0, Read(back).Active);

        var wrapped = Apply(back, new WorldStateTransform.CompletePhase("turn", Read(back).Sequence), WorldPrincipal.Seat(0));
        Assert.Equal(2, Read(wrapped).Active);
        Assert.Equal(1L, Read(wrapped).Round);
    }

    [Fact]
    public void SkippingTheActiveParticipantHandsTheTurnOnwardAroundTheRing() {
        var definition = Document(Phase(Three, [new("play", WorldPhaseMode.Sequential, "play")]));

        var advanced = Apply(definition, new WorldStateTransform.CompletePhase("turn", 0), WorldPrincipal.Seat(0));
        var advancedAgain = Apply(advanced, new WorldStateTransform.CompletePhase("turn", Read(advanced).Sequence), WorldPrincipal.Seat(1));
        Assert.Equal(2, Read(advancedAgain).Active);

        var eliminated = Apply(advancedAgain, new WorldStateTransform.TurnOrder("turn", Skip: ["seat3"]), WorldPrincipal.World);
        Assert.Equal(0, Read(eliminated).Active);
        Assert.Equal(Read(advancedAgain).Sequence + 1, Read(eliminated).Sequence);

        var restored = Apply(eliminated, new WorldStateTransform.TurnOrder("turn", Unskip: ["seat3"], Active: "seat3"), WorldPrincipal.World);
        Assert.Equal(2, Read(restored).Active);
        Assert.Equal(0u, Read(restored).Skipped);

        Assert.False(WorldStateTransforms.TryApply(definition, new WorldStateTransform.TurnOrder("turn", Direction: 0), WorldPrincipal.World, 0, "test", out _, out var reason));
        Assert.Contains("1 or -1", reason);
        Assert.False(WorldStateTransforms.TryApply(definition, new WorldStateTransform.TurnOrder("turn", Skip: ["seat2"]), WorldPrincipal.Seat(0), 0, "test", out _, out var actorReason));
        Assert.Contains("world program", actorReason);
    }

    [Fact]
    public void TogetherPhasesNeverWaitOnSkippedParticipantsAndTheWorldMayBranch() {
        var definition = Document(Phase(Three, [
            new("commit", WorldPhaseMode.Together, "reveal"),
            new("reveal", WorldPhaseMode.Resolution, "commit"),
            new("sudden-death", WorldPhaseMode.Resolution, "commit"),
        ]));

        var folded = Apply(definition, new WorldStateTransform.TurnOrder("turn", Skip: ["seat3"]), WorldPrincipal.World);
        var one = Apply(folded, new WorldStateTransform.CompletePhase("turn", 0), WorldPrincipal.Seat(0));
        Assert.Equal(0, Read(one).Current);
        var two = Apply(one, new WorldStateTransform.CompletePhase("turn", 0), WorldPrincipal.Seat(1));
        Assert.Equal(1, Read(two).Current);

        Assert.False(WorldStateTransforms.TryApply(one, new WorldStateTransform.CompletePhase("turn", 0, Next: "sudden-death"), WorldPrincipal.Seat(1), 0, "test", out _, out var reason));
        Assert.Contains("branch", reason);

        var branched = Apply(two, new WorldStateTransform.CompletePhase("turn", Next: "sudden-death"), WorldPrincipal.World);
        Assert.Equal(2, Read(branched).Current);
        Assert.False(WorldStateTransforms.TryApply(two, new WorldStateTransform.CompletePhase("turn", Next: "overtime"), WorldPrincipal.World, 0, "test", out _, out _));
    }

    [Fact]
    public void OrderFactsReachRulesAndTheAuthoritativeHashSeesEveryOrderChange() {
        var definition = Document(Phase(Three, [new("play", WorldPhaseMode.Sequential, "play")])) with {
            Rules = [new WorldRule(
                Name: WorldCellName.Parse("mirror"),
                Effects: [
                    new ActionEffect.SetState(State: "direction", FromState: "$phase:turn:direction"),
                    new ActionEffect.SetState(State: "skipped", FromState: "$phase:turn:skipped"),
                ]
            )],
        };
        definition = definition with { StateRaw = definition.StateRaw! with { World = [.. definition.StateRaw.World!, Slot("direction"), Slot("skipped")] } };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var validation), validation);

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        Assert.Equal(1L, Value(fixture, "direction"));
        Assert.Equal(0L, Value(fixture, "skipped"));

        var reversed = WorldStateTransforms.TryApply(fixture.Server.Definition, new WorldStateTransform.TurnOrder("turn", Direction: -1, Skip: ["seat2"]), WorldPrincipal.World, 1, "test", out var candidate, out var reason);
        Assert.True(reversed, reason);
        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 1, 1, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.TransformState(WorldPrincipal.Console, new WorldStateTransform.TurnOrder("turn", Direction: -1, Skip: ["seat2"])))), _ => { });
        fixture.Step();
        Assert.Equal(1L, Value(fixture, "direction"));

        var bad = Document(Phase(Three, [new("play", WorldPhaseMode.Sequential, "play")], skipped: 0b1000u));
        Assert.False(WorldDefinitionValidator.TryValidateLocally(bad, out var badReason));
        Assert.Contains("outside its declared domain", badReason);

        var authored = Document(Phase(Three, [new("play", WorldPhaseMode.Sequential, "play")], direction: -1, skipped: 0b010u)) with { Rules = definition.Rules, StateRaw = definition.StateRaw with { World = [Phase(Three, [new("play", WorldPhaseMode.Sequential, "play")], direction: -1, skipped: 0b010u), Slot("direction"), Slot("skipped")] } };
        using var other = Fixtures.FreshServer(definition: authored);
        other.Step();
        Assert.Equal(-1L, Value(other, "direction"));
        Assert.Equal(2L, Value(other, "skipped"));
        Assert.NotEqual(before, WorldRuntimeStateHash.HashAuthoritative(other.Server, 0));
    }

    [Fact]
    public void OrderTransformsRoundTripThroughTheStrictWireShape() {
        var definition = Document(Phase(Three, [new("play", WorldPhaseMode.Sequential, "play")], direction: -1, skipped: 1u)) with {
            Rules = [new WorldRule(WorldCellName.Parse("order"), [
                new ActionEffect.TransformState(new WorldStateTransform.TurnOrder("turn", Direction: 1, Skip: ["seat1"], Unskip: ["seat2"], Active: "seat3")),
                new ActionEffect.TransformState(new WorldStateTransform.CompletePhase("turn", Next: "play")),
            ])],
        };

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        var phase = WorldDefinitionRows.FindStateRow(parsed.State, "turn")!.Phase!;
        Assert.Equal(-1, phase.Direction);
        Assert.Equal(1u, phase.Skipped);
        var effects = Assert.Single(parsed.Rules ?? []).Effects;
        var order = Assert.IsType<WorldStateTransform.TurnOrder>(Assert.IsType<ActionEffect.TransformState>(effects[0]).Transform);
        Assert.Equal("seat3", order.Active);
        Assert.Equal("play", Assert.IsType<WorldStateTransform.CompletePhase>(Assert.IsType<ActionEffect.TransformState>(effects[1]).Transform).Next);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(parsed, out var reason), reason);
    }

    private static WorldStateRow Phase(string[] participants, WorldPhaseDefinition[] phases, int direction = 1, uint skipped = 0) =>
        new(WorldCellName.Parse("turn"), CellKind.Int, Phase: new(participants, phases, Direction: direction, Skipped: skipped));
    private static WorldStateRow Slot(string name) => new(WorldCellName.Parse(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
    private static WorldDefinition Document(params WorldStateRow[] rows) => Fixtures.BuildDocument() with { StateRaw = new(World: rows), Rules = [] };
    private static WorldStatePhase Read(WorldDefinition definition) => WorldDefinitionRows.FindStateRow(definition.State, "turn")!.Phase!;
    private static WorldDefinition Apply(WorldDefinition definition, WorldStateTransform transform, WorldPrincipal actor) {
        Assert.True(WorldStateTransforms.TryApply(definition, transform, actor, 1, "test", out var candidate, out var reason), reason);
        return candidate!;
    }
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;
}
