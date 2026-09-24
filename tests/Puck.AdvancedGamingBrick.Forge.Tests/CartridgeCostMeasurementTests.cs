using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers the measurement <c>puck cartridge-cost</c> runs: its probes must reach a machine however far past a
/// reservation they go, and its search must report the machine's largest sustained count rather than a neighbour of it.
/// The full sweep boots hundreds of images and is the verb's job, not a test's.
/// </summary>
public sealed class CartridgeCostMeasurementTests {
    // A machine that sustains a probe exactly when the model prices it within a budget: monotone in the outer count,
    // like a real one, and cheap enough to search exhaustively beside the bisection.
    private static Func<CartridgeDocument, int, int> Budget(long units) => (document, frames) => ((CartridgeCost.Frame(
        document: document,
        profile: CartridgeCostProfile.For(target: document.Target)
    ).Cycles <= units)
        ? frames
        : 0
    );
    private static CartridgeCostShape StepShape(string target, int granularity) => new(
        Body: [CartridgeCostMeasurement.Step],
        Granularity: granularity,
        Load: default,
        Name: "step",
        Target: target
    );

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void TheHarnessCanStillReachTheMachine(string target) {
        // Far past what the target's reservation grants: nothing refuses a document for being slow, so a probe compiles
        // and simply misses frames, which is what the search needs to be able to see.
        var beyond = CartridgeCostMeasurement.Probe(
            outer: CartridgeCostMeasurement.SearchCeiling,
            shape: StepShape(
                granularity: 255,
                target: target
            )
        );

        Assert.Empty(collection: CartridgeDocuments.Validate(document: beyond));

        var (frame, reservation) = CartridgeDocuments.Estimate(document: beyond);
        Assert.True(condition: (frame.Cycles > reservation));
        Assert.NotEmpty(collection: CartridgeProbe.Compile(document: beyond).Rom);
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void TheSearchFindsTheLargestSustainedCount(string target) {
        var shape = StepShape(
            granularity: 10,
            target: target
        );
        var units = CartridgeCost.Frame(
            document: CartridgeCostMeasurement.Probe(
                outer: 97,
                shape: shape
            ),
            profile: CartridgeCostProfile.For(target: target)
        ).Cycles;
        var sustains = Budget(units: units);
        var largest = Enumerable.Range(
            count: (CartridgeCostMeasurement.SearchCeiling + 1),
            start: 0
        ).Last(predicate: outer => (sustains(
            arg1: CartridgeCostMeasurement.Probe(
                outer: outer,
                shape: shape
            ),
            arg2: CartridgeCostMeasurement.Frames
        ) != 0));

        var capacity = CartridgeCostMeasurement.LargestSustained(
            run: sustains,
            shape: shape
        );

        Assert.False(condition: capacity.Clipped);
        Assert.Equal(
            expected: (largest * shape.Granularity),
            actual: capacity.Iterations
        );
        Assert.Equal(
            expected: units,
            actual: capacity.Cost.Cycles
        );
    }
    [Fact]
    public void ASearchThatReachesItsCeilingSaysSo() {
        var shape = StepShape(
            granularity: 10,
            target: "cgb"
        );
        var capacity = CartridgeCostMeasurement.LargestSustained(
            run: Budget(units: long.MaxValue),
            shape: shape
        );

        Assert.True(condition: capacity.Clipped);
        Assert.Equal(
            expected: (CartridgeCostMeasurement.SearchCeiling * shape.Granularity),
            actual: capacity.Iterations
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void EveryShapeBuildsAValidProbe(string target) {
        foreach (var shape in CartridgeCostMeasurement.Shapes(target: target)) {
            Assert.Empty(collection: CartridgeDocuments.Validate(document: CartridgeCostMeasurement.Probe(
                outer: 1,
                shape: shape
            )));
        }
    }
}
