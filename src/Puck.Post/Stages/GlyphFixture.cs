using Puck.Text;

namespace Puck.Post;

/// <summary>
/// A deterministic SDF font atlas fixture for <see cref="WorldGlyphStage"/> — a tiny hand-drawn 5×7 block font
/// (no GDI+/System.Drawing, no installed-font dependency) upscaled into a monospace grid, run through the engine's
/// exact-EDT coverage→SDF generator (<see cref="SdfCoverageAtlas"/>). Baking the atlas in-process rather than reading
/// the machine's fonts keeps the cross-backend gate reproducible anywhere the POST runs.
/// </summary>
internal static class GlyphFixture {
    private const int Columns = 5;   // glyph bitmap width
    private const int Rows = 7;      // glyph bitmap height
    private const int Upscale = 8;   // texels per bitmap cell
    private const int Padding = 8;   // empty margin per cell (≥ the distance range)
    private const float DistanceRange = 8f;

    // 5×7 block glyphs spelling the charset PUCK — '#' = solid coverage. Enough distinct silhouettes (a bowl, verticals,
    // an open curve, diagonals) to exercise the atlas reconstruction and give the engraved/raised artifacts real letters.
    private static readonly (char Character, string[] Rows)[] s_glyphs = [
        ('P', ["#### ", "#   #", "#   #", "#### ", "#    ", "#    ", "#    "]),
        ('U', ["#   #", "#   #", "#   #", "#   #", "#   #", "#   #", " ### "]),
        ('C', [" ####", "#    ", "#    ", "#    ", "#    ", "#    ", " ####"]),
        ('K', ["#   #", "#  # ", "# #  ", "##   ", "# #  ", "#  # ", "#   #"]),
    ];

    /// <summary>Builds the single-channel SDF atlas (true distance replicated into every channel, alpha the marchable
    /// channel) plus its glyph table.</summary>
    public static FontAtlas Build() {
        var glyphWidth = (Columns * Upscale);
        var glyphHeight = (Rows * Upscale);
        var cellWidth = (glyphWidth + (Padding * 2));
        var cellHeight = (glyphHeight + (Padding * 2));
        var atlasWidth = (cellWidth * s_glyphs.Length);
        var rgba = new byte[atlasWidth * cellHeight * 4];

        for (var index = 0; (index < s_glyphs.Length); index++) {
            var cellLeft = (index * cellWidth);
            var bitmap = s_glyphs[index].Rows;

            for (var row = 0; (row < Rows); row++) {
                var line = bitmap[row];

                for (var column = 0; (column < Columns); column++) {
                    if (line[column] != '#') {
                        continue;
                    }

                    // Paint the bitmap cell as an Upscale×Upscale solid block, offset into the padded atlas cell.
                    for (var dy = 0; (dy < Upscale); dy++) {
                        var y = ((Padding + (row * Upscale)) + dy);

                        for (var dx = 0; (dx < Upscale); dx++) {
                            var x = (((cellLeft + Padding) + (column * Upscale)) + dx);
                            var offset = (((y * atlasWidth) + x) * 4);

                            rgba[offset] = 255;
                            rgba[(offset + 1)] = 255;
                            rgba[(offset + 2)] = 255;
                            rgba[(offset + 3)] = 255;
                        }
                    }
                }
            }
        }

        var em = (float)glyphHeight;                    // one em == the glyph's cap-to-baseline height
        var padEm = (Padding / em);
        var glyphs = new List<FontAtlasGlyph>(capacity: s_glyphs.Length);

        for (var index = 0; (index < s_glyphs.Length); index++) {
            var cellLeft = (index * cellWidth);

            glyphs.Add(item: new FontAtlasGlyph(
                advance: (cellWidth / em),
                atlasBounds: new FontAtlasBounds(Bottom: cellHeight, Left: cellLeft, Right: (cellLeft + cellWidth), Top: 0f),
                planeBounds: new FontAtlasBounds(Bottom: -padEm, Left: -padEm, Right: ((glyphWidth + Padding) / em), Top: (1f + padEm)),
                unicode: s_glyphs[index].Character
            ));
        }

        var coverage = new FontAtlas(
            distanceRange: 1f,
            glyphs: glyphs,
            height: cellHeight,
            imageData: new FontAtlasImageData(height: cellHeight, rgbaPixels: rgba, width: atlasWidth),
            imagePath: "post:glyph-fixture",
            kerningPairs: [],
            kind: FontAtlasKind.SoftMask,
            metrics: new FontAtlasMetrics(Ascender: 1f, Descender: 0f, LineHeight: (cellHeight / em), UnderlineThickness: (1f / em), UnderlineY: -0.1f),
            size: em,
            width: atlasWidth
        );

        return SdfCoverageAtlas.Generate(coverage: coverage, distanceRange: DistanceRange);
    }
}
