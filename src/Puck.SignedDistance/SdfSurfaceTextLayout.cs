using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>
/// The capacity and packing of surface text: runs of glyph quads a text paint (<see cref="SdfPaint.Text"/>) draws on
/// the surface of its material at the hit, lit as the surface is, where a screen's glyph decal
/// (<see cref="SdfScreenDecalLayout"/>) is a display's own picture. A program names each run by a slot; the host
/// supplies a slot's glyphs each frame, so the lettering changes without a new program or a bake.
/// </summary>
/// <remarks>The packing is read by <c>sdfSurfaceTextInk</c> in <c>shade/sdf-glyph-ink.hlsli</c>. A slot's run is its
/// glyphs, then the uniform grid that indexes them (<see cref="IndexedEntries"/>), so a pixel tests only the glyphs whose
/// quads overlap its grid cell. Each glyph is two four-word entries. The first is the glyph quad's rectangle in the
/// paint's bound box, as four floats (minimum x and y, maximum x and y), each axis running from -1 at the box's low
/// face to 1 at its high face with y up. The second is the glyph's atlas cell (its top-left and bottom-right corners,
/// each a packed unorm2x16 UV), its ink as rgba8 with red in the low byte and its coverage in alpha, and its flags
/// (<see cref="TintFlag"/>). The grid follows the glyphs: one header entry (the grid's low corner as two floats, then
/// its cells per unit along x and y), the cells (one word each, four to an entry: the cell's first index in the low
/// sixteen bits and its glyph count in the high sixteen), then the index list (one sixteen-bit glyph index each, two to
/// a word and eight to an entry). The slot's descriptor carries the glyph count, the run's first entry, the atlas's
/// distance range and the grid's columns (low sixteen bits) and rows (high sixteen bits).</remarks>
public static class SdfSurfaceTextLayout {
    /// <summary>The most surface text slots one program's paints name (<c>SDF_MAX_SURFACE_TEXTS</c>).</summary>
    public const int MaxTexts = 256;
    /// <summary>The most glyph quads one slot draws; a longer text draws its first this many. The slots share one pool
    /// (<see cref="PoolEntries"/>), so a slot's run is as long as its text, not this cap.</summary>
    public const int MaxGlyphs = 1024;
    /// <summary>The four-word entries every slot's runs share: room for about a hundred plaque-length texts at once.</summary>
    public const int PoolEntries = 65_536;
    /// <summary>The four-word entries one glyph packs.</summary>
    public const int EntriesPerGlyph = 2;
    /// <summary>The words one glyph packs.</summary>
    public const int WordsPerGlyph = (EntriesPerGlyph * 4);
    /// <summary>The flag of a glyph whose packed ink replaces the paint's own colour (<c>SDF_SURFACE_TEXT_TINT</c>); a
    /// glyph without it draws in the paint's colour at its packed coverage.</summary>
    public const uint TintFlag = 1u;
    // The most grid cells along one axis, so a column or row count fits sixteen bits.
    private const int MaxGridAxis = 256;
    // The most index entries one grid holds, so an index fits sixteen bits.
    private const int MaxIndices = ushort.MaxValue;
    // A glyph's cell range widens by this share of a cell on each side, so a pixel on a cell boundary, rounded either way
    // on the device, still finds the glyph.
    private const float CellSlack = (1f / 1024f);

    /// <summary>Packs a slot's run: <paramref name="glyphWords"/> followed by the grid that indexes them, in the layout
    /// the remarks state.</summary>
    /// <param name="glyphWords">The packed glyphs, <see cref="WordsPerGlyph"/> words each; at least one and at most
    /// <see cref="MaxGlyphs"/>.</param>
    /// <param name="grid">The grid's columns in the low sixteen bits and rows in the high sixteen, the descriptor's last
    /// word.</param>
    /// <returns>The run's words, a whole number of four-word entries.</returns>
    /// <exception cref="ArgumentException"><paramref name="glyphWords"/> is empty, longer than <see cref="MaxGlyphs"/>
    /// glyphs, or not a whole number of glyphs.</exception>
    public static uint[] IndexedEntries(ReadOnlySpan<uint> glyphWords, out uint grid) {
        var glyphs = (glyphWords.Length / WordsPerGlyph);

        if ((glyphs == 0) || (glyphs > MaxGlyphs) || ((glyphs * WordsPerGlyph) != glyphWords.Length)) {
            throw new ArgumentException(message: $"A surface text packs 1..{MaxGlyphs} glyphs of {WordsPerGlyph} words; got {glyphWords.Length} words.", paramName: nameof(glyphWords));
        }

        // The grid spans the glyphs' union, its cells sized to the mean glyph, so a cell meets about one glyph.
        var low = new Vector2(value: float.PositiveInfinity);
        var high = new Vector2(value: float.NegativeInfinity);
        var meanSize = Vector2.Zero;

        for (var glyph = 0; (glyph < glyphs); glyph++) {
            var (glyphLow, glyphHigh) = Quad(glyphWords: glyphWords, glyph: glyph);

            low = Vector2.Min(value1: low, value2: glyphLow);
            high = Vector2.Max(value1: high, value2: glyphHigh);
            meanSize += Vector2.Max(value1: (glyphHigh - glyphLow), value2: Vector2.Zero);
        }

        var extent = Vector2.Max(value1: (high - low), value2: new Vector2(value: 1.0e-6f));

        meanSize = Vector2.Max(value1: (meanSize / glyphs), value2: (extent / MaxGridAxis));

        var columns = Math.Clamp(value: (int)MathF.Ceiling(x: (extent.X / meanSize.X)), min: 1, max: MaxGridAxis);
        var rows = Math.Clamp(value: (int)MathF.Ceiling(x: (extent.Y / meanSize.Y)), min: 1, max: MaxGridAxis);

        while (true) {
            var perUnit = new Vector2(x: (columns / extent.X), y: (rows / extent.Y));
            var cells = (columns * rows);
            var counts = new int[(cells + 1)];
            var total = 0L;

            for (var glyph = 0; (glyph < glyphs); glyph++) {
                var (x0, y0, x1, y1) = CellRange(glyphWords: glyphWords, glyph: glyph, low: low, perUnit: perUnit, columns: columns, rows: rows);

                total += (((long)((x1 - x0) + 1)) * ((y1 - y0) + 1));
                for (var y = y0; (y <= y1); y++) {
                    for (var x = x0; (x <= x1); x++) { counts[((y * columns) + x)]++; }
                }
            }
            // An index list too long for sixteen bits halves the grid until it fits; a single cell lists each glyph once.
            if ((total > MaxIndices) && (cells > 1)) {
                columns = Math.Max(val1: 1, val2: (columns / 2));
                rows = Math.Max(val1: 1, val2: (rows / 2));
                continue;
            }

            // Counts become each cell's first index (an exclusive prefix sum), then the glyphs are scattered by cell.
            var starts = new int[(cells + 1)];

            for (var cell = 0; (cell < cells); cell++) { starts[(cell + 1)] = (starts[cell] + counts[cell]); }

            var indices = new ushort[(int)total];
            var cursor = (int[])starts.Clone();

            for (var glyph = 0; (glyph < glyphs); glyph++) {
                var (x0, y0, x1, y1) = CellRange(glyphWords: glyphWords, glyph: glyph, low: low, perUnit: perUnit, columns: columns, rows: rows);

                for (var y = y0; (y <= y1); y++) {
                    for (var x = x0; (x <= x1); x++) { indices[cursor[((y * columns) + x)]++] = (ushort)glyph; }
                }
            }

            var cellEntries = ((cells + 3) / 4);
            var indexEntries = ((indices.Length + 7) / 8);
            var words = new uint[(glyphWords.Length + ((1 + cellEntries + indexEntries) * 4))];
            var header = glyphWords.Length;
            var cellBase = (header + 4);
            var indexBase = (cellBase + (cellEntries * 4));

            glyphWords.CopyTo(destination: words);
            words[(header + 0)] = BitConverter.SingleToUInt32Bits(value: low.X);
            words[(header + 1)] = BitConverter.SingleToUInt32Bits(value: low.Y);
            words[(header + 2)] = BitConverter.SingleToUInt32Bits(value: perUnit.X);
            words[(header + 3)] = BitConverter.SingleToUInt32Bits(value: perUnit.Y);
            for (var cell = 0; (cell < cells); cell++) {
                words[(cellBase + cell)] = (((uint)starts[cell]) | (((uint)counts[cell]) << 16));
            }
            for (var index = 0; (index < indices.Length); index++) {
                words[(indexBase + (index >> 1))] |= (((uint)indices[index]) << ((index & 1) * 16));
            }
            grid = (((uint)columns) | (((uint)rows) << 16));

            return words;
        }
    }

    private static (Vector2 Low, Vector2 High) Quad(ReadOnlySpan<uint> glyphWords, int glyph) {
        var at = (glyph * WordsPerGlyph);

        return (
            new Vector2(x: BitConverter.UInt32BitsToSingle(value: glyphWords[at]), y: BitConverter.UInt32BitsToSingle(value: glyphWords[(at + 1)])),
            new Vector2(x: BitConverter.UInt32BitsToSingle(value: glyphWords[(at + 2)]), y: BitConverter.UInt32BitsToSingle(value: glyphWords[(at + 3)]))
        );
    }
    // The inclusive cells a glyph's quad overlaps, widened by the slack and clamped to the grid; an inverted quad (a glyph
    // scaled to nothing) still takes the cell its low corner falls in, and the shader's rectangle test rejects it.
    private static (int X0, int Y0, int X1, int Y1) CellRange(ReadOnlySpan<uint> glyphWords, int glyph, Vector2 low, Vector2 perUnit, int columns, int rows) {
        var (glyphLow, glyphHigh) = Quad(glyphWords: glyphWords, glyph: glyph);
        var from = (((glyphLow - low) * perUnit) - new Vector2(value: CellSlack));
        var to = (((Vector2.Max(value1: glyphLow, value2: glyphHigh) - low) * perUnit) + new Vector2(value: CellSlack));
        var x0 = Math.Clamp(value: (int)MathF.Floor(x: from.X), min: 0, max: (columns - 1));
        var y0 = Math.Clamp(value: (int)MathF.Floor(x: from.Y), min: 0, max: (rows - 1));
        var x1 = Math.Clamp(value: (int)MathF.Floor(x: to.X), min: x0, max: (columns - 1));
        var y1 = Math.Clamp(value: (int)MathF.Floor(x: to.Y), min: y0, max: (rows - 1));

        return (x0, y0, x1, y1);
    }
}
