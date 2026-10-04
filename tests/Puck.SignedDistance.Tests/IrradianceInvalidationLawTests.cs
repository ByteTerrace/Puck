using System.Numerics;
using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class IrradianceInvalidationLawTests {
    private const double ExitDistance = 8;
    private static readonly Double3 Up = new(0, 1, 0);
    private static readonly IrradianceLevel Level = new("room", Radius: 0, Reach: 0, Spacing: 1, Strata: 1);
    private static readonly IrradianceProbeKey Probe = new(0, 1, 1, 1);

    [Fact]
    public void APointLightBesideAStoredHitRelightsItWithoutRetracingItsDistantProbe() {
        var program = Floor();
        var field = new IrradianceField(program);
        var light = new Double3(100, 100, 100);
        var surfaces = new IrradianceSurfaces(albedo: static _ => new Double3(1, 1, 1),
            emission: static _ => default, direct: (point, normal, _) => PointLight(point, normal, light));
        var warm = Model(field, surfaces);
        var ray = warm.NearestRayOf(Probe, -Up);
        var hit = warm.HitsOf(Probe)[ray];
        Assert.Equal(IrradianceHitKind.Hit, hit.Kind);
        warm.Solve(bounces: 0);
        var dark = warm.RadianceOf(Probe, ray)!.Value;
        var stored = warm.HitsOf(Probe).ToArray();
        var casts = field.Casts;

        light = hit.Point + hit.Normal * .05;
        // The point-light radius is a falloff scale, not a hard cutoff. The probe lies outside its bright core.
        Assert.True(PointLight(IrradianceLattice.Position(Probe, Level), hit.Normal, light).X < .01);
        warm.Solve(bounces: 0);
        var lit = warm.RadianceOf(Probe, ray)!.Value;
        Assert.True(lit.X > .5 && lit.X > dark.X);
        Assert.Equal(casts, field.Casts);
        Assert.Equal(stored, warm.HitsOf(Probe));
        var cold = Model(new IrradianceField(program), surfaces);
        cold.Solve(bounces: 0);
        EqualPublished(warm, cold);
    }

    [Fact]
    public void AShadowCasterBeyondEveryStoredRayChangesLightingAndMatchesAColdSolve() {
        var before = Floor(new Vector3(100, 32, 100));
        var shadowField = new IrradianceField(before);
        var surfaces = new IrradianceSurfaces(albedo: static _ => new Double3(1, 1, 1),
            emission: static _ => default, direct: (point, normal, _) =>
                shadowField.SegmentClear(point + normal * .004, point + Up * 64)
                    ? new Double3(1, 1, 1) : default);
        var transportField = new IrradianceField(before);
        var warm = Model(transportField, surfaces);
        var ray = warm.NearestRayOf(Probe, -Up);
        var hit = warm.HitsOf(Probe)[ray];
        Assert.Equal(IrradianceHitKind.Hit, hit.Kind);
        warm.Solve(bounces: 0);
        Assert.Equal(new Double3(1, 1, 1), warm.RadianceOf(Probe, ray));
        var stored = Keys().ToDictionary(key => key, key => warm.HitsOf(key).ToArray());
        var casts = transportField.Casts;
        var center = hit.Point + Up * 32;
        foreach (var key in Keys()) {
            var placement = IrradianceCells.Place(shadowField, IrradianceLattice.Position(key, Level), Level.Spacing);
            Assert.True((center - placement.Position).Length - 1 > ExitDistance);
        }

        var after = Floor(new Vector3((float)center.X, (float)center.Y, (float)center.Z));
        shadowField = new IrradianceField(after);
        Assert.False(shadowField.SegmentClear(hit.Point + hit.Normal * .004, hit.Point + Up * 64));
        warm.Solve(bounces: 0);
        Assert.Equal(casts, transportField.Casts);
        Assert.Equal(Double3.Zero, warm.RadianceOf(Probe, ray));
        var cold = Model(shadowField, surfaces);
        cold.Solve(bounces: 0);
        foreach (var key in Keys()) {
            Assert.Equal(stored[key], warm.HitsOf(key));
            Assert.Equal(stored[key], cold.HitsOf(key));
        }
        EqualPublished(warm, cold);
    }

    private static Double3 PointLight(Double3 point, Double3 normal, Double3 light) {
        var delta = light - point;
        var distance = delta.Length;
        var intensity = Math.Max(0, Double3.Dot(normal, delta.Normalize())) / (1 + Math.Pow(distance / .1, 2));
        return new Double3(intensity, intensity, intensity);
    }

    private static SdfProgram Floor(Vector3? caster = null) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.Plane(Vector3.UnitY, 1f, material);
        if (caster is { } center) { builder.ResetPoint().Translate(center).Sphere(1f, material); }
        return builder.Build(buildInstanceGrid: false);
    }

    private static IrradianceCacheModel Model(IrradianceField field, IrradianceSurfaces surfaces) {
        var model = new IrradianceCacheModel(field, surfaces, [Level], new IrradianceModelOptions(ExitDistance));
        model.Allocate(0, Double3.Zero, Double3.Zero);
        model.Classify();
        model.Trace();
        return model;
    }

    private static IEnumerable<IrradianceProbeKey> Keys() => IrradianceLattice.ProbesOf(new IrradianceBrickKey(0, 0, 0, 0));

    private static void EqualPublished(IrradianceCacheModel warm, IrradianceCacheModel cold) {
        foreach (var key in Keys()) {
            Assert.Equal(warm.HitsOf(key).Count, cold.HitsOf(key).Count);
            for (var ray = 0; ray < warm.HitsOf(key).Count; ray++) {
                Assert.Equal(cold.RadianceOf(key, ray), warm.RadianceOf(key, ray));
            }
        }
    }
}
