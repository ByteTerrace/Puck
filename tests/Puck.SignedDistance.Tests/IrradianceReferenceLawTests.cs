using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Illumination;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

// The irradiance reference against closed forms: the furnace's finite-bounce series, a floor under a uniform sky, a
// floor beside an emissive half-plane, and a floor under a finite emissive disc.
public sealed class IrradianceReferenceLawTests {
    [Theory]
    [InlineData(0d)]
    [InlineData(.25d)]
    [InlineData(1d)]
    public void FeedbackGainScalesEachReflectedHopWithoutDimmingTheFirstSource(double gain) {
        var field = IrradianceScenes.Shell(3, .2);
        var surfaces = IrradianceScenes.Uniform(albedo: .5, emission: .75, sky: 9);
        var reference = new IrradianceReference(field, surfaces, 100, feedbackGain: gain);
        var result = reference.EstimateIncidentSources(Double3.Zero, new(0, 1, 0), bounces: 2, paths: 8);
        var expected = .75 * (1 + .5 * gain + .25 * gain * gain);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(new Double3(.75, .75, .75), result.Contributions.Emission);
        Assert.Equal(expected, result.Contributions.Total.X, precision: 9);
        Assert.Equal(expected - .75, result.Contributions.Feedback.X, precision: 9);
        Assert.Equal(gain == 0 ? 8L : 24L, field.Casts);
    }
    [Theory]
    [InlineData(1d, 1)]
    [InlineData(-1d, 256)]
    public void ACapturedFirstRaySelectsItsColoredHitAndKeepsEachSourceIndependent(double side, int paths) {
        var builder = new SdfProgramBuilder();
        var red = builder.AddMaterial(new SdfMaterial(Vector3.UnitX));
        var green = builder.AddMaterial(new SdfMaterial(Vector3.UnitY));
        builder.Translate(Vector3.UnitX).Sphere(.1f, red);
        builder.ResetPoint().Translate(-Vector3.UnitX).Sphere(.1f, green);
        var field = new IrradianceField(builder.Build());
        var reference = new IrradianceReference(field, new IrradianceSurfaces(
            albedo: static _ => new(.25, .25, .25),
            emission: material => material == red ? new(1, 0, 0) : new(0, 1, 0),
            direct: static (_, _, _) => new(2, 0, 0), screens: static (_, _, _) => new(0, 0, 4),
            sky: static _ => new(9, 9, 9)), exitDistance: 8);

        var result = reference.EstimateIncidentSources(Double3.Zero, new Double3(side * 3, 0, 0), bounces: 0, paths);
        Assert.Equal(paths, result.Paths);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(side > 0 ? new Double3(1, 0, 0) : new Double3(0, 1, 0), result.Contributions.Emission);
        Assert.Equal(new Double3(.5, 0, 0), result.Contributions.Direct);
        Assert.Equal(new Double3(0, 0, 1), result.Contributions.Screens);
        Assert.Equal(Double3.Zero, result.Contributions.Sky);
        Assert.Equal(Double3.Zero, result.Contributions.Feedback);
        Assert.Equal(paths, field.Casts);
        Assert.True(field.Samples > 0);
    }

    [Fact]
    public void ACapturedDirectionDoesNotBecomeAReceiverHemisphere() {
        var field = Floor();
        var reference = new IrradianceReference(field, new IrradianceSurfaces(
            albedo: static _ => default, emission: static _ => default,
            sky: static direction => direction.Y > .999 ? new(1, 0, 0) : new(0, 0, 1)), exitDistance: 8);
        var captured = reference.EstimateIncidentSources(new(0, .004, 0), new(0, 1, 0), bounces: 0);
        Assert.Equal(64, captured.Paths);
        Assert.Equal(0, captured.Unresolved);
        Assert.Equal(new Double3(1, 0, 0), captured.Contributions.Sky);
        Assert.Equal(64, field.Casts);
        var hemisphere = reference.EstimateSources(Double3.Zero, new(0, 1, 0), bounces: 0, paths: 64);
        Assert.Equal(0, hemisphere.Unresolved);
        Assert.Equal(new Double3(0, 0, 1), hemisphere.Contributions.Sky);
        Assert.Equal(128, field.Casts);
    }

    [Theory]
    [InlineData(.5d, 2)]
    [InlineData(1d, 9)]
    public void ACapturedIncomingRayRetainsTheFurnacesFiniteDepthAndFeedbackAttribution(double albedo, int bounces) {
        const double Emission = .75;
        const int Paths = 8;
        var field = IrradianceScenes.Shell(3, .2);
        var reference = new IrradianceReference(field, IrradianceScenes.Uniform(albedo, Emission, sky: 9), 100);
        var result = reference.EstimateIncidentSources(Double3.Zero, new(0, 1, 0), bounces, Paths);
        var expected = IrradianceScenes.Furnace(albedo: albedo, emission: Emission, bounces: bounces);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(new Double3(Emission, Emission, Emission), result.Contributions.Emission);
        Assert.Equal(expected - Emission, result.Contributions.Feedback.X, precision: 9);
        Assert.Equal(expected, result.Contributions.Total.X, precision: 9);
        Assert.Equal(expected, result.Contributions.Total.Y, precision: 9);
        Assert.Equal(expected, result.Contributions.Total.Z, precision: 9);
        Assert.Equal(Double3.Zero, result.Contributions.Direct);
        Assert.Equal(Double3.Zero, result.Contributions.Sky);
        Assert.Equal(Double3.Zero, result.Contributions.Screens);
        Assert.Equal(Paths * (bounces + 1), field.Casts);
    }

    [Fact]
    public void AnExactlyNonReflectingHitKeepsItsEmissionWhenTheNextLaunchCannotResolve() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.UnitX, Emissive: 1f, Metal: 1f));
        builder.Plane(Vector3.UnitY, 0f, material);
        builder.Plane(-Vector3.UnitY, .0005f, material);
        var program = builder.Build();
        var origin = new Double3(0, .0001, 0);
        var direction = new Double3(0, -1, 0);
        var witness = new IrradianceField(program);
        var hit = witness.Cast(origin, direction, 1);
        Assert.Equal(IrradianceRayKind.Hit, hit.Kind);
        Assert.True(witness.TryGradient(hit.Point, out var normal));
        Assert.Equal(new Double3(0, 1, 0), normal);
        // The first hit is certified, but its 0.001 m launch starts beyond the opposite wall of this 0.0005 m gap.
        Assert.Null(IrradianceCells.Launch(witness, hit.Point, normal, .004));

        var field = new IrradianceField(program);
        var surfaces = IrradianceSurfaces.FromMaterials(program.Materials,
            direct: static (_, _, _) => new(3, 4, 5), screens: static (_, _, _) => new(6, 7, 8));
        var reference = new IrradianceReference(field, surfaces, 1);
        var result = reference.EstimateIncidentSources(origin, direction, bounces: 1, paths: 4);
        Assert.Equal(0, result.Unresolved);
        Assert.Equal(new Double3(1, 0, 0), result.Contributions.Emission);
        Assert.Equal(new Double3(1, 0, 0), result.Contributions.Total);
        Assert.Equal(Double3.Zero, result.Contributions.Direct);
        Assert.Equal(Double3.Zero, result.Contributions.Screens);
        Assert.Equal(Double3.Zero, result.Contributions.Feedback);
        Assert.Equal(4, field.Casts);
        Assert.True(field.Samples > 0);

        // Neither callback can contribute at zero reflectance, even if its independent visibility work refuses.
        foreach (var directRefuses in new[] { true, false }) {
            Func<Double3, Double3, int, Double3> direct = (_, _, _) => directRefuses
                ? throw new InvalidOperationException("blocked direct visibility") : Double3.Zero;
            Func<Double3, Double3, int, Double3> screens = (_, _, _) => directRefuses
                ? Double3.Zero : throw new InvalidOperationException("blocked screen visibility");
            var callbackField = new IrradianceField(program);
            var callbackSurfaces = IrradianceSurfaces.FromMaterials(program.Materials, direct: direct, screens: screens);
            var callbackResult = new IrradianceReference(callbackField, callbackSurfaces, 1)
                .EstimateIncidentSources(origin, direction, bounces: 1, paths: 4);
            Assert.Equal(result, callbackResult);
            Assert.Equal(4, callbackField.Casts);
            foreach (var nonzero in new[] { 1d, double.Epsilon }) {
                var required = IrradianceSurfaces.FromMaterials(program.Materials, direct: direct, screens: screens,
                    reflection: (_, _, _) => new(nonzero, 0, 0));
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    new IrradianceReference(new IrradianceField(program), required, 1)
                        .EstimateIncidentSources(origin, direction, bounces: 0, paths: 1));
                Assert.Equal(directRefuses ? "blocked direct visibility" : "blocked screen visibility", exception.Message);
            }
        }

        foreach (var reflectance in new[] { 1d, double.Epsilon }) {
            var controlField = new IrradianceField(program);
            var controlSurfaces = IrradianceSurfaces.FromMaterials(program.Materials,
                reflection: (_, _, _) => new(reflectance, 0, 0));
            var control = new IrradianceReference(controlField, controlSurfaces, 1)
                .EstimateIncidentSources(origin, direction, bounces: 1, paths: 4);
            Assert.Equal(4, control.Unresolved);
            Assert.Equal(Double3.Zero, control.Contributions.Total);
            Assert.Equal(4, controlField.Casts);
            Assert.True(controlField.Samples > field.Samples);
        }
    }

    [Fact]
    public void CapturedRayArgumentsRefuseBeforeAnyFieldQuery() {
        var field = Floor();
        var reference = new IrradianceReference(field, IrradianceScenes.Uniform(0, 0, 1), 8);
        Double3[] directions = [default, new(double.NaN, 0, 0), new(0, double.PositiveInfinity, 0), new(0, 0, double.NegativeInfinity)];
        foreach (var direction in directions) {
            Assert.Throws<ArgumentOutOfRangeException>("direction", () => reference.EstimateIncidentSources(new(0, .004, 0), direction, 0));
        }
        Double3[] origins = [new(double.NaN, 0, 0), new(0, double.PositiveInfinity, 0), new(0, 0, double.NegativeInfinity)];
        foreach (var origin in origins) {
            Assert.Throws<ArgumentOutOfRangeException>("origin", () => reference.EstimateIncidentSources(origin, new(0, 1, 0), 0));
        }
        foreach (var bounces in new[] { -1, 10, int.MaxValue }) {
            Assert.Throws<ArgumentOutOfRangeException>("bounces", () => reference.EstimateIncidentSources(new(0, .004, 0), new(0, 1, 0), bounces));
        }
        foreach (var paths in new[] { -1, 0, 257, int.MaxValue }) {
            Assert.Throws<ArgumentOutOfRangeException>("paths", () => reference.EstimateIncidentSources(new(0, .004, 0), new(0, 1, 0), 0, paths));
        }
        Assert.Equal(0, field.Casts);
        Assert.Equal(0, field.Samples);
    }

    [Fact]
    public void AnUnresolvedCapturedRayCannotBecomeACompleteBlackOrSkyAnswer() {
        var field = Floor();
        var reference = new IrradianceReference(field, IrradianceScenes.Uniform(0, 0, 1), 10);
        var result = reference.EstimateIncidentSources(new(0, .0015, 0), new(1, 0, 0), bounces: 0, paths: 4);
        Assert.Equal(4, result.Paths);
        Assert.Equal(4, result.Unresolved);
        Assert.Equal(Double3.Zero, result.Contributions.Total);
        Assert.Equal(4, field.Casts);
    }

    [Fact]
    public void BleedScalesTransportWhileReceiveAndFillRemainViewControls() {
        var material = new SdfMaterial(new Vector3(0.5f, 0.25f, 1f), Emissive: 2,
            Metal: 0.5f, Bleed: new Vector3(0.25f, 0.5f, 0.75f), Fill: Vector3.One, Receive: 7);
        var surfaces = IrradianceSurfaces.FromMaterials([material]);
        Assert.Equal(new Double3(0.0625, 0.0625, 0.375), surfaces.Albedo(0));
        Assert.Equal(new Double3(0.25, 0.25, 1.5), surfaces.Emission(0));
        var control = IrradianceSurfaces.FromMaterials([material with { Receive = 0, Fill = Vector3.Zero }]);
        Assert.Equal(surfaces.Albedo(0), control.Albedo(0));
        Assert.Equal(surfaces.Emission(0), control.Emission(0));
        var black = IrradianceSurfaces.FromMaterials([material with { Bleed = Vector3.Zero }]);
        Assert.Equal(Double3.Zero, black.Albedo(0));
        Assert.Equal(Double3.Zero, black.Emission(0));
    }
    [InlineData(0.0, 0)]
    [InlineData(0.0, 3)]
    [InlineData(0.5, 0)]
    [InlineData(0.5, 1)]
    [InlineData(0.5, 4)]
    [InlineData(1.0, 0)]
    [InlineData(1.0, 3)]
    [Theory]
    public void TheFurnaceSumsItsFiniteSeries(double albedo, int bounces) {
        const double Emission = 0.75;

        var reference = new IrradianceReference(
            exitDistance: 100.0,
            field: IrradianceScenes.Shell(inner: 3.0, thickness: 0.2),
            surfaces: IrradianceScenes.Uniform(albedo: albedo, emission: Emission, sky: 9.0)
        );
        var estimate = reference.Estimate(bounces: bounces, normal: new Double3(X: 0.0, Y: 1.0, Z: 0.0), paths: 64, point: new Double3(X: 0.0, Y: -3.0, Z: 0.0));
        var expected = IrradianceScenes.Furnace(albedo: albedo, bounces: bounces, emission: Emission);

        Assert.Equal(expected: 0, actual: estimate.Unresolved);
        Assert.Equal(expected: expected, actual: estimate.Irradiance.X, precision: 9);

        // Red leg: one bounce more is a different value at every albedo above zero, and at zero the sky (9) never
        // reaches a sealed shell's inside.
        if (albedo > 0.0) {
            Assert.NotEqual(expected: IrradianceScenes.Furnace(albedo: albedo, bounces: (bounces + 1), emission: Emission), actual: estimate.Irradiance.X, precision: 6);
        }

        Assert.True(condition: (estimate.Irradiance.X < 9.0));
    }
    [Fact]
    public void AnOpenFloorReceivesTheSkysColour() {
        var reference = new IrradianceReference(
            exitDistance: 100.0,
            field: Floor(),
            surfaces: IrradianceScenes.Uniform(albedo: 0.0, emission: 0.0, sky: 1.0)
        );
        var estimate = reference.Estimate(bounces: 0, normal: new Double3(X: 0.0, Y: 1.0, Z: 0.0), paths: 256, point: Double3.Zero);

        Assert.Equal(expected: 0, actual: estimate.Unresolved);
        Assert.Equal(expected: 1.0, actual: estimate.Irradiance.X, precision: 9);
    }
    [Fact]
    public void AFiniteWallSuppliesItsFormFactor() {
        // A thin emissive wall whose near face spans x = 1, y in [0, 2], z in [-1, 1], beside an upward receiver at the
        // origin in free space. Its form factor is integrated over that face by quadrature, independently of the field.
        var builder = new SdfProgramBuilder();

        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var wall = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: new Vector3(x: 1.01f, y: 1f, z: 0f));
        _ = builder.Box(halfExtents: new Vector3(x: 0.01f, y: 1f, z: 1f), material: wall, round: 0f);

        var reference = new IrradianceReference(
            exitDistance: 100.0,
            field: new IrradianceField(program: builder.Build()),
            surfaces: new IrradianceSurfaces(
                albedo: static _ => Double3.Zero,
                emission: static material => ((material == 1) ? new Double3(X: 1.0, Y: 1.0, Z: 1.0) : Double3.Zero)
            )
        );
        var estimate = reference.Estimate(bounces: 0, normal: new Double3(X: 0.0, Y: 1.0, Z: 0.0), paths: 4096, point: Double3.Zero);
        var formFactor = WallFormFactor(distance: 1.0, halfDepth: 1.0, height: 2.0);

        Assert.Equal(expected: 0, actual: estimate.Unresolved);
        Assert.InRange(actual: estimate.Irradiance.X, high: (formFactor + 0.01), low: (formFactor - 0.01));
        // Red leg: rays passing through geometry would read the black sky, and an infinite half-plane would give one half.
        Assert.True(condition: (formFactor > 0.05));
        Assert.True(condition: (Math.Abs(value: (formFactor - 0.5)) > 0.05));
    }
    [Fact]
    public void AFiniteDiscOverAFloorSuppliesItsFormFactor() {
        const double Radius = 1.0;
        const double Height = 1.5;

        var builder = new SdfProgramBuilder();
        var floor = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var disc = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: floor, normal: Vector3.UnitY, offset: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 0f, y: ((float)Height), z: 0f));
        _ = builder.Cylinder(halfHeight: 0.005f, material: disc, radius: ((float)Radius));

        var reference = new IrradianceReference(
            exitDistance: 100.0,
            field: new IrradianceField(program: builder.Build()),
            surfaces: new IrradianceSurfaces(
                albedo: static _ => Double3.Zero,
                emission: static material => ((material == 1) ? new Double3(X: 1.0, Y: 1.0, Z: 1.0) : Double3.Zero)
            )
        );
        var estimate = reference.Estimate(bounces: 0, normal: new Double3(X: 0.0, Y: 1.0, Z: 0.0), paths: 4096, point: Double3.Zero);
        var formFactor = ((Radius * Radius) / ((Radius * Radius) + ((Height - 0.005) * (Height - 0.005))));

        Assert.Equal(expected: 0, actual: estimate.Unresolved);
        Assert.InRange(actual: estimate.Irradiance.X, high: (formFactor + 0.01), low: (formFactor - 0.01));
        // Red leg: the infinite half-plane's value (one half) and an unoccluded sky's (one) are both far outside.
        Assert.False(condition: (Math.Abs(value: (estimate.Irradiance.X - 0.5)) < 0.05));
    }
    [Fact]
    public void ASegmentEndingExactlyOnASurfaceIsBlocked() {
        var field = Floor();
        var from = new Double3(X: 0.0, Y: 1.0, Z: 0.0);

        Assert.False(condition: field.SegmentClear(from: from, to: Double3.Zero));
        Assert.True(condition: field.SegmentClear(from: from, to: new Double3(X: 0.0, Y: 0.01, Z: 0.0)));
    }
    [InlineData(0.12345)]
    [InlineData(10000.0)]
    [Theory]
    public void ARayReturnsThePointTheEvaluatorActuallyHit(double height) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f);

        var program = builder.Build();
        var field = new IrradianceField(program: program);
        var evaluator = new SdfFieldEvaluator(program: program);
        var origin = new Double3(X: 0.0, Y: height, Z: 0.0);
        var direction = new Double3(X: 1.0, Y: -1.0, Z: 0.0).Normalize();
        var reach = (height * 2.0);

        Assert.True(condition: evaluator.Raycast(
            dir: new FixedVector3(X: FixedQ4816.FromDouble(value: direction.X), Y: FixedQ4816.FromDouble(value: direction.Y), Z: FixedQ4816.Zero),
            hit: out var hit,
            maxDist: FixedQ4816.FromDouble(value: reach),
            origin: FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromDouble(value: height), Z: FixedQ4816.Zero))
        ));

        var actual = field.Cast(direction: direction, maxDistance: reach, origin: origin);
        var point = (hit.Point - FixedPosition.Zero);

        Assert.Equal(expected: IrradianceRayKind.Hit, actual: actual.Kind);
        Assert.Equal(expected: new Double3(X: ((double)point.X), Y: ((double)point.Y), Z: ((double)point.Z)), actual: actual.Point);
        // Red leg: origin + doubleDirection * hit.Distance discards the evaluator's quantized origin, direction
        // and per-step positions, and returns a different point, increasingly far from the surface on a long ray.
    }
    [Fact]
    public void AnUnresolvedRayIsNamedNotShaded() {
        var field = Floor();
        // Parallel to the floor and just above its accept threshold, the march advances by the clearance and its sample
        // budget ends long before the ray's reach.
        var ray = field.Cast(direction: new Double3(X: 1.0, Y: 0.0, Z: 0.0), maxDistance: 10.0, origin: new Double3(X: 0.0, Y: 0.0015, Z: 0.0));

        Assert.Equal(expected: IrradianceRayKind.Unresolved, actual: ray.Kind);
        Assert.True(condition: (ray.Distance < 10.0));
    }
    [Fact]
    public void AnUnsupportedOperationIsRefusedByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.TwistY(rate: 0.5f);
        _ = builder.Sphere(material: material, radius: 1f);

        var refusal = Assert.ThrowsAny<ArgumentException>(testCode: () => new IrradianceField(program: builder.Build()));

        Assert.False(condition: string.IsNullOrWhiteSpace(value: refusal.Message));
    }

    // (1 / pi) times the integral over the wall face x = distance of cos(at receiver) cos(at wall) / r^2, by the
    // midpoint rule: cos at the upward receiver is y / r and at the wall distance / r.
    private static double WallFormFactor(double distance, double height, double halfDepth) {
        const int Steps = 800;

        var dy = (height / Steps);
        var dz = ((2.0 * halfDepth) / Steps);
        var sum = 0.0;

        for (var i = 0; (i < Steps); i++) {
            var y = ((i + 0.5) * dy);

            for (var j = 0; (j < Steps); j++) {
                var z = (-halfDepth + ((j + 0.5) * dz));
                var squared = (((distance * distance) + (y * y)) + (z * z));

                sum += ((y * distance) / (squared * squared));
            }
        }

        return ((sum * (dy * dz)) / Math.PI);
    }
    private static IrradianceField Floor() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f);

        return new IrradianceField(program: builder.Build());
    }
}
