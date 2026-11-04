using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class GroundedPlacementEmissionLawTests {
    private static WorldDefinition Document(out WorldPlacement ground, out WorldPlacement roots) {
        var plane = CreationFixtures.Prototype(CreationFixtures.Document("plane", [CreationFixtures.Shape(SdfSolidPrimitive.Plane)]));
        ground = new WorldPlacement("ground", plane.Id, Vector3.Zero, 0, 1, Solid: new WorldSolid(0));
        roots = new WorldPlacement("roots", "marker", new Vector3(-2, 0, 0), 0, 1,
            Distribution: new WorldDistribution(new WorldDistributionRegion.Lattice(Vector3.UnitX * 2, 3,
                Vector3.UnitZ, 1), new WorldSequence(WorldSequence.None, 0, 0)),
            Surface: new WorldPlacementSurface([ground.Id], new Vector3(0, 4, 0), -Vector3.UnitY, 10));
        return new WorldDefinition(CreationsRaw: [plane, CreationFixtures.UnitSphere("marker")],
            PlacementsRaw: new WorldPlacementsSection(Rows: [ground, roots]));
    }
    [Fact]
    public void ASelectedGroundEditMovesSharedCopiesWithoutRebakingTheirLocalAssetOrLosingIdentity() {
        var world = Document(out var ground, out var roots);
        var assets = new WorldRigidAssets();
        (uint[] Words, Vector3[] Origins, object[] Identities) Emit(WorldDefinition current) {
            assets.ReconcileStatic(current, [roots]);
            assets.Reconcile(current.Creations, [], null, [roots]);
            var builder = new SdfProgramBuilder();
            assets.Emit(builder, WorldBakedColors.Of(current), 0, null, new WorldPickMapBuilder());
            return (builder.Build().Words.ToArray(), assets.Copies.Select(c => c.Transform.Position).ToArray(),
                assets.Copies.Select(c => c.Identity).ToArray());
        }
        var before = Emit(world);
        var after = Emit(world with { PlacementRowsRaw = [ground with { Position = new Vector3(0, 2, 0) }, roots] });
        Assert.Equal(before.Words, after.Words);
        Assert.Equal(before.Identities, after.Identities);
        Assert.Equal(3, after.Origins.Length);
        for (var i = 0; i < after.Origins.Length; i++) {
            Assert.InRange(after.Origins[i].Y - before.Origins[i].Y, 1.999f, 2.001f);
        }
    }
    [Fact]
    public void ASelectedGroundEditInvalidatesOrdinaryRetainedEmissionForDistributedRoots() {
        var world = Document(out var ground, out var roots);
        var cache = new WorldStaticPalettes();
        uint[] Emit(WorldDefinition current, WorldStaticPalettes? palettes) {
            var builder = new SdfProgramBuilder();
            WorldPlacementStamper.EmitStatic(builder, current, current.Creations, [roots], palettes: palettes);
            return builder.Build().Words.ToArray();
        }
        var before = Emit(world, cache);
        var changed = world with { PlacementRowsRaw = [ground with { Position = new Vector3(0, 2, 0) }, roots] };
        var after = Emit(changed, cache);
        Assert.NotEqual(before, after);
        Assert.Equal(Emit(changed, null), after);
        Assert.Equal(after, Emit(changed, cache));
    }
}
