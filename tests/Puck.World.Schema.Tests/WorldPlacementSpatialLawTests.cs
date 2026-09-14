using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldPlacementSpatialLawTests {
    private static WorldSpatialShape Box(float x, float y, float z, float yaw = 0f) => new(
        Kind: WorldSpatialShapeKind.Box,
        Center: new DocumentVector3(value: Vector3.Zero),
        HalfExtents: new DocumentVector3(
            x: x,
            y: y,
            z: z
        ),
        YawDegrees: yaw
    );
    private static WorldPlacement Placement(string id, WorldPlacementSpatialRole role, WorldSpatialShape shape, string? channel = null) => new(
        Id: id,
        PrototypeId: "prototype",
        Position: Vector3.Zero,
        YawDegrees: 0f,
        Scale: 1f,
        Spatial: [new WorldPlacementSpatialVolume(
                Name: "volume",
                Role: role,
                Shape: shape,
                Channel: channel
            )]
    );

    [Fact]
    public void AabbCandidatesStillUseExactYawAwareNarrowphase() {
        var first = Placement(
            "first",
            WorldPlacementSpatialRole.Occupation,
            Box(
                x: 2f,
                y: .5f,
                yaw: 45f,
                z: .1f
            )
        );
        var second = Placement(
            "second",
            WorldPlacementSpatialRole.Occupation,
            Box(
                x: 2f,
                y: .5f,
                yaw: 45f,
                z: .1f
            )
        ) with {
            Position = new DocumentVector3(value: new Vector3(
            x: .3535534f,
            y: 0f,
            z: .3535534f
        )),
        };
        var index = WorldSpatialQueryCompilation.Compile(placements: [first, second]);
        var left = index.Volumes[0];
        var right = index.Volumes[1];

        Assert.True(condition: left.Bounds.Intersects(other: right.Bounds));
        Assert.False(condition: WorldSpatialQueryIndex.TryOverlap(
            left: left,
            right: right
        ));
    }
    [Fact]
    public void BvhUnionRetainsAnOddRawOuterBoundary() {
        var half = Box(
            .5f,
            .5f,
            .5f
        );
        var first = Placement(
            "first",
            WorldPlacementSpatialRole.Occupation,
            half
        );
        var second = Placement(
            "second",
            WorldPlacementSpatialRole.Occupation,
            half
        ) with {
            Position = new DocumentVector3(
            x: ((float)(1d / 65536d)),
            y: 0f,
            z: 0f
        ),
        };
        var index = WorldSpatialQueryCompilation.Compile(placements: [first, second]);
        var edge = FixedQ4816.FromRawBits(value: 32769L);
        var result = index.Query(new FixedSpatialAabb(
            Center: new FixedVector3(
                X: edge,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            Extent: FixedVector3.Zero
        ));

        Assert.Contains(
            collection: result.Volumes,
            filter: volume => (volume.PlacementId == "second")
        );
    }
    [Fact]
    public void DistantVolumesDoNotBecomeCandidatesOrLinearWork() {
        var placements = new List<WorldPlacement>();

        for (var index = 0; (index < 128); index++) {
            placements.Add(item: Placement(
                $"far-{index}",
                WorldPlacementSpatialRole.Occupation,
                Box(
                    .25f,
                    .25f,
                    .25f
                )
            ) with {
                Position = new DocumentVector3(
                x: ((index + 1) * 100f),
                y: 0f,
                z: 0f
            ),
            });
        }
        placements.Add(item: Placement(
            "near",
            WorldPlacementSpatialRole.Occupation,
            Box(
                .25f,
                .25f,
                .25f
            )
        ));
        var query = WorldSpatialQueryCompilation.Compile(placements: placements).Query(new FixedSpatialAabb(
            Center: FixedVector3.Zero,
            Extent: FixedVector3.FromVector3(value: new Vector3(
                x: 1f,
                y: 1f,
                z: 1f
            ))
        ));

        Assert.Single(collection: query.Volumes);
        Assert.Equal(
            "near",
            query.Volumes[0].PlacementId
        );
        Assert.True(condition: (query.WorkUnits < placements.Count));
    }
    [Fact]
    public void GeometryBeyondTheNumericNarrowphaseEnvelopeIsRefusedAtItsBoundary() {
        const float Boundary = 4_194_304f; // 2^22 world units = 2^38 Q48.16 raw units.
        var control = Placement(
            "numeric-boundary",
            WorldPlacementSpatialRole.Occupation,
            Box(
                Boundary,
                Boundary,
                Boundary
            )
        );
        var oversized = Placement(
            "numeric-oversized",
            WorldPlacementSpatialRole.Occupation,
            Box(
                (Boundary + 1f),
                (Boundary + 1f),
                (Boundary + 1f)
            )
        );

        var index = WorldSpatialQueryCompilation.Compile(placements: [control, oversized]);

        Assert.Contains(
            collection: index.Volumes,
            filter: volume => (volume.PlacementId == control.Id)
        );
        Assert.DoesNotContain(
            collection: index.Volumes,
            filter: volume => (volume.PlacementId == oversized.Id)
        );
        Assert.Contains(
            collection: index.Unsupported,
            filter: item => ((item.PlacementId == oversized.Id) &&
            item.Reason.Contains(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "narrowphase range"
            ))
        );
    }
    [Fact]
    public void InfluenceCountIsDistinctByProviderAndChannel() {
        var target = Placement(
            "target",
            WorldPlacementSpatialRole.Occupation,
            Box(
                .5f,
                .5f,
                .5f
            )
        );
        var source = Placement(
            "source",
            WorldPlacementSpatialRole.Influence,
            Box(
                2f,
                2f,
                2f
            ),
            "water"
        ) with {
            Spatial = [
                new WorldPlacementSpatialVolume(
                "a",
                WorldPlacementSpatialRole.Influence,
                Box(
                    2f,
                    2f,
                    2f
                ),
                "water"
            ),
                new WorldPlacementSpatialVolume(
                "b",
                WorldPlacementSpatialRole.Influence,
                Box(
                    2f,
                    2f,
                    2f
                ),
                "water"
            )
            ],
        };
        var second = Placement(
            "second",
            WorldPlacementSpatialRole.Influence,
            Box(
                2f,
                2f,
                2f
            ),
            "water"
        );
        var index = WorldSpatialQueryCompilation.Compile(placements: [target, source, second]);

        Assert.True(condition: index.HasOccupationTarget(placementId: "target"));
        Assert.False(condition: index.HasOccupationTarget(placementId: "source"));
        Assert.Equal(
            2,
            index.CountInfluences(
                channel: "water",
                placementId: "target"
            )
        );
        Assert.Equal(
            0,
            index.CountInfluences(
                channel: "power",
                placementId: "target"
            )
        );
    }
    [Fact]
    public void OccupationBlocksClearanceButClearanceMayShareClearance() {
        var clearance = Placement(
            "clearance",
            WorldPlacementSpatialRole.Clearance,
            Box(
                1f,
                1f,
                1f
            )
        );
        var secondClearance = Placement(
            "clearance2",
            WorldPlacementSpatialRole.Clearance,
            Box(
                1f,
                1f,
                1f
            )
        );
        var index = WorldSpatialQueryCompilation.Compile(placements: [clearance, secondClearance]);

        Assert.False(condition: index.HasBlockingOverlap(candidate: index.Volumes[1]));

        var occupation = Placement(
            "occupied",
            WorldPlacementSpatialRole.Occupation,
            Box(
                1f,
                1f,
                1f
            )
        );
        var occupiedIndex = WorldSpatialQueryCompilation.Compile(placements: [clearance, occupation]);

        Assert.True(condition: occupiedIndex.HasBlockingOverlap(candidate: occupiedIndex.Volumes[1]));
    }
    [Fact]
    public void ParentScaleAndFiniteExtentDriveTheCompiledBounds() {
        var parent = new WorldPlacement(
            "frame",
            "prototype",
            new DocumentVector3(
                x: 10f,
                y: 0f,
                z: 0f
            ),
            0f,
            2f
        );
        var child = Placement(
            "child",
            WorldPlacementSpatialRole.Occupation,
            Box(
                1f,
                1f,
                1f
            )
        ) with {
            Parent = parent.Id,
            Position = new DocumentVector3(
            x: 3f,
            y: 0f,
            z: 0f
        ),
        };
        var index = WorldSpatialQueryCompilation.Compile(placements: [parent, child]);
        var volume = index.Volumes[0];

        Assert.Equal(
            16f,
            ((float)((double)volume.Shape.Center.X)),
            3
        );
        Assert.Equal(
            2f,
            ((float)((double)volume.Bounds.Extent.X)),
            3
        );
        Assert.Single(collection: index.Query(new FixedSpatialAabb(
            Center: FixedVector3.FromVector3(value: new Vector3(
                x: 16f,
                y: 0f,
                z: 0f
            )),
            Extent: FixedVector3.FromVector3(value: new Vector3(
                x: .1f,
                y: .1f,
                z: .1f
            ))
        )).Volumes);
    }
    [Fact]
    public void PositiveFixedScaleAndExtentThatMultiplyToZeroAreUnsupported() {
        var minimum = FixedEpsilon;
        var placement = Placement(
            "underflow",
            WorldPlacementSpatialRole.Occupation,
            Box(
                minimum,
                minimum,
                minimum
            )
        ) with { Scale = minimum };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidatePlacementGeometry(
                placement: placement,
                reason: out var reason
            ),
            userMessage: reason
        );
        var index = WorldSpatialQueryCompilation.Compile(placements: [placement]);

        Assert.Empty(collection: index.Volumes);
        Assert.Contains(
            collection: index.Unsupported,
            filter: item => ((item.PlacementId == placement.Id) &&
            item.Reason.Contains(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "half-extents"
            ))
        );
    }
    [Fact]
    public void PositiveSubquantumPlacementScaleIsNamedAndNeverCompiledAsAPoint() {
        var placement = Placement(
            "tiny-scale",
            WorldPlacementSpatialRole.Occupation,
            Box(
                1f,
                1f,
                1f
            )
        ) with {
            Scale = 0.000001f,
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidatePlacementGeometry(
            placement: placement,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.OrdinalIgnoreCase,
            expectedSubstring: "scale"
        );

        var index = WorldSpatialQueryCompilation.Compile(placements: [placement]);

        Assert.Empty(collection: index.Volumes);
        Assert.Contains(
            collection: index.Unsupported,
            filter: item => ((item.PlacementId == placement.Id) &&
            item.Reason.Contains(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "scale"
            ))
        );
        Assert.Empty(collection: WorldSpatialQueryCompilation.CompilePlacement(
            placement,
            new Dictionary<string, WorldPlacement>(comparer: StringComparer.Ordinal) { [placement.Id] = placement }
        ));
    }
    [Fact]
    public void RotatedParentScaleMovesChildVolumeInParentFrame() {
        var parent = new WorldPlacement(
            "frame",
            "prototype",
            Vector3.Zero,
            90f,
            2f
        );
        var child = Placement(
            "child",
            WorldPlacementSpatialRole.Occupation,
            Box(
                .5f,
                .5f,
                .5f
            )
        ) with {
            Parent = parent.Id,
            Position = new DocumentVector3(
            x: 1f,
            y: 0f,
            z: 0f
        ),
        };
        var volume = WorldSpatialQueryCompilation.Compile(placements: [parent, child]).Volumes[0];

        Assert.Equal(
            0f,
            ((float)((double)volume.Shape.Center.X)),
            3
        );
        Assert.Equal(
            -2f,
            ((float)((double)volume.Shape.Center.Z)),
            3
        );
    }
    [Fact]
    public void SmallSphereDistanceDoesNotDisappearWhenSquared() {
        var shape = new WorldSpatialShape(
            WorldSpatialShapeKind.Sphere,
            Vector3.Zero,
            Vector3.Zero,
            FixedEpsilon
        );
        var first = Placement(
            "first",
            WorldPlacementSpatialRole.Occupation,
            shape
        );
        var touching = first with { Id = "touching", Position = new DocumentVector3(
            x: (2 * FixedEpsilon),
            y: 0,
            z: 0
        ) };
        var separated = first with { Id = "separated", Position = new DocumentVector3(
            x: (3 * FixedEpsilon),
            y: 0,
            z: 0
        ) };
        var index = WorldSpatialQueryCompilation.Compile(placements: [first, touching, separated]);

        Assert.True(condition: WorldSpatialQueryIndex.TryOverlap(
            left: index.Volumes[0],
            right: index.Volumes[1]
        ));
        Assert.False(condition: WorldSpatialQueryIndex.TryOverlap(
            left: index.Volumes[0],
            right: index.Volumes[2]
        ));
    }
    [Fact]
    public void SmallestRepresentableRadiusAndExtentRemainValidAtUnitScale() {
        var minimum = FixedEpsilon;
        var box = Placement(
            "boundary-box",
            WorldPlacementSpatialRole.Occupation,
            Box(
                minimum,
                minimum,
                minimum
            )
        );
        var sphere = Placement(
            "boundary-sphere",
            WorldPlacementSpatialRole.Occupation,
            new WorldSpatialShape(
                WorldSpatialShapeKind.Sphere,
                new DocumentVector3(value: Vector3.Zero),
                new DocumentVector3(value: Vector3.Zero),
                Radius: minimum
            )
        );

        var index = WorldSpatialQueryCompilation.Compile(placements: [box, sphere]);

        Assert.Equal(
            2,
            index.Volumes.Count
        );
        Assert.Equal(
            FixedQ4816.FromRawBits(value: 1L),
            index.Volumes[0].Shape.HalfExtents.X
        );
        Assert.Equal(
            FixedQ4816.FromRawBits(value: 1L),
            index.Volumes[1].Shape.Radius
        );
    }
    [Fact]
    public void VerticalBridgeIsOutsideTheCourtQuery() {
        var court = Placement(
            "court",
            WorldPlacementSpatialRole.Occupation,
            Box(
                2f,
                .5f,
                2f
            )
        );
        var bridge = Placement(
            "bridge",
            WorldPlacementSpatialRole.Occupation,
            Box(
                2f,
                .5f,
                2f
            )
        ) with {
            Position = new DocumentVector3(
            x: 0f,
            y: 3f,
            z: 0f
        ),
        };
        var index = WorldSpatialQueryCompilation.Compile(placements: [court, bridge]);
        var result = index.Query(new FixedSpatialAabb(
            Center: FixedVector3.Zero,
            Extent: FixedVector3.FromVector3(value: new Vector3(
                x: 3f,
                y: .5f,
                z: 3f
            ))
        ));

        Assert.Single(collection: result.Volumes);
        Assert.Equal(
            "court",
            result.Volumes[0].PlacementId
        );
    }

    private static float FixedEpsilon => ((float)((double)FixedQ4816.FromRawBits(value: 1L)));
}
