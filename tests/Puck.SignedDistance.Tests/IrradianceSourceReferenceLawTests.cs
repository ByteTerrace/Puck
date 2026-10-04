using Puck.SignedDistance.Illumination;
using Xunit;
namespace Puck.SignedDistance.Tests;
public sealed class IrradianceSourceReferenceLawTests {
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
        var reference = new IrradianceReference(IrradianceScenes.Shell(3, 0.2), new IrradianceSurfaces(
            albedo: _ => new(0.9, 0.9, 0.9), emission: _ => new(1, 1, 1),
            direct: (_, _, _) => new(2, 2, 2), reflection: (_, _, _) => new(0.25, 0.25, 0.25)), 100);
        var result = reference.EstimateSources(new(0, -3, 0), new(0, 1, 0), 1, 64);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(new Double3(0.5, 0.5, 0.5), result.Contributions.Direct);
        Assert.Equal(new Double3(0.375, 0.375, 0.375), result.Contributions.Feedback);
        Assert.Equal(new Double3(1, 1, 1), result.Contributions.Emission);
    }
    [Fact]
    public void AFailedReceiverLaunchIsUnresolvedInsteadOfACompleteBlackReference() {
        var reference = new IrradianceReference(IrradianceScenes.Shell(3, 0.2), IrradianceScenes.Uniform(0, 0, 1), 100);
        var result = reference.EstimateSources(new(0, -3.1, 0), new(0, 1, 0), 0, 8);
        Assert.Equal(8, result.Unresolved);
        Assert.Equal(Double3.Zero, result.Contributions.Total);
    }
}
