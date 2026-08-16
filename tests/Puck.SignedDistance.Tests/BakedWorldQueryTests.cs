using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class BakedWorldQueryTests {
    private const long CellSizeRaw = 16384L;

    private static BakedWorldQuery TwoCellQuery() =>
        new(artifact: new WorldQueryArtifact(
            OriginXRaw: 0L,
            OriginZRaw: 0L,
            CellSizeRaw: CellSizeRaw,
            Width: 2,
            Height: 1,
            HeightRaw: [WorldQueryArtifact.NoHeightSentinel, WorldQueryArtifact.NoHeightSentinel,],
            Blocked: [2UL,]
        ));
    private static FixedPosition Position(double x, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: z)
        ));

    [Fact]
    public void BakeCoversPartialCellsAtMaximumEdges() {
        var artifact = WorldQueryBaker.Bake(
            minX: 0f,
            minZ: 0f,
            maxX: 0.3f,
            maxZ: 0.3f,
            terrain: [],
            blockers: []
        );

        Assert.Equal(
            expected: 2,
            actual: artifact.Width
        );
        Assert.Equal(
            expected: 2,
            actual: artifact.Height
        );
    }

    [Fact]
    public void OverlapTreatsABlockedCellAsAreaRatherThanAsItsCenterPoint() {
        var query = TwoCellQuery();

        var overlaps = query.Overlap(
            center: Position(
                x: 0.30,
                z: 0.125
            ),
            radius: FixedQ4816.Zero
        );

        Assert.True(condition: overlaps);
    }

    [Fact]
    public void RaycastDoesNotReportACellBeyondMaximumDistance() {
        var query = TwoCellQuery();

        var hit = query.Raycast(
            origin: Position(
                x: 0.0,
                z: 0.125
            ),
            dir: new FixedVector3(
                X: FixedQ4816.One,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            maxDist: FixedQ4816.FromDouble(value: 0.02),
            hit: out _
        );

        Assert.False(condition: hit);
    }

    [Fact]
    public void RaycastStillReportsAHitExactlyAtMaximumDistance() {
        var query = TwoCellQuery();

        var found = query.Raycast(
            origin: Position(
                x: 0.0,
                z: 0.125
            ),
            dir: new FixedVector3(
                X: FixedQ4816.One,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            maxDist: FixedQ4816.FromDouble(value: 0.25),
            hit: out var hit
        );

        Assert.True(condition: found);
        Assert.Equal(
            expected: FixedQ4816.FromDouble(value: 0.25),
            actual: hit.Distance
        );
    }
}
