using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: every way of authoring a placement's Noise/Scatter distribution wrong is refused BY NAME, mirroring the
/// field lattice's own Noise/Scatter fill bounds (<see cref="WorldLatticeFill"/>), and the well-formed spellings of
/// both validate. Each arm is a denial paired with a control differing in exactly one authored field.
/// </summary>
public sealed class PlacementSamplingValidationLawTests {
    private const string PlacementId = "field";
    private const string PrototypeId = "marker";

    private static WorldDistributionRegion.Noise WellFormedNoise() => new(
        CellSize: 1f,
        Depth: 16,
        Frequency: 4,
        Octaves: 3,
        Seed: 7u,
        Threshold: 0.4f,
        Width: 16
    );
    private static WorldDistributionRegion.Scatter WellFormedScatter() => new(
        CellSize: 1f,
        Depth: 10,
        Radius: 1,
        Seed: 3u,
        Spacing: 3,
        Width: 10
    );
    private static WorldDefinition With(WorldDistributionRegion region) {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [CreationFixtures.UnitSphere(id: PrototypeId)],
            PlacementRowsRaw = [
                new WorldPlacement(
                Id: PlacementId,
                PrototypeId: PrototypeId,
                Position: new DocumentVector3(value: Vector3.Zero),
                YawDegrees: 0f,
                Scale: 1f,
                Distribution: new WorldDistribution(
                    Region: region,
                    Fill: new WorldSequence(
                        Name: WorldSequence.None,
                        Offset: 0,
                        Step: 0f
                    )
                )
            ),
            ],
        });
    }

    [Fact]
    public void ANoiseFrequencyMustBeAtLeastOne() {
        Laws.RefusalWithControl(
            control: With(region: WellFormedNoise()),
            denied: With(region: (WellFormedNoise() with { Frequency = 0 })),
            locally: true,
            needle: "region.frequency must be at least 1"
        );
    }
    [Fact]
    public void ANoiseGridWorstCaseCannotExceedTheEngineInstanceCeiling() {
        var oversized = (WellFormedNoise() with { Width = 300, Depth = 300 });

        Assert.True(condition: ((300L * 300L) > SdfProgramBuilder.MaxInstances));
        Laws.RefusalWithControl(
            control: With(region: WellFormedNoise()),
            denied: With(region: oversized),
            locally: true,
            needle: $"worst-case exceeds the {SdfProgramBuilder.MaxInstances}-instance engine ceiling"
        );
    }
    [Fact]
    public void ANoiseOctaveCountMustLieInOneToFour() {
        Laws.Refuses(
            definition: With(region: (WellFormedNoise() with { Octaves = 0 })),
            locally: true,
            needle: "region.octaves must be in 1..4"
        );
        Laws.RefusalWithControl(
            control: With(region: WellFormedNoise()),
            denied: With(region: (WellFormedNoise() with { Octaves = 5 })),
            locally: true,
            needle: "region.octaves must be in 1..4"
        );
    }
    [Fact]
    public void ANoiseThresholdMustLieInZeroOneHalfOpen() {
        Laws.Refuses(
            definition: With(region: (WellFormedNoise() with { Threshold = -0.01f })),
            locally: true,
            needle: "region.threshold must be in [0, 1)"
        );
        Laws.RefusalWithControl(
            control: With(region: WellFormedNoise()),
            denied: With(region: (WellFormedNoise() with { Threshold = 1f })),
            locally: true,
            needle: "region.threshold must be in [0, 1)"
        );
    }
    [Fact]
    public void ASampledGridNeedsAPositiveCellSizeAndAtLeastOneCellPerAxis() {
        Laws.Refuses(
            definition: With(region: (WellFormedScatter() with { CellSize = 0f })),
            locally: true,
            needle: "region.cellSize must be finite and positive"
        );
        Laws.Refuses(
            definition: With(region: (WellFormedScatter() with { Width = 0 })),
            locally: true,
            needle: "region.width must be at least 1"
        );
        Laws.RefusalWithControl(
            control: With(region: WellFormedScatter()),
            denied: With(region: (WellFormedScatter() with { Depth = 0 })),
            locally: true,
            needle: "region.depth must be at least 1"
        );
    }
    [Fact]
    public void AScatterRadiusMustFitInsideHalfTheSpacing() {
        Laws.Refuses(
            definition: With(region: (WellFormedScatter() with { Radius = 0 })),
            locally: true,
            needle: "region.radius must be at least 1 and at most spacing/2"
        );
        Laws.RefusalWithControl(
            control: With(region: WellFormedScatter()),
            denied: With(region: (WellFormedScatter() with { Radius = 2, Spacing = 3 })),
            locally: true,
            needle: "region.radius must be at least 1 and at most spacing/2"
        );
    }
    [Fact]
    public void AScatterSpacingMustBeAtLeastTwoCells() {
        Laws.RefusalWithControl(
            control: With(region: WellFormedScatter()),
            denied: With(region: (WellFormedScatter() with { Spacing = 1 })),
            locally: true,
            needle: "region.spacing must be at least 2 cells"
        );
    }
}
