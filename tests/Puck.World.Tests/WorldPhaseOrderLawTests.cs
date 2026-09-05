using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the phase row down to a guarded submission stamp: a guard whose sequence matches admits the
/// mutation, a matching guard's success advances the row's own generation by one, and a stale guard is refused
/// without composing anything.</summary>
public sealed class WorldPhaseOrderLawTests {
    [Fact]
    public void AMatchingGuardAdmitsAndItsSuccessAdvancesTheGenerationByOne() {
        var definition = Document(Phase(), Board());

        Assert.True(WorldStateTransforms.CanAct(definition, new("turn", 0), WorldPrincipal.Seat(0)));
        Assert.False(WorldStateTransforms.CanAct(definition, new("turn", 1), WorldPrincipal.Seat(0)));

        Assert.True(CompiledWorldPatterns.TryCompileAll(definition, out var patterns, []));
        Assert.True(WorldStateTransforms.TryApply(definition, Ray(), WorldPrincipal.Seat(0), 1, "test", out var candidate, out var reason, patterns), reason);
        var advanced = WorldStateTransforms.Advance(candidate, "turn");

        Assert.Equal(1L, Read(advanced).Sequence);
        Assert.False(WorldStateTransforms.CanAct(advanced, new("turn", 0), WorldPrincipal.Seat(0)));
        Assert.True(WorldStateTransforms.CanAct(advanced, new("turn", 1), WorldPrincipal.Seat(0)));
    }

    [Fact]
    public void AStaleSequenceRefusesAdmissionRegardlessOfActor() {
        var definition = Document(Phase(sequence: 3));

        Assert.False(WorldStateTransforms.CanAct(definition, new("turn", 2), WorldPrincipal.Seat(0)));
        Assert.False(WorldStateTransforms.CanAct(definition, new("turn", 2), WorldPrincipal.World));
        Assert.True(WorldStateTransforms.CanAct(definition, new("turn", 3), WorldPrincipal.Seat(0)));
    }

    [Fact]
    public void NamingAParticipantIsWorldProgramOnly() {
        var definition = Document(Phase());

        Assert.False(WorldStateTransforms.CanAct(definition, new("turn", 0, "seat1"), WorldPrincipal.Seat(0)));
        Assert.True(WorldStateTransforms.CanAct(definition, new("turn", 0, "seat1"), WorldPrincipal.World));
    }

    [Fact]
    public void EveryPhaseRowRequiresAPlainIntegerRowWithoutCellsOrCapacity() {
        Assert.True(WorldDefinitionValidator.TryValidateLocally(Document(Phase()), out var reason), reason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(
            Document(new WorldStateRow(WorldCellName.Parse("turn"), CellKind.Int, Phase: new(), Capacity: 4)), out var capacityReason));
        Assert.Contains("without cells/capacity", capacityReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(
            Document(new WorldStateRow(WorldCellName.Parse("turn"), CellKind.Bool, Phase: new())), out var kindReason));
        Assert.Contains("without cells/capacity", kindReason);
    }

    [Fact]
    public void ARowTaggedPhaseOfRefusesATransformWithoutItsGuardAndAMatchingGuardAdvancesTheGenerationLive() {
        var definition = Document(Phase(), Board() with { PhaseOf = "turn" });
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);

        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 1, 1, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.TransformState(WorldPrincipal.Console, Ray()))), _ => { });
        fixture.Step();
        Assert.Equal(0L, Read(fixture.Server.Definition).Sequence);

        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 2, 2, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.TransformState(WorldPrincipal.Console, Ray(), new("turn", 0)))), _ => { });
        fixture.Step();
        Assert.Equal(1L, Read(fixture.Server.Definition).Sequence);
        Assert.NotEqual(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
    }

    [Fact]
    public void PhaseRowsRoundTripThroughTheStrictWireShape() {
        var definition = Document(Phase(sequence: 4));

        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        var phase = WorldDefinitionRows.FindStateRow(parsed.State, "turn")!.Phase!;
        Assert.Equal(4L, phase.Sequence);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(parsed, out var reason), reason);
    }

    private static WorldStateRow Phase(long sequence = 0) => new(WorldCellName.Parse("turn"), CellKind.Int, Phase: new(sequence));
    private static WorldStateRow Board() => new(WorldCellName.Parse("board"), CellKind.Int,
        Cells: [new(WorldCellName.Parse("0"), 1), new(WorldCellName.Parse("1"), 2), new(WorldCellName.Parse("2"), 2), new(WorldCellName.Parse("3"), 1)], Board: new("map"));
    private static WorldStateTransform.SetRay Ray() => new("board", "0", "E", "capture", 1);
    private static WorldDefinition Document(params WorldStateRow[] rows) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows, Lattices: rows.Any(row => row.Board is not null) ? [new WorldStateLatticeTopology.Grid("map", new(0, 0, 0), 1, 4, 4)] : []),
        PatternsRaw = [new(WorldCellName.Parse("capture"), CellKind.Int,
            [new(WorldCellName.Parse("through"), 2, 2), new(WorldCellName.Parse("until"), 1, 1)],
            new WorldPatternNode.Sequence([new WorldPatternNode.Plus(new WorldPatternNode.Symbol("through")), new WorldPatternNode.Symbol("until")]))],
        Rules = [],
    };
    private static WorldStatePhase Read(WorldDefinition definition) => WorldDefinitionRows.FindStateRow(definition.State, "turn")!.Phase!;
}
