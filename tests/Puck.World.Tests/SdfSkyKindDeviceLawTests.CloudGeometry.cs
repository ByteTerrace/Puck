using System.Numerics;
using Puck.Maths;

namespace Puck.World.Tests;

public sealed partial class SdfSkyKindDeviceLawTests {
    private static void AddCloudGeometryCases(List<KindCase> cases) {
        foreach (var radius in new[] { 0f, 0.5f, 1f, 6f, 1e20f, float.MaxValue }) {
            foreach (var elevation in new[] { 0f, 1e-20f, 0.5f, 1f }) {
                var r = ((double)radius);
                var y = ((double)elevation);
                // Rationalized positive sphere intersection in double: no shader root/rsqrt implementation reused.
                var expected = ((float)(((2 * r) + 1) / (Math.Sqrt(d: (((((r * r) * y) * y) + (2 * r)) + 1)) + (r * y))));

                cases.Add(item: new($"cloud dome radius {radius} elevation {elevation}", default,
                    CloudParameters() with { DomeRadius = radius }, new Vector4(w: 0, x: 0, y: elevation, z: 0),
                    new Vector4(w: 0, x: 8, y: 0, z: 0), new Vector4(w: 1, x: expected, y: 0, z: 0), 0, RelativeTolerance: 0.000002f));
            }
        }
        var lightParameters = CloudParameters();
        var lightRgb = ((((lightParameters.Color * lightParameters.AmbientFloor) * new Vector3(x: 0.25f, y: 0.5f, z: 0.75f))
            + (((lightParameters.Color * (1 - lightParameters.AmbientFloor)) * new Vector3(x: 0.5f, y: 0.75f, z: 1))
                * (1 - (lightParameters.SelfShadow * 0.75f))))
            + ((new Vector3(x: 1, y: 0.5f, z: 0.25f) * lightParameters.SilverLining) * 0.75f));

        cases.Add(item: new("cloud almost-zenith negative projection is finite", default, lightParameters,
            new Vector4(value: Vector3.UnitY, w: 2), new Vector4(w: 0, x: 5, y: 1, z: 1), new Vector4(value: lightRgb, w: 0.5f), 32));
        cases.Add(item: new("cloud zero exponent at zero alignment is one", default, lightParameters with { SilverExponent = 0 },
            new Vector4(value: Vector3.UnitY, w: 2), new Vector4(w: 0, x: 5, y: 1, z: 2), new Vector4(value: lightRgb, w: 0.5f), 32));
        var parameters = CloudParameters() with {
            DomeRadius = 0,
            InverseScale = 5,
            InverseSoftness = 1,
            Coverage = 1,
        };
        var u = IntegerCloudNoise(octaves: 3, seed: parameters.Seed, x: 3, y: 0);
        var thickness = ((u * u) * (3 - (2 * u)));
        var alpha = ((float)(1 - Math.Exp(d: (-thickness * parameters.Extinction))));
        var rgb = ((parameters.Color * parameters.AmbientFloor) * new Vector3(x: 0.25f, y: 0.5f, z: 0.75f));

        cases.Add(item: new("cloud shape consumes reciprocal scale and softness", default, parameters,
            new Vector4(w: 0, x: 0.6f, y: 0.8f, z: 0), new Vector4(w: 0, x: 1, y: 0, z: 0), new Vector4(value: rgb, w: alpha), 24));
        var horizon = (0.8f * 0.5f);
        var fade = ((horizon * horizon) * (3 - (2 * horizon)));

        cases.Add(item: new("cloud shape consumes reciprocal horizon fade", default, parameters with { InverseHorizonFade = 0.5f },
            new Vector4(w: 0, x: 0.6f, y: 0.8f, z: 0), new Vector4(w: 0, x: 1, y: 0, z: 0), new Vector4(value: rgb, w: (alpha * fade)), 24));
        var normalParameters = parameters with { Offset = new(x: 3, y: 0), NormalTap = 2, InverseNormalTap = 0.5f };

        foreach (var quality in new[] { 0u, 2u }) {
            var octaves = ((quality == 0) ? 3u : 4u);

            double Thickness(uint x, uint y) {
                var value = IntegerCloudNoise(octaves: octaves, seed: normalParameters.Seed, x: x, y: y);

                return ((value * value) * (3 - (2 * value)));
            }
            var center = Thickness(x: 3, y: 0);
            var normal = ((quality == 0) ? Vector3.UnitY : Vector3.Normalize(value: new Vector3(
                x: ((float)((-(Thickness(x: 5, y: 0) - center) * normalParameters.InverseNormalTap) * normalParameters.Height)), y: 1,
                z: ((float)((-(Thickness(x: 3, y: 2) - center) * normalParameters.InverseNormalTap) * normalParameters.Height)))));
            var coverage = ((float)(1 - Math.Exp(d: (-center * normalParameters.Extinction))));

            cases.Add(item: new($"cloud finite-difference normal quality {quality}", default, normalParameters,
                new Vector4(value: Vector3.UnitY, w: quality), new Vector4(w: 0, x: 9, y: 0, z: 0), new Vector4(value: normal, w: coverage),
                ((quality == 0) ? 24u : 96u)));
        }
    }
    // At integer lattice points each octave is a single known corner; no shader interpolation is reproduced.
    private static double IntegerCloudNoise(uint x, uint y, uint octaves, uint seed) {
        var sum = 0d;
        var weight = 0.5d;
        var normalization = 0d;

        for (var octave = 0u; (octave < octaves); octave++) {
            var hash = Pcg3dLatticeNoise.Pcg3d(x: x & 4095, y: y & 4095, z: (seed + octave));

            sum += ((weight * hash.X) / 4294967296d);
            normalization += weight;
            weight *= 0.5;
            x = ((x * 2) + 17);
            y = ((y * 2) + 17);
        }
        return (sum / normalization);
    }
}
