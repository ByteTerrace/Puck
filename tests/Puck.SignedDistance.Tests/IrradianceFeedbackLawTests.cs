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
        var fine = new[] { new IrradianceProbeKey(Level: 0, X: 0, Y: 0, Z: 0), new IrradianceProbeKey(Level: 0, X: 1, Y: 0, Z: 0) };
        var coarse = new[] { new IrradianceProbeKey(Level: 1, X: 0, Y: 0, Z: 0), new IrradianceProbeKey(Level: 1, X: 1, Y: 0, Z: 0) };
        var schedule = new IrradianceSolveSchedule([fine, coarse], bounces: 2, probeBudget: 1);
        var actual = new List<(int Sweep, int Level, int Write)>();

        for (var sweep = 0; (sweep <= 2); sweep++) {
            for (var index = 0; (index < 4); index++) {
                var batch = Assert.IsType<IrradianceSolveBatch>(@object: schedule.Plan());

                Assert.Same(batch, schedule.Plan());
                Assert.Single(collection: batch.Probes);
                Assert.Equal(sweep, schedule.CompletedSweeps);
                Assert.Equal(((sweep == 0) ? -1 : (sweep - 1) & 1), schedule.PublishedGeneration);
                Assert.Equal((index == 3), batch.CompletesSweep);
                actual.Add(item: (batch.Sweep, batch.Level, batch.WriteGeneration));
                schedule.Submitted();
            }
        }
        Assert.Equal(actual: actual, expected: new[] { (0, 1, 0), (0, 1, 0), (0, 0, 0), (0, 0, 0),
            (1, 1, 1), (1, 1, 1), (1, 0, 1), (1, 0, 1), (2, 1, 0), (2, 1, 0), (2, 0, 0), (2, 0, 0) });
        Assert.True(condition: schedule.IsComplete);
        Assert.Null(@object: schedule.Plan());
        schedule.Submitted();
        Assert.Equal(3, schedule.CompletedSweeps);
    }
    [InlineData(0)]
    [InlineData(1)]
    [Theory]
    public void SourcesRemainIndependentThroughTheFiniteSolveAndReceiverWeights(int bounces) {
        var surfaces = new IrradianceSurfaces(
            albedo: static _ => new Double3(X: 0.5, Y: 0.5, Z: 0.5),
            emission: static _ => new Double3(X: 0, Y: 3, Z: 0),
            direct: static (_, _, _) => new Double3(X: 2, Y: 0, Z: 0),
            screens: static (_, _, _) => new Double3(X: 0, Y: 0, Z: 4));
        var model = Furnace(albedo: 0.5, surfaces: surfaces);

        model.Solve(bounces: bounces);
        foreach (var (point, normal) in Receivers()) {
            var contributions = model.Contributions(normal: normal, surface: point);

            Assert.NotNull(value: contributions);
            Equal(new Double3(X: 1, Y: 0, Z: 0), contributions.Value.Direct);
            Equal(new Double3(X: 0, Y: 3, Z: 0), contributions.Value.Emission);
            Equal(new Double3(X: 0, Y: 0, Z: 2), contributions.Value.Screens);
            Equal(Double3.Zero, contributions.Value.Sky);
            Equal(((bounces == 0) ? Double3.Zero : new Double3(X: 0.5, Y: 1.5, Z: 1)), contributions.Value.Feedback);
            Equal(((bounces == 0) ? new Double3(X: 1, Y: 3, Z: 2) : new Double3(X: 1.5, Y: 4.5, Z: 3)), contributions.Value.Total);
            Equal(contributions.Value.Total, model.Irradiance(normal: normal, surface: point)!.Value);
        }

        static void Equal(Double3 expected, Double3 actual) {
            Assert.Equal(expected.X, actual.X, precision: 9);
            Assert.Equal(expected.Y, actual.Y, precision: 9);
            Assert.Equal(expected.Z, actual.Z, precision: 9);
        }
    }
    [InlineData(1.0, 0.75)]
    [InlineData(0.0, 0.75)]
    [InlineData(0.0, 0.0)]
    [Theory]
    public void MissingReflectedSupportIsUnknownWithoutDiscardingDirectOrZeroThroughput(double albedo, double emission) {
        // Only the lattice cube 0..3 is allocated. Every inner wall lies at -.25 or 3.25, so a hit is real but
        // its launched feedback cell needs an unallocated corner. The first sweep needs no such support.
        var model = PartialRoom(albedo, emission, halfHeight: 1.75);
        var probe = new IrradianceProbeKey(Level: 0, X: 1, Y: 1, Z: 1);
        var up = new Double3(X: 0, Y: 1, Z: 0);
        var ray = model.NearestRayOf(direction: up, key: probe);
        var hit = model.HitsOf(key: probe)[ray];

        Assert.Equal(IrradianceHitKind.Hit, hit.Kind);
        Assert.Equal(-1, model.ReadableCorners(0, hit.Point, hit.Normal));

        model.Solve(bounces: 0);
        Assert.Equal(emission, model.RadianceOf(key: probe, ray: ray)!.Value.X, precision: 9);
        Assert.Equal(emission, model.ProbeIrradianceOf(key: probe, normal: up)!.Value.X, precision: 9);
        model.Solve(bounces: 1);
        if (albedo == 0.0) {
            Assert.Equal(emission, model.RadianceOf(key: probe, ray: ray)!.Value.X, precision: 9);
            Assert.Equal(emission, model.ProbeIrradianceOf(key: probe, normal: up)!.Value.X, precision: 9);
        } else {
            Assert.Null(value: model.RadianceOf(key: probe, ray: ray));
            Assert.Null(value: model.ProbeIrradianceOf(key: probe, normal: up));
            Assert.Equal(1.0, model.UnresolvedShareOf(key: probe, normal: up), precision: 9);
        }
    }
    [Fact]
    public void APartlySupportedRoomNormalizesItsKnownLightingWithoutInventingBlack() {
        // The floor and ceiling now lie at .25 and 2.75, within allocated feedback cells. Side walls still
        // have no support. Known rays must conserve the uniform finite series, excluding the unknown side rays.
        var model = PartialRoom(albedo: 1.0, emission: 0.75, halfHeight: 1.25);
        var probe = new IrradianceProbeKey(Level: 0, X: 1, Y: 1, Z: 1);
        var up = new Double3(X: 0, Y: 1, Z: 0);

        model.Solve(bounces: 2);
        var known = 0;
        var unknown = 0;
        var hits = model.HitsOf(key: probe);

        for (var ray = 0; (ray < hits.Count); ray++) {
            if (hits[ray].Kind != IrradianceHitKind.Hit) { continue; }
            if (model.RadianceOf(key: probe, ray: ray) is { } value) {
                Assert.Equal(2.25, value.X, precision: 9);
                known++;
            } else {
                unknown++;
            }
        }
        Assert.True(condition: (known > 0));
        Assert.True(condition: (unknown > 0));
        var share = model.UnresolvedShareOf(key: probe, normal: up);

        Assert.True(condition: ((share > 0.0) && (share < 1.0)));
        Assert.Equal(2.25, model.ProbeIrradianceOf(key: probe, normal: up)!.Value.X, precision: 9);
        Assert.Equal(2.25, model.ProbeIrradianceOf(key: probe, normal: -up)!.Value.X, precision: 9);
    }

    private static IrradianceCacheModel PartialRoom(double albedo, double emission, double halfHeight) {
        var model = new IrradianceCacheModel(
            field: IrradianceScenes.Room(new Double3(X: 1.5, Y: 1.5, Z: 1.5), new Double3(X: 1.75, Y: halfHeight, Z: 1.75), thickness: 0.2),
            levels: [Level],
            options: new IrradianceModelOptions(ExitDistance: 100.0),
            surfaces: IrradianceScenes.Uniform(albedo: albedo, emission: emission, sky: 0.0));

        model.Allocate(level: 0, min: Double3.Zero, max: Double3.Zero);
        model.Classify();
        model.Trace();
        return model;
    }
    private static IrradianceCacheModel Furnace(double albedo, IrradianceSurfaces? surfaces = null) {
        var model = new IrradianceCacheModel(
            field: IrradianceScenes.Shell(inner: 3.0, thickness: 0.2),
            levels: [Level],
            options: new IrradianceModelOptions(ExitDistance: 100.0),
            surfaces: (surfaces ?? IrradianceScenes.Uniform(albedo: albedo, emission: Emission, sky: 0.0))
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
