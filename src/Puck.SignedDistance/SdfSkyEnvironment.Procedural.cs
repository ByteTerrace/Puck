using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Illumination;

namespace Puck.SignedDistance;

public static partial class SdfSkyEnvironment {
    // Presentation reference of sky/kinds and field/sdf-noise. The shared integer hash keeps the lattice's identity;
    // the float arithmetic is presentation only and never enters a world query or simulation state.
    private static Vector4 Procedural(ref SdfSkyLayer layer, in SdfSkyBlock block, Vector3 direction) => layer.Kind switch {
        SdfSkyLayerKind.Noise => Noise(Payload<SdfSkyNoise>(ref layer), layer.Phase, block.Quality, direction),
        SdfSkyLayerKind.Pattern => Pattern(Payload<SdfSkyPattern>(ref layer), layer.Phase, direction),
        SdfSkyLayerKind.Aurora => Aurora(Payload<SdfSkyAurora>(ref layer), layer.Phase, block.Quality, direction),
        SdfSkyLayerKind.Clouds => Clouds(Payload<SdfSkyClouds>(ref layer), layer, block, direction),
        SdfSkyLayerKind.Stars => Stars(Payload<SdfSkyStars>(ref layer), block.Quality, direction),
        _ => throw new NotSupportedException($"The environment cannot project {layer.Kind}."),
    };
    private static T Payload<T>(ref SdfSkyLayer layer) where T : unmanaged, ISdfSkyKind => SdfSky.PayloadOf<T>(ref layer);
    private static float Saturate(float value) => Math.Clamp(value, 0f, 1f);
    private static float Mix(float a, float b, float t) => (a + ((b - a) * t));
    private static float Smooth(float a, float b, float x) {
        var t = Saturate(((x - a) / (b - a)));

        return ((t * t) * (3f - (2f * t)));
    }
    private static float Fraction(float value) => (value - MathF.Floor(value));
    private static float Quintic(float f) => (((f * f) * f) * ((f * ((f * 6f) - 15f)) + 10f));
    private static float Hash(uint x, uint y, uint z) => (Pcg3dLatticeNoise.Pcg3d(x, y, z).X * (1f / 4294967296f));
    private static uint Cell(float value) => unchecked((uint)((int)MathF.Floor(value))) & (SdfVolume.NoisePeriodCells - 1u);
    private static float Fbm(Vector3 point, uint seed, uint octaves, float gain, bool volume) {
        var value = 0f;
        var amplitude = 0.5f;
        var total = 0f;

        for (uint octave = 0; (octave < octaves); octave++) {
            var x = Cell(point.X); var y = Cell(point.Y); var z = Cell(point.Z);
            var nx = (x + 1u) & (SdfVolume.NoisePeriodCells - 1u);
            var ny = (y + 1u) & (SdfVolume.NoisePeriodCells - 1u);
            var nz = (z + 1u) & (SdfVolume.NoisePeriodCells - 1u);
            var u = Quintic(Fraction(point.X)); var v = Quintic(Fraction(point.Y)); var w = Quintic(Fraction(point.Z));
            var key = unchecked((seed + octave));
            float sample;

            if (volume) {
                var sy = key ^ 0x9E3779B9u; var sz = key ^ 0x85EBCA77u;
                var low = Mix(Mix(Hash(x ^ key, y ^ sy, z ^ sz), Hash(nx ^ key, y ^ sy, z ^ sz), u), Mix(Hash(x ^ key, ny ^ sy, z ^ sz), Hash(nx ^ key, ny ^ sy, z ^ sz), u), v);
                var high = Mix(Mix(Hash(x ^ key, y ^ sy, nz ^ sz), Hash(nx ^ key, y ^ sy, nz ^ sz), u), Mix(Hash(x ^ key, ny ^ sy, nz ^ sz), Hash(nx ^ key, ny ^ sy, nz ^ sz), u), v);

                sample = Mix(low, high, w);
            } else {
                sample = Mix(Mix(Hash(x, y, key), Hash(nx, y, key), u), Mix(Hash(x, ny, key), Hash(nx, ny, key), u), v);
            }
            value += (amplitude * sample);
            total += amplitude;
            point = ((point * 2f) + new Vector3(17f));
            amplitude *= gain;
        }
        return (value / MathF.Max(total, 1e-6f));
    }
    private static float Fbm2(Vector2 point, uint seed, uint octaves) => Fbm(new Vector3(point, 0f), seed, octaves, 0.5f, false);
    private static Vector4 Noise(SdfSkyNoise noise, float phase, SdfSkyTier tier, Vector3 d) {
        if (noise.Coverage <= 0f) { return Vector4.Zero; }
        var octaves = Math.Clamp(noise.Octaves, 1u, 8u);

        if (tier < SdfSkyTier.High) { octaves = Math.Min(octaves, 2u); }
        var value = Fbm(((d * noise.Scale) + new Vector3(0f, (phase * SdfVolume.NoisePeriodCells), 0f)), noise.Seed, octaves, noise.Gain, true);
        var alpha = ((noise.Coverage >= 1f) ? 1f : Rise(((1f - noise.Coverage) + noise.Softness), noise.Softness, value));

        return new Vector4(Vector3.Lerp(noise.ColorLow, noise.ColorHigh, value), alpha);
    }
    private static Vector4 Pattern(SdfSkyPattern pattern, float phase, Vector3 d) {
        var cells = MathF.Max(MathF.Round(pattern.Cells), 1f);
        var u = ((((MathF.Atan2(d.Z, d.X) * 0.15915494f) + 0.5f) * cells) + phase);
        var v = ((MathF.Asin(Math.Clamp(d.Y, -1f, 1f)) * 0.15915494f) * cells);
        var soft = MathF.Max(pattern.Softness, 0f);

        float Line(float coordinate) => (1f - Rise(((0.5f * pattern.Line) + soft), soft, MathF.Abs((Fraction((coordinate + 0.5f)) - 0.5f))));
        float Wave(float coordinate) {
            var width = MathF.Max((0.5f * soft), 1e-4f);

            return (Math.Clamp(((Fraction(coordinate) - 0.5f) / width), -1f, 1f) * Saturate(((0.5f - MathF.Abs((Fraction(coordinate) - 0.5f))) / width)));
        }
        var t = pattern.Shape switch {
            SdfSkyPatternShape.Stripes => Line(v),
            SdfSkyPatternShape.Grid => MathF.Max(Line(u), Line(v)),
            _ => (0.5f + ((0.5f * Wave(u)) * Wave(v))),
        };

        return new Vector4(Vector3.Lerp(pattern.ColorA, pattern.ColorB, t), 1f);
    }
    private static Vector4 Aurora(SdfSkyAurora aurora, float phase, SdfSkyTier tier, Vector3 d) {
        if ((aurora.Intensity <= 0f) || (aurora.Height <= 0f) || (d.Y <= ((aurora.Base - aurora.Fold) - 0.05f))) { return Vector4.Zero; }
        var around = new Vector2(d.X, d.Z);

        around = ((around.Length() > 1e-6f) ? Vector2.Normalize(around) : Vector2.UnitX);
        var drift = (phase * SdfVolume.NoisePeriodCells);
        var wave = Fbm2(((around * (aurora.Waves * 0.15915494f)) + new Vector2(drift, 0f)), aurora.Seed, ((tier >= SdfSkyTier.High) ? 3u : 2u));
        var rays = Fbm2(((around * (aurora.Rays * 0.15915494f)) + new Vector2(0f, drift)), aurora.Seed ^ 0x85EBCA77u, ((tier >= SdfSkyTier.Medium) ? 2u : 1u));
        var rise = ((d.Y - (aurora.Base + (aurora.Fold * ((2f * wave) - 1f)))) / aurora.Height);

        if (rise <= -0.05f) { return Vector4.Zero; }
        var strength = Saturate(((((rays * rays) * 1.5f) * Smooth(-0.05f, 0f, rise)) * MathF.Exp((-3f * MathF.Max(rise, 0f)))));

        return new Vector4((Vector3.Lerp(aurora.Color, aurora.TopColor, Saturate(rise)) * aurora.Intensity), strength);
    }
    private static Vector4 Clouds(SdfSkyClouds clouds, SdfSkyLayer layer, SdfSkyBlock block, Vector3 d) {
        if ((clouds.Coverage <= 0f) || (d.Y <= 0f)) { return Vector4.Zero; }
        var octaves = ((block.Quality >= SdfSkyTier.High) ? Math.Clamp(clouds.Octaves, 1u, 8u) : 3u);
        var b = (clouds.DomeRadius * d.Y);
        var t = (MathF.Sqrt((((b * b) + (2f * clouds.DomeRadius)) + 1f)) - b);
        var point = (new Vector2(d.X, d.Z) * t);
        var radius = point.Length();
        var angle = (clouds.SpinAngle + (((clouds.Curl * 2f) * radius) / (1f + (radius * radius))));

        var (sin, cos) = MathF.SinCos(angle);
        Vector2 Turn(Vector2 p) => new(((p.X * cos) - (p.Y * sin)), ((p.X * sin) + (p.Y * cos)));
        var p = ((Turn(point) / MathF.Max(clouds.Scale, 1e-3f)) + clouds.DriftOffset);

        float Thickness(Vector2 at) {
            var warp = Fbm2((at + clouds.ShearOffset), clouds.Seed ^ 0x9E3779B9u, octaves);
            var density = Fbm2((at + new Vector2((clouds.Warp * (warp - 0.5f)))), clouds.Seed, octaves);

            return Smooth((1f - clouds.Coverage), ((1f - clouds.Coverage) + clouds.Softness), density);
        }
        var thickness = Thickness(p);

        if (thickness <= 0f) { return Vector4.Zero; }
        var alpha = ((1f - MathF.Exp((-thickness * clouds.Extinction))) * Smooth(0f, clouds.HorizonFade, d.Y));

        if (block.Quality == SdfSkyTier.Low) { return new Vector4(clouds.Color, alpha); }
        var sun = Rotate(FrameDirection(block, clouds.LightDirection), layer.Rotation);
        var sunTurned = Turn(new Vector2(sun.X, sun.Z));
        var tx = Thickness((p + new Vector2(clouds.NormalTap, 0f)));
        var ty = Thickness((p + new Vector2(0f, clouds.NormalTap)));
        var ts = Thickness((p + ((clouds.NormalTap * 2f) * Vector2.Normalize((sunTurned + new Vector2(1e-5f))))));
        var normal = Vector3.Normalize(new Vector3(((-(tx - thickness) / clouds.NormalTap) * clouds.Height), 1f, ((-(ty - thickness) / clouds.NormalTap) * clouds.Height)));
        var diffuse = Saturate(Vector3.Dot(normal, Vector3.Normalize(new Vector3(sunTurned.X, sun.Y, sunTurned.Y))));
        var shadow = (1f - (clouds.SelfShadow * Saturate((ts - thickness))));
        var lining = ((clouds.SilverLining * MathF.Pow(Saturate(Vector3.Dot(d, sun)), 8f)) * (1f - thickness));

        return new Vector4(((clouds.Color * (Mix(0.45f, 1f, diffuse) * shadow)) + (clouds.LightColor * lining)), alpha);
    }
    private static Vector4 Stars(SdfSkyStars stars, SdfSkyTier tier, Vector3 d) {
        if ((d.Y <= 0f) || (stars.Brightness <= 0f)) { return Vector4.Zero; }
        var density = MathF.Max(stars.Density, 1f);

        var (u, v) = IrradianceLattice.Encode(new Double3(d.X, d.Z, d.Y));
        var x = MathF.Floor((((float)u) * density)); var y = MathF.Floor((((float)v) * density));
        var h = Pcg3dLatticeNoise.Pcg3d(BitConverter.SingleToUInt32Bits(x), BitConverter.SingleToUInt32Bits(y), stars.Seed);
        const float Unit = (1f / 4294967296f);

        if ((h.X * Unit) > stars.Sparsity) { return Vector4.Zero; }
        var h2 = Pcg3dLatticeNoise.Pcg3d(h.X, h.Y, h.Z);
        var luminosity = MathF.Min(1f, (stars.LuminosityFloor * MathF.Pow(MathF.Max((h2.X * Unit), 1e-6f), -0.6666667f)));
        ReadOnlySpan<Vector3> spectrum = [new(1f, .71f, .42f), new(1f, .82f, .64f), new(1f, .89f, .81f), new(1f, .98f, .99f), new(.89f, .91f, 1f), new(.79f, .85f, 1f), new(.71f, .80f, 1f)];
        var colorIndex = ((h2.Y * Unit) * 6f);
        var index = Math.Min(((int)colorIndex), 5);
        var tint = Vector3.Lerp(spectrum[index], spectrum[(index + 1)], (colorIndex - index));

        if ((tier > SdfSkyTier.Low) && ((h2.Z * Unit) < stars.TwinkleShare)) {
            var h3 = Pcg3dLatticeNoise.Pcg3d(h2.X, h2.Y, h2.Z);
            var phase = stars.TwinklePhase;
            var offset = (h3.Z * Unit);
            var flicker = (0.5f + ((0.5f * MathF.Sin((6.28318531f * (((1u + (h3.X % 3u)) * phase) + offset)))) * MathF.Sin((6.28318531f * (((2u + (h3.Y % 3u)) * phase) + (offset * 1.7f))))));

            luminosity *= (1f - (stars.TwinkleDepth * flicker));
        }
        var star = IrradianceLattice.Decode(((x + Mix(stars.Inset, (1f - stars.Inset), (h.Y * Unit))) / density), ((y + Mix(stars.Inset, (1f - stars.Inset), (h.Z * Unit))) / density));
        var starDirection = new Vector3(((float)star.X), ((float)star.Z), ((float)star.Y));
        var radius = (((stars.RadiusFraction * 3.14159265f) / density) * Mix(0.6f, 1f, MathF.Sqrt(luminosity)));
        var coverage = Smooth(((0.5f * radius) * radius), 0f, (1f - Vector3.Dot(d, starDirection)));

        return new Vector4(((stars.Brightness * luminosity) * tint), coverage);
    }
}
