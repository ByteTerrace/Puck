using System.Globalization;
using System.Numerics;

namespace Puck.World;

/// <summary>
/// Shared color math for the world: the HSV→RGB conversion the population's simulated-avatar palette
/// (<c>world.population</c>) uses, plus the uppercase <c>#RRGGBB</c> formatting the persisted owned-world
/// identity catalog stores.
/// </summary>
public static class WorldColor {
    /// <summary>Returns an authored sequence hue for a 0-based index, wrapped to <c>[0, 1)</c>.</summary>
    /// <param name="index">The 0-based sequence index.</param>
    /// <param name="sequence">The authored scalar sequence.</param>
    /// <returns>The hue in <c>[0, 1)</c>.</returns>
    public static float SequenceHue(int index, WorldSequence sequence) => WorldSequenceSampling.Scalar(sequence: sequence, index: index);

    /// <summary>Returns an authored generated color for a 0-based index.</summary>
    /// <param name="index">The 0-based sequence index.</param>
    /// <param name="defaults">The authored color sequence.</param>
    /// <returns>The RGB color as a <see cref="Vector3"/>.</returns>
    public static Vector3 SequenceColor(int index, WorldPlayerDefaults defaults) =>
        HsvToRgb(h: SequenceHue(index: index, sequence: defaults.ColorSequence), s: defaults.Saturation, v: defaults.Value);

    /// <summary>Returns an authored generated color as an uppercase <c>#RRGGBB</c> string.</summary>
    /// <param name="index">The 0-based sequence index.</param>
    /// <param name="defaults">The authored color sequence.</param>
    /// <returns>The <c>#RRGGBB</c> hex string.</returns>
    public static string SequenceColorHex(int index, WorldPlayerDefaults defaults) =>
        HsvToHex(h: SequenceHue(index: index, sequence: defaults.ColorSequence), s: defaults.Saturation, v: defaults.Value);

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
    /// string.</summary>
    /// <param name="h">Hue in <c>[0, 1)</c>.</param>
    /// <param name="s">Saturation in <c>[0, 1]</c>.</param>
    /// <param name="v">Value in <c>[0, 1]</c>.</param>
    /// <returns>The <c>#RRGGBB</c> hex string.</returns>
    public static string HsvToHex(float h, float s, float v) => RgbToHex(rgb: HsvToRgb(h: h, s: s, v: v));
}
