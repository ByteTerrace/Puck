using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldPlacementSurfaceLawTests {
    private static WorldPrototype Surface(string id, SdfSolidPrimitive kind = SdfSolidPrimitive.Plane) =>
        CreationFixtures.Prototype(CreationFixtures.Document(id, [CreationFixtures.Shape(kind)]), id);
    private static WorldPlacement Ground(string id, string prototype, float y = 0f, float scale = 1f) =>
        new(id, prototype, new Vector3(0, y, 0), 0, scale, Solid: new WorldSolid(0));
    private static WorldPlacement Root(string id, string target = "ground") =>
        new(id, "marker", Vector3.Zero, 0, 1, Surface: new WorldPlacementSurface([target],
            new Vector3(0, 4, 0), -Vector3.UnitY, 10));
    private static WorldDefinition World(params WorldPlacement[] rows) => new(CreationsRaw:
        [Surface("plane"), CreationFixtures.UnitSphere("marker")], PlacementsRaw: new WorldPlacementsSection(Rows: rows));
    private static long Read(WorldBootWork work, Puck.Abstractions.Counting.WorkKind kind) {
        Assert.True(work.TryRead(kind, out var value)); return value;
    }

    [Fact]
    public void OneProjectedRootIsTheSameCanonicalFrameForEveryConsumerAndDoesNoWorkWhenRetained() {
        var work = new WorldBootWork(); using var attributed = WorldBootWork.Attribute(work);
        var root = Root("root") with { Surface = Root("root").Surface! with { Offset = -0.125f } };
        var world = World(Ground("ground", "plane", y: 2), root);
        var stamp = WorldPlacementStamp.Resolve(world, root);
        Assert.Single(stamp.Instances.ToArray());
        Assert.InRange((double)stamp.Instances[0].Origin.Y, 1.874, 1.876);
        Assert.Equal(stamp.Frame, WorldDefinitionRows.ResolvedFrame(world, root));
        Assert.Equal(stamp.Instances[0].Origin.ToVector3(), world.PlacementFrames[root.Id].Position);
        var rays = Read(work, WorldBootWork.SurfaceRays);
        Assert.Equal(1, rays);
        Assert.Equal(1, Read(work, WorldBootWork.SurfaceNormals));
        for (var i = 0; i < 100; i++) {
            Assert.Same(stamp, WorldPlacementStamp.Resolve(world, root));
            Assert.Equal(stamp.Frame, world.PlacementFrames[root.Id]);
        }
        Assert.Equal(rays, Read(work, WorldBootWork.SurfaceRays));
        Assert.Equal(1, Read(work, WorldBootWork.SurfaceFields));
    }

    [Fact]
    public void NamedGroundEditsInvalidateOnlyDependentOriginsWhileUnrelatedWorldRootsRetainThem() {
        var a = Root("a"); var b = Root("b", "other");
        var ground = Ground("ground", "plane"); var other = Ground("other", "plane", y: -1);
        var world = World(ground, other, a, b);
        var beforeA = WorldPlacementStamp.Resolve(world, a); var beforeB = WorldPlacementStamp.Resolve(world, b);
        Assert.Same(beforeA, WorldPlacementStamp.Resolve(world with { Generation = new WorldGenerationDefaults(0) }, a));
        var changed = world with { PlacementRowsRaw = [ground with { Position = new Vector3(0, 2, 0) }, other, a, b] };
        var afterA = WorldPlacementStamp.Resolve(changed, a);
        Assert.NotSame(beforeA, afterA);
        Assert.Same(beforeB, WorldPlacementStamp.Resolve(changed, b));
        Assert.InRange((double)(afterA.Instances[0].Origin.Y - beforeA.Instances[0].Origin.Y), 1.999, 2.001);
        Assert.Equal(afterA.Frame, changed.PlacementFrames[a.Id]);
    }

    [Fact]
    public void RejectedCandidatesKeepTheirOriginalOccurrenceIdentitiesAndRequireHitRefuses() {
        var root = Root("root") with {
            Position = new Vector3(-2, 0, 0),
            Distribution = new WorldDistribution(new WorldDistributionRegion.Lattice(Vector3.UnitX * 2, 3,
                Vector3.UnitZ, 1), new WorldSequence(WorldSequence.None, 0, 0))
        };
        var world = new WorldDefinition(CreationsRaw: [Surface("ball", SdfSolidPrimitive.Sphere),
            CreationFixtures.UnitSphere("marker")], PlacementsRaw: new WorldPlacementsSection(
            Rows: [Ground("ground", "ball"), root]));
        var stamp = WorldPlacementStamp.Resolve(world, root);
        Assert.Single(stamp.Instances.ToArray());
        Assert.Equal(1, stamp.OccurrenceAt(0));
        Assert.Equal((Vector3)root.Position, stamp.Frame.Position);
        var required = root with { Surface = root.Surface! with { RequireHit = true } };
        Assert.Throws<InvalidOperationException>(() => WorldPlacementStamp.Resolve(world, required));
    }

    [Fact]
    public void SeveralRowsShareOneSelectedSurfaceFieldAndASelectedScaleChangeMovesTheirRoots() {
        var work = new WorldBootWork(); using var attributed = WorldBootWork.Attribute(work);
        var root = Root("root");
        var ground = Ground("ground", "ball");
        var world = new WorldDefinition(CreationsRaw: [Surface("ball", SdfSolidPrimitive.Sphere),
            CreationFixtures.UnitSphere("marker")], PlacementsRaw: new WorldPlacementsSection(
            Rows: [ground, root, Root("second")]));
        var before = WorldPlacementStamp.Resolve(world, root);
        _ = WorldPlacementStamp.Resolve(world, world.Placements[2]);
        Assert.Equal(1, Read(work, WorldBootWork.SurfaceFields));
        var changed = world with { PlacementRowsRaw = [ground with { Scale = 2 }, root] };
        var after = WorldPlacementStamp.Resolve(changed, root);
        Assert.InRange((double)(after.Instances[0].Origin.Y - before.Instances[0].Origin.Y), 0.999, 1.001);
    }

    [Fact]
    public void SelfDependenciesAreRefusedBeforeFieldConstruction() {
        var root = Root("root"); var ground = Ground("ground", "plane") with { Surface = root.Surface };
        var world = World(ground, root);
        Assert.False(WorldPlacementStamp.TryValidateSurface(world, root, out var reason));
        Assert.Contains("static contact", reason);
        Assert.Throws<InvalidOperationException>(() => WorldPlacementStamp.Resolve(world, root));
        var self = root with { Surface = root.Surface! with { Targets = [root.Id] } };
        Assert.False(WorldPlacementStamp.TryValidateSurface(World(Ground("ground", "plane"), self), self, out _));
    }

    [Fact]
    public void AnAcyclicProjectedSourceAndItsDependentChildUseTheSameResolvedFrames() {
        var floor = Ground("floor", "plane");
        var ledge = Ground("ledge", "marker") with {
            Surface = Root("unused", "floor").Surface! with { Offset = 2 }
        };
        var root = Root("root", "ledge");
        var child = new WorldPlacement("child", "marker", Vector3.UnitX, 0, 1, Parent: root.Id);
        var world = World(floor, ledge, root, child);
        Assert.True(WorldPlacementStamp.TryValidateSurface(world, root, out var reason), reason);
        Assert.InRange((double)WorldPlacementStamp.Resolve(world, ledge).Frame.FixedPosition.Y, 1.999, 2.001);
        var projected = WorldPlacementStamp.Resolve(world, root).Frame;
        Assert.InRange((double)projected.FixedPosition.Y, 2.999, 3.001);
        Assert.Equal(projected.FixedPosition + FixedVector3.UnitX, world.PlacementFrames[child.Id].FixedPosition);
    }

    [Fact]
    public void ASurfaceDependencyThroughAParentIsRefusedWithoutConstructingAField() {
        var work = new WorldBootWork(); using var attributed = WorldBootWork.Attribute(work);
        var root = Root("root", "dependent");
        var dependent = Ground("dependent", "marker") with { Parent = root.Id };
        var world = World(root, dependent);
        Assert.False(WorldPlacementStamp.TryValidateSurface(world, root, out var reason));
        Assert.Contains("cyclic", reason);
        Assert.Equal(0, Read(work, WorldBootWork.SurfaceFields));
        Assert.Throws<InvalidOperationException>(() => WorldPlacementStamp.Resolve(world, root));
    }

    [Fact]
    public void TheCanonicalScatterPlanUsesTheExistingSeededSamplerAndFixedOrientation() {
        var row = new WorldPlacement("sampled", "marker", new Vector3(3, 0, -5), 37, 1,
            Distribution: new WorldDistribution(new WorldDistributionRegion.Scatter(.25f, 32, 24, 8, 1, 317),
                new WorldSequence(WorldSequence.None, 0, 0)));
        var world = World(row) with { Generation = new WorldGenerationDefaults(517) };
        var plan = WorldPlacementStamp.Resolve(world, row);
        var points = CreationStampSampling.ResolveScatter(FixedQ4816.FromDouble(.25), 32, 24, 8, 1, 317, 517);
        Assert.Equal(points.Length, plan.Count);
        for (var i = 0; i < points.Length; i++) {
            Assert.Equal(FixedVector3.FromVector3(row.Position) + plan.FixedRotation.Rotate(points[i]), plan.Instances[i].Origin);
            Assert.Equal(i, plan.OccurrenceAt(i));
        }
    }
    [Fact]
    public void GravityReadsTheProjectedSingleRootButKeepsOneAuthoredDistributionGroupSource() {
        var single = Root("single");
        var group = Root("group") with {
            Position = new Vector3(3, 0, 0),
            Distribution = new WorldDistribution(new WorldDistributionRegion.Lattice(Vector3.UnitX, 2,
                Vector3.UnitZ, 1), new WorldSequence(WorldSequence.None, 0, 0))
        };
        var world = World(Ground("ground", "plane", y: 2), single, group) with {
            GravityRaw = new WorldGravity(WorldGravitySolver.Pairwise, 1, 1,
                [new WorldGravityAttractor(single.Id, 1), new WorldGravityAttractor(group.Id, 1)])
        };
        var compiled = FixedWorldGravity.Compile(world);
        Assert.Equal(2, compiled.Attractors.Length);
        Assert.Equal(WorldPlacementStamp.Resolve(world, single).Instances[0].Origin, compiled.Attractors[0].Position);
        Assert.Equal(FixedVector3.FromVector3(group.Position), compiled.Attractors[1].Position);
    }

}
