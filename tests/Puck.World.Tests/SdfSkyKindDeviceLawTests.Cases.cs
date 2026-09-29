using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Maths;
using Puck.Shaders;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfSkyKindDeviceLawTests {
    [Fact]
    public void NativeKindRecordsHaveAlignedDeclaredStrides() {
        Assert.Equal(16, Unsafe.SizeOf<SdfSkyGradientData>());
        Assert.Equal(16u, ShaderInterfaceStructure.From<SdfSkyGradientData>().SizeBytes);
        Assert.Equal(64u, ShaderInterfaceStructure.From<SdfSkyNoiseData>().SizeBytes);
        Assert.Equal(48u, ShaderInterfaceStructure.From<SdfSkyPatternData>().SizeBytes);
        Assert.Equal(64u, ShaderInterfaceStructure.From<SdfSkyAuroraData>().SizeBytes);
        Assert.Equal(32u, ShaderInterfaceStructure.From<SdfSkyPanoramaData>().SizeBytes);
        Assert.Equal(160, Unsafe.SizeOf<SdfSkyStarsData>());
        Assert.Equal(96, Unsafe.SizeOf<SdfSkyCloudsData>());
        Assert.Equal(160u, ShaderInterfaceStructure.From<SdfSkyStarsData>().SizeBytes);
        Assert.Equal(96u, ShaderInterfaceStructure.From<SdfSkyCloudsData>().SizeBytes);
    }

    private static KindCase[] Cases() {
        var cases = new List<KindCase>();
        var stars = StarParameters();
        var tint = new Vector3(x: 0.25f, y: 0.5f, z: 0.75f);

        void Star(string name, SdfSkyStarsData value, uint quality, Vector4 expected, uint hashes) =>
            cases.Add(item: new(name, value, default, new Vector4(value: Vector3.UnitZ, w: quality), Vector4.Zero, expected, hashes));
        Star("star straight emission and coverage", stars, 2, new Vector4(value: (tint * 2), w: 1), 2);
        Star("zero star radius is transparent", stars with { AngularRadius = 0 }, 2, Vector4.Zero, 2);
        Star("extinguished zero-floor star is transparent", stars with { LuminosityFloor = 0, RadiusFloor = 0 }, 2, Vector4.Zero, 2);
        Star("tiny positive normal star keeps centered coverage", stars with { AngularRadius = 1e-30f }, 2, new Vector4(value: (tint * 2), w: 1), 2);
        Star("zero star brightness does no hashes", stars with { Brightness = 0 }, 2, Vector4.Zero, 0);
        Star("zero star sparsity does no hashes", stars with { Sparsity = 0 }, 2, Vector4.Zero, 0);
        Star("empty star cell costs one hash", stars with { Sparsity = 0.000001f }, 2, Vector4.Zero, 1);
        foreach (var seed in new[] { 0x01000000u, 0x01000001u }) {
            foreach (var phase in new[] { 0f, 0.25f, 1f }) {
                var twinkle = stars with { Seed = seed, TwinkleShare = 1, TwinkleDepth = 0.5f, TwinklePhase = phase };

                Star($"low stars ignore phase {phase} and exact seed {seed}", twinkle, 0, new Vector4(value: (tint * 2), w: 1), 2);
                Star($"high twinkle phase {phase} exact seed {seed}", twinkle, 2,
                    new Vector4(value: (tint * (2 * Twinkle(phase: phase, seed: seed))), w: 1), 3);
            }
        }
        var spectrumColors = new[] {
            Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.One,
            new Vector3(x: 0.2f, y: 0.6f, z: 0.4f), new Vector3(x: 0.7f, y: 0.1f, z: 0.3f), new Vector3(x: 0.4f, y: 0.2f, z: 0.8f),
        };
        var spectrum = stars with {
            Spectrum0 = spectrumColors[0],
            Spectrum1 = spectrumColors[1],
            Spectrum2 = spectrumColors[2],
            Spectrum3 = spectrumColors[3],
            Spectrum4 = spectrumColors[4],
            Spectrum5 = spectrumColors[5],
            Spectrum6 = spectrumColors[6],
        };
        var covered = new bool[6];

        for (var seed = 0u; (seed < 256); seed++) {
            var first = Pcg3dLatticeNoise.Pcg3d(x: 0, y: 0, z: seed);
            var second = Pcg3dLatticeNoise.Pcg3d(x: first.X, y: first.Y, z: first.Z);
            var position = ((second.Y / 4294967296d) * 6);
            var interval = Math.Min(val1: ((int)position), val2: 5);

            if (covered[interval]) { continue; }
            covered[interval] = true;
            var color = Vector3.Lerp(spectrumColors[interval], spectrumColors[(interval + 1)], ((float)(position - interval)));

            Star($"native spectrum interval {interval}", spectrum with { Seed = seed }, 2,
                new Vector4(value: (color * stars.Brightness), w: 1), 2);
        }
        Assert.All(covered, value => Assert.True(condition: value));
        var clouds = CloudParameters();
        var diffuse = new Vector3(x: 0.5f, y: 0.75f, z: 1f);
        var ambient = new Vector3(x: 0.25f, y: 0.5f, z: 0.75f);

        foreach (var quality in new[] { 0u, 1u, 2u }) {
            foreach (var lights in new[] { 0u, 1u, 2u }) {
                var color = (clouds.Color * ((clouds.AmbientFloor * ambient) + (((1 - clouds.AmbientFloor) * lights) * diffuse)));

                cases.Add(item: new($"cloud quality {quality} lights {lights}", default, clouds,
                    new Vector4(value: Vector3.UnitY, w: quality), new Vector4(w: 0, x: 1, y: lights, z: 0),
                    new Vector4(value: color, w: (1 - MathF.Exp(x: -clouds.Extinction))), ((quality == 0) ? 24u : (96u + (32u * lights)))));
            }
        }
        cases.Add(item: new("zero cloud coverage does no hashes", default, clouds with { Coverage = 0 },
            new Vector4(value: Vector3.UnitY, w: 2), new Vector4(w: 0, x: 1, y: 2, z: 0), Vector4.Zero, 0));
        cases.Add(item: new("below cloud horizon does no hashes", default, clouds,
            new Vector4(value: -Vector3.UnitY, w: 2), new Vector4(w: 0, x: 1, y: 2, z: 0), Vector4.Zero, 0));
        void Gradient(string name, uint first, uint count, Vector3 direction, Vector4 expected) =>
            cases.Add(item: new(name, default, default, new Vector4(value: direction, w: 2), new Vector4(w: 0, x: 2, y: 0, z: 0), expected, 0,
                new SdfSkyGradientData { FirstStop = first, StopCount = count }));
        Gradient("gradient starts at native offset", 1, 2, -Vector3.UnitY, new Vector4(w: 1, x: 1, y: 0, z: 0));
        Gradient("gradient interpolates direction Y", 1, 2, Vector3.UnitX, new Vector4(w: 1, x: 0.5f, y: 0, z: 0.5f));
        Gradient("gradient clamps to last stop", 1, 2, Vector3.UnitY, new Vector4(w: 1, x: 0, y: 0, z: 1));
        Gradient("gradient keeps first equal boundary", 1, 3, Vector3.UnitY, new Vector4(w: 1, x: 0, y: 0, z: 1));
        Gradient("single gradient stop", 3, 1, Vector3.UnitX, new Vector4(w: 1, x: 0, y: 1, z: 0));
        Gradient("empty gradient reads no table", uint.MaxValue, 0, Vector3.UnitY, Vector4.Zero);
        Gradient("small positive gradient gap is not flattened", 4, 2, new Vector3(x: 1, y: 0.0000005f, z: 0), new Vector4(w: 1, x: 0.5f, y: 0, z: 0.5f));
        foreach (var quality in new[] { 0u, 2u }) {
            foreach (var lights in new[] { 1u, 2u }) {
                var shadow = ((quality == 0) ? 1f : (1 - (clouds.SelfShadow * 0.75f)));
                var lining = ((quality == 0) ? 0f : (clouds.SilverLining * 0.75f));
                var lightColor = ((((clouds.Color * (1 - clouds.AmbientFloor)) * diffuse) * shadow)
                    + (new Vector3(x: 1, y: 0.5f, z: 0.25f) * lining));
                var color = (((clouds.Color * clouds.AmbientFloor) * ambient) + (lights * lightColor));

                cases.Add(item: new($"nonflat cloud quality {quality} lights {lights}", default, clouds,
                    new Vector4(value: Vector3.UnitY, w: quality), new Vector4(w: 0, x: 5, y: lights, z: 0), new Vector4(value: color, w: 0.5f),
                    ((quality == 0) ? 0u : (32u * lights))));
            }
        }
        AddCloudGeometryCases(cases: cases);
        AddNoiseAndPatternCases(cases: cases);
        AddAuroraAndPanoramaCases(cases: cases);
        return [.. cases];
    }
    // At the exact center of a density-one cell with Inset=.5, coverage is one. A unit luminosity floor fixes
    // the untwinkled emission, leaving this independent integer hash plus analytic periodic modulation oracle.
    private static float Twinkle(uint seed, float phase) {
        var first = Pcg3dLatticeNoise.Pcg3d(x: 0, y: 0, z: seed);
        var second = Pcg3dLatticeNoise.Pcg3d(x: first.X, y: first.Y, z: first.Z);
        var third = Pcg3dLatticeNoise.Pcg3d(x: second.X, y: second.Y, z: second.Z);
        var offset = (third.Z / 4294967296d);
        var a = (1 + (third.X % 3));
        var b = (2 + (third.Y % 3));
        var flicker = (0.5 + ((0.5 * Math.Sin(a: ((2 * Math.PI) * ((a * phase) + offset)))) *
            Math.Sin(a: ((2 * Math.PI) * ((b * phase) + (offset * 1.7))))));

        return ((float)(1 - (0.5 * flicker)));
    }
    private static SdfSkyStopData[] GradientStops() => [
        new() { Color = new(x: 0, y: 1, z: 0), Elevation = -1 },
        new() { Color = new(x: 1, y: 0, z: 0), Elevation = -1 },
        new() { Color = new(x: 0, y: 0, z: 1), Elevation = 1 },
        new() { Color = new(x: 0, y: 1, z: 0), Elevation = 1 },
        new() { Color = new(x: 1, y: 0, z: 0), Elevation = 0 },
        new() { Color = new(x: 0, y: 0, z: 1), Elevation = 0.000001f },
    ];
    private static SdfSkyStarsData StarParameters() => new() {
        Density = 1,
        Brightness = 2,
        Seed = 0,
        Sparsity = 1,
        Inset = 0.5f,
        AngularRadius = (0.12f * MathF.PI),
        LuminosityFloor = 1,
        RadiusFloor = 1,
        Spectrum0 = new(x: 0.25f, y: 0.5f, z: 0.75f),
        Spectrum1 = new(x: 0.25f, y: 0.5f, z: 0.75f),
        Spectrum2 = new(x: 0.25f, y: 0.5f, z: 0.75f),
        Spectrum3 = new(x: 0.25f, y: 0.5f, z: 0.75f),
        Spectrum4 = new(x: 0.25f, y: 0.5f, z: 0.75f),
        Spectrum5 = new(x: 0.25f, y: 0.5f, z: 0.75f),
        Spectrum6 = new(x: 0.25f, y: 0.5f, z: 0.75f),
    };
    // The positive periodic noise saturates this narrow threshold at every sampled point. Thickness is exactly one,
    // so all normals are up, shadow is one and lining is zero; the expected color and alpha are analytic.
    private static SdfSkyCloudsData CloudParameters() => new() {
        Color = new(x: 0.5f, y: 0.75f, z: 1),
        Coverage = 1,
        InverseSoftness = 1000000,
        InverseScale = 0.5f,
        Seed = 0,
        DomeRadius = 6,
        Warp = 0,
        Height = 0.7f,
        NormalTap = 0.18f,
        InverseNormalTap = (1f / 0.18f),
        SelfShadow = 0.6f,
        SilverLining = 0.5f,
        Extinction = 3.5f,
        InverseHorizonFade = 20,
        AmbientFloor = 0.45f,
        SilverExponent = 8,
    };
}
