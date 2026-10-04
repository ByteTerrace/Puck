using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Illumination;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

// The irradiance reference against closed forms: the furnace's finite-bounce series, a floor under a uniform sky, a
// floor beside an emissive half-plane, and a floor under a finite emissive disc.
public sealed class IrradianceReferenceLawTests {
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
