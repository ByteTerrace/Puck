using System.Numerics;
using Xunit;

using Puck.Assets.Documents;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a placement's Noise/Scatter distribution resolves the same instance offsets from the same document and
/// world seed, on every construction — bit-for-bit, Q48.16 throughout — and a rerolled seed moves the pattern. The
/// placement twin of <see cref="WorldFieldLatticeLawTests"/>'s field-fill laws.
/// </summary>
public sealed class WorldPlacementSamplingLawTests {
    private static WorldPlacement Placement(WorldDistributionRegion region) => new(
        Id: "field",
        PrototypeId: "marker",
        Position: new DocumentVector3(value: Vector3.Zero),
        YawDegrees: 0f,
        Scale: 1f,
        Distribution: new WorldDistribution(
            Region: region,
            Fill: new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0f)
        )
    );

    [Fact]
    public void ANoiseDistributionIsBitIdenticalAcrossResolvesAndMovesWithTheWorldSeed() {
        var placement = Placement(region: new WorldDistributionRegion.Noise(CellSize: 1f, Depth: 16, Frequency: 4, Octaves: 3, Seed: 7u, Threshold: 0.4f, Width: 16));

        var a = WorldPlacementStamp.SampledFixedOffsetsFor(placement: placement, worldSeed: 5UL)!;
        var b = WorldPlacementStamp.SampledFixedOffsetsFor(placement: placement, worldSeed: 5UL)!;
        var rerolled = WorldPlacementStamp.SampledFixedOffsetsFor(placement: placement, worldSeed: 6UL)!;

        Assert.Equal(
            actual: b.Count,
            expected: a.Count
        );

        for (var index = 0; (index < a.Count); index++) {
            Assert.Equal(
                actual: b[index],
                expected: a[index]
            );
        }

        // Patchy, not degenerate: some cells admitted, not the whole 16x16 grid.
        Assert.InRange(actual: a.Count, high: ((16 * 16) - 1), low: 1);
        Assert.NotEqual(
            actual: rerolled.Count,
            expected: a.Count
        );
    }
    [Fact]
    public void AScatterDistributionEmitsExactlyOneInstancePerBlockAndMovesWithTheWorldSeed() {
        // Radius 1 against spacing 5 leaves a 3-cell jitter inset per axis (spacing - 2*radius), wide enough that a
        // rerolled seed is expected to move at least one point; spacing 3 (inset 1) would be degenerate.
        var placement = Placement(region: new WorldDistributionRegion.Scatter(CellSize: 1f, Depth: 10, Radius: 1, Seed: 3u, Spacing: 5, Width: 10));

        var a = WorldPlacementStamp.SampledFixedOffsetsFor(placement: placement, worldSeed: 1UL)!;
        var b = WorldPlacementStamp.SampledFixedOffsetsFor(placement: placement, worldSeed: 1UL)!;
        var rerolled = WorldPlacementStamp.SampledFixedOffsetsFor(placement: placement, worldSeed: 2UL)!;

        // ceil(10/5) x ceil(10/5) blocks, one instance each -- exact, not a worst case.
        Assert.Equal(
            actual: a.Count,
            expected: (2 * 2)
        );

        for (var index = 0; (index < a.Count); index++) {
            Assert.Equal(
                actual: b[index],
                expected: a[index]
            );
        }

        // The block count is seed-independent; only the jittered positions move.
        Assert.Equal(
            actual: a.Count,
            expected: rerolled.Count
        );

        var moved = false;

        for (var index = 0; (index < a.Count); index++) {
            if (a[index] != rerolled[index]) {
                moved = true;

                break;
            }
        }

        Assert.True(condition: moved);
    }
    [Fact]
    public void ALatticeDistributionResolvesNoSampledOffsets() {
        var placement = Placement(region: new WorldDistributionRegion.Lattice(StepA: new DocumentVector3(x: 1f, y: 0f, z: 0f), CountA: 3, StepB: new DocumentVector3(x: 0f, y: 0f, z: 1f), CountB: 2));

        Assert.Null(@object: WorldPlacementStamp.SampledFixedOffsetsFor(placement: placement, worldSeed: 1UL));
    }
    [Fact]
    public void TheScatterCeilingIsExactAndTheNoiseCeilingIsWorstCase() {
        var scatter = Placement(region: new WorldDistributionRegion.Scatter(CellSize: 1f, Width: 10, Depth: 10, Spacing: 3, Radius: 1));
        var noise = Placement(region: new WorldDistributionRegion.Noise(CellSize: 1f, Width: 16, Depth: 16, Frequency: 4, Threshold: 0.4f));

        Assert.Equal(
            actual: WorldPlacementStamp.MaterializedCopyCeiling(placement: scatter),
            expected: 16L
        );
        Assert.Equal(
            actual: WorldPlacementStamp.MaterializedCopyCeiling(placement: noise),
            expected: (16L * 16L)
        );

        var admitted = WorldPlacementStamp.SampledFixedOffsetsFor(placement: noise, worldSeed: 9UL)!.Count;

        Assert.True(condition: (admitted <= WorldPlacementStamp.MaterializedCopyCeiling(placement: noise)));
    }
}
