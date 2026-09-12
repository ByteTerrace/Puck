namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Independent, readable pixel expectations for the native boot presentation, not decoded firmware tables.</summary>
internal static class FirmwarePresentationArtwork {
    private static readonly string[][] Wordmark = [
        [".#####..", ".##..##.", ".##..##.", ".#####..", ".##.....", ".##.....", ".##....."],
        [".##..##.", ".##..##.", ".##..##.", ".##..##.", ".##..##.", ".##..##.", "..####.."],
        ["..####..", ".##..##.", ".##.....", ".##.....", ".##.....", ".##..##.", "..####.."],
        [".##..##.", ".##.##..", ".####...", ".###....", ".####...", ".##.##..", ".##..##."],
    ];
    private static readonly Dictionary<char, string[]> Publisher = new() {
        ['B'] = ["####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."],
        ['Y'] = ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
        ['T'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
        ['E'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#####"],
        ['R'] = ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
        ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['C'] = [".####", "#....", "#....", "#....", "#....", "#....", ".####"],
    };

    /// <summary>Builds the complete 240-by-160 expected image, with an optional hardware darkening coefficient.</summary>
    /// <param name="dark">The GBA brightness-decrease coefficient, from zero through sixteen.</param>
    /// <returns>RGBA pixels in the framebuffer's native little-endian storage layout.</returns>
    public static uint[] Create(int dark = 0) {
        var pixels = new uint[240 * 160];
        Array.Fill(array: pixels, value: Color(rgb555: 0x1C83, dark: dark));
        for (var glyph = 0; glyph < Wordmark.Length; ++glyph) {
            Draw(pixels: pixels, rows: Wordmark[glyph], left: 56 + glyph * 32, top: 55, scale: 4, color: Color(rgb555: 0x6FBC, dark: dark));
        }
        const string PublisherText = "BYTETERRACE";
        for (var glyph = 0; glyph < PublisherText.Length; ++glyph) {
            Draw(pixels: pixels, rows: Publisher[PublisherText[glyph]], left: 87 + glyph * 6, top: 102, scale: 1, color: Color(rgb555: 0x5F6C, dark: dark));
        }
        for (var y = 91; y < 93; ++y) {
            pixels.AsSpan(start: y * 240 + 111, length: 18).Fill(value: Color(rgb555: 0x5F6C, dark: dark));
        }
        return pixels;
    }

    /// <summary>Expands three five-bit channels after integer hardware darkening, without calling the production PPU.</summary>
    /// <param name="rgb555">The source hardware color.</param>
    /// <param name="dark">The brightness-decrease coefficient.</param>
    /// <returns>The native framebuffer pixel.</returns>
    public static uint Color(ushort rgb555, int dark = 0) {
        var rgba = 0xFF000000u;
        for (var channel = 0; channel < 3; ++channel) {
            var value = (rgb555 >> (channel * 5)) & 31;
            value -= value * dark / 16;
            rgba |= (uint)(value * 8 + value / 4) << (channel * 8);
        }
        return rgba;
    }

    private static void Draw(uint[] pixels, string[] rows, int left, int top, int scale, uint color) {
        for (var y = 0; y < rows.Length * scale; ++y) {
            for (var x = 0; x < rows[y / scale].Length * scale; ++x) {
                if (rows[y / scale][x / scale] == '#') {
                    pixels[(top + y) * 240 + left + x] = color;
                }
            }
        }
    }
}
