using System.Numerics;
using System.Text.Json;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class WorldIndirectParticipationLawTests {
    [Fact]
    public void ADeliveredPlacementIdentityStaysInThePinnedEntityImage() {
        var mirror = new WorldSessionMirror(placeholder: Fixtures.BuildDocument());
        var entry = new EntitySnapshot(Active: true, BodyColor: Vector3.One, CatalogRig: 0,
            Continuity: EntityContinuity.Continuous, Generation: 1, Index: 0, Kit: 0, Look: 0,
            Orientation: Quaternion.Identity, Position: Vector3.Zero, PlacementId: "old-body");

        mirror.DeliverSnapshot(snapshot: new WorldSnapshot(Authority: "neighbour", Entries: new[] { entry }, Revision: 1, StepTicks: 1, Tick: 1));
        var count = WorldBodiesLimits.CapacityCeiling;
        var placementIds = new string?[count];
        var addresses = new WorldEntityAddress[count];

        mirror.CopySnapshotTo(active: new bool[count], addresses: addresses, arrivalTimestamp: out _, bodyContacts: new WorldBodyContactMode[count], catalogRigs: new byte[count],
            colliders: new FixedWorldCollider?[count], colors: new Vector3[count], currentOrientations: new Quaternion[count], currentPositions: new Vector3[count], looks: new WorldLook[count],
            placementIds: placementIds, previousOrientations: new Quaternion[count], previousPositions: new Vector3[count], revision: out _, stepSeconds: out _, tick: out var tick);
        mirror.DeliverSnapshot(snapshot: new WorldSnapshot(Authority: "neighbour", Entries: new[] { entry with { Generation = 2, PlacementId = "new-body" } },
            Revision: 2, StepTicks: 1, Tick: 2));
        Assert.Equal(actual: tick, expected: 1UL);
        Assert.Equal(1, addresses[0].Generation);
        Assert.Equal("old-body", placementIds[0]);
        Assert.Equal("new-body", mirror.PlacementId(index: 0));
    }
    [InlineData(SdfIndirectParticipation.Default)]
    [InlineData(SdfIndirectParticipation.Cast)]
    [InlineData(SdfIndirectParticipation.Receive)]
    [InlineData(SdfIndirectParticipation.Off)]
    [Theory]
    public void AuthoredPlacementPoliciesReachStaticAndBodyInstances(SdfIndirectParticipation policy) {
        var creation = CreationFixtures.Prototype(CreationFixtures.Document(name: "indirect-body", palette: CreationFixtures.GreyAndBlue,
            shapes: [CreationFixtures.UnitSphereShape]));
        var placement = new WorldPlacement("body", creation.Id, Vector3.Zero, 0f, 1f, Indirect: policy);
        var definition = Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [placement],
            RenderRaw = WorldRenderDefaults.Absent with { Indirect = new WorldRenderIndirect(SdfIndirectParticipation.Off) },
        };
        var bytes = WorldDefinitionSerialization.Serialize(definition: definition);
        using var wire = JsonDocument.Parse(bytes);

        Assert.Equal("Off", wire.RootElement.GetProperty(propertyName: "render").GetProperty(propertyName: "indirect").GetProperty(propertyName: "bodies").GetString());
        var restored = WorldDefinitionSerialization.Deserialize(bytes);

        Assert.Equal(SdfIndirectParticipation.Off, restored.Render.Indirect!.Bodies);
        Assert.Equal(((policy == SdfIndirectParticipation.Default) ? SdfIndirectParticipation.Off : policy),
            WorldIndirectParticipation.ForPlacement(definition: restored, placementId: "body"));
        Assert.Equal(SdfIndirectParticipation.Off, WorldIndirectParticipation.ForPlacement(definition: restored, placementId: null));
        Assert.Equal(SdfIndirectParticipation.Off, SdfIndirectPolicy.Resolve(
            WorldIndirectParticipation.ForPlacement(definition: restored, placementId: null), dynamic: true,
            tier: SdfIndirectTier.High, bodies: SdfIndirectParticipation.Cast));
        Assert.Equal(SdfIndirectParticipation.Off, WorldIndirectParticipation.ForPlacement(definition: restored, placementId: "missing"));
        Assert.Equal(SdfIndirectParticipation.Default, WorldIndirectParticipation.ForPlacement(
            definition: restored with { RenderRaw = restored.Render with { Indirect = null } }, placementId: null));
        var builder = new SdfProgramBuilder();

        WorldPlacementStamper.EmitStatic(builder: builder, creations: restored.Creations, placements: restored.Placements, definition: restored);
        Assert.Equal(policy, Assert.Single(collection: builder.Build(buildInstanceGrid: false).Instances).Indirect);

        var pool = new WorldStampPool();

        pool.Reconcile(placements: [], creations: [creation], dynamics: [], bodyStamps: [
            new WorldStampPool.BodyStamp(0, creation, 1f, WorldLook.Implicit, policy),
        ]);
        builder = new SdfProgramBuilder();
        pool.Emit(builder, WorldBakedColors.Of(definition: restored), probeWorstCase: false, maxPlacementScale: 1f, slotBase: 0);
        var body = Assert.Single(collection: builder.Build(buildInstanceGrid: false).Instances);

        Assert.True(condition: body.IsDynamic);
        Assert.Equal(policy, body.Indirect);
    }
    [Fact]
    public void UndefinedWorldBodyAndPlacementPoliciesAreRefusedByName() {
        var definition = Fixtures.BuildDocument();
        var badBodies = definition with { RenderRaw = definition.Render with { Indirect = new WorldRenderIndirect(((SdfIndirectParticipation)9)) } };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: badBodies, neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "render.indirect.bodies");
        var creation = CreationFixtures.Prototype(CreationFixtures.Document(name: "indirect-body", palette: CreationFixtures.GreyAndBlue,
            shapes: [CreationFixtures.UnitSphereShape]));
        var badPlacement = definition with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [
            new WorldPlacement("body", creation.Id, Vector3.Zero, 0f, 1f, Indirect: ((SdfIndirectParticipation)9)),
        ],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: badPlacement, neighbours: null, reason: out reason));
        Assert.Contains(actualString: reason, expectedSubstring: ".indirect");
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: badPlacement with { PlacementRowsRaw = [badPlacement.Placements[0] with { Indirect = SdfIndirectParticipation.Default }] }, neighbours: null, reason: out reason), userMessage: reason);
    }
}
