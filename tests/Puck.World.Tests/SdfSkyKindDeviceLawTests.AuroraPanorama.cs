using System.Numerics;
using Puck.Shaders;

namespace Puck.World.Tests;

public sealed partial class SdfSkyKindDeviceLawTests {
    private static void AddAuroraAndPanoramaCases(List<KindCase> cases) {
        var aurora = new SdfSkyAuroraData {
            Color = new(x: 0.25f, y: 0.5f, z: 0.75f),
            Intensity = 2,
            InverseScale = 1,
            InverseWidth = (1f / 1.5f),
            Sharpness = 2,
            HeightScale = 0.15f,
            Seed = 0x01000001u,
            Octaves = 8,
        };

        void Aurora(string name, SdfSkyAuroraData value, uint quality, Vector4 expected, uint hashes) =>
            cases.Add(item: new(name, default, default, new Vector4(value: Vector3.UnitX, w: quality), new Vector4(w: 0, x: 6, y: 0, z: 0),
                expected, hashes, Aurora: value));
        Aurora("zero aurora intensity does no hashes", aurora with { Intensity = 0 }, 2, Vector4.Zero, 0);
        foreach (var quality in new[] { 0u, 1u, 2u }) {
            var octaves = ((quality == 0) ? 1u : 8u);
            var value = IntegerNoise(octaves, aurora.Seed);
            var ridge = Math.Clamp((1 - (MathF.Abs(x: (value - aurora.Bias)) * aurora.InverseWidth)), 0, 1);
            var expected = new Vector4(value: (aurora.Color * aurora.Intensity), w: MathF.Pow(x: ridge, y: aurora.Sharpness));

            Aurora(expected: expected, hashes: (8 * octaves), name: $"signed aurora ridge quality {quality}", quality: quality, value: aurora);
            Aurora($"periodic aurora ridge quality {quality}", aurora with { Offset = new(x: 4096, y: -4096, z: 4096) }, quality, expected, (8 * octaves));
        }
        Aurora("aurora outside narrow ridge", aurora with { InverseWidth = 100000, Bias = 1 }, 2,
            new Vector4(value: (aurora.Color * aurora.Intensity), w: 0), 64);
        Aurora("zero aurora ridge survives subnormal exponent", aurora with { InverseWidth = 100000, Bias = 1, Sharpness = float.Epsilon }, 2,
            new Vector4(value: (aurora.Color * aurora.Intensity), w: 0), 64);
        var panorama = new SdfSkyPanoramaData { Tint = new(x: 0.5f, y: 1, z: 0.25f), Intensity = 2, SourceIndex = 17 };

        void Panorama(string name, Vector3 direction, uint filter, Vector4 source) =>
            cases.Add(item: new(name, default, default, new Vector4(value: direction, w: 2), new Vector4(w: 0, x: 7, y: 0, z: 0),
                new Vector4(value: ((new Vector3(x: source.X, y: source.Y, z: source.Z) * panorama.Tint) * panorama.Intensity), w: source.W), 0,
                Panorama: panorama with { Filter = filter }, Samples: ((filter == 0) ? 1u : 4u)));
        Panorama("nearest panorama middle", Vector3.UnitZ, 0, new(w: (128f / 255), x: 0, y: 1, z: 1));
        Panorama("linear panorama middle", Vector3.UnitZ, 1, new(w: (160f / 255), x: 0.25f, y: 0.75f, z: 0.5f));
        Panorama("nearest panorama seam wraps", -Vector3.UnitZ, 0, new(w: 1, x: 0, y: 0, z: 0));
        Panorama("linear panorama blends across seam", -Vector3.UnitZ, 1, new(w: (159.5f / 255), x: 0.75f, y: 0.25f, z: 0.5f));
        Panorama("nearest panorama north pole", Vector3.UnitY, 0, new(w: (192f / 255), x: 0, y: 0, z: 1));
        Panorama("nearest panorama south pole", -Vector3.UnitY, 0, new(w: (128f / 255), x: 0, y: 1, z: 1));
        Panorama("linear panorama north clamps", Vector3.UnitY, 1, new(w: (160f / 255), x: 0, y: 0.5f, z: 0.5f));
        Panorama("linear panorama south clamps", -Vector3.UnitY, 1, new(w: (160f / 255), x: 0.5f, y: 1, z: 0.5f));
        cases.Add(item: new("zero panorama intensity does no samples", default, default, new Vector4(value: Vector3.UnitZ, w: 2), new Vector4(w: 0, x: 7, y: 0, z: 0),
            Vector4.Zero, 0, Panorama: panorama with { Intensity = 0 }));
    }
    private static byte[] PanoramaPixels() => [
        255, 0, 0, 64, 0, 255, 0, 128, 0, 0, 255, 192, 255, 255, 255, 255,
        0, 0, 0, 255, 255, 255, 0, 192, 0, 255, 255, 128, 255, 0, 255, 64,
    ];
}
