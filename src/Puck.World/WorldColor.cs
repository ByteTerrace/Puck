using System.Globalization;
using System.Numerics;

namespace Puck.World;

/// <summary>
/// Shared color math for the world: the HSV→RGB conversion both the profile catalog's auto-color
/// (<c>profile.create</c>) and the population's simulated-avatar palette (<c>world.population</c>) use, plus the
/// uppercase <c>#RRGGBB</c> formatting the persisted catalog stores.
/// </summary>
internal static class WorldColor {
    /// <summary>The golden-ratio conjugate — the low-discrepancy hue step both auto-color walks take, so successive
    /// hues spread evenly around the wheel.</summary>
    public const float GoldenRatioConjugate = 0.61803399f;
    /// <summary>The saturation a generated hue is baked at.</summary>
    public const float SeedSaturation = 0.65f;
    /// <summary>The value (brightness) a generated hue is baked at.</summary>
    public const float SeedValue = 0.85f;

    /// <summary>Returns the golden-ratio hue for a 0-based index — the index times <see cref="GoldenRatioConjugate"/>
    /// wrapped to <c>[0, 1)</c>. Shared by the profile catalog's auto-color and the population's simulated palette.</summary>
    /// <param name="index">The 0-based sequence index.</param>
    /// <returns>The hue in <c>[0, 1)</c>.</returns>
    public static float GoldenRatioHue(int index) {
        var hue = (index * GoldenRatioConjugate);

        return (hue - MathF.Floor(x: hue));
    }

    /// <summary>Returns the seed-palette color for a 0-based index — its <see cref="GoldenRatioHue"/> baked at the seed
    /// saturation and value. The population's simulated palette uses this directly; the profile catalog walks the hex
    /// form (<see cref="IndexColorHex"/>) so it can dedupe against stored colors.</summary>
    /// <param name="index">The 0-based sequence index.</param>
    /// <returns>The RGB color as a <see cref="Vector3"/>.</returns>
    public static Vector3 IndexColor(int index) => HsvToRgb(h: GoldenRatioHue(index: index), s: SeedSaturation, v: SeedValue);

    /// <summary>Returns the seed-palette color for a 0-based index as an uppercase <c>#RRGGBB</c> hex string — the hex
    /// peer of <see cref="IndexColor"/>, used by the profile catalog's auto-color walk.</summary>
    /// <param name="index">The 0-based sequence index.</param>
    /// <returns>The <c>#RRGGBB</c> hex string.</returns>
    public static string IndexColorHex(int index) => HsvToHex(h: GoldenRatioHue(index: index), s: SeedSaturation, v: SeedValue);

    /// <summary>Returns the derived nose color for a body color — the body hue darkened by
    /// <see cref="WorldProfile.NoseFactor"/>. Shared by every avatar surface (seat, profile, simulated stand-in).</summary>
    /// <param name="body">The body albedo.</param>
    /// <returns>The nose albedo.</returns>
    public static Vector3 Nose(Vector3 body) => (body * WorldProfile.NoseFactor);

    /// <summary>Converts an HSV triple (each component in <c>[0, 1]</c>) to RGB in <c>[0, 1]</c>.</summary>
    /// <param name="h">Hue in <c>[0, 1)</c> (values outside wrap through the sextant math).</param>
    /// <param name="s">Saturation in <c>[0, 1]</c>.</param>
    /// <param name="v">Value in <c>[0, 1]</c>.</param>
    /// <returns>The RGB color as a <see cref="Vector3"/>.</returns>
    public static Vector3 HsvToRgb(float h, float s, float v) {
        var sector = (int)MathF.Floor(x: (h * 6f));
        var fraction = ((h * 6f) - sector);
        var p = (v * (1f - s));
        var q = (v * (1f - (s * fraction)));
        var t = (v * (1f - (s * (1f - fraction))));

        return (((sector % 6) + 6) % 6) switch {
            0 => new Vector3(x: v, y: t, z: p),
            1 => new Vector3(x: q, y: v, z: p),
            2 => new Vector3(x: p, y: v, z: t),
            3 => new Vector3(x: p, y: q, z: v),
            4 => new Vector3(x: t, y: p, z: v),
            _ => new Vector3(x: v, y: p, z: q),
        };
    }

    /// <summary>Formats an RGB triple (each component in <c>[0, 1]</c>) as an uppercase <c>#RRGGBB</c> hex string,
    /// matching the catalog's stored convention.</summary>
    /// <param name="rgb">The RGB color.</param>
    /// <returns>The <c>#RRGGBB</c> hex string.</returns>
    public static string RgbToHex(Vector3 rgb) {
        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"#{(int)MathF.Round(x: (rgb.X * 255f)):X2}{(int)MathF.Round(x: (rgb.Y * 255f)):X2}{(int)MathF.Round(x: (rgb.Z * 255f)):X2}"
        );
    }

    /// <summary>Converts an HSV triple (each component in <c>[0, 1]</c>) straight to an uppercase <c>#RRGGBB</c> hex
    /// string — the color-less <c>profile.create</c> path.</summary>
    /// <param name="h">Hue in <c>[0, 1)</c>.</param>
    /// <param name="s">Saturation in <c>[0, 1]</c>.</param>
    /// <param name="v">Value in <c>[0, 1]</c>.</param>
    /// <returns>The <c>#RRGGBB</c> hex string.</returns>
    public static string HsvToHex(float h, float s, float v) => RgbToHex(rgb: HsvToRgb(h: h, s: s, v: v));
}
