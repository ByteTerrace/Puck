using System.Globalization;
using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The one hex colour grammar every authored colour crosses: a document palette, an identity colour, a HUD or theme
/// token, a <c>.puck</c> colour literal and a text effect's tint. A colour is <c>#</c> followed by three, four, six or
/// eight hex digits in either case (CSS Color Level 4's hex notation): <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or
/// <c>#RRGGBBAA</c>, where a short form doubles each digit and a form without alpha is opaque. As in CSS, the digits are
/// sRGB-encoded display values, so a parse answers a <see cref="DisplayColor"/>: its components are the codes over
/// <c>255</c>. A colour meant as light or reflectance enters shading only as the <see cref="SceneColor"/>
/// <see cref="DisplayColor.ToScene"/> decodes from it, so an unlit <c>#FF66B3</c> at an exposure that maps it to white
/// reaches the screen as <c>#FF66B3</c>. The canonical spelling a compiled document holds is the long uppercase form
/// (<see cref="TryCanonicalize"/>).
/// </summary>
public static class HexColor {
    /// <summary>Formats an encoded RGB triple (each component in <c>[0, 1]</c>) as an uppercase <c>#RRGGBB</c>
    /// string.</summary>
    /// <param name="rgb">The sRGB-encoded colour.</param>
    /// <returns>The <c>#RRGGBB</c> string, each component clamped and rounded to the nearest byte.</returns>
    public static string Format(Vector3 rgb) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"#{Byte(value: rgb.X):X2}{Byte(value: rgb.Y):X2}{Byte(value: rgb.Z):X2}"
        );
    /// <summary>Parses an opaque colour, falling back when it does not parse.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="fallback">The colour returned when <paramref name="value"/> is not an opaque colour.</param>
    /// <returns>The colour.</returns>
    public static DisplayColor Parse(string? value, DisplayColor fallback) => (TryParse(color: out var color, value: value) ? color : fallback);
    /// <summary>Parses an opaque colour: <c>#RGB</c> or <c>#RRGGBB</c>.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="color">The colour, opaque; <see langword="default"/> when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed colour without an alpha digit.</returns>
    public static bool TryParse(string? value, out DisplayColor color) {
        if (TryPack(packed: out var packed, value: value, withAlpha: out var withAlpha) && !withAlpha) {
            color = new DisplayColor(Rgba: VectorFunctions.UnpackRgba8(packed: packed));
            return true;
        }
        color = default;
        return false;
    }
    /// <summary>Parses a colour in any of the grammar's four forms; a form without alpha digits is opaque.</summary>
    /// <param name="value">The candidate string.</param>
    /// <param name="color">The colour; <see langword="default"/> when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed colour.</returns>
    public static bool TryParseRgba(string? value, out DisplayColor color) {
        if (TryPack(packed: out var packed, value: value, withAlpha: out _)) {
            color = new DisplayColor(Rgba: VectorFunctions.UnpackRgba8(packed: packed));
            return true;
        }
        color = default;
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
    // AllowHexSpecifier alone admits hex digits only: no sign, prefix or whitespace.
    private static bool TryPack(string? value, out uint packed, out bool withAlpha) {
        packed = 0U;
        withAlpha = false;
        if ((value is not { Length: (4 or 5 or 7 or 9) }) || (value[0] != '#') ||
            !uint.TryParse(s: value.AsSpan(start: 1), style: NumberStyles.AllowHexSpecifier, provider: CultureInfo.InvariantCulture, result: out var digits)) {
            return false;
        }
        var count = (value.Length - 1);
        var shortForm = (count <= 4);
        var channels = (shortForm ? count : (count / 2));
        var width = (shortForm ? 4 : 8);

        for (var channel = 0; (channel < channels); channel++) {
            var field = ((digits >> (((channels - 1) - channel) * width)) & ((1U << width) - 1U));

            packed |= ((shortForm ? (field * 0x11U) : field) << (channel * 8));
        }
        withAlpha = (channels == 4);
        if (!withAlpha) { packed |= 0xFF000000U; }
        return true;
    }
}
/// <summary>
/// A colour meant for the display: sRGB-encoded components, one at SDR white, and a straight alpha. What a hex literal
/// spells (<see cref="HexColor"/>), what a working image holds and what reaches a pane, a letterbox, a matte, a HUD
/// or an image source as it is. It enters shading only through <see cref="ToScene"/>.
/// </summary>
/// <param name="Rgba">The encoded red, green and blue in <c>X</c>, <c>Y</c> and <c>Z</c>, and the straight alpha in
/// <c>W</c>.</param>
public readonly record struct DisplayColor(Vector4 Rgba) {
    /// <summary>Initializes a new instance of the <see cref="DisplayColor"/> struct from encoded components.</summary>
    /// <param name="rgb">The encoded red, green and blue.</param>
    /// <param name="alpha">The straight alpha.</param>
    public DisplayColor(Vector3 rgb, float alpha = 1f) : this(Rgba: new Vector4(value: rgb, w: alpha)) { }

    /// <summary>Gets the encoded red, green and blue.</summary>
    public Vector3 Rgb => new(x: Rgba.X, y: Rgba.Y, z: Rgba.Z);
    /// <summary>Gets the straight alpha.</summary>
    public float Alpha => Rgba.W;

    /// <summary>Decodes the colour to the linear light it means, alpha dropped.</summary>
    /// <returns>The scene colour.</returns>
    public SceneColor ToScene() => SceneColor.FromLinear(linear: new Vector3(
        x: SrgbTransfer.ToLinear(encoded: Rgba.X),
        y: SrgbTransfer.ToLinear(encoded: Rgba.Y),
        z: SrgbTransfer.ToLinear(encoded: Rgba.Z)
    ));
}
/// <summary>
/// A colour meant as light or reflectance: linear components, twice the value twice the light. The only colour shading
/// reads. One is made from an authored hex colour by decoding it (<see cref="DisplayColor.ToScene"/>), or from a value
/// already linear (<see cref="FromLinear"/>: an engine default, a numeric tint, a measured colour).
/// </summary>
public readonly record struct SceneColor {
    private SceneColor(Vector3 linear) => Linear = linear;

    /// <summary>Gets the linear red, green and blue.</summary>
    public Vector3 Linear { get; }

    /// <summary>Returns the scene colour of linear components.</summary>
    /// <param name="linear">The linear red, green and blue.</param>
    /// <returns>The scene colour.</returns>
    public static SceneColor FromLinear(Vector3 linear) => new(linear: linear);
}
