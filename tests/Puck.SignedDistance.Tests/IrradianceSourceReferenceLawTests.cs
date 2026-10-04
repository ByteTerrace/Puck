using Puck.SignedDistance.Illumination;
using Xunit;
namespace Puck.SignedDistance.Tests;
public sealed class IrradianceSourceReferenceLawTests {
    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(1073741823)]
    [InlineData(int.MaxValue)]
    public void UnsupportedDepthIsRefusedBeforeTracingEvenWhenSequenceArithmeticWouldOverflow(int bounces) {
        var field = IrradianceScenes.Shell(3, 0.2);
        var reference = new IrradianceReference(field, IrradianceScenes.Uniform(0, 0, 1), 100);
        Assert.Throws<ArgumentOutOfRangeException>("bounces", () => reference.EstimateSources(new(0, -3, 0), new(0, 1, 0), bounces, 1));
        Assert.Throws<ArgumentOutOfRangeException>("bounces", () => reference.Estimate(new(0, -3, 0), new(0, 1, 0), bounces, 1));
        Assert.Equal(0, field.Casts);
        Assert.Equal(0, field.Samples);
    }
    [Fact]
    public void FirstHitOriginsStayIndependentFromLaterFeedbackAndReceiverSelection() {
        var reference = new IrradianceReference(IrradianceScenes.Shell(3, 0.2), new IrradianceSurfaces(
            albedo: _ => new(0.5, 0.5, 0.5), emission: _ => new(0.75, 0.75, 0.75),
            direct: (_, _, _) => new(0.25, 0.25, 0.25), screens: (_, _, _) => new(0.5, 0.5, 0.5),
            sky: _ => new(9, 9, 9)), 100);
        var result = reference.EstimateSources(new(0, -3, 0), new(0, 1, 0), 1, 64);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(new Double3(0.125, 0.125, 0.125), result.Contributions.Direct);
        Assert.Equal(new Double3(0.25, 0.25, 0.25), result.Contributions.Screens);
        Assert.Equal(new Double3(0.75, 0.75, 0.75), result.Contributions.Emission);
        Assert.Equal(new Double3(0.5625, 0.5625, 0.5625), result.Contributions.Feedback);
        Assert.Equal(Double3.Zero, result.Contributions.Sky);
        Assert.Equal(new Double3(1.3125, 1.3125, 1.3125), result.Contributions.Select(SdfIndirectSources.Feedback | SdfIndirectSources.Emission));
        Assert.Equal(reference.Estimate(new(0, -3, 0), new(0, 1, 0), 1, 64).Irradiance, result.Contributions.Total);
    }
    [Fact]
    public void PointReflectanceAttenuatesReflectionsButLeavesSelfEmissionIndependent() {
        var field = IrradianceScenes.Shell(3, 0.2);
        var surfaces = new IrradianceSurfaces(
            albedo: _ => new(0.9, 0.9, 0.9), emission: _ => new(1, 1, 1),
            direct: (_, _, _) => new(2, 2, 2), reflection: (_, _, _) => new(0.25, 0.25, 0.25));
        var reference = new IrradianceReference(field, surfaces, 100);
        var result = reference.EstimateSources(new(0, -3, 0), new(0, 1, 0), 1, 64);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(new Double3(0.5, 0.5, 0.5), result.Contributions.Direct);
        Assert.Equal(new Double3(0.375, 0.375, 0.375), result.Contributions.Feedback);
        Assert.Equal(new Double3(1, 1, 1), result.Contributions.Emission);

        var model = new IrradianceCacheModel(field, surfaces,
            [new IrradianceLevel(Name: "room", Radius: 0, Reach: 0, Spacing: 1, Strata: 2)], new(ExitDistance: 100));
        model.Allocate(0, new(-4, -4, -4), new(4, 4, 4));
        model.Classify();
        model.Trace();
        model.Solve(bounces: 0);
        var firstSweep = model.Contributions(new(0, -3, 0), new(0, 1, 0));
        Assert.NotNull(firstSweep);
        Assert.Equal(0.5, firstSweep.Value.Direct.X, precision: 9);
        Assert.Equal(0.5, firstSweep.Value.Direct.Y, precision: 9);
        Assert.Equal(0.5, firstSweep.Value.Direct.Z, precision: 9);
        Assert.Equal(Double3.Zero, firstSweep.Value.Feedback);
        Assert.Equal(1, firstSweep.Value.Emission.X, precision: 9);
        Assert.Equal(1, firstSweep.Value.Emission.Y, precision: 9);
        Assert.Equal(1, firstSweep.Value.Emission.Z, precision: 9);
    }
    [Fact]
    public void AFailedReceiverLaunchIsUnresolvedInsteadOfACompleteBlackReference() {
        var reference = new IrradianceReference(IrradianceScenes.Shell(3, 0.2), IrradianceScenes.Uniform(0, 0, 1), 100);
        var result = reference.EstimateSources(new(0, -3.1, 0), new(0, 1, 0), 0, 8);
        Assert.Equal(8, result.Unresolved);
        Assert.Equal(Double3.Zero, result.Contributions.Total);
    }
}
