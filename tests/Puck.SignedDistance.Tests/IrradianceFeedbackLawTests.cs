using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

// The cache model's finite solve: the furnace's series at every albedo, unit albedo included, and a result that the
// order probes are swept in and the frames a sweep is split across leave unchanged.
public sealed class IrradianceFeedbackLawTests {
    private const double Emission = 0.75;

    private static readonly IrradianceLevel Level = new(Name: "room", Radius: 0.0, Reach: 0.0, Spacing: 1.0, Strata: 2);

    [InlineData(0.0, 2)]
    [InlineData(0.5, 0)]
    [InlineData(0.5, 3)]
    [InlineData(1.0, 2)]
    [Theory]
    public void TheCacheSumsTheFurnacesFiniteSeries(double albedo, int bounces) {
        var model = Furnace(albedo: albedo);

        model.Solve(bounces: bounces);

        var expected = IrradianceScenes.Furnace(albedo: albedo, bounces: bounces, emission: Emission);

        foreach (var (point, normal) in Receivers()) {
            var irradiance = model.Irradiance(normal: normal, surface: point);

            Assert.NotNull(@object: irradiance);
            Assert.Equal(expected: expected, actual: irradiance.Value.X, precision: 9);
        }

        Assert.Equal(expected: 0, actual: model.UnresolvedRays);
    }
    [Fact]
    public void OrderAndSplittingChangeNothing() {
        var whole = Furnace(albedo: 0.6);
        var reversed = Furnace(albedo: 0.6);
        var split = Furnace(albedo: 0.6);

        whole.Solve(bounces: 3);
        reversed.Solve(bounces: 3, reverseOrder: true);
        split.Solve(bounces: 3, probesPerStep: 7);

        foreach (var (point, normal) in Receivers()) {
            var expected = whole.Irradiance(normal: normal, surface: point)!.Value;

            Assert.Equal(expected: expected, actual: reversed.Irradiance(normal: normal, surface: point)!.Value);
            Assert.Equal(expected: expected, actual: split.Irradiance(normal: normal, surface: point)!.Value);
        }
    }

    private static IrradianceCacheModel Furnace(double albedo) {
        var model = new IrradianceCacheModel(
            field: IrradianceScenes.Shell(inner: 3.0, thickness: 0.2),
            levels: [Level],
            options: new IrradianceModelOptions(ExitDistance: 100.0),
            surfaces: IrradianceScenes.Uniform(albedo: albedo, emission: Emission, sky: 0.0)
        );

        model.Allocate(level: 0, max: new Double3(X: 4.0, Y: 4.0, Z: 4.0), min: new Double3(X: -4.0, Y: -4.0, Z: -4.0));
        model.Classify();
        model.Trace();

        return model;
    }
    // Points on the shell's inner surface, each with its inward normal.
    private static IEnumerable<(Double3 Point, Double3 Normal)> Receivers() {
        Double3[] directions = [
            new(X: 0.0, Y: -1.0, Z: 0.0),
            new(X: 1.0, Y: 0.0, Z: 0.0),
            new(X: 0.3, Y: 0.4, Z: -0.866),
            new(X: -0.577, Y: 0.577, Z: 0.577),
        ];

        foreach (var direction in directions) {
            var unit = direction.Normalize();

            yield return ((unit * 3.0), -unit);
        }
    }
}
