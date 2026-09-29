using System.Numerics;
using Puck.Maths;
using Puck.Shaders;

namespace Puck.World.Tests;

public sealed partial class SdfSkyKindDeviceLawTests {
    private static void AddNoiseAndPatternCases(List<KindCase> cases) {
        var noise = new SdfSkyNoiseData {
            ColorLow = new(x: 0.125f, y: 0.25f, z: 0.5f),
            ColorHigh = new(x: 0.75f, y: 0.5f, z: 0.25f),
            Intensity = 2,
            InverseScale = 1,
            Seed = 0x01000001u,
            Octaves = 8,
            Contrast = 0.5f,
            Bias = 0.5f,
        };

        void Noise(string name, SdfSkyNoiseData value, uint quality, Vector4 expected, uint hashes) =>
            cases.Add(item: new(name, default, default, new Vector4(value: Vector3.UnitX, w: quality), new Vector4(w: 0, x: 3, y: 0, z: 0),
                expected, hashes, Noise: value));
        Noise("zero noise intensity does no hashes", noise with { Intensity = 0 }, 2, Vector4.Zero, 0);
        foreach (var quality in new[] { 0u, 1u, 2u }) {
            var octaves = ((quality == 0) ? 3u : 8u);
            var signed = IntegerNoise(octaves, noise.Seed);
            var color = (Vector3.Lerp(amount: ((signed * 0.5f) + 0.5f), value1: noise.ColorLow, value2: noise.ColorHigh) * noise.Intensity);

            Noise($"signed integer noise quality {quality}", noise, quality, new Vector4(value: color, w: 1), (8 * octaves));
            Noise($"periodic integer noise quality {quality}", noise with { Offset = new(x: 4096, y: -4096, z: 4096) }, quality,
                new Vector4(value: color, w: 1), (8 * octaves));
        }
        var largeSigned = IntegerNoise(octaves: 8, seed: noise.Seed, x: 0);
        var largeColor = (Vector3.Lerp(amount: ((largeSigned * 0.5f) + 0.5f), value1: noise.ColorLow, value2: noise.ColorHigh) * noise.Intensity);

        Noise("finite coordinates beyond int32 wrap before conversion", noise with { Offset = new(x: 8589934592f, y: -8589934592f, z: 8589934592f) },
            2, new Vector4(value: largeColor, w: 1), 64);
        Noise("noise low endpoint shaping", noise with { Contrast = 0, Bias = 0 }, 2,
            new Vector4(value: (noise.ColorLow * noise.Intensity), w: 1), 64);
        Noise("noise high endpoint shaping", noise with { Contrast = 0, Bias = 1 }, 2,
            new Vector4(value: (noise.ColorHigh * noise.Intensity), w: 1), 64);
        var pattern = new SdfSkyPatternData {
            ColorA = new(x: 0.2f, y: 0.4f, z: 0.6f),
            ColorB = new(x: 0.7f, y: 0.5f, z: 0.3f),
            Intensity = 2,
            Cells = 4,
            Offset = new(x: 0.5f, y: 0.5f),
        };

        void Pattern(string name, SdfSkyPatternData value, Vector4 expected) =>
            cases.Add(item: new(name, default, default, new Vector4(value: Vector3.UnitZ, w: 2), new Vector4(w: 0, x: 4, y: 0, z: 0),
                expected, 0, Pattern: value));
        Pattern("checker even center", pattern, new Vector4(value: (pattern.ColorA * pattern.Intensity), w: 1));
        Pattern("checker odd center", pattern with { Offset = new(x: 1.5f, y: 0.5f) }, new Vector4(value: (pattern.ColorB * pattern.Intensity), w: 1));
        Pattern("checker negative cell", pattern with { Offset = new(x: -2.5f, y: 0.5f) }, new Vector4(value: (pattern.ColorB * pattern.Intensity), w: 1));
        Pattern("zero checker intensity is transparent", pattern with { Intensity = 0 }, Vector4.Zero);
    }
    // Integer lattice points need no interpolation: every octave selects its exact PCG corner. This independent
    // integer primitive also makes the periodic translation's expected answer identical before any float conversion.
    private static float IntegerNoise(uint octaves, uint seed, uint x = 1u) {
        var y = 0u;
        var z = 0u;
        var sum = 0d;
        var weight = 0.5d;
        var normalization = 0d;

        for (var octave = 0u; (octave < octaves); octave++) {
            var stream = (seed + octave);
            var hash = Pcg3dLatticeNoise.Pcg3d(x: (x & 4095u) ^ stream,
                y: (y & 4095u) ^ (stream ^ 0x9E3779B9u), z: (z & 4095u) ^ (stream ^ 0x85EBCA77u));

            sum += (weight * (((2d * hash.X) / 4294967296d) - 1d));
            normalization += weight;
            weight *= 0.5d;
            x = ((x * 2) + 17);
            y = ((y * 2) + 17);
            z = ((z * 2) + 17);
        }
        return ((float)(sum / normalization));
    }
}
