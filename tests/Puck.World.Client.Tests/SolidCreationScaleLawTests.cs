using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A solid's root follows the contact sample even when current rule state names another size.</summary>
public sealed class SolidCreationScaleLawTests {
    [Fact]
    public void PackedScaleUsesTheSampleAndRevalidatesWithoutAStateWrite() {
        var creation = CreationFixtures.Prototype(CreationFixtures.Document("growing", [CreationFixtures.UnitSphereShape],
            CreationFixtures.GreyAndBlue) with { SizeBy = "state.growth" });
        var row = new WorldPlacement("growing", creation.Id, Vector3.Zero, 0, 2, Solid: new WorldSolid(Margin: 0));
        var definition = CreationDomainParentLawTests.Definition(creation) with {
            CollisionRaw = Fixtures.BuildGradientUpDocument(gradientUp: false).Collision with { Requirements = [] },
            PlacementRowsRaw = [row],
            StateRaw = new WorldStateSection(World: [StateFixtures.FixedSlot(name: "growth", rawBits: FixedQ4816.One.Value)]),
        };
        var client = ClientFixtures.Client(definition: definition);
        var pool = new WorldCreationInstances();

        pool.Reconcile(WorldPlacementFrameCompilation.Compile(placements: [row], worldSeed: 0), [row], [creation], [], []);
        var transforms = new DynamicTransform[pool.DynamicSlotCount];

        void Deliver(double size, ulong tick) => client.DeliverSnapshot(snapshot: new WorldSnapshot(tick, 0, 1680, ReadOnlyMemory<EntitySnapshot>.Empty,
            SolidScales: new WorldSolidScale[] { new(Placement: "growing", Scale: FixedQ4816.FromDouble(value: size)) }));
        float Pack(float alpha) {
            client.UpdateRenderPoses(alpha: alpha);
            pool.Tick(deltaSeconds: (1f / 60f));
            var moved = new SdfMovedTransforms();

            moved.Begin(everything: false, tableRows: transforms.Length);
            pool.PackTransforms(transforms, client, moved, 0, new Vector3(x: 0, y: -1000, z: 0));
            return transforms[0].Scale;
        }
        Deliver(size: 0.25, tick: 1);
        Assert.Equal(0.5f, Pack(alpha: 1));
        Deliver(size: 0.75, tick: 2);
        Assert.Equal(1f, Pack(alpha: 0.5f));
        Assert.Equal(1.5f, Pack(alpha: 1));
        // A parked root hides the geometry while keeping a nonzero transform for the inverse field matrix.
        Deliver(size: 0, tick: 3);
        Pack(alpha: 1);
        Assert.Equal(-1000, transforms[0].Position.Y);
    }
}
