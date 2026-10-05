using Puck.SignedDistance.Illumination;
using Xunit;
namespace Puck.SignedDistance.Tests;

public sealed class IrradianceSourceReferenceLawTests {
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(1073741823)]
    [InlineData(int.MaxValue)]
    [Theory]
    public void UnsupportedDepthIsRefusedBeforeTracingEvenWhenSequenceArithmeticWouldOverflow(int bounces) {
        var field = IrradianceScenes.Shell(inner: 3, thickness: 0.2);
        var reference = new IrradianceReference(field, IrradianceScenes.Uniform(albedo: 0, emission: 0, sky: 1), 100);

        Assert.Throws<ArgumentOutOfRangeException>(paramName: "bounces", testCode: () => reference.EstimateSources(new(X: 0, Y: -3, Z: 0), new(X: 0, Y: 1, Z: 0), bounces, 1));
        Assert.Throws<ArgumentOutOfRangeException>(paramName: "bounces", testCode: () => reference.Estimate(new(X: 0, Y: -3, Z: 0), new(X: 0, Y: 1, Z: 0), bounces, 1));
        Assert.Equal(0, field.Casts);
        Assert.Equal(0, field.Samples);
    }
    [Fact]
    public void FirstHitOriginsStayIndependentFromLaterFeedbackAndReceiverSelection() {
        var reference = new IrradianceReference(IrradianceScenes.Shell(inner: 3, thickness: 0.2), new IrradianceSurfaces(
            albedo: _ => new(X: 0.5, Y: 0.5, Z: 0.5), emission: _ => new(X: 0.75, Y: 0.75, Z: 0.75),
            direct: (_, _, _) => new(X: 0.25, Y: 0.25, Z: 0.25), screens: (_, _, _) => new(X: 0.5, Y: 0.5, Z: 0.5),
            sky: _ => new(X: 9, Y: 9, Z: 9)), 100);
        var result = reference.EstimateSources(new(X: 0, Y: -3, Z: 0), new(X: 0, Y: 1, Z: 0), 1, 64);

        Assert.Equal(0, result.Unresolved);
        Assert.Equal(new Double3(X: 0.125, Y: 0.125, Z: 0.125), result.Contributions.Direct);
        Assert.Equal(new Double3(X: 0.25, Y: 0.25, Z: 0.25), result.Contributions.Screens);
        Assert.Equal(new Double3(X: 0.75, Y: 0.75, Z: 0.75), result.Contributions.Emission);
        Assert.Equal(new Double3(X: 0.5625, Y: 0.5625, Z: 0.5625), result.Contributions.Feedback);
        Assert.Equal(Double3.Zero, result.Contributions.Sky);
        Assert.Equal(new Double3(X: 1.3125, Y: 1.3125, Z: 1.3125), result.Contributions.Select(sources: SdfIndirectSources.Feedback | SdfIndirectSources.Emission));
        Assert.Equal(reference.Estimate(new(X: 0, Y: -3, Z: 0), new(X: 0, Y: 1, Z: 0), 1, 64).Irradiance, result.Contributions.Total);
    }
    [Fact]
    public void PointReflectanceAttenuatesReflectionsButLeavesSelfEmissionIndependent() {
        var field = IrradianceScenes.Shell(inner: 3, thickness: 0.2);
        var surfaces = new IrradianceSurfaces(
            albedo: _ => new(X: 0.9, Y: 0.9, Z: 0.9), emission: _ => new(X: 1, Y: 1, Z: 1),
            direct: (_, _, _) => new(X: 2, Y: 2, Z: 2), reflection: (_, _, _) => new(X: 0.25, Y: 0.25, Z: 0.25));
        var reference = new IrradianceReference(field, surfaces, 100);
        var result = reference.EstimateSources(new(X: 0, Y: -3, Z: 0), new(X: 0, Y: 1, Z: 0), 1, 64);

        Assert.Equal(0, result.Unresolved);
        Assert.Equal(new Double3(X: 0.5, Y: 0.5, Z: 0.5), result.Contributions.Direct);
        Assert.Equal(new Double3(X: 0.375, Y: 0.375, Z: 0.375), result.Contributions.Feedback);
        Assert.Equal(new Double3(X: 1, Y: 1, Z: 1), result.Contributions.Emission);

        var model = new IrradianceCacheModel(field, surfaces,
            [new IrradianceLevel(Name: "room", Radius: 0, Reach: 0, Spacing: 1, Strata: 2)], new(ExitDistance: 100));

        model.Allocate(0, new(X: -4, Y: -4, Z: -4), new(X: 4, Y: 4, Z: 4));
        model.Classify();
        model.Trace();
        model.Solve(bounces: 0);
        var firstSweep = model.Contributions(new(X: 0, Y: -3, Z: 0), new(X: 0, Y: 1, Z: 0));

        Assert.NotNull(value: firstSweep);
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
        var reference = new IrradianceReference(IrradianceScenes.Shell(inner: 3, thickness: 0.2), IrradianceScenes.Uniform(albedo: 0, emission: 0, sky: 1), 100);
        var result = reference.EstimateSources(new(X: 0, Y: -3.1, Z: 0), new(X: 0, Y: 1, Z: 0), 0, 8);

        Assert.Equal(8, result.Unresolved);
        Assert.Equal(Double3.Zero, result.Contributions.Total);
    }
}
