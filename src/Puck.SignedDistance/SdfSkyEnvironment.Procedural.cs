using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Illumination;

namespace Puck.SignedDistance;

public static partial class SdfSkyEnvironment {
    // Presentation reference of sky/kinds and field/sdf-noise. The shared integer hash keeps the lattice's identity;
    // the float arithmetic is presentation only and never enters a world query or simulation state.
    private static Vector4 Procedural(ref SdfSkyLayer layer, in SdfSkyBlock block, Vector3 direction) => layer.Kind switch {
        SdfSkyLayerKind.Noise => Noise(Payload<SdfSkyNoise>(layer: ref layer), layer.Phase, block.Quality, direction),
        SdfSkyLayerKind.Pattern => Pattern(Payload<SdfSkyPattern>(layer: ref layer), layer.Phase, direction),
        SdfSkyLayerKind.Aurora => Aurora(Payload<SdfSkyAurora>(layer: ref layer), layer.Phase, block.Quality, direction),
        SdfSkyLayerKind.Clouds => Clouds(Payload<SdfSkyClouds>(layer: ref layer), layer, block, direction),
        SdfSkyLayerKind.Stars => Stars(Payload<SdfSkyStars>(layer: ref layer), block.Quality, direction),
        _ => throw new NotSupportedException(message: $"The environment cannot project {layer.Kind}."),
    };
    private static T Payload<T>(ref SdfSkyLayer layer) where T : unmanaged, ISdfSkyKind => SdfSky.PayloadOf<T>(layer: ref layer);
    private static float Saturate(float value) => Math.Clamp(max: 1f, min: 0f, value: value);
    private static float Mix(float a, float b, float t) => (a + ((b - a) * t));
    private static float Smooth(float a, float b, float x) {
        var t = Saturate(value: ((x - a) / (b - a)));

        return ((t * t) * (3f - (2f * t)));
    }
    private static float Fraction(float value) => (value - MathF.Floor(x: value));
    private static float Quintic(float f) => (((f * f) * f) * ((f * ((f * 6f) - 15f)) + 10f));
    private static float Hash(uint x, uint y, uint z) => (Pcg3dLatticeNoise.Pcg3d(x: x, y: y, z: z).X * (1f / 4294967296f));
    private static uint Cell(float value) => unchecked((uint)((int)MathF.Floor(x: value))) & (SdfVolume.NoisePeriodCells - 1u);
    private static float Fbm(Vector3 point, uint seed, uint octaves, float gain, bool volume) {
        var value = 0f;
        var amplitude = 0.5f;
        var total = 0f;

        for (uint octave = 0; (octave < octaves); octave++) {
            var x = Cell(value: point.X); var y = Cell(value: point.Y); var z = Cell(value: point.Z);
            var nx = (x + 1u) & (SdfVolume.NoisePeriodCells - 1u);
            var ny = (y + 1u) & (SdfVolume.NoisePeriodCells - 1u);
            var nz = (z + 1u) & (SdfVolume.NoisePeriodCells - 1u);
            var u = Quintic(f: Fraction(value: point.X)); var v = Quintic(f: Fraction(value: point.Y)); var w = Quintic(f: Fraction(value: point.Z));
            var key = unchecked((seed + octave));
            float sample;

            if (volume) {
                var sy = key ^ 0x9E3779B9u; var sz = key ^ 0x85EBCA77u;
                var low = Mix(a: Mix(a: Hash(x: x ^ key, y: y ^ sy, z: z ^ sz), b: Hash(x: nx ^ key, y: y ^ sy, z: z ^ sz), t: u), b: Mix(a: Hash(x: x ^ key, y: ny ^ sy, z: z ^ sz), b: Hash(x: nx ^ key, y: ny ^ sy, z: z ^ sz), t: u), t: v);
                var high = Mix(a: Mix(a: Hash(x: x ^ key, y: y ^ sy, z: nz ^ sz), b: Hash(x: nx ^ key, y: y ^ sy, z: nz ^ sz), t: u), b: Mix(a: Hash(x: x ^ key, y: ny ^ sy, z: nz ^ sz), b: Hash(x: nx ^ key, y: ny ^ sy, z: nz ^ sz), t: u), t: v);

                sample = Mix(a: low, b: high, t: w);
            } else {
                sample = Mix(a: Mix(a: Hash(x: x, y: y, z: key), b: Hash(x: nx, y: y, z: key), t: u), b: Mix(a: Hash(x: x, y: ny, z: key), b: Hash(x: nx, y: ny, z: key), t: u), t: v);
            }
            value += (amplitude * sample);
            total += amplitude;
            point = ((point * 2f) + new Vector3(value: 17f));
            amplitude *= gain;
        }
        return (value / MathF.Max(x: total, y: 1e-6f));
    }
    private static float Fbm2(Vector2 point, uint seed, uint octaves) => Fbm(new Vector3(value: point, z: 0f), seed, octaves, 0.5f, false);
    private static Vector4 Noise(SdfSkyNoise noise, float phase, SdfSkyTier tier, Vector3 d) {
        if (noise.Coverage <= 0f) { return Vector4.Zero; }
        var octaves = Math.Clamp(max: 8u, min: 1u, value: noise.Octaves);

        if (tier < SdfSkyTier.High) { octaves = Math.Min(val1: octaves, val2: 2u); }
        var value = Fbm(((d * noise.Scale) + new Vector3(x: 0f, y: (phase * SdfVolume.NoisePeriodCells), z: 0f)), noise.Seed, octaves, noise.Gain, true);
        var alpha = ((noise.Coverage >= 1f) ? 1f : Rise(edge: ((1f - noise.Coverage) + noise.Softness), soft: noise.Softness, value: value));

        return new Vector4(value: Vector3.Lerp(amount: value, value1: noise.ColorLow, value2: noise.ColorHigh), w: alpha);
    }
    private static Vector4 Pattern(SdfSkyPattern pattern, float phase, Vector3 d) {
        var cells = MathF.Max(x: MathF.Round(x: pattern.Cells), y: 1f);
        var u = ((((MathF.Atan2(x: d.X, y: d.Z) * 0.15915494f) + 0.5f) * cells) + phase);
        var v = ((MathF.Asin(x: Math.Clamp(max: 1f, min: -1f, value: d.Y)) * 0.15915494f) * cells);
        var soft = MathF.Max(x: pattern.Softness, y: 0f);

        float Line(float coordinate) => (1f - Rise(edge: ((0.5f * pattern.Line) + soft), soft: soft, value: MathF.Abs(x: (Fraction(value: (coordinate + 0.5f)) - 0.5f))));
        float Wave(float coordinate) {
            var width = MathF.Max(x: (0.5f * soft), y: 1e-4f);

            return (Math.Clamp(((Fraction(value: coordinate) - 0.5f) / width), -1f, 1f) * Saturate(value: ((0.5f - MathF.Abs(x: (Fraction(value: coordinate) - 0.5f))) / width)));
        }
        var t = pattern.Shape switch {
            SdfSkyPatternShape.Stripes => Line(coordinate: v),
            SdfSkyPatternShape.Grid => MathF.Max(x: Line(coordinate: u), y: Line(coordinate: v)),
            _ => (0.5f + ((0.5f * Wave(coordinate: u)) * Wave(coordinate: v))),
        };

        return new Vector4(value: Vector3.Lerp(amount: t, value1: pattern.ColorA, value2: pattern.ColorB), w: 1f);
    }
    private static Vector4 Aurora(SdfSkyAurora aurora, float phase, SdfSkyTier tier, Vector3 d) {
        if ((aurora.Intensity <= 0f) || (aurora.Height <= 0f) || (d.Y <= ((aurora.Base - aurora.Fold) - 0.05f))) { return Vector4.Zero; }
        var around = new Vector2(x: d.X, y: d.Z);

        around = ((around.Length() > 1e-6f) ? Vector2.Normalize(value: around) : Vector2.UnitX);
        var drift = (phase * SdfVolume.NoisePeriodCells);
        var wave = Fbm2(((around * (aurora.Waves * 0.15915494f)) + new Vector2(x: drift, y: 0f)), aurora.Seed, ((tier >= SdfSkyTier.High) ? 3u : 2u));
        var rays = Fbm2(((around * (aurora.Rays * 0.15915494f)) + new Vector2(x: 0f, y: drift)), aurora.Seed ^ 0x85EBCA77u, ((tier >= SdfSkyTier.Medium) ? 2u : 1u));
        var rise = ((d.Y - (aurora.Base + (aurora.Fold * ((2f * wave) - 1f)))) / aurora.Height);

        if (rise <= -0.05f) { return Vector4.Zero; }
        var strength = Saturate(value: ((((rays * rays) * 1.5f) * Smooth(a: -0.05f, b: 0f, x: rise)) * MathF.Exp(x: (-3f * MathF.Max(x: rise, y: 0f)))));

        return new Vector4(value: (Vector3.Lerp(aurora.Color, aurora.TopColor, Saturate(value: rise)) * aurora.Intensity), w: strength);
    }
    private static Vector4 Clouds(SdfSkyClouds clouds, SdfSkyLayer layer, SdfSkyBlock block, Vector3 d) {
        if ((clouds.Coverage <= 0f) || (d.Y <= 0f)) { return Vector4.Zero; }
        var octaves = ((block.Quality >= SdfSkyTier.High) ? Math.Clamp(max: 8u, min: 1u, value: clouds.Octaves) : 3u);
        var b = (clouds.DomeRadius * d.Y);
        var t = (MathF.Sqrt(x: (((b * b) + (2f * clouds.DomeRadius)) + 1f)) - b);
        var point = (new Vector2(x: d.X, y: d.Z) * t);
        var radius = point.Length();
        var angle = (clouds.SpinAngle + (((clouds.Curl * 2f) * radius) / (1f + (radius * radius))));

        var (sin, cos) = MathF.SinCos(x: angle);
        Vector2 Turn(Vector2 p) => new(x: ((p.X * cos) - (p.Y * sin)), y: ((p.X * sin) + (p.Y * cos)));
        var p = ((Turn(p: point) / MathF.Max(x: clouds.Scale, y: 1e-3f)) + clouds.DriftOffset);

        float Thickness(Vector2 at) {
            var warp = Fbm2(octaves: octaves, point: (at + clouds.ShearOffset), seed: clouds.Seed ^ 0x9E3779B9u);
            var density = Fbm2((at + new Vector2(value: (clouds.Warp * (warp - 0.5f)))), clouds.Seed, octaves);

            return Smooth(a: (1f - clouds.Coverage), b: ((1f - clouds.Coverage) + clouds.Softness), x: density);
        }
        var thickness = Thickness(at: p);

        if (thickness <= 0f) { return Vector4.Zero; }
        var alpha = ((1f - MathF.Exp(x: (-thickness * clouds.Extinction))) * Smooth(a: 0f, b: clouds.HorizonFade, x: d.Y));

        if (block.Quality == SdfSkyTier.Low) { return new Vector4(value: clouds.Color, w: alpha); }
        var sun = Rotate(direction: FrameDirection(block: block, direction: clouds.LightDirection), rotation: layer.Rotation);
        var sunTurned = Turn(p: new Vector2(x: sun.X, y: sun.Z));
        var tx = Thickness(at: (p + new Vector2(x: clouds.NormalTap, y: 0f)));
        var ty = Thickness(at: (p + new Vector2(x: 0f, y: clouds.NormalTap)));
        var ts = Thickness(at: (p + ((clouds.NormalTap * 2f) * Vector2.Normalize(value: (sunTurned + new Vector2(value: 1e-5f))))));
        var normal = Vector3.Normalize(value: new Vector3(x: ((-(tx - thickness) / clouds.NormalTap) * clouds.Height), y: 1f, z: ((-(ty - thickness) / clouds.NormalTap) * clouds.Height)));
        var diffuse = Saturate(value: Vector3.Dot(vector1: normal, vector2: Vector3.Normalize(value: new Vector3(x: sunTurned.X, y: sun.Y, z: sunTurned.Y))));
        var shadow = (1f - (clouds.SelfShadow * Saturate(value: (ts - thickness))));
        var lining = ((clouds.SilverLining * MathF.Pow(x: Saturate(value: Vector3.Dot(vector1: d, vector2: sun)), y: 8f)) * (1f - thickness));

        return new Vector4(value: ((clouds.Color * (Mix(a: 0.45f, b: 1f, t: diffuse) * shadow)) + (clouds.LightColor * lining)), w: alpha);
    }
    private static Vector4 Stars(SdfSkyStars stars, SdfSkyTier tier, Vector3 d) {
        if ((d.Y <= 0f) || (stars.Brightness <= 0f)) { return Vector4.Zero; }
        var density = MathF.Max(x: stars.Density, y: 1f);

        var (u, v) = IrradianceLattice.Encode(direction: new Double3(X: d.X, Y: d.Z, Z: d.Y));
        var x = MathF.Floor(x: (((float)u) * density)); var y = MathF.Floor(x: (((float)v) * density));
        var h = Pcg3dLatticeNoise.Pcg3d(x: BitConverter.SingleToUInt32Bits(value: x), y: BitConverter.SingleToUInt32Bits(value: y), z: stars.Seed);
        const float Unit = (1f / 4294967296f);

        if ((h.X * Unit) > stars.Sparsity) { return Vector4.Zero; }
        var h2 = Pcg3dLatticeNoise.Pcg3d(x: h.X, y: h.Y, z: h.Z);
        var luminosity = MathF.Min(x: 1f, y: (stars.LuminosityFloor * MathF.Pow(x: MathF.Max(x: (h2.X * Unit), y: 1e-6f), y: -0.6666667f)));
        ReadOnlySpan<Vector3> spectrum = [new(x: 1f, y: .71f, z: .42f), new(x: 1f, y: .82f, z: .64f), new(x: 1f, y: .89f, z: .81f), new(x: 1f, y: .98f, z: .99f), new(x: .89f, y: .91f, z: 1f), new(x: .79f, y: .85f, z: 1f), new(x: .71f, y: .80f, z: 1f)];
        var colorIndex = ((h2.Y * Unit) * 6f);
        var index = Math.Min(val1: ((int)colorIndex), val2: 5);
        var tint = Vector3.Lerp(spectrum[index], spectrum[(index + 1)], (colorIndex - index));

        if ((tier > SdfSkyTier.Low) && ((h2.Z * Unit) < stars.TwinkleShare)) {
            var h3 = Pcg3dLatticeNoise.Pcg3d(x: h2.X, y: h2.Y, z: h2.Z);
            var phase = stars.TwinklePhase;
            var offset = (h3.Z * Unit);
            var flicker = (0.5f + ((0.5f * MathF.Sin(x: (6.28318531f * (((1u + (h3.X % 3u)) * phase) + offset)))) * MathF.Sin(x: (6.28318531f * (((2u + (h3.Y % 3u)) * phase) + (offset * 1.7f))))));

            luminosity *= (1f - (stars.TwinkleDepth * flicker));
        }
        var star = IrradianceLattice.Decode(u: ((x + Mix(a: stars.Inset, b: (1f - stars.Inset), t: (h.Y * Unit))) / density), v: ((y + Mix(a: stars.Inset, b: (1f - stars.Inset), t: (h.Z * Unit))) / density));
        var starDirection = new Vector3(x: ((float)star.X), y: ((float)star.Z), z: ((float)star.Y));
        var radius = (((stars.RadiusFraction * 3.14159265f) / density) * Mix(a: 0.6f, b: 1f, t: MathF.Sqrt(x: luminosity)));
        var coverage = Smooth(a: ((0.5f * radius) * radius), b: 0f, x: (1f - Vector3.Dot(vector1: d, vector2: starDirection)));

        return new Vector4(value: ((stars.Brightness * luminosity) * tint), w: coverage);
    }
}
