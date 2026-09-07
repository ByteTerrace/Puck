using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldPlacementSpatialLawTests {
    private static WorldSpatialShape Box(float x, float y, float z, float yaw = 0f) => new(
        Kind: WorldSpatialShapeKind.Box,
        Center: new DocumentVector3(Vector3.Zero),
        HalfExtents: new DocumentVector3(x, y, z),
        YawDegrees: yaw
    );

    private static WorldPlacement Placement(string id, WorldPlacementSpatialRole role, WorldSpatialShape shape, string? channel = null) => new(
        Id: id,
        PrototypeId: "prototype",
        Position: Vector3.Zero,
        YawDegrees: 0f,
        Scale: 1f,
        Spatial: [new WorldPlacementSpatialVolume(Name: "volume", Role: role, Shape: shape, Channel: channel)]
    );

    private static float FixedEpsilon => (float)(double)FixedQ4816.FromRawBits(1L);

    [Fact]
    public void SmallSphereDistanceDoesNotDisappearWhenSquared() {
        var shape = new WorldSpatialShape(WorldSpatialShapeKind.Sphere, Vector3.Zero, Vector3.Zero, FixedEpsilon);
        var first = Placement("first", WorldPlacementSpatialRole.Occupation, shape);
        var touching = first with { Id = "touching", Position = new DocumentVector3(2 * FixedEpsilon, 0, 0) };
        var separated = first with { Id = "separated", Position = new DocumentVector3(3 * FixedEpsilon, 0, 0) };
        var index = WorldSpatialQueryCompilation.Compile([first, touching, separated]);
        Assert.True(WorldSpatialQueryIndex.TryOverlap(index.Volumes[0], index.Volumes[1]));
        Assert.False(WorldSpatialQueryIndex.TryOverlap(index.Volumes[0], index.Volumes[2]));
    }

    [Fact]
    public void PositiveSubquantumPlacementScaleIsNamedAndNeverCompiledAsAPoint() {
        var placement = Placement("tiny-scale", WorldPlacementSpatialRole.Occupation, Box(1f, 1f, 1f)) with {
            Scale = 0.000001f
        };

        Assert.False(WorldDefinitionValidator.TryValidatePlacementGeometry(placement, out var reason));
        Assert.Contains("scale", reason, StringComparison.OrdinalIgnoreCase);

        var index = WorldSpatialQueryCompilation.Compile([placement]);
        Assert.Empty(index.Volumes);
        Assert.Contains(index.Unsupported, item => item.PlacementId == placement.Id &&
            item.Reason.Contains("scale", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(WorldSpatialQueryCompilation.CompilePlacement(placement,
            new Dictionary<string, WorldPlacement>(StringComparer.Ordinal) { [placement.Id] = placement }));
    }

    [Fact]
    public void PositiveFixedScaleAndExtentThatMultiplyToZeroAreUnsupported() {
        var minimum = FixedEpsilon;
        var placement = Placement("underflow", WorldPlacementSpatialRole.Occupation,
            Box(minimum, minimum, minimum)) with { Scale = minimum };

        Assert.True(WorldDefinitionValidator.TryValidatePlacementGeometry(placement, out var reason), reason);
        var index = WorldSpatialQueryCompilation.Compile([placement]);

        Assert.Empty(index.Volumes);
        Assert.Contains(index.Unsupported, item => item.PlacementId == placement.Id &&
            item.Reason.Contains("half-extents", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SmallestRepresentableRadiusAndExtentRemainValidAtUnitScale() {
        var minimum = FixedEpsilon;
        var box = Placement("boundary-box", WorldPlacementSpatialRole.Occupation,
            Box(minimum, minimum, minimum));
        var sphere = Placement("boundary-sphere", WorldPlacementSpatialRole.Occupation,
            new WorldSpatialShape(WorldSpatialShapeKind.Sphere, new DocumentVector3(Vector3.Zero), new DocumentVector3(Vector3.Zero),
                Radius: minimum));

        var index = WorldSpatialQueryCompilation.Compile([box, sphere]);

        Assert.Equal(2, index.Volumes.Count);
        Assert.Equal(FixedQ4816.FromRawBits(1L), index.Volumes[0].Shape.HalfExtents.X);
        Assert.Equal(FixedQ4816.FromRawBits(1L), index.Volumes[1].Shape.Radius);
    }

    [Fact]
    public void GeometryBeyondTheNumericNarrowphaseEnvelopeIsRefusedAtItsBoundary() {
        const float boundary = 4_194_304f; // 2^22 world units = 2^38 Q48.16 raw units.
        var control = Placement("numeric-boundary", WorldPlacementSpatialRole.Occupation,
            Box(boundary, boundary, boundary));
        var oversized = Placement("numeric-oversized", WorldPlacementSpatialRole.Occupation,
            Box(boundary + 1f, boundary + 1f, boundary + 1f));

        var index = WorldSpatialQueryCompilation.Compile([control, oversized]);

        Assert.Contains(index.Volumes, volume => volume.PlacementId == control.Id);
        Assert.DoesNotContain(index.Volumes, volume => volume.PlacementId == oversized.Id);
        Assert.Contains(index.Unsupported, item => item.PlacementId == oversized.Id &&
            item.Reason.Contains("narrowphase range", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AabbCandidatesStillUseExactYawAwareNarrowphase() {
        var first = Placement("first", WorldPlacementSpatialRole.Occupation, Box(2f, .5f, .1f, 45f));
        var second = Placement("second", WorldPlacementSpatialRole.Occupation, Box(2f, .5f, .1f, 45f)) with {
            Position = new DocumentVector3(new Vector3(.3535534f, 0f, .3535534f))
        };
        var index = WorldSpatialQueryCompilation.Compile([first, second]);
        var left = index.Volumes[0];
        var right = index.Volumes[1];

        Assert.True(left.Bounds.Intersects(right.Bounds));
        Assert.False(WorldSpatialQueryIndex.TryOverlap(left, right));
    }

    [Fact]
    public void OccupationBlocksClearanceButClearanceMayShareClearance() {
        var clearance = Placement("clearance", WorldPlacementSpatialRole.Clearance, Box(1f, 1f, 1f));
        var secondClearance = Placement("clearance2", WorldPlacementSpatialRole.Clearance, Box(1f, 1f, 1f));
        var index = WorldSpatialQueryCompilation.Compile([clearance, secondClearance]);
        Assert.False(index.HasBlockingOverlap(index.Volumes[1]));

        var occupation = Placement("occupied", WorldPlacementSpatialRole.Occupation, Box(1f, 1f, 1f));
        var occupiedIndex = WorldSpatialQueryCompilation.Compile([clearance, occupation]);
        Assert.True(occupiedIndex.HasBlockingOverlap(occupiedIndex.Volumes[1]));
    }

    [Fact]
    public void ParentScaleAndFiniteExtentDriveTheCompiledBounds() {
        var parent = new WorldPlacement("frame", "prototype", new DocumentVector3(10f, 0f, 0f), 0f, 2f);
        var child = Placement("child", WorldPlacementSpatialRole.Occupation, Box(1f, 1f, 1f)) with {
            Parent = parent.Id,
            Position = new DocumentVector3(3f, 0f, 0f)
        };
        var index = WorldSpatialQueryCompilation.Compile([parent, child]);
        var volume = index.Volumes[0];

        Assert.Equal(16f, (float)(double)volume.Shape.Center.X, 3);
        Assert.Equal(2f, (float)(double)volume.Bounds.Extent.X, 3);
        Assert.Single(index.Query(new FixedSpatialAabb(
            Center: FixedVector3.FromVector3(new Vector3(16f, 0f, 0f)),
            Extent: FixedVector3.FromVector3(new Vector3(.1f, .1f, .1f))
        )).Volumes);
    }

    [Fact]
    public void InfluenceCountIsDistinctByProviderAndChannel() {
        var target = Placement("target", WorldPlacementSpatialRole.Occupation, Box(.5f, .5f, .5f));
        var source = Placement("source", WorldPlacementSpatialRole.Influence, Box(2f, 2f, 2f), "water") with {
            Spatial = [
                new WorldPlacementSpatialVolume("a", WorldPlacementSpatialRole.Influence, Box(2f, 2f, 2f), "water"),
                new WorldPlacementSpatialVolume("b", WorldPlacementSpatialRole.Influence, Box(2f, 2f, 2f), "water")
            ]
        };
        var second = Placement("second", WorldPlacementSpatialRole.Influence, Box(2f, 2f, 2f), "water");
        var index = WorldSpatialQueryCompilation.Compile([target, source, second]);

        Assert.True(index.HasOccupationTarget("target"));
        Assert.False(index.HasOccupationTarget("source"));
        Assert.Equal(2, index.CountInfluences("water", "target"));
        Assert.Equal(0, index.CountInfluences("power", "target"));
    }

    [Fact]
    public void VerticalBridgeIsOutsideTheCourtQuery() {
        var court = Placement("court", WorldPlacementSpatialRole.Occupation, Box(2f, .5f, 2f));
        var bridge = Placement("bridge", WorldPlacementSpatialRole.Occupation, Box(2f, .5f, 2f)) with {
            Position = new DocumentVector3(0f, 3f, 0f)
        };
        var index = WorldSpatialQueryCompilation.Compile([court, bridge]);
        var result = index.Query(new FixedSpatialAabb(
            Center: FixedVector3.Zero,
            Extent: FixedVector3.FromVector3(new Vector3(3f, .5f, 3f))
        ));

        Assert.Single(result.Volumes);
        Assert.Equal("court", result.Volumes[0].PlacementId);
    }

    [Fact]
    public void DistantVolumesDoNotBecomeCandidatesOrLinearWork() {
        var placements = new List<WorldPlacement>();
        for (var index = 0; index < 128; index++) {
            placements.Add(Placement($"far-{index}", WorldPlacementSpatialRole.Occupation, Box(.25f, .25f, .25f)) with {
                Position = new DocumentVector3((index + 1) * 100f, 0f, 0f)
            });
        }
        placements.Add(Placement("near", WorldPlacementSpatialRole.Occupation, Box(.25f, .25f, .25f)));
        var query = WorldSpatialQueryCompilation.Compile(placements).Query(new FixedSpatialAabb(FixedVector3.Zero, FixedVector3.FromVector3(new Vector3(1f, 1f, 1f))));

        Assert.Single(query.Volumes);
        Assert.Equal("near", query.Volumes[0].PlacementId);
        Assert.True(query.WorkUnits < placements.Count);
    }

    [Fact]
    public void BvhUnionRetainsAnOddRawOuterBoundary() {
        var half = Box(.5f, .5f, .5f);
        var first = Placement("first", WorldPlacementSpatialRole.Occupation, half);
        var second = Placement("second", WorldPlacementSpatialRole.Occupation, half) with {
            Position = new DocumentVector3((float)(1d / 65536d), 0f, 0f)
        };
        var index = WorldSpatialQueryCompilation.Compile([first, second]);
        var edge = FixedQ4816.FromRawBits(32769L);
        var result = index.Query(new FixedSpatialAabb(
            Center: new FixedVector3(edge, FixedQ4816.Zero, FixedQ4816.Zero),
            Extent: FixedVector3.Zero
        ));

        Assert.Contains(result.Volumes, volume => volume.PlacementId == "second");
    }

    [Fact]
    public void RotatedParentScaleMovesChildVolumeInParentFrame() {
        var parent = new WorldPlacement("frame", "prototype", Vector3.Zero, 90f, 2f);
        var child = Placement("child", WorldPlacementSpatialRole.Occupation, Box(.5f, .5f, .5f)) with {
            Parent = parent.Id,
            Position = new DocumentVector3(1f, 0f, 0f)
        };
        var volume = WorldSpatialQueryCompilation.Compile([parent, child]).Volumes[0];

        Assert.Equal(0f, (float)(double)volume.Shape.Center.X, 3);
        Assert.Equal(-2f, (float)(double)volume.Shape.Center.Z, 3);
    }
}
