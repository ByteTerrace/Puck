using System.Numerics;

using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

// The cache's bounded errors, each a number held against the reference: light-view visibility, continuation merging,
// and the irradiance a probe estimates around unresolved grazing rays; and energy conservation across levels, which
// is exact.
public sealed class IrradianceBoundLawTests(ITestOutputHelper output) {
    private static readonly Double3 Up = new(X: 0.0, Y: 1.0, Z: 0.0);

    [Fact]
    public void ALightViewNeverLightsAShadowedReceiverAndWidensShadowsByAtMostTwoTexels() {
        var field = LightViewScene();
        var sun = new Double3(X: 0.3, Y: 1.0, Z: 0.2).Normalize();
        var view = new IrradianceLightView(center: Double3.Zero, distance: 20.0, field: field, halfWidth: 4.0, resolution: 80, towardLight: sun);
        var texel = view.TexelSize;
        var widened = 0;
        var rodShadow = 0;
        var answered = 0;

        for (var x = -3.5; (x <= 3.5); x += 0.05) {
            for (var z = -3.5; (z <= 3.5); z += 0.05) {
                var point = new Double3(X: x, Y: 0.0, Z: z);
                var exact = Visible(field: field, point: point, sun: sun);
                var mapped = view.Lit(normal: Up, point: point);
                var lit = (mapped ?? exact);

                answered += (mapped.HasValue ? 1 : 0);

                // No false light: a receiver the reference shadows is shadowed.
                Assert.False(condition: (lit && !exact), userMessage: $"lit through a caster at ({x}, {z})");

                if (!exact && (Math.Abs(value: (z - (2.0 - ((0.8 * sun.Z) / sun.Y)))) < 0.05)) {
                    rodShadow++;
                }

                if (exact && !lit) {
                    // A widened shadow lies within two texels of the exact shadow, found analytically over the disc.
                    Assert.True(condition: NearShadow(point: point, radius: (2.0 * texel), sun: sun), userMessage: $"shadowed far from any edge at ({x}, {z})");
                    widened++;
                }
            }
        }

        Assert.True(condition: (rodShadow > 0));
        Assert.True(condition: (widened > 0));
        Assert.True(condition: (answered > 0));

        // Red leg: point rays (a zero sweep radius) slip between the subtexel rod and light its shadow.
        var pointRays = new IrradianceLightView(center: Double3.Zero, distance: 20.0, field: field, halfWidth: 4.0, resolution: 80, sweepRadius: 0.0, towardLight: sun);
        var missed = 0;

        for (var x = -1.0; (x <= 1.0); x += 0.05) {
            var point = new Double3(X: x, Y: 0.0, Z: (2.0 - ((0.8 * sun.Z) / sun.Y)));

            if (!Visible(field: field, point: point, sun: sun) && (pointRays.Lit(normal: Up, point: point) == true)) {
                missed++;
            }
        }

        Assert.True(condition: (missed > 0));
    }
    [Fact]
    public void EnergyIsConservedAcrossLevelsAndContinuation() {
        // A uniformly emissive diffuse shell (inner radius 6): the fine level fills the middle and continues into the
        // coarse level, whose probes fill the shell. Every reconstruction step is a normalized convex combination, so the
        // furnace's series holds exactly at every albedo, one included.
        IrradianceLevel[] levels = [
            new(Name: "near", Radius: 0.0, Reach: 1.5, Spacing: 0.5, Strata: 2),
            new(Name: "world", Radius: 0.0, Reach: 0.0, Spacing: 2.0, Strata: 2),
        ];

        foreach (var albedo in new[] { 0.0, 0.6, 1.0 }) {
            var model = new IrradianceCacheModel(
                field: IrradianceScenes.HallWithReceiver(inner: 6.0, receiver: 0.2, thickness: 0.2),
                levels: levels,
                options: new IrradianceModelOptions(ExitDistance: 100.0),
                surfaces: IrradianceScenes.Uniform(albedo: albedo, emission: 0.5, sky: 9.0)
            );

            model.Allocate(level: 0, max: new Double3(X: 1.0, Y: 1.0, Z: 1.0), min: new Double3(X: -1.0, Y: -1.0, Z: -1.0));
            model.Allocate(level: 1, max: new Double3(X: 7.0, Y: 7.0, Z: 7.0), min: new Double3(X: -7.0, Y: -7.0, Z: -7.0));
            model.Classify();
            model.Trace();
            model.Solve(bounces: 2);

            Assert.True(condition: (model.ContinuedRays > 0));
            Assert.Equal(expected: IrradianceScenes.Furnace(albedo: albedo, bounces: 2, emission: 0.5), actual: model.Irradiance(normal: Up, surface: new Double3(X: 0.0, Y: 0.2, Z: 0.0))!.Value.X, precision: 9);
        }
    }
    [Fact]
    public void EnergyIsConservedInABoxWhoseGrazingRaysGoUnresolved() {
        // A closed 24-unit box room, every surface emitting 0.5 with albedo 0.6: probes 0.05 above its floor send rays
        // along it that exhaust their march. Excluding those rays from a probe's mean keeps the furnace's series exact; reading them
        // as black would lose their share of the light.
        var model = new IrradianceCacheModel(
            field: IrradianceScenes.Room(center: new Double3(X: 0.0, Y: 3.0, Z: 0.0), interiorHalf: new Double3(X: 12.0, Y: 3.0, Z: 12.0), thickness: 0.1),
            levels: [new(Name: "near", Radius: 0.0, Reach: 30.0, Spacing: 0.5, Strata: 2), new(Name: "world", Radius: 0.0, Reach: 0.0, Spacing: 3.0, Strata: 2)],
            options: new IrradianceModelOptions(ExitDistance: 100.0),
            surfaces: IrradianceScenes.Uniform(albedo: 0.6, emission: 0.5, sky: 9.0)
        );

        model.Allocate(level: 0, max: new Double3(X: 1.0, Y: 1.0, Z: 1.0), min: new Double3(X: -1.0, Y: -0.5, Z: -1.0));
        model.Allocate(level: 1, max: new Double3(X: 13.0, Y: 7.0, Z: 13.0), min: new Double3(X: -13.0, Y: -1.0, Z: -13.0));
        model.Classify();
        model.Trace();
        model.Solve(bounces: 2);

        var expected = IrradianceScenes.Furnace(albedo: 0.6, bounces: 2, emission: 0.5);
        var withUnresolved = 0;

        Assert.True(condition: (model.UnresolvedRays > 0), userMessage: "no grazing ray went unresolved");
        Assert.Equal(expected: expected, actual: model.Irradiance(normal: Up, surface: new Double3(X: 0.5, Y: 0.2, Z: 0.4))!.Value.X, precision: 9);

        // Every probe above the floor reads the series about every sideways normal, the hemispheres its grazing rays lie in.
        for (var z = -2; (z <= 1); z++) {
            for (var x = -2; (x <= 1); x++) {
                var probe = new IrradianceProbeKey(Level: 0, X: x, Y: 0, Z: z);

                foreach (var normal in new Double3[] { new(X: 1.0, Y: 0.0, Z: 0.0), new(X: -1.0, Y: 0.0, Z: 0.0), new(X: 0.0, Y: 0.0, Z: 1.0), new(X: 0.0, Y: 0.0, Z: -1.0) }) {
                    if (model.ProbeIrradianceOf(key: probe, normal: normal) is not { } irradiance) {
                        continue;
                    }

                    withUnresolved += ((model.UnresolvedShareOf(key: probe, normal: normal) > 0.0) ? 1 : 0);
                    Assert.Equal(expected: expected, actual: irradiance.X, precision: 9);
                }
            }
        }

        Assert.True(condition: (withUnresolved > 0), userMessage: "no sideways hemisphere held an unresolved ray");
    }
    [InlineData(0.5, 1.5, 2.0, 8.0)]
    [InlineData(1.5, 9.0, 4.5, 20.0)]
    [Theory]
    public void ContinuationMergingStaysWithinItsBound(double fineSpacing, double fineReach, double coarseSpacing, double hall) {
        // A dark hall under an emissive plate; receivers on a small ball at its middle read the plate through fine rays
        // that continue into the coarse level, each reading the coarse corner's stored ray that best continues it. The
        // merged estimate stays within 0.03 of the reference against the plate's radiance of 1, at the test layout
        // (0.5 into 2) and at medium's (1.5 into 4.5). Red leg: the nearest ray by direction misses the bound.
        var (field, surfaces) = PlateHall(hall: hall);
        var levels = new IrradianceLevel[] {
            new(Name: "fine", Radius: 0.0, Reach: fineReach, Spacing: fineSpacing, Strata: 2),
            new(Name: "coarse", Radius: 0.0, Reach: 0.0, Spacing: coarseSpacing, Strata: 2),
        };
        var model = Solved(field: field, hall: hall, levels: levels, options: new IrradianceModelOptions(ExitDistance: (hall * 10.0)), surfaces: surfaces);
        var nearest = Solved(field: field, hall: hall, levels: levels, options: new IrradianceModelOptions(ExitDistance: (hall * 10.0), HitReprojection: false), surfaces: surfaces);
        var reference = new IrradianceReference(exitDistance: (hall * 10.0), field: field, surfaces: surfaces);
        var worst = 0.0;
        var worstNearest = 0.0;

        foreach (var (surface, normal) in BallReceivers()) {
            var expected = reference.Estimate(bounces: 0, normal: normal, paths: 4096, point: surface);

            Assert.Equal(expected: 0, actual: expected.Unresolved);
            worst = Math.Max(val1: worst, val2: Math.Abs(value: (model.Irradiance(normal: normal, surface: surface)!.Value.X - expected.Irradiance.X)));
            worstNearest = Math.Max(val1: worstNearest, val2: Math.Abs(value: (nearest.Irradiance(normal: normal, surface: surface)!.Value.X - expected.Irradiance.X)));
        }

        Assert.True(condition: (model.ContinuedRays > 0));
        Assert.True(condition: (worst <= 0.03), userMessage: $"merging error {worst}");
        Assert.True(condition: (worstNearest > 0.03), userMessage: $"nearest-ray error {worstNearest}");
    }
    [Fact]
    public void InterpolationAtTheRoomSpacingStaysWithinItsBound() {
        // The same plate over a small object, read through one 1.5-unit level whose rays trace to the far distance: the
        // error is the lattice's own parallax and quadrature, within 0.13 of the reference against the plate's 1.
        var (field, surfaces) = PlateHall(hall: 20.0);
        var model = Solved(
            field: field,
            hall: 20.0,
            levels: [new(Name: "room", Radius: 0.0, Reach: 0.0, Spacing: 1.5, Strata: 2)],
            options: new IrradianceModelOptions(ExitDistance: 200.0),
            surfaces: surfaces
        );
        var reference = new IrradianceReference(exitDistance: 200.0, field: field, surfaces: surfaces);
        var worst = 0.0;

        foreach (var (surface, normal) in BallReceivers()) {
            worst = Math.Max(val1: worst, val2: Math.Abs(value: (model.Irradiance(normal: normal, surface: surface)!.Value.X - reference.Estimate(bounces: 0, normal: normal, paths: 4096, point: surface).Irradiance.X)));
        }

        Assert.True(condition: (worst <= 0.13), userMessage: $"interpolation error {worst}");
    }
    [Fact]
    public void AProbeEstimatesAroundItsUnresolvedRaysWithinTheirShare() {
        // A probe 0.15 above an open floor under a sky of 1: its rays grazing the floor exhaust their march. About a
        // sideways normal it excludes them, so its irradiance differs from the reference by at most their cosine share
        // (times the radiance range, one) plus the 128-ray quadrature's 0.03.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f);

        var field = new IrradianceField(program: builder.Build());
        var surfaces = IrradianceScenes.Uniform(albedo: 0.0, emission: 0.0, sky: 1.0);
        var model = new IrradianceCacheModel(
            field: field,
            levels: [new(Name: "room", Radius: 0.0, Reach: 0.0, Spacing: 1.5, Strata: 2)],
            options: new IrradianceModelOptions(ExitDistance: 200.0),
            surfaces: surfaces
        );

        model.Allocate(level: 0, max: new Double3(X: 0.1, Y: 0.1, Z: 0.1), min: new Double3(X: -0.1, Y: -0.1, Z: -0.1));
        model.Classify();
        model.Trace();
        model.Solve(bounces: 0);

        var probe = new IrradianceProbeKey(Level: 0, X: 0, Y: 0, Z: 0);

        Assert.True(condition: model.TryGetProbe(key: probe, placement: out var placement));

        // The sideways normal whose hemisphere holds the most unresolved rays.
        Double3[] candidates = [new(X: 1.0, Y: 0.0, Z: 0.0), new(X: -1.0, Y: 0.0, Z: 0.0), new(X: 0.0, Y: 0.0, Z: 1.0), new(X: 0.0, Y: 0.0, Z: -1.0)];
        var sideways = candidates.MaxBy(keySelector: normal => model.UnresolvedShareOf(key: probe, normal: normal));
        var share = model.UnresolvedShareOf(key: probe, normal: sideways);
        var estimate = model.ProbeIrradianceOf(key: probe, normal: sideways)!.Value.X;
        // This Halton set resolves every path. A larger set includes grazing
        // paths the evaluator cannot resolve, which makes its black-biased estimate invalid as a reference answer.
        var reference = new IrradianceReference(exitDistance: 200.0, field: field, surfaces: surfaces).Estimate(bounces: 0, normal: sideways, paths: 64, point: placement.Position);
        var expected = reference.Irradiance.X;

        Assert.Equal(expected: 0, actual: reference.Unresolved);

        output.WriteLine(message: $"grazing share {share}, estimate {estimate}, reference {expected}");
        Assert.True(condition: (model.UnresolvedRays > 0));
        Assert.True(condition: (share > 0.0));
        Assert.True(condition: (share <= 0.1), userMessage: $"unresolved share {share}");
        Assert.True(condition: (Math.Abs(value: (estimate - expected)) <= (share + 0.03)), userMessage: $"{estimate} against {expected}, share {share}");
        Assert.True(condition: (Math.Abs(value: (estimate - 0.5)) <= (share + 0.03)), userMessage: $"{estimate} against the analytic floor answer, share {share}");
    }

    // Whether any floor point within a disc of the radius lies in the casters' exact shadow, by ray-box slab tests
    // against the scene's two casters on a 0.01 grid.
    private static bool NearShadow(Double3 point, double radius, Double3 sun) {
        for (var dx = -radius; (dx <= radius); dx += 0.01) {
            for (var dz = -radius; (dz <= radius); dz += 0.01) {
                if (((dx * dx) + (dz * dz)) > (radius * radius)) {
                    continue;
                }

                var sample = (point + new Double3(X: dx, Y: 0.0, Z: dz));

                if (Crosses(center: new Double3(X: -1.0, Y: 1.2, Z: -1.0), half: new Double3(X: 0.6, Y: 0.05, Z: 0.6), origin: sample, direction: sun) ||
                    Crosses(center: new Double3(X: 0.0, Y: 0.8, Z: 2.0), half: new Double3(X: 1.5, Y: 0.01, Z: 0.01), origin: sample, direction: sun)) {
                    return true;
                }
            }
        }

        return false;
    }
    private static bool Crosses(Double3 center, Double3 half, Double3 origin, Double3 direction) {
        var near = 0.0;
        var far = double.MaxValue;
        double[] o = [(origin.X - center.X), (origin.Y - center.Y), (origin.Z - center.Z)];
        double[] d = [direction.X, direction.Y, direction.Z];
        double[] h = [half.X, half.Y, half.Z];

        for (var axis = 0; (axis < 3); axis++) {
            if (Math.Abs(value: d[axis]) < 1.0e-12) {
                if (Math.Abs(value: o[axis]) > h[axis]) {
                    return false;
                }

                continue;
            }

            var a = ((-h[axis] - o[axis]) / d[axis]);
            var b = ((h[axis] - o[axis]) / d[axis]);

            near = Math.Max(val1: near, val2: Math.Min(val1: a, val2: b));
            far = Math.Min(val1: far, val2: Math.Max(val1: a, val2: b));
        }

        return (near <= far);
    }
    // A dark hall of the given inner radius under an emissive plate (material 1), with a small dark ball at its middle.
    private static (IrradianceField Field, IrradianceSurfaces Surfaces) PlateHall(double hall) {
        var builder = new SdfProgramBuilder();
        var wall = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var plate = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var scale = ((float)(hall / 8.0));

        _ = builder.Sphere(material: wall, radius: ((float)(hall + 0.3)));
        _ = builder.Sphere(blend: SdfBlendOp.Subtraction, material: wall, radius: ((float)hall));
        _ = builder.ResetPoint();
        _ = builder.Sphere(material: wall, radius: 0.2f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: (new Vector3(x: 1.5f, y: 4.5f, z: -1f) * scale));
        _ = builder.Box(halfExtents: new Vector3(x: (2f * scale), y: 0.05f, z: (2f * scale)), material: plate, round: 0f);

        return (new IrradianceField(program: builder.Build()), new IrradianceSurfaces(
            albedo: static _ => Double3.Zero,
            emission: static material => ((material == 1) ? new Double3(X: 1.0, Y: 1.0, Z: 1.0) : Double3.Zero)
        ));
    }
    private static IrradianceCacheModel Solved(IrradianceField field, IrradianceSurfaces surfaces, IrradianceLevel[] levels, IrradianceModelOptions options, double hall) {
        var model = new IrradianceCacheModel(field: field, levels: levels, options: options, surfaces: surfaces);
        var near = (2.0 * levels[0].Spacing);

        model.Allocate(level: 0, max: new Double3(X: near, Y: near, Z: near), min: new Double3(X: -near, Y: -near, Z: -near));

        if (levels.Length > 1) {
            model.Allocate(level: 1, max: new Double3(X: (hall + 1.0), Y: (hall + 1.0), Z: (hall + 1.0)), min: new Double3(X: -(hall + 1.0), Y: -(hall + 1.0), Z: -(hall + 1.0)));
        }

        model.Classify();
        model.Trace();
        model.Solve(bounces: 0);

        return model;
    }
    private static IEnumerable<(Double3 Surface, Double3 Normal)> BallReceivers() {
        foreach (var normal in new[] { Up, new Double3(X: 1.0, Y: 0.0, Z: 0.0), new Double3(X: 0.0, Y: 0.6, Z: -0.8), new Double3(X: -0.7, Y: 0.7, Z: 0.0) }) {
            var unit = normal.Normalize();

            yield return ((unit * 0.2), unit);
        }
    }
    private static bool Visible(IrradianceField field, Double3 point, Double3 sun) =>
        field.SegmentClear(from: (point + (Up * 0.004)), to: (point + (sun * 10.0)));
    // A floor slab whose top is y = 0, a box caster at height 1.2, and a rod 0.02 thick, far thinner than a 0.1 texel,
    // across the X axis at height 0.8 and z = 2.
    private static IrradianceField LightViewScene() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: new Vector3(x: 0f, y: -0.1f, z: 0f));
        _ = builder.Box(halfExtents: new Vector3(x: 6f, y: 0.1f, z: 6f), material: material, round: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: -1f, y: 1.2f, z: -1f));
        _ = builder.Box(halfExtents: new Vector3(x: 0.6f, y: 0.05f, z: 0.6f), material: material, round: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 0f, y: 0.8f, z: 2f));
        _ = builder.Box(halfExtents: new Vector3(x: 1.5f, y: 0.01f, z: 0.01f), material: material, round: 0f);

        return new IrradianceField(program: builder.Build());
    }
}
