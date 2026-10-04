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
    [Fact]
    public void OnlySubmittedWholeSweepsPublishAndFinerReadersWaitForTheirCoarserWriters() {
        var fine = new[] { new IrradianceProbeKey(0, 0, 0, 0), new IrradianceProbeKey(0, 1, 0, 0) };
        var coarse = new[] { new IrradianceProbeKey(1, 0, 0, 0), new IrradianceProbeKey(1, 1, 0, 0) };
        var schedule = new IrradianceSolveSchedule([fine, coarse], bounces: 2, probeBudget: 1);
        var actual = new List<(int Sweep, int Level, int Write)>();
        for (var sweep = 0; sweep <= 2; sweep++) {
            for (var index = 0; index < 4; index++) {
                var batch = Assert.IsType<IrradianceSolveBatch>(schedule.Plan());
                Assert.Same(batch, schedule.Plan());
                Assert.Single(batch.Probes);
                Assert.Equal(sweep, schedule.CompletedSweeps);
                Assert.Equal(sweep == 0 ? -1 : ((sweep - 1) & 1), schedule.PublishedGeneration);
                Assert.Equal(index == 3, batch.CompletesSweep);
                actual.Add((batch.Sweep, batch.Level, batch.WriteGeneration));
                schedule.Submitted();
            }
        }
        Assert.Equal(new[] { (0, 1, 0), (0, 1, 0), (0, 0, 0), (0, 0, 0),
            (1, 1, 1), (1, 1, 1), (1, 0, 1), (1, 0, 1), (2, 1, 0), (2, 1, 0), (2, 0, 0), (2, 0, 0) }, actual);
        Assert.True(schedule.IsComplete);
        Assert.Null(schedule.Plan());
        schedule.Submitted();
        Assert.Equal(3, schedule.CompletedSweeps);
    }

    [InlineData(0)]
    [InlineData(1)]
    [Theory]
    public void SourcesRemainIndependentThroughTheFiniteSolveAndReceiverWeights(int bounces) {
        var surfaces = new IrradianceSurfaces(
            albedo: static _ => new Double3(0.5, 0.5, 0.5),
            emission: static _ => new Double3(0, 3, 0),
            direct: static (_, _, _) => new Double3(2, 0, 0),
            screens: static (_, _, _) => new Double3(0, 0, 4));
        var model = Furnace(albedo: 0.5, surfaces: surfaces);
        model.Solve(bounces: bounces);
        foreach (var (point, normal) in Receivers()) {
            var contributions = model.Contributions(point, normal);
            Assert.NotNull(contributions);
            Equal(new Double3(1, 0, 0), contributions.Value.Direct);
            Equal(new Double3(0, 3, 0), contributions.Value.Emission);
            Equal(new Double3(0, 0, 2), contributions.Value.Screens);
            Equal(Double3.Zero, contributions.Value.Sky);
            Equal(bounces == 0 ? Double3.Zero : new Double3(0.5, 1.5, 1), contributions.Value.Feedback);
            Equal(bounces == 0 ? new Double3(1, 3, 2) : new Double3(1.5, 4.5, 3), contributions.Value.Total);
            Equal(contributions.Value.Total, model.Irradiance(point, normal)!.Value);
        }

        static void Equal(Double3 expected, Double3 actual) {
            Assert.Equal(expected.X, actual.X, precision: 9);
            Assert.Equal(expected.Y, actual.Y, precision: 9);
            Assert.Equal(expected.Z, actual.Z, precision: 9);
        }
    }

    private static IrradianceCacheModel Furnace(double albedo, IrradianceSurfaces? surfaces = null) {
        var model = new IrradianceCacheModel(
            field: IrradianceScenes.Shell(inner: 3.0, thickness: 0.2),
            levels: [Level],
            options: new IrradianceModelOptions(ExitDistance: 100.0),
            surfaces: surfaces ?? IrradianceScenes.Uniform(albedo: albedo, emission: Emission, sky: 0.0)
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
