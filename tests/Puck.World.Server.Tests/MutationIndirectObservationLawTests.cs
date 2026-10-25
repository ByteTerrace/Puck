using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Indirect document and declared-set reads require the actor's Observe grant on the actual state rows.</summary>
public sealed class MutationIndirectObservationLawTests {
    private static readonly Principal Actor = Principal.Peer(generation: 1, index: 4);

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static void GrantMutation(WorldFixture fixture, WorldSection section) => fixture.Server.Grant(
        actor: Principal.Console, grant: new WorldGrant(Grantee: Actor, Capability: WorldCapability.Mutate,
            Subject: GrantSubject.Section(section: section), Exclusive: false, Budget: 16,
            KindMask: WorldMutationKindCatalog.KindsOf(section: section)));
    private static void Observe(WorldFixture fixture, string row) => fixture.Server.Grant(
        actor: Principal.Console, grant: new WorldGrant(Grantee: Actor, Capability: WorldCapability.Observe,
            Subject: GrantSubject.State(name: row), Exclusive: false, Budget: 16));
    private static WorldMutationOutcome Apply(WorldFixture fixture, WorldMutation mutation) {
        var results = new List<WorldSubmissionResult>();

        fixture.Server.Submit(new SubmissionEnvelope(ConnectionId: SubmissionEnvelope.LocalConnectionId,
            SessionGeneration: 0, Sequence: 1, CorrelationId: 1, Principal: Actor,
            Payload: new WorldSubmissionPayload.Mutation(Value: mutation), OperationId: Guid.NewGuid()), results.Add);
        fixture.Step();
        return Assert.IsType<WorldSubmissionResult.Mutation>(@object: Assert.Single(collection: results)).Outcome;
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ADeclaredSetRequiresObservationOfItsSourcesRatherThanItsOwnName(bool combine) {
        var board = new WorldStateRow(Name(value: "secret"), CellKind.Int, Domain: new StateDomain.CellsOf("map"),
            Cells: [new StateCell(Name(value: "0"), CellValue.Int(value: 1))]);
        var target = board with { Name = Name(value: "target"), Cells = [] };
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [board, target],
                Lattices: [new LatticeTopology.Grid("map", new DocumentVector3(x: 0, y: 0, z: 0), 1, 2, 2)]),
            SetsRaw = [new CellSetRow(Name: Name(value: "selection"), Set: new CellSetExpression.Both(Items: [
                new CellSetExpression.Board(Name(value: "secret"), 1, 1), new CellSetExpression.Everything()]))],
        });

        GrantMutation(fixture: fixture, section: WorldSection.State);
        fixture.Server.Grant(actor: Principal.Console, grant: new WorldGrant(Grantee: Actor,
            Capability: WorldCapability.Edit, Subject: GrantSubject.State(name: "target"), Exclusive: false));
        Observe(fixture: fixture, row: "selection");
        var mutation = new WorldMutation.TransformState(Actor, (combine
            ? new StateTransform.BoardCombine(Row: "target", Operation: BoardCombineOp.Or, Left: "selection", Right: "selection")
            : new StateTransform.WriteSet(Row: "target", Set: "selection", Value: 1)));
        var denied = Apply(fixture: fixture, mutation: mutation);

        Assert.False(condition: denied.Applied);
        Assert.Contains("cannot observe state:secret", denied.Detail);
        Assert.Empty(collection: WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "target")!.Cells!);
        Observe(fixture: fixture, row: "secret");
        var allowed = Apply(fixture: fixture, mutation: mutation);

        Assert.True(condition: allowed.Applied, userMessage: allowed.Detail);
        Assert.Equal(1, WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "target")!.Cells![0].Value.AsInt);
    }
    [Fact]
    public void ASubmittedDocumentValueRequiresObservationBeforeResolvingItsStateReference() {
        var crate = CreationFixtures.UnitSphere(id: "crate");
        var placement = new WorldPlacement(Id: "placed", PrototypeId: "crate",
            Position: System.Text.Json.JsonSerializer.Deserialize<DocumentVector3>(json: "\"state.secret\"", options: DocumentJsonOptions.Shared)!,
            Scale: 1, YawDegrees: 0);
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument() with {
            CreationsRaw = [crate],
            StateRaw = new WorldStateSection(World: [new WorldStateRow(Name(value: "secret"), CellKind.Text,
                Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Text(value: "[2,3,4]"))])]),
        });

        GrantMutation(fixture: fixture, section: WorldSection.Placements);
        var mutation = new WorldMutation.UpsertPlacement(Placement: placement, Principal: Actor);
        var denied = Apply(fixture: fixture, mutation: mutation);

        Assert.False(condition: denied.Applied);
        Assert.Contains("cannot observe state:secret", denied.Detail);
        Assert.Null(@object: WorldDefinitionRows.FindPlacement(fixture.Server.Definition.Placements, "placed"));
        Observe(fixture: fixture, row: "secret");
        var allowed = Apply(fixture: fixture, mutation: mutation);

        Assert.True(condition: allowed.Applied, userMessage: allowed.Detail);
        Assert.Equal(new System.Numerics.Vector3(x: 2, y: 3, z: 4), ((System.Numerics.Vector3)WorldDefinitionRows.FindPlacement(fixture.Server.Definition.Placements, "placed")!.Position));
    }
}
