using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>
/// The photometric units the lit path shades in, and the conversions from what a world authors to what a light record
/// carries. The frame's radiometric unit is luminance in nits (candela per square metre): a directional light's
/// illuminance in lux, a point's or spot's luminous intensity in candela (or its luminous power in lumens), an emissive
/// surface's and a screen's luminance in nits, and the sky's radiance in nits. A Lambertian surface of albedo ρ under an
/// illuminance E has the luminance ρE/π, so a light record carries its illuminance (or intensity) over π and the shader
/// multiplies by the albedo alone. Each view's exposure (<see cref="ExposureOf"/>) maps the luminance it shades to the
/// display, before the tonemap. Presentation only: nothing here reaches simulation state.
/// </summary>
public static class SdfPhotometry {
    /// <summary>The share of full brightness below which a point or spot light's contribution ends: a light's range is
    /// the distance at which a white surface it lights faces it head on and reaches this share of display white at the
    /// darkest exposure the world adapts to (<see cref="RangeOf"/>).</summary>
    public const float RangeThreshold = (1f / 256f);
    /// <summary>The nearest a point light's distance is taken as, in world units: the radius of a bulb, inside which its
    /// inverse square would grow without bound.</summary>
    public const float MinimumDistance = 0.05f;
    /// <summary>The reflected-light meter's calibration constant (ISO 2720), with which an average scene luminance
    /// <c>L</c> meters at <c>EV100 = log2(L · 100 / K)</c>.</summary>
    public const float MeterCalibration = 12.5f;
    /// <summary>The saturation-based exposure's headroom (ISO 12232): a view at <c>EV100</c> maps the luminance
    /// <c>1.2 · 2^EV100</c> to display white.</summary>
    public const float SaturationHeadroom = 1.2f;
    /// <summary>The highest luminance a view stores, in nits: below the half-float images' largest finite value, so a
    /// sun's glint on a mirror clips rather than overflowing to infinity.</summary>
    public const float LuminanceCeiling = 65000f;
    /// <summary>The lowest colour temperature <see cref="Blackbody"/> spans, in kelvin.</summary>
    public const float MinimumTemperature = 1000f;
    /// <summary>The highest colour temperature <see cref="Blackbody"/> spans, in kelvin.</summary>
    public const float MaximumTemperature = 40000f;
    /// <summary>The lowest <c>EV100</c> an exposure admits: starlight.</summary>
    public const float MinimumEv100 = -6f;
    /// <summary>The highest <c>EV100</c> an exposure admits: beyond the brightest sunlit snow.</summary>
    public const float MaximumEv100 = 20f;

    /// <summary>Returns the luminance of a linear Rec. 709 colour, its Y.</summary>
    /// <param name="color">The linear RGB colour.</param>
    /// <returns>The luminance, in the colour's own units.</returns>
    public static float LuminanceOf(Vector3 color) =>
        ((0.2126f * color.X) + (0.7152f * color.Y) + (0.0722f * color.Z));
    /// <summary>Returns a colour scaled to unit luminance, so a light of that colour emits exactly its authored
    /// photometric quantity; a colour of no luminance is returned as it is.</summary>
    /// <param name="color">The linear RGB colour.</param>
    /// <returns>The colour of luminance one, or <paramref name="color"/> when its luminance is not positive.</returns>
    public static Vector3 UnitLuminance(Vector3 color) {
        var luminance = LuminanceOf(color: color);

        return ((luminance > 0f) ? (color / luminance) : color);
    }
    /// <summary>Returns the light-record weight of a directional light: its illuminance over π.</summary>
    /// <param name="lux">The illuminance on a surface facing the light, in lux.</param>
    /// <returns>The weight.</returns>
    public static float DirectionalWeight(float lux) => (lux / MathF.PI);
    /// <summary>Returns the light-record weight of a point or spot light: its luminous intensity over π, so the
    /// illuminance it casts at a distance <c>d</c> head on is <c>π · weight / d²</c>.</summary>
    /// <param name="candela">The luminous intensity, in candela.</param>
    /// <returns>The weight.</returns>
    public static float PositionalWeight(float candela) => (candela / MathF.PI);
    /// <summary>Returns the luminous intensity of a point light that emits a luminous power alike in every
    /// direction.</summary>
    /// <param name="lumens">The luminous power, in lumens.</param>
    /// <returns>The luminous intensity, in candela.</returns>
    public static float PointIntensity(float lumens) => (lumens / (4f * MathF.PI));
    /// <summary>Returns the luminous intensity of a spot light that emits a luminous power into its cone, alike across
    /// it.</summary>
    /// <param name="lumens">The luminous power, in lumens.</param>
    /// <param name="angle">The cone's half angle, in radians, in (0, π/2].</param>
    /// <returns>The luminous intensity, in candela.</returns>
    public static float SpotIntensity(float lumens, float angle) =>
        (lumens / ((2f * MathF.PI) * MathF.Max(x: (1f - MathF.Cos(x: angle)), y: 1e-6f)));
    /// <summary>Returns a spot cone's record lanes (<see cref="SdfLight.SpotAxis"/>, <see cref="SdfLight.SpotOffset"/>):
    /// full strength inside the inner half angle, none past the outer, and a smooth square between.</summary>
    /// <param name="aim">The direction the spot shines along, any nonzero length.</param>
    /// <param name="angle">The outer half angle, in radians.</param>
    /// <param name="innerAngle">The inner half angle, in radians, at most <paramref name="angle"/>.</param>
    /// <returns>The scaled axis and the offset.</returns>
    public static (Vector3 Axis, float Offset) Cone(Vector3 aim, float angle, float innerAngle) {
        var outer = MathF.Cos(x: angle);
        var inner = MathF.Cos(x: MathF.Min(x: innerAngle, y: angle));
        var scale = (1f / MathF.Max(x: (inner - outer), y: 1e-4f));
        var length = aim.Length();
        var unit = ((length > 0f) ? (aim / length) : -Vector3.UnitY);

        return ((unit * scale), (-outer * scale));
    }
    /// <summary>Returns the multiplier a view at an <c>EV100</c> maps luminance to the display by: the luminance
    /// <c>1.2 · 2^EV100</c> displays as white.</summary>
    /// <param name="ev100">The exposure value at ISO 100.</param>
    /// <returns>The multiplier, per nit.</returns>
    public static float ExposureOf(float ev100) => (1f / (SaturationHeadroom * MathF.Pow(x: 2f, y: ev100)));
    /// <summary>Returns the <c>EV100</c> a reflected-light meter reads for an average scene luminance.</summary>
    /// <param name="luminance">The average luminance, in nits; at least a millionth of a nit is read.</param>
    /// <returns>The exposure value at ISO 100.</returns>
    public static float MeteredEv100(float luminance) =>
        MathF.Log2(x: ((MathF.Max(x: luminance, y: 1e-6f) * 100f) / MeterCalibration));
    /// <summary>Returns a point or spot light's range: the distance at which a white surface facing it receives a
    /// luminance of <see cref="RangeThreshold"/> of display white at the brightest exposure gain the world uses, past
    /// which its windowed falloff is zero. A light of no intensity has no range.</summary>
    /// <param name="weight">The light-record weight (<see cref="PositionalWeight"/>).</param>
    /// <param name="exposure">The largest exposure multiplier the world adapts to (<see cref="ExposureOf"/> of its darkest
    /// <c>EV100</c>).</param>
    /// <returns>The range, in world units; zero when <paramref name="weight"/> or <paramref name="exposure"/> is not
    /// positive.</returns>
    public static float RangeOf(float weight, float exposure) =>
        (((weight > 0f) && (exposure > 0f)) ? MathF.Sqrt(x: ((weight * exposure) / RangeThreshold)) : 0f);
    /// <summary>Returns the windowed inverse-square falloff of a point or spot light at a distance:
    /// <c>saturate(1 − (d / r)^4)² / max(d, MinimumDistance)²</c>, smooth and exactly zero at the range.</summary>
    /// <param name="distance">The distance from the light, in world units.</param>
    /// <param name="range">The light's range, in world units.</param>
    /// <returns>The falloff, per square world unit.</returns>
    public static float Falloff(float distance, float range) {
        if (!(range > 0f)) {
            return 0f;
        }

        var ratio = (distance / range);
        var window = Math.Clamp(value: (1f - ((ratio * ratio) * (ratio * ratio))), min: 0f, max: 1f);
        var near = MathF.Max(x: distance, y: MinimumDistance);

        return ((window * window) / (near * near));
    }
    /// <summary>Returns the linear Rec. 709 colour of a black body at a temperature, scaled to unit luminance: its
    /// chromaticity on the Planckian locus (Kim et al.'s cubic fit), converted from CIE XYZ, with any out-of-gamut channel
    /// clipped to zero.</summary>
    /// <param name="kelvin">The colour temperature, clamped to [<see cref="MinimumTemperature"/>,
    /// <see cref="MaximumTemperature"/>].</param>
    /// <returns>The colour.</returns>
    public static Vector3 Blackbody(float kelvin) {
        double t = Math.Clamp(value: kelvin, min: MinimumTemperature, max: MaximumTemperature);
        double x;

        if (t <= 4000d) {
            x = ((((-0.2661239e9d / (t * t * t)) - (0.2343589e6d / (t * t))) + (0.8776956e3d / t)) + 0.179910d);
        } else {
            x = ((((-3.0258469e9d / (t * t * t)) + (2.1070379e6d / (t * t))) + (0.2226347e3d / t)) + 0.240390d);
        }

        double y;

        if (t <= 2222d) {
            y = ((((-1.1063814d * x * x * x) - (1.34811020d * x * x)) + (2.18555832d * x)) - 0.20219683d);
        } else if (t <= 4000d) {
            y = ((((-0.9549476d * x * x * x) - (1.37418593d * x * x)) + (2.09137015d * x)) - 0.16748867d);
        } else {
            y = ((((3.0817580d * x * x * x) - (5.87338670d * x * x)) + (3.75112997d * x)) - 0.37001483d);
        }

        var bigX = (x / y);
        var bigZ = (((1d - x) - y) / y);
        var r = Math.Max(val1: 0d, val2: (((3.2404542d * bigX) - 1.5371385d) - (0.4985314d * bigZ)));
        var g = Math.Max(val1: 0d, val2: (((-0.9692660d * bigX) + 1.8760108d) + (0.0415560d * bigZ)));
        var b = Math.Max(val1: 0d, val2: (((0.0556434d * bigX) - 0.2040259d) + (1.0572252d * bigZ)));

        return UnitLuminance(color: new Vector3(x: ((float)r), y: ((float)g), z: ((float)b)));
    }
}
