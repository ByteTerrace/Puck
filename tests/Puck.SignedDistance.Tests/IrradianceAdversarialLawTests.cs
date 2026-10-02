using System.Numerics;

using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class IrradianceAdversarialLawTests {
    private static readonly Double3 Up = new(X: 0.0, Y: 1.0, Z: 0.0);

    [Fact]
    public void ALightViewDoesNotAnswerBeyondItsSweptDepth() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: new Vector3(x: 0f, y: -25f, z: 0f));
        _ = builder.Box(halfExtents: new Vector3(x: 2f, y: 0.1f, z: 2f), material: material, round: 0f);

        var field = new IrradianceField(program: builder.Build());
        var point = new Double3(X: 0.0, Y: -30.0, Z: 0.0);
        var view = new IrradianceLightView(center: Double3.Zero, distance: 20.0, field: field, halfWidth: 1.0, resolution: 4, towardLight: Up);

        Assert.False(condition: field.SegmentClear(from: point, to: Double3.Zero));
        Assert.Null(@object: view.Lit(normal: Up, point: point));
        Assert.Null(@object: view.Lit(normal: Up, point: new Double3(X: 0.0, Y: 21.0, Z: 0.0)));
    }
    [Fact]
    public void AnUnresolvedLightViewTexelRequiresItsOwnShadowRay() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 100f, y: 0f, z: 100f));
        _ = builder.Box(blend: SdfBlendOp.ChamferUnion, halfExtents: Vector3.One, material: material, round: 0f, smooth: 0.1f);

        var field = new IrradianceField(program: builder.Build());
        var point = Double3.Zero;
        var view = new IrradianceLightView(center: Double3.Zero, distance: 20.0, field: field, halfWidth: 1.0, resolution: 4, towardLight: Up);

        Assert.True(condition: (field.StepScale < 1.0));
        Assert.True(condition: (view.Unresolved > 0));
        Assert.True(condition: field.SegmentClear(from: (point + (Up * 0.004)), to: (point + (Up * 10.0))));
        Assert.Null(@object: view.Lit(normal: Up, point: point));
    }
    [Fact]
    public void ALowerBoundFieldDoesNotProveAHitWithinTheSurfaceEpsilon() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Superellipsoid(exponent: 2f, material: material, radii: new Vector3(x: 10f, y: 1f, z: 1f));

        var field = new IrradianceField(program: builder.Build());
        var ray = field.Cast(direction: Up, maxDistance: 1.0, origin: new Double3(X: 10.005, Y: 0.0, Z: 0.0));

        // Every point on this ray is at least 0.005 outside the ellipsoid's maximum X of 10, even though its
        // min-radius gauge reads about 0.0005 at the origin. A lower bound is insufficient to accept a surface.
        Assert.NotEqual(expected: IrradianceRayKind.Hit, actual: ray.Kind);
    }
    [Fact]
    public void AFailedProofCannotSuppressANearbyProvableReceiver() {
        // The sphere relocates the cell's lower-left probe, putting the nearest-corner boundary inside one proof
        // slot. The wall blocks the anchor's nearest corner; the receiver 0.002 away reaches the other corner.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: -0.1f, y: 0f, z: 0f));
        _ = builder.Sphere(material: material, radius: 0.2f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 0.8f, y: 0.2f, z: 0f));
        _ = builder.Box(halfExtents: new Vector3(x: 0.01f, y: 0.15f, z: 0.2f), material: material, round: 0f);

        var field = new IrradianceField(program: builder.Build());
        var level = new IrradianceLevel(Name: "room", Radius: 0.0, Reach: 0.0, Spacing: 1.5, Strata: 2);
        var anchor = new Double3(X: 0.8746, Y: 0.0, Z: 0.0);
        var receiver = new Double3(X: 0.8766, Y: 0.0, Z: 0.0);

        IrradianceCacheModel Model() {
            var model = new IrradianceCacheModel(field: field, levels: [level], options: new IrradianceModelOptions(ExitDistance: 20.0), surfaces: IrradianceScenes.Uniform(albedo: 0.0, emission: 0.0, sky: 1.0));

            model.Allocate(level: 0, max: new Double3(X: 1.5, Y: 1.5, Z: 1.5), min: Double3.Zero);
            model.Classify();

            return model;
        }

        var fresh = Model();
        var cached = Model();
        var expected = fresh.ReadableCorners(level: 0, normal: Up, surface: receiver);

        Assert.True(condition: (expected > 0));
        var anchorMask = cached.ReadableCorners(level: 0, normal: Up, surface: anchor);

        Assert.Equal(actual: anchorMask, expected: 0);
        Assert.Equal(expected: expected, actual: cached.ReadableCorners(level: 0, normal: Up, surface: receiver));
    }
    [Fact]
    public void AConservativeGaugeDoesNotDarkenAClosedFurnace() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Superellipsoid(exponent: 2f, material: material, radii: new Vector3(x: 5.05f, y: 1.05f, z: 1.05f));
        _ = builder.Superellipsoid(blend: SdfBlendOp.Subtraction, exponent: 2f, material: material, radii: new Vector3(x: 5f, y: 1f, z: 1f));

        var field = new IrradianceField(program: builder.Build());
        var inward = new Double3(X: -1.0, Y: 0.0, Z: 0.0);
        var point = new Double3(X: 5.0, Y: 0.0, Z: 0.0);
        var model = new IrradianceCacheModel(
            field: field,
            levels: [new(Name: "room", Radius: 0.0, Reach: 0.0, Spacing: 1.0, Strata: 2)],
            options: new IrradianceModelOptions(ExitDistance: 40.0),
            surfaces: IrradianceScenes.Uniform(albedo: 0.0, emission: 1.0, sky: 0.0)
        );

        model.Allocate(level: 0, max: new Double3(X: 6.0, Y: 2.0, Z: 2.0), min: new Double3(X: -1.0, Y: -1.0, Z: -1.0));
        model.Classify();
        model.Trace();
        model.Solve(bounces: 0);

        Assert.Equal(expected: 1.0, actual: model.Irradiance(normal: inward, surface: point)!.Value.X, precision: 9);
    }
    [Fact]
    public void ALaunchCannotJumpASlabBeforeItsFirstSample() {
        // The slab lies wholly beneath the launch's first sample, between 3 and 10 ticks off the surface. The descent
        // from the first sample stalls on its top, so the launch fails and the receiver reads dark, never the sky.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f);
        _ = builder.ResetPoint();
        _ = builder.PushField();
        _ = builder.Plane(material: material, normal: -Vector3.UnitY, offset: 0.00005f);
        _ = builder.Plane(blend: SdfBlendOp.Intersection, material: material, normal: Vector3.UnitY, offset: -0.00015f);
        _ = builder.PopField();

        var field = new IrradianceField(program: builder.Build());
        var launch = IrradianceCells.Launch(field: field, height: 0.004, normal: Up, surface: Double3.Zero);
        var reference = new IrradianceReference(exitDistance: 10.0, field: field, surfaces: IrradianceScenes.Uniform(albedo: 0.0, emission: 0.0, sky: 1.0)).Estimate(bounces: 0, normal: Up, paths: 64, point: Double3.Zero);

        Assert.True(condition: ((launch is null) || (launch.Value.Point.Y < 0.00005)), userMessage: $"launch crossed the slab: {launch}; reference reads {reference.Irradiance.X} through the slab");
        Assert.Equal(expected: 0.0, actual: reference.Irradiance.X);

        // Without the slab the same launch certifies its first interval and reaches its height.
        var clear = IrradianceCells.Launch(field: new IrradianceField(program: Floor()), height: 0.004, normal: Up, surface: Double3.Zero);

        Assert.NotNull(@object: clear);
        Assert.Equal(expected: 0.004, actual: clear.Value.Point.Y, precision: 9);
    }

    private static SdfProgram Floor() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f);

        return builder.Build();
    }
}
