using System.Globalization;
using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The one hex colour grammar every authored colour crosses: a document palette, an identity colour, a HUD or theme
/// token, a <c>.puck</c> colour literal and a text effect's tint. A colour is <c>#</c> followed by three, four, six or
/// eight hex digits in either case (CSS Color Level 4's hex notation): <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or
/// <c>#RRGGBBAA</c>, where a short form doubles each digit and a form without alpha is opaque. Components map straight
/// through <c>/255</c> into <see cref="VectorFunctions.UnpackRgba8"/>'s unit lanes; the render path is gamma-naive end to
/// end (linear shading written to a UNORM surface), so an unlit <c>#FF66B3</c> reaches the screen as <c>#FF66B3</c>.
/// The canonical spelling a compiled document holds is the long uppercase form (<see cref="TryCanonicalize"/>).
/// </summary>
public static class HexColor {
    /// <summary>Formats an RGB triple (each component in <c>[0, 1]</c>) as an uppercase <c>#RRGGBB</c> string.</summary>
    /// <param name="rgb">The RGB colour.</param>
    /// <returns>The <c>#RRGGBB</c> string, each component clamped and rounded to the nearest byte.</returns>
    public static string Format(Vector3 rgb) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"#{Byte(value: rgb.X):X2}{Byte(value: rgb.Y):X2}{Byte(value: rgb.Z):X2}"
        );
    /// <summary>Formats an RGBA quad (each component in <c>[0, 1]</c>) as an uppercase <c>#RRGGBB</c> string when
    /// opaque, else <c>#RRGGBBAA</c>.</summary>
    /// <param name="rgba">The RGBA colour.</param>
    /// <returns>The <c>#RRGGBB</c> or <c>#RRGGBBAA</c> string.</returns>
    public static string FormatRgba(Vector4 rgba) {
        var rgb = Format(rgb: new Vector3(x: rgba.X, y: rgba.Y, z: rgba.Z));

        return ((rgba.W >= 1f)
            ? rgb
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{rgb}{Byte(value: rgba.W):X2}"));
    }
    /// <summary>Parses an opaque colour, falling back when it does not parse.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="fallback">The RGB colour returned when <paramref name="value"/> is not an opaque colour.</param>
    /// <returns>The RGB colour.</returns>
    public static Vector3 Parse(string? value, Vector3 fallback) => (TryParse(rgb: out var rgb, value: value) ? rgb : fallback);
    /// <summary>Parses a colour in any of the grammar's four forms, falling back when it does not parse.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="fallback">The RGBA colour returned when <paramref name="value"/> does not parse.</param>
    /// <returns>The RGBA colour.</returns>
    public static Vector4 ParseRgba(string? value, Vector4 fallback) => (TryParseRgba(rgba: out var rgba, value: value) ? rgba : fallback);
    /// <summary>Parses an opaque colour: <c>#RGB</c> or <c>#RRGGBB</c>.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="rgb">The RGB colour, each component in <c>[0, 1]</c>; zero when this returns
    /// <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed colour without an alpha digit.</returns>
    public static bool TryParse(string? value, out Vector3 rgb) {
        if (TryPack(packed: out var packed, value: value, withAlpha: out var withAlpha) && !withAlpha) {
            var unpacked = VectorFunctions.UnpackRgba8(packed: packed);

            rgb = new Vector3(x: unpacked.X, y: unpacked.Y, z: unpacked.Z);
            return true;
        }
        rgb = default;
        return false;
    }
    /// <summary>Parses a colour in any of the grammar's four forms; a form without alpha digits is opaque.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="rgba">The RGBA colour, each component in <c>[0, 1]</c>; zero when this returns
    /// <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed colour.</returns>
    public static bool TryParseRgba(string? value, out Vector4 rgba) {
        if (TryPack(packed: out var packed, value: value, withAlpha: out _)) {
            rgba = VectorFunctions.UnpackRgba8(packed: packed);
            return true;
        }
        rgba = default;
        return false;
    }
    /// <summary>Rewrites a colour in its canonical spelling: the long form (<c>#RRGGBB</c> or <c>#RRGGBBAA</c>, alpha
    /// kept exactly when it was written) in uppercase, so every spelling of one colour lowers to the same text.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="canonical">The canonical spelling; empty when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed colour.</returns>
    public static bool TryCanonicalize(string? value, out string canonical) {
        if (!TryPack(packed: out var packed, value: value, withAlpha: out var withAlpha)) {
            canonical = string.Empty;
            return false;
        }
        var rgb = string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"#{(packed & 0xFFU):X2}{((packed >> 8) & 0xFFU):X2}{((packed >> 16) & 0xFFU):X2}");

        canonical = (withAlpha ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{rgb}{(packed >> 24):X2}") : rgb);
        return true;
    }

    private static int Byte(float value) => ((int)MathF.Round(x: (Math.Clamp(max: 1f, min: 0f, value: value) * 255f)));
    // The R8G8B8A8 word VectorFunctions.UnpackRgba8 reads, red in the low byte; a form without alpha digits is opaque.
    private static bool TryPack(string? value, out uint packed, out bool withAlpha) {
        packed = 0U;
        withAlpha = false;
        if ((value is not { Length: (4 or 5 or 7 or 9) }) || (value[0] != '#')) { return false; }
        var digits = (value.Length - 1);
        var shortForm = (digits <= 4);
        var channels = (shortForm ? digits : (digits / 2));
        var cursor = 1;

        for (var channel = 0; (channel < channels); channel++) {
            int byteValue;

            if (shortForm) {
                var nibble = HexDigit(character: value[cursor++]);

                if (nibble < 0) { return false; }
                byteValue = (nibble * 0x11);
            } else {
                var high = HexDigit(character: value[cursor++]);
                var low = HexDigit(character: value[cursor++]);

                if ((high < 0) || (low < 0)) { return false; }
                byteValue = ((high << 4) | low);
            }
            packed |= (((uint)byteValue) << (channel * 8));
        }
        withAlpha = (channels == 4);
        if (!withAlpha) { packed |= 0xFF000000U; }
        return true;
    }
    private static int HexDigit(char character) => character switch {
        >= '0' and <= '9' => (character - '0'),
        >= 'a' and <= 'f' => ((character - 'a') + 10),
        >= 'A' and <= 'F' => ((character - 'A') + 10),
        _ => -1,
    };
}
