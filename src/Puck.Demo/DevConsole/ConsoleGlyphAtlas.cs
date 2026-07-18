using Puck.Demo.Text;
using Puck.Demo.Ui;

namespace Puck.Demo.DevConsole;

/// <summary>
/// The on-screen console's view of the ONE shared SDF glyph atlas (<see cref="SharedGlyphSdfPack"/> over
/// <see cref="SharedGlyphAtlas"/>), adapted to the console's fixed cell grid. Where the retired <c>ConsoleGlyphFont</c>
/// rasterized its own GDI+ COVERAGE atlas, this carries the shared exact-EDT SIGNED-DISTANCE pack — the same field the
/// world-glyph op marches and the diegetic UI embosses — so overlay text is one atlas + one <c>TextLayout</c> with
/// every other tier. It adds only the console's ON-SCREEN cell size (chosen to preserve the atlas cell aspect at a
/// target height); the fragment shader reconstructs each edge with bilinear sampling + a screenPxRange coverage ramp,
/// so the LETTER, not the cell, anti-aliases crisply at any overlay scale, and adds an outline band from the same
/// field for readability over bright world content.
/// </summary>
/// <remarks>
/// GDI+ is Windows-only (like the whole demo), so <see cref="TryCreate"/> returns <see langword="null"/> anywhere the
/// shared atlas is unavailable (non-Windows / no suitable font) and the console falls back to the terminal.
/// </remarks>
internal sealed class ConsoleGlyphAtlas {
    /// <summary>The first code point in the pack (space).</summary>
    public const int FirstChar = SharedGlyphSdfPack.FirstChar;
    /// <summary>The number of glyphs (printable ASCII 0x20–0x7E), matching the shared atlas' charset.</summary>
    public const int GlyphCount = SharedGlyphSdfPack.GlyphCount;

    private ConsoleGlyphAtlas(SharedGlyphSdfPack pack, int cellWidth, int cellHeight) {
        CellHeight = cellHeight;
        CellWidth = cellWidth;
        Pack = pack;
    }

    /// <summary>The source cell height, in atlas texels — the vertical extent of one glyph's block in <see cref="PackedSdf"/>.</summary>
    public int AtlasCellHeight => Pack.AtlasCellHeight;
    /// <summary>The source cell width, in atlas texels — the horizontal stride of one glyph's block in <see cref="PackedSdf"/>.</summary>
    public int AtlasCellWidth => Pack.AtlasCellWidth;
    /// <summary>The on-screen glyph cell height, in pixels.</summary>
    public int CellHeight { get; }
    /// <summary>The on-screen glyph cell width, in pixels (chosen to preserve the atlas cell aspect at <see cref="CellHeight"/>).</summary>
    public int CellWidth { get; }
    /// <summary>The signed-distance band width, in atlas texels — the screenPxRange numerator (see the overlay node).</summary>
    public float DistanceRange => Pack.DistanceRange;
    /// <summary>The per-glyph contiguous SDF alpha bytes, packed four to a <see cref="uint"/> (little-endian).</summary>
    public IReadOnlyList<uint> PackedSdf => Pack.PackedSdf;

    private SharedGlyphSdfPack Pack { get; }

    /// <summary>Adapts the shared SDF pack into the console's cell grid, or returns <see langword="null"/> when no
    /// shared atlas is available (non-Windows / no suitable font).</summary>
    /// <param name="targetCellHeight">The on-screen glyph cell height in pixels; the width derives from the atlas cell
    /// aspect. Defaults to <see cref="DesignTokens.Type.TypeMonoLine"/> (the console body/scrollback line height).</param>
    public static ConsoleGlyphAtlas? TryCreate(int targetCellHeight = (int)DesignTokens.Type.TypeMonoLine) {
        if (SharedGlyphSdfPack.TryCreate() is not { } pack) {
            return null;
        }

        var cellHeight = Math.Max(val1: 1, val2: targetCellHeight);
        var cellWidth = Math.Max(val1: 1, val2: (int)MathF.Round(x: ((cellHeight * (float)pack.AtlasCellWidth) / pack.AtlasCellHeight)));

        return new ConsoleGlyphAtlas(cellHeight: cellHeight, cellWidth: cellWidth, pack: pack);
    }
}
