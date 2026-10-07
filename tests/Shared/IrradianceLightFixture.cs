using System.Numerics;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.Testing;

// The G1/G3 visibility fixture: a floor, a raised box and a rod narrower than one light-map texel.
internal static class IrradianceLightFixture {
    public static readonly Double3 Sun = new Double3(X: 0.3, Y: 1.0, Z: 0.2).Normalize();
    public static readonly Double3 Up = new(X: 0, Y: 1, Z: 0);

    public static SdfProgram Program() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: new Vector3(x: 0, y: -0.1f, z: 0));
        _ = builder.Box(halfExtents: new Vector3(x: 6, y: 0.1f, z: 6), material: material, round: 0);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: -1, y: 1.2f, z: -1));
        _ = builder.Box(halfExtents: new Vector3(x: 0.6f, y: 0.05f, z: 0.6f), material: material, round: 0);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 0, y: 0.8f, z: 2));
        _ = builder.Box(halfExtents: new Vector3(x: 1.5f, y: 0.01f, z: 0.01f), material: material, round: 0);
        return builder.Build();
    }
    public static IrradianceLightProjection Projection(int resolution) => IrradianceLightProjection.Create(
        receiverMin: new Double3(X: -4, Y: 0, Z: -4), receiverMax: new Double3(X: 4, Y: 0, Z: 4),
        casterMin: new Double3(X: -6, Y: -0.2, Z: -6), casterMax: new Double3(X: 6, Y: 1.25, Z: 6),
        towardLight: Sun, penumbraSlope: 0.1, resolution: resolution);
    // Includes exact rod-shadow points as well as both sides of the box silhouette and openly lit floor.
    public static Double3[] Receivers() => [
        .. Enumerable.Range(count: 41, start: 0).Select(selector: index => new Double3(
            X: (-1.0 + (index * 0.05)), Y: 0, Z: (2.0 - ((0.8 * Sun.Z) / Sun.Y)))),
        .. Enumerable.Range(count: 17, start: 0).SelectMany(selector: x => Enumerable.Range(count: 17, start: 0)
            .Select(selector: z => new Double3(X: (-3.2 + (x * 0.4)), Y: 0, Z: (-3.2 + (z * 0.4))))),
    ];
    // Whether any floor point within a disc of the radius lies in the casters' exact shadow, by ray-box slab tests
    // against the scene's two casters on a 0.01 grid.
    public static bool NearShadow(Double3 point, double radius, Double3 sun) {
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
}
