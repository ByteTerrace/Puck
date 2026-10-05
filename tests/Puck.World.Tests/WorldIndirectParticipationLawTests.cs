using System.Numerics;
using System.Text.Json;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldIndirectParticipationLawTests {
    [Fact]
    public void ADeliveredPlacementIdentityStaysInThePinnedEntityImage() {
        var mirror = new WorldSessionMirror(Fixtures.BuildDocument());
        var entry = new EntitySnapshot(Active: true, BodyColor: Vector3.One, CatalogRig: 0,
            Continuity: EntityContinuity.Continuous, Generation: 1, Index: 0, Kit: 0, Look: 0,
            Orientation: Quaternion.Identity, Position: Vector3.Zero, PlacementId: "old-body");
        mirror.DeliverSnapshot(new WorldSnapshot(Authority: "neighbour", Entries: new[] { entry }, Revision: 1, StepTicks: 1, Tick: 1));
        var count = WorldBodiesLimits.CapacityCeiling;
        var placementIds = new string?[count];
        var addresses = new WorldEntityAddress[count];
        mirror.CopySnapshotTo(new bool[count], addresses, placementIds, new Vector3[count], new Quaternion[count],
            new Vector3[count], new Quaternion[count], new Vector3[count], new WorldLook[count], new byte[count],
            new FixedWorldCollider?[count], new WorldBodyContactMode[count], out var tick, out _, out _, out _);
        mirror.DeliverSnapshot(new WorldSnapshot(Authority: "neighbour", Entries: new[] { entry with { Generation = 2, PlacementId = "new-body" } },
            Revision: 2, StepTicks: 1, Tick: 2));
        Assert.Equal(1UL, tick);
        Assert.Equal(1, addresses[0].Generation);
        Assert.Equal("old-body", placementIds[0]);
        Assert.Equal("new-body", mirror.PlacementId(0));
    }

    [Theory]
    [InlineData(SdfIndirectParticipation.Default)]
    [InlineData(SdfIndirectParticipation.Cast)]
    [InlineData(SdfIndirectParticipation.Receive)]
    [InlineData(SdfIndirectParticipation.Off)]
    public void AuthoredPlacementPoliciesReachStaticAndBodyInstances(SdfIndirectParticipation policy) {
        var creation = CreationFixtures.Prototype(CreationFixtures.Document(name: "indirect-body", palette: CreationFixtures.GreyAndBlue,
            shapes: [CreationFixtures.UnitSphereShape]));
        var placement = new WorldPlacement("body", creation.Id, Vector3.Zero, 0f, 1f, Indirect: policy);
        var definition = Fixtures.BuildDocument() with { CreationsRaw = [creation], PlacementRowsRaw = [placement],
            RenderRaw = WorldRenderDefaults.Absent with { Indirect = new WorldRenderIndirect(SdfIndirectParticipation.Off) } };
        var bytes = WorldDefinitionSerialization.Serialize(definition);
        using var wire = JsonDocument.Parse(bytes);
        Assert.Equal("Off", wire.RootElement.GetProperty("render").GetProperty("indirect").GetProperty("bodies").GetString());
        var restored = WorldDefinitionSerialization.Deserialize(bytes);
        Assert.Equal(SdfIndirectParticipation.Off, restored.Render.Indirect!.Bodies);
        Assert.Equal(policy == SdfIndirectParticipation.Default ? SdfIndirectParticipation.Off : policy,
            WorldIndirectParticipation.ForPlacement(restored, "body"));
        Assert.Equal(SdfIndirectParticipation.Off, WorldIndirectParticipation.ForPlacement(restored, null));
        Assert.Equal(SdfIndirectParticipation.Off, SdfIndirectPolicy.Resolve(
            WorldIndirectParticipation.ForPlacement(restored, null), dynamic: true,
            tier: SdfIndirectTier.High, bodies: SdfIndirectParticipation.Cast));
        Assert.Equal(SdfIndirectParticipation.Off, WorldIndirectParticipation.ForPlacement(restored, "missing"));
        Assert.Equal(SdfIndirectParticipation.Default, WorldIndirectParticipation.ForPlacement(
            restored with { RenderRaw = restored.Render with { Indirect = null } }, null));
        var builder = new SdfProgramBuilder();
        WorldPlacementStamper.EmitStatic(builder: builder, creations: restored.Creations, placements: restored.Placements, definition: restored);
        Assert.Equal(policy, Assert.Single(builder.Build(buildInstanceGrid: false).Instances).Indirect);

        var pool = new WorldStampPool();
        pool.Reconcile(placements: [], creations: [creation], dynamics: [], bodyStamps: [
            new WorldStampPool.BodyStamp(0, creation, 1f, WorldLook.Implicit, policy),
        ]);
        builder = new SdfProgramBuilder();
        pool.Emit(builder, WorldBakedColors.Of(restored), probeWorstCase: false, maxPlacementScale: 1f, slotBase: 0);
        var body = Assert.Single(builder.Build(buildInstanceGrid: false).Instances);
        Assert.True(body.IsDynamic);
        Assert.Equal(policy, body.Indirect);
    }

    [Fact]
    public void UndefinedWorldBodyAndPlacementPoliciesAreRefusedByName() {
        var definition = Fixtures.BuildDocument();
        var badBodies = definition with { RenderRaw = definition.Render with { Indirect = new WorldRenderIndirect((SdfIndirectParticipation)9) } };
        Assert.False(WorldDefinitionValidator.TryValidate(definition: badBodies, neighbours: null, reason: out var reason));
        Assert.Contains("render.indirect.bodies", reason);
        var creation = CreationFixtures.Prototype(CreationFixtures.Document(name: "indirect-body", palette: CreationFixtures.GreyAndBlue,
            shapes: [CreationFixtures.UnitSphereShape]));
        var badPlacement = definition with { CreationsRaw = [creation], PlacementRowsRaw = [
            new WorldPlacement("body", creation.Id, Vector3.Zero, 0f, 1f, Indirect: (SdfIndirectParticipation)9),
        ] };
        Assert.False(WorldDefinitionValidator.TryValidate(definition: badPlacement, neighbours: null, reason: out reason));
        Assert.Contains(".indirect", reason);
        Assert.True(WorldDefinitionValidator.TryValidate(definition: badPlacement with { PlacementRowsRaw = [badPlacement.Placements[0] with { Indirect = SdfIndirectParticipation.Default }] }, neighbours: null, reason: out reason), reason);
    }
}
