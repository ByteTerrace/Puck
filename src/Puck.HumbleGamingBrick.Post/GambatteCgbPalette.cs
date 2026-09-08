namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// gambatte's own CGB-to-RGB mix (<c>test/testrunner.cpp</c>'s <c>setCgbPalette</c> lookup table), applied to a
/// gambatte CGB screenshot case's actual framebuffer so it lands in the same color space as gambatte's own reference
/// image. Given the hardware's 5-bit channels <c>r</c>, <c>g</c>, <c>b</c> (each 0-31):
/// <code>
/// red   = (r*13 + g*2 + b) / 2
/// green = (g*3 + b) * 2
/// blue  = (r*3 + g*2 + b*11) / 2
/// </code>
/// with the two divisions truncating. This framebuffer's channels are already expanded to 8 bits via
/// <c>(X&lt;&lt;3)|(X&gt;&gt;2)</c>, a bijection over 0-31, so the source 5-bit value is recovered with a plain right
/// shift by 3 before the mix runs.
/// </summary>
internal static class GambatteCgbPalette {
    /// <summary>Converts one already-expanded <c>0x00RRGGBB</c> pixel through gambatte's CGB mix.</summary>
    /// <param name="pixel">A packed pixel whose channels were expanded from 5-bit hardware values.</param>
    /// <returns>The packed <c>0x00RRGGBB</c> pixel gambatte's own formula produces for the same 5-bit source.</returns>
    public static uint Transform(uint pixel) {
        var r = (((int)(pixel >> 16) & 0xFF) >> 3);
        var g = (((int)(pixel >> 8) & 0xFF) >> 3);
        var b = (((int)pixel & 0xFF) >> 3);
        var red = (((r * 13) + (g * 2) + b) >> 1);
        var green = (((g * 3) + b) * 2);
        var blue = (((r * 3) + (g * 2) + (b * 11)) >> 1);

        return (((uint)red << 16) | ((uint)green << 8) | (uint)blue);
    }
}
