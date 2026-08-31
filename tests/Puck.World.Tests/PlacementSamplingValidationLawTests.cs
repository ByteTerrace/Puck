using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: every way of authoring a placement's Noise/Scatter distribution wrong is refused BY NAME, mirroring the
/// field lattice's own Noise/Scatter fill bounds (<see cref="WorldLatticeFill"/>), and the well-formed spellings of
/// both validate. Each arm is a denial paired with a control differing in exactly one authored field.
/// </summary>
public sealed class PlacementSamplingValidationLawTests {
    private const string PrototypeId = "marker";
    private const string PlacementId = "field";

    private static void AssertValidates(WorldDefinition definition) {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    private static void AssertRefusedNaming(WorldDefinition definition, string needle) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: needle
        );
    }
    private static WorldPrototype Creation() {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: null,
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: null,
                    Type: SdfSolidPrimitive.Sphere,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    Scale: new Vector3(value: 1f),
                    Material: 0,
                    Blend: SdfBlendOp.Union,
                    Smooth: 0f,
                    Group: 0
                ),
            ],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: PrototypeId
        );

        return new WorldPrototype(
            Id: PrototypeId,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    private static WorldDefinition With(WorldDistributionRegion region) {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [Creation()],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: PlacementId,
                    PrototypeId: PrototypeId,
                    Position: new DocumentVector3(value: Vector3.Zero),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Distribution: new WorldDistribution(
                        Region: region,
                        Fill: new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0f)
                    )
                ),
            ],
        });
    }
    private static WorldDistributionRegion.Noise WellFormedNoise() => new(CellSize: 1f, Width: 16, Depth: 16, Frequency: 4, Threshold: 0.4f, Octaves: 3, Seed: 7u);
    private static WorldDistributionRegion.Scatter WellFormedScatter() => new(CellSize: 1f, Width: 10, Depth: 10, Spacing: 3, Radius: 1, Seed: 3u);

    [Fact]
    public void ANoiseThresholdMustLieInZeroOneHalfOpen() {
        AssertRefusedNaming(
            definition: With(region: (WellFormedNoise() with { Threshold = -0.01f })),
            needle: "region.threshold must be in [0, 1)"
        );
        AssertRefusedNaming(
            definition: With(region: (WellFormedNoise() with { Threshold = 1f })),
            needle: "region.threshold must be in [0, 1)"
        );
        AssertValidates(definition: With(region: WellFormedNoise()));
    }
    [Fact]
    public void ANoiseFrequencyMustBeAtLeastOne() {
        AssertRefusedNaming(
            definition: With(region: (WellFormedNoise() with { Frequency = 0 })),
            needle: "region.frequency must be at least 1"
        );
        AssertValidates(definition: With(region: WellFormedNoise()));
    }
    [Fact]
    public void ANoiseOctaveCountMustLieInOneToFour() {
        AssertRefusedNaming(
            definition: With(region: (WellFormedNoise() with { Octaves = 0 })),
            needle: "region.octaves must be in 1..4"
        );
        AssertRefusedNaming(
            definition: With(region: (WellFormedNoise() with { Octaves = 5 })),
            needle: "region.octaves must be in 1..4"
        );
        AssertValidates(definition: With(region: WellFormedNoise()));
    }
    [Fact]
    public void AScatterSpacingMustBeAtLeastTwoCells() {
        AssertRefusedNaming(
            definition: With(region: (WellFormedScatter() with { Spacing = 1 })),
            needle: "region.spacing must be at least 2 cells"
        );
        AssertValidates(definition: With(region: WellFormedScatter()));
    }
    [Fact]
    public void AScatterRadiusMustFitInsideHalfTheSpacing() {
        AssertRefusedNaming(
            definition: With(region: (WellFormedScatter() with { Radius = 0 })),
            needle: "region.radius must be at least 1 and at most spacing/2"
        );
        AssertRefusedNaming(
            definition: With(region: (WellFormedScatter() with { Radius = 2, Spacing = 3 })),
            needle: "region.radius must be at least 1 and at most spacing/2"
        );
        AssertValidates(definition: With(region: WellFormedScatter()));
    }
    [Fact]
    public void ASampledGridNeedsAPositiveCellSizeAndAtLeastOneCellPerAxis() {
        AssertRefusedNaming(
            definition: With(region: (WellFormedScatter() with { CellSize = 0f })),
            needle: "region.cellSize must be finite and positive"
        );
        AssertRefusedNaming(
            definition: With(region: (WellFormedScatter() with { Width = 0 })),
            needle: "region.width must be at least 1"
        );
        AssertRefusedNaming(
            definition: With(region: (WellFormedScatter() with { Depth = 0 })),
            needle: "region.depth must be at least 1"
        );
        AssertValidates(definition: With(region: WellFormedScatter()));
    }
    [Fact]
    public void ANoiseGridWorstCaseCannotExceedTheEngineInstanceCeiling() {
        var oversized = (WellFormedNoise() with { Width = 200, Depth = 200 });

        Assert.True(condition: ((200L * 200L) > SdfProgramBuilder.MaxInstances));
        AssertRefusedNaming(
            definition: With(region: oversized),
            needle: $"worst-case exceeds the {SdfProgramBuilder.MaxInstances}-instance engine ceiling"
        );
        AssertValidates(definition: With(region: WellFormedNoise()));
    }
}
