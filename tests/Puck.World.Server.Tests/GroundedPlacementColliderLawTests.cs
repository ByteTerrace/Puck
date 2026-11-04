using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class GroundedPlacementColliderLawTests {
    [Fact]
    public void ASelectedGroundEditRebuildsDependentDistributedCollidersAtTheCanonicalRoots() {
        var plane = CreationFixtures.Prototype(CreationFixtures.Document("plane", [CreationFixtures.Shape(SdfSolidPrimitive.Plane)]));
        var ground = new WorldPlacement("ground", plane.Id, Vector3.Zero, 0, 1, Solid: new WorldSolid(0));
        var roots = new WorldPlacement("roots", "marker", new Vector3(-2, 0, 0), 0, 1,
            Solid: new WorldSolid(0),
            Distribution: new WorldDistribution(new WorldDistributionRegion.Lattice(Vector3.UnitX * 2, 3,
                Vector3.UnitZ, 1), new WorldSequence(WorldSequence.None, 0, 0)),
            Surface: new WorldPlacementSurface([ground.Id], new Vector3(0, 4, 0), -Vector3.UnitY, 10));
        var world = Fixtures.BuildGradientUpDocument(false) with {
            CreationsRaw = [plane, CreationFixtures.UnitSphere("marker")],
            PlacementRowsRaw = [ground, roots]
        };
        var before = WorldColliderSet.Build(world);
        var changed = world with { PlacementRowsRaw = [ground with { Position = new Vector3(0, 2, 0) }, roots] };
        var after = WorldColliderSet.Build(changed, before);
        var oldRoots = before.DiagnosticColliders.Where(c => c.Stamp == roots.Id).ToArray();
        var newRoots = after.DiagnosticColliders.Where(c => c.Stamp == roots.Id).ToArray();
        Assert.Equal(3, oldRoots.Length);
        Assert.Equal(3, newRoots.Length);
        var stamp = WorldPlacementStamp.Resolve(changed, roots);
        for (var i = 0; i < newRoots.Length; i++) {
            Assert.Equal(stamp.Instances[i].Origin, newRoots[i].Collider.Center);
            Assert.InRange((double)(newRoots[i].Collider.Center.Y - oldRoots[i].Collider.Center.Y), 1.999, 2.001);
        }
        Assert.True(WorldSolidField.TryBuild(changed, out var field, out var reason), reason);
        foreach (var instance in stamp.Instances) {
            Assert.True(field!.Probe(instance.Origin + FixedVector3.UnitY, out var distance, out _, out _));
            Assert.InRange((double)distance, -.002, .002);
        }
    }
}
