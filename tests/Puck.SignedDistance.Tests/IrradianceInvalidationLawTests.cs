using System.Numerics;
using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class IrradianceInvalidationLawTests {
    private const double ExitDistance = 8;

    private static readonly Double3 Up = new(X: 0, Y: 1, Z: 0);
    private static readonly IrradianceLevel Level = new("room", Radius: 0, Reach: 0, Spacing: 1, Strata: 1);
    private static readonly IrradianceProbeKey Probe = new(Level: 0, X: 1, Y: 1, Z: 1);

    [Fact]
    public void APointLightBesideAStoredHitRelightsItWithoutRetracingItsDistantProbe() {
        var program = Floor();
        var field = new IrradianceField(program: program);
        var light = new Double3(X: 100, Y: 100, Z: 100);
        var surfaces = new IrradianceSurfaces(albedo: static _ => new Double3(X: 1, Y: 1, Z: 1),
            emission: static _ => default, direct: (point, normal, _) => PointLight(light: light, normal: normal, point: point));
        var warm = Model(field: field, surfaces: surfaces);
        var ray = warm.NearestRayOf(direction: -Up, key: Probe);
        var hit = warm.HitsOf(key: Probe)[ray];

        Assert.Equal(IrradianceHitKind.Hit, hit.Kind);
        warm.Solve(bounces: 0);
        var dark = warm.RadianceOf(key: Probe, ray: ray)!.Value;
        var stored = warm.HitsOf(key: Probe).ToArray();
        var casts = field.Casts;

        light = (hit.Point + (hit.Normal * .05));
        // The point-light radius is a falloff scale, not a hard cutoff. The probe lies outside its bright core.
        Assert.True(condition: (PointLight(IrradianceLattice.Position(key: Probe, level: Level), hit.Normal, light).X < .01));
        warm.Solve(bounces: 0);
        var lit = warm.RadianceOf(key: Probe, ray: ray)!.Value;

        Assert.True(condition: ((lit.X > .5) && (lit.X > dark.X)));
        Assert.Equal(casts, field.Casts);
        Assert.Equal(stored, warm.HitsOf(key: Probe));
        var cold = Model(field: new IrradianceField(program: program), surfaces: surfaces);

        cold.Solve(bounces: 0);
        EqualPublished(cold: cold, warm: warm);
    }
    [Fact]
    public void AShadowCasterBeyondEveryStoredRayChangesLightingAndMatchesAColdSolve() {
        var before = Floor(caster: new Vector3(x: 100, y: 32, z: 100));
        var shadowField = new IrradianceField(program: before);
        var surfaces = new IrradianceSurfaces(albedo: static _ => new Double3(X: 1, Y: 1, Z: 1),
            emission: static _ => default, direct: (point, normal, _) =>
                (shadowField.SegmentClear(from: (point + (normal * .004)), to: (point + (Up * 64)))
                    ? new Double3(X: 1, Y: 1, Z: 1) : default));
        var transportField = new IrradianceField(program: before);
        var warm = Model(field: transportField, surfaces: surfaces);
        var ray = warm.NearestRayOf(direction: -Up, key: Probe);
        var hit = warm.HitsOf(key: Probe)[ray];

        Assert.Equal(IrradianceHitKind.Hit, hit.Kind);
        warm.Solve(bounces: 0);
        Assert.Equal(new Double3(X: 1, Y: 1, Z: 1), warm.RadianceOf(key: Probe, ray: ray));
        var stored = Keys().ToDictionary(key => key, key => warm.HitsOf(key: key).ToArray());
        var casts = transportField.Casts;
        var center = (hit.Point + (Up * 32));

        foreach (var key in Keys()) {
            var placement = IrradianceCells.Place(field: shadowField, lattice: IrradianceLattice.Position(key: key, level: Level), spacing: Level.Spacing);

            Assert.True(condition: (((center - placement.Position).Length - 1) > ExitDistance));
        }

        var after = Floor(caster: new Vector3(x: ((float)center.X), y: ((float)center.Y), z: ((float)center.Z)));

        shadowField = new IrradianceField(program: after);
        Assert.False(condition: shadowField.SegmentClear(from: (hit.Point + (hit.Normal * .004)), to: (hit.Point + (Up * 64))));
        warm.Solve(bounces: 0);
        Assert.Equal(casts, transportField.Casts);
        Assert.Equal(Double3.Zero, warm.RadianceOf(key: Probe, ray: ray));
        var cold = Model(field: shadowField, surfaces: surfaces);

        cold.Solve(bounces: 0);
        foreach (var key in Keys()) {
            Assert.Equal(stored[key], warm.HitsOf(key: key));
            Assert.Equal(stored[key], cold.HitsOf(key: key));
        }
        EqualPublished(cold: cold, warm: warm);
    }

    private static Double3 PointLight(Double3 point, Double3 normal, Double3 light) {
        var delta = (light - point);
        var distance = delta.Length;
        var intensity = (Math.Max(val1: 0, val2: Double3.Dot(a: normal, b: delta.Normalize())) / (1 + Math.Pow(x: (distance / .1), y: 2)));

        return new Double3(X: intensity, Y: intensity, Z: intensity);
    }
    private static SdfProgram Floor(Vector3? caster = null) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.Plane(Vector3.UnitY, 1f, material);
        if (caster is { } center) { builder.ResetPoint().Translate(offset: center).Sphere(1f, material); }
        return builder.Build(buildInstanceGrid: false);
    }
    private static IrradianceCacheModel Model(IrradianceField field, IrradianceSurfaces surfaces) {
        var model = new IrradianceCacheModel(field, surfaces, [Level], new IrradianceModelOptions(ExitDistance));

        model.Allocate(0, Double3.Zero, Double3.Zero);
        model.Classify();
        model.Trace();
        return model;
    }
    private static IEnumerable<IrradianceProbeKey> Keys() => IrradianceLattice.ProbesOf(brick: new IrradianceBrickKey(Level: 0, X: 0, Y: 0, Z: 0));
    private static void EqualPublished(IrradianceCacheModel warm, IrradianceCacheModel cold) {
        foreach (var key in Keys()) {
            Assert.Equal(warm.HitsOf(key: key).Count, cold.HitsOf(key: key).Count);
            for (var ray = 0; (ray < warm.HitsOf(key: key).Count); ray++) {
                Assert.Equal(cold.RadianceOf(key: key, ray: ray), warm.RadianceOf(key: key, ray: ray));
            }
        }
    }
}
