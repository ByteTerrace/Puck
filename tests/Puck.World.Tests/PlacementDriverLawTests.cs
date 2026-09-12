using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Scenery drivers animate without borrowing an inhabitant's facts or duplicating static geometry.</summary>
public sealed class PlacementDriverLawTests {
    [Fact]
    public void DriverOnlyPlacementCannotBypassAnimatedDistributionRestrictions() {
        var document = CreationDomainParentLawTests.FollowRig() with {
            Drivers = [new CreationDriverDocument(Name: "sway", Signal: "time", Cadence: 1f)],
        };
        var creation = CreationDomainParentLawTests.Prototype(document);
        var placement = new WorldPlacement("grass", creation.Id, Vector3.Zero, 0f, 1f,
            Distribution: new WorldDistribution(
                Region: new WorldDistributionRegion.Scatter(CellSize: 1f, Depth: 10,
                    Radius: 1, Seed: 3u, Spacing: 3, Width: 10),
                Fill: new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0f)));
        var definition = Fixtures.BuildDocument() with {
            CreationsRaw = [creation], PlacementRowsRaw = [placement],
        };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var reason));
        Assert.Contains("distribution/mirror facets are static-stamp-only", reason);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(
            definition with { PlacementRowsRaw = [placement with { Distribution = null }] }, out reason), reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UninhabitedTimeDriverMovesOnlyWhenItsOwnGateHolds(bool requireGrounded) {
        var shape = new ShapeDocument(
            Id: 0, Name: "blade", Type: SdfSolidPrimitive.Box,
            Position: Vector3.UnitY, Rotation: Quaternion.Identity,
            Scale: new Vector3(.05f, 1f, .01f), Material: 0,
            Blend: SdfBlendOp.Union, Smooth: 0f, Group: null,
            Swings: [new ShapeSwingDocument(Driver: "wind", Pivot: Vector3.Zero,
                Axis: Vector3.UnitZ, Amplitude: .5f)]);
        var creation = CreationDomainParentLawTests.Prototype(new CreationDocument(
            Schema: CreationDocument.CurrentSchema, Name: "wind",
            Palette: [new("#779944", null, null, null)], Shapes: [shape], Frames: null,
            Drivers: [new CreationDriverDocument(Name: "wind", Signal: "time", Cadence: 1f,
                When: requireGrounded ? ["Grounded"] : null, BlendInSeconds: 0f)]));
        var placement = new WorldPlacement("grass", creation.Id, new Vector3(3f, 0f, 4f), 0f, 1f);
        Assert.True(WorldPlacementStamper.IsAnimated(creation));
        Assert.False(WorldPlacementStamper.IsStaticStamp(placement, creation));
        Assert.Equal(0, WorldPlacementStamper.StaticStampInstances([creation], [placement]));
        var pool = new WorldStampPool();
        pool.Reconcile([placement], [creation], [], []);
        var definition = CreationDomainParentLawTests.Definition(creation);
        var client = CreationDomainParentLawTests.Client(definition);
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];
        CreationDomainParentLawTests.Advance(client, pool, transforms, 1);
        var root = transforms[0];
        var initial = transforms[1];
        for (ulong tick = 2; tick <= 31; tick++) {
            CreationDomainParentLawTests.Advance(client, pool, transforms, tick);
        }
        Assert.Equal(root.Position, transforms[0].Position);
        Assert.Equal(root.Orientation, transforms[0].Orientation);
        var movement = Vector3.Distance(initial.Position, transforms[1].Position);
        if (requireGrounded) {
            // The fixture has a grounded body, but this placement does not ride it.
            Assert.InRange(movement, 0f, 1e-6f);
        } else {
            Assert.InRange(movement, .1f, .5f);
        }
    }
}
