using Puck.Text;

namespace Puck.Demo.Text;

/// <summary>
/// The ONE shared SDF glyph atlas (<see cref="SharedGlyphAtlas"/>) flattened into a per-glyph, contiguous
/// signed-distance cell pack — the form the 2D overlay surfaces carry in their single storage buffer (one RGBA texel
/// per <see cref="uint"/>, little-endian R|G|B|A) so they sample the shared field with no second texture binding. The
/// shared atlas is a uniform monospace grid (one cell per printable-ASCII glyph), so each glyph's cell is copied out
/// into a tightly packed block; a shader reconstructs each edge with per-channel bilinear sampling + MEDIAN-OF-3 + a
/// screenPxRange coverage ramp. Carrying all four channels is what graduates the overlays to true multi-channel
/// reconstruction: a replicated single-channel atlas medians to exactly its own value (bit-identical to the old
/// alpha-only decode), while a true MTSDF atlas medians to sharp corners. Geometry is different law — the world-glyph
/// op keeps marching the TRUE alpha channel (median is only C0 at channel-crossover lines and must never be marched).
/// This is the neutral packer BOTH the on-screen console and the binding bar share, so their glyph source is literally
/// one atlas.
/// </summary>
/// <remarks>
/// <see cref="TryCreate"/> returns <see langword="null"/> when the shared atlas is unavailable (non-Windows / no
/// suitable font). The pack is DETERMINISTIC — a pure function of the shared atlas, itself a pure function of the
/// installed font + charset.
/// </remarks>
internal sealed class SharedGlyphSdfPack {
    /// <summary>The first code point in the pack (space).</summary>
    public const int FirstChar = 0x20;
    /// <summary>The number of glyphs (printable ASCII 0x20–0x7E).</summary>
    public const int GlyphCount = (0x7F - 0x20);

    private readonly uint[] m_packedSdf;

    private SharedGlyphSdfPack(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf) {
        AtlasCellHeight = atlasCellHeight;
        AtlasCellWidth = atlasCellWidth;
        DistanceRange = distanceRange;
        m_packedSdf = packedSdf;
    }

    /// <summary>The source cell height, in atlas texels — the vertical extent of one glyph's block in <see cref="PackedSdf"/>.</summary>
    public int AtlasCellHeight { get; }
    /// <summary>The source cell width, in atlas texels — the horizontal stride of one glyph's block in <see cref="PackedSdf"/>.</summary>
    public int AtlasCellWidth { get; }
    /// <summary>The signed-distance band width, in atlas texels — the screenPxRange numerator.</summary>
    public float DistanceRange { get; }
    /// <summary>The per-glyph contiguous SDF texels, one RGBA texel per <see cref="uint"/> (little-endian R|G|B|A). Word
    /// index of glyph g's atlas texel (x, y) is <c>((g * AtlasCellWidth * AtlasCellHeight) + (y * AtlasCellWidth) + x)</c>.</summary>
    public IReadOnlyList<uint> PackedSdf => m_packedSdf;
    /// <summary>The packed SDF words as a span — the storage-buffer upload view.</summary>
    public ReadOnlySpan<uint> SdfWords => m_packedSdf;

    /// <summary>Flattens the shared SDF atlas into the per-glyph cell pack, or returns <see langword="null"/> when no
    /// shared atlas is available (non-Windows / no suitable font).</summary>
    public static SharedGlyphSdfPack? TryCreate() {
        if (SharedGlyphAtlas.MonoFont is not { ImageData: { } image } font) {
            return null;
        }

        // Probe the first glyph that HAS atlas bounds for the uniform cell size — SPACE (the old probe) carries no
        // bounds in the committed atlas (no ink, no cell); boundless glyphs stay blank cells in the pack.
        FontAtlasBounds? probed = null;

        for (var unicode = FirstChar; ((unicode < (FirstChar + GlyphCount)) && (probed is null)); unicode++) {
            if (font.TryGetGlyph(unicode: unicode, glyph: out var probe) && (probe.AtlasBounds is { } bounds)) {
                probed = bounds;
            }
        }

        if (probed is not { } probeBounds) {
            return null;
        }

        var atlasCellWidth = Math.Max(val1: 1, val2: (int)MathF.Round(x: (probeBounds.Right - probeBounds.Left)));
        var atlasCellHeight = Math.Max(val1: 1, val2: (int)MathF.Round(x: (probeBounds.Bottom - probeBounds.Top)));
        var cellStride = (atlasCellWidth * atlasCellHeight);
        var packedSdf = new uint[(GlyphCount * cellStride)];
        var pixels = image.RgbaPixels;
        var imageWidth = image.Width;
        var imageHeight = image.Height;

        for (var index = 0; (index < GlyphCount); index++) {
            if ((!font.TryGetGlyph(unicode: (FirstChar + index), glyph: out var glyph)) ||
                (glyph.AtlasBounds is not { } bounds)) {
                continue;
            }

            var left = (int)MathF.Round(x: bounds.Left);
            var top = (int)MathF.Round(x: bounds.Top);
            var glyphBase = (index * cellStride);

            for (var y = 0; (y < atlasCellHeight); y++) {
                var sourceY = Math.Clamp(value: (top + y), max: (imageHeight - 1), min: 0);

                for (var x = 0; (x < atlasCellWidth); x++) {
                    var sourceX = Math.Clamp(value: (left + x), max: (imageWidth - 1), min: 0);
                    var sourceBase = (((sourceY * imageWidth) + sourceX) * 4);

                    // All four channels ride along (little-endian R|G|B|A): the overlays median RGB at shade time,
                    // and the runtime exact-EDT atlas replicates its single channel so its median IS the old alpha.
                    packedSdf[((glyphBase + (y * atlasCellWidth)) + x)] =
                        pixels[sourceBase]
                        | ((uint)pixels[(sourceBase + 1)] << 8)
                        | ((uint)pixels[(sourceBase + 2)] << 16)
                        | ((uint)pixels[(sourceBase + 3)] << 24);
                }
            }
        }

        return new SharedGlyphSdfPack(
            atlasCellHeight: atlasCellHeight,
            atlasCellWidth: atlasCellWidth,
            distanceRange: font.DistanceRange,
            packedSdf: packedSdf
        );
    }

    /// <summary>The glyph index of a code point within this pack, or -1 when it is outside the printable-ASCII range.</summary>
    public static int GlyphIndex(int codePoint) =>
        (((codePoint >= FirstChar) && (codePoint < (FirstChar + GlyphCount)))
            ? (codePoint - FirstChar)
            : -1);
}
