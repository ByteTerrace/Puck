using System.Globalization;

namespace Puck.Recording.Overlay;

/// <summary>
/// A straight (non-premultiplied) 8-bit RGBA color, and the parser for the document's <c>#RRGGBBAA</c> /
/// <c>#RRGGBB</c> hex form. Shared by the document validator (to reject malformed colors at the boundary) and the
/// overlay compositor (to alpha-over onto the frame).
/// </summary>
/// <param name="R">The red channel.</param>
/// <param name="G">The green channel.</param>
/// <param name="B">The blue channel.</param>
/// <param name="A">The alpha channel (255 opaque).</param>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A) {
    /// <summary>Attempts to parse a <c>#RRGGBBAA</c> or <c>#RRGGBB</c> (opaque) hex color.</summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="color">The parsed color on success; otherwise the default.</param>
    /// <returns><see langword="true"/> when the value is a valid hex color.</returns>
    public static bool TryParse(string? value, out Rgba32 color) {
        color = default;

        var span = (value ?? string.Empty).AsSpan().Trim();

        if ((span.Length > 0) && (span[0] == '#')) {
            span = span[1..];
        }

        if ((span.Length != 6) && (span.Length != 8)) {
            return false;
        }

        if (!byte.TryParse(s: span[0..2], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out var r) ||
            !byte.TryParse(s: span[2..4], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out var g) ||
            !byte.TryParse(s: span[4..6], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out var b)) {
            return false;
        }

        var a = (byte)255;

        if ((span.Length == 8) &&
            !byte.TryParse(s: span[6..8], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out a)) {
            return false;
        }

        color = new Rgba32(A: a, B: b, G: g, R: r);

        return true;
    }
}
