using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Runs a gambatte <c>_out&lt;hex&gt;</c> case and compares the framebuffer against gambatte's own monochrome digit
/// pattern, ported from <c>test/testrunner.cpp</c>'s <c>tileFromChar</c>/<c>tilesAreEqual</c>/<c>frameBufferMatchesOut</c>.
/// Each hex digit of <see cref="LedgerCase.ExpectedHexPattern"/> is an 8x8 glyph drawn at a flat pixel offset of
/// <c>digitIndex * 8</c> into the row-major framebuffer — not clamped to one scanline, so a pattern longer than one
/// screen row (20 digits at 160px wide) continues into the next row exactly as the reference tester's own pointer
/// arithmetic does. A pixel is expected pure white or pure black once the low 3 bits of each channel are masked off
/// (gambatte's own comparison mask), which is why this probe needs no CGB color-space conversion: a genuinely gray or
/// black pixel maps to the same masked value whether or not it passed through gambatte's CGB mix. A pattern character
/// with no glyph is <see cref="ProbeVerdict.Inconclusive"/> rather than a silent early pass. The cell right after the
/// last digit is deliberately NOT required to be clear: several real cases (the <c>cgbpal_m3</c>/<c>display_startstate</c>
/// families among them) tile further hex-shaped content immediately past their own result digits as part of the
/// screen they draw, so that cell being occupied is not evidence of a truncated pattern.
/// </summary>
internal static class HexPatternProbe {
    private const uint ChannelMask = 0xF8F8F8;
    private const uint SetPixel = 0x000000;
    private const uint ClearPixel = 0xF8F8F8;
    private const int GlyphSize = 8;

    // 16 hex-digit glyphs (0-9, A-F), 8x8 monochrome pixels each, row-major, '#' set / '.' clear — ported verbatim
    // from testrunner.cpp's tileFromChar table.
    private static readonly bool[][] s_glyphs = [
        Glyph("........" + ".#######" + ".#.....#" + ".#.....#" + ".#.....#" + ".#.....#" + ".#.....#" + ".#######"),
        Glyph("........" + "....#..." + "....#..." + "....#..." + "....#..." + "....#..." + "....#..." + "....#..."),
        Glyph("........" + ".#######" + ".......#" + ".......#" + ".#######" + ".#......" + ".#......" + ".#######"),
        Glyph("........" + ".#######" + ".......#" + ".......#" + "..######" + ".......#" + ".......#" + ".#######"),
        Glyph("........" + ".#.....#" + ".#.....#" + ".#.....#" + ".#######" + ".......#" + ".......#" + ".......#"),
        Glyph("........" + ".#######" + ".#......" + ".#......" + ".######." + ".......#" + ".......#" + ".######."),
        Glyph("........" + ".#######" + ".#......" + ".#......" + ".#######" + ".#.....#" + ".#.....#" + ".#######"),
        Glyph("........" + ".#######" + ".......#" + "......#." + ".....#.." + "....#..." + "...#...." + "...#...."),
        Glyph("........" + "..#####." + ".#.....#" + ".#.....#" + "..#####." + ".#.....#" + ".#.....#" + "..#####."),
        Glyph("........" + ".#######" + ".#.....#" + ".#.....#" + ".#######" + ".......#" + ".......#" + ".#######"),
        Glyph("........" + "....#..." + "..#...#." + ".#.....#" + ".#######" + ".#.....#" + ".#.....#" + ".#.....#"),
        Glyph("........" + ".######." + ".#.....#" + ".#.....#" + ".######." + ".#.....#" + ".#.....#" + ".######."),
        Glyph("........" + "..#####." + ".#.....#" + ".#......" + ".#......" + ".#......" + ".#.....#" + "..#####."),
        Glyph("........" + ".######." + ".#.....#" + ".#.....#" + ".#.....#" + ".#.....#" + ".#.....#" + ".######."),
        Glyph("........" + ".#######" + ".#......" + ".#......" + ".#######" + ".#......" + ".#......" + ".#######"),
        Glyph("........" + ".#######" + ".#......" + ".#......" + ".#######" + ".#......" + ".#......" + ".#......"),
    ];

    /// <summary>Runs a case to a verdict.</summary>
    /// <param name="ledgerCase">The case to run; <see cref="LedgerCase.ExpectedHexPattern"/> must be non-empty.</param>
    /// <returns>The probe outcome.</returns>
    public static ProbeOutcome Run(LedgerCase ledgerCase) {
        var pattern = ledgerCase.ExpectedHexPattern;

        if (string.IsNullOrEmpty(value: pattern)) {
            return new ProbeOutcome(
                Detail: "no expected hex pattern configured for this case",
                Verdict: ProbeVerdict.Inconclusive
            );
        }

        var rom = File.ReadAllBytes(path: ledgerCase.FullPath);

        using var machine = PostMachine.Build(
            model: ledgerCase.Model,
            rom: rom
        );

        PostMachine.RunFrames(
            frames: ledgerCase.FrameCap,
            instance: machine
        );

        var framebuffer = machine.GetRequiredService<IFramebuffer>();
        var pixels = framebuffer.Pixels;
        var width = framebuffer.Width;

        for (var digit = 0; (digit < pattern.Length); ++digit) {
            var glyph = GlyphFor(character: pattern[digit]);

            if (glyph is null) {
                return new ProbeOutcome(
                    Detail: $"pattern '{pattern}' has no glyph for digit {digit} ('{pattern[digit]}')",
                    Verdict: ProbeVerdict.Inconclusive
                );
            }

            var baseOffset = (digit * GlyphSize);

            for (var row = 0; (row < GlyphSize); ++row) {
                for (var column = 0; (column < GlyphSize); ++column) {
                    var index = (baseOffset + (row * width) + column);

                    if (index >= pixels.Length) {
                        return new ProbeOutcome(
                            Detail: $"pattern '{pattern}' overflows the {width}x{framebuffer.Height} framebuffer at digit {digit}",
                            Verdict: ProbeVerdict.Fail
                        );
                    }

                    var expected = (glyph[((row * GlyphSize) + column)]
                        ? SetPixel
                        : ClearPixel);

                    if ((pixels[index] & ChannelMask) != expected) {
                        return new ProbeOutcome(
                            Detail: $"pixel mismatch at digit {digit} ('{pattern[digit]}') after {ledgerCase.FrameCap} frames",
                            Verdict: ProbeVerdict.Fail
                        );
                    }
                }
            }
        }

        return new ProbeOutcome(
            Detail: $"matches pattern '{pattern}' after {ledgerCase.FrameCap} frames",
            Verdict: ProbeVerdict.Pass
        );
    }

    private static bool[]? GlyphFor(char character) {
        if (character is (>= '0' and <= '9')) {
            return s_glyphs[(character - '0')];
        }

        var upper = char.ToUpperInvariant(c: character);

        return ((upper is (>= 'A' and <= 'F'))
            ? s_glyphs[((upper - 'A') + 0xA)]
            : null);
    }
    private static bool[] Glyph(string pixels) =>
        [.. pixels.Select(selector: static c => (c == '#'))];
}
