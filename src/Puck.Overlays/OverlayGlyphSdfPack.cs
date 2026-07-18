using Puck.Text;

namespace Puck.Overlays;

/// <summary>
/// A shared SDF glyph atlas flattened into a per-glyph, contiguous signed-distance cell pack — the form every 2D
/// overlay surface carries in its single storage buffer (one RGBA texel per <see cref="uint"/>, little-endian
/// R|G|B|A) so it samples the field with no second texture binding. The source atlas is a uniform monospace grid
/// (one cell per printable-ASCII glyph), so each glyph's cell is copied out into a tightly packed block; a shader
/// reconstructs each edge with per-channel bilinear sampling + MEDIAN-OF-3 + a screenPxRange coverage ramp (see
/// <c>Assets/Shaders/overlay-common.hlsli</c>). Carrying all four channels is what graduates the overlays to true
/// multi-channel reconstruction: a replicated single-channel atlas medians to exactly its own value, while a true
/// MTSDF atlas medians to sharp corners.
/// </summary>
/// <remarks>
/// Lifted from <c>Puck.Demo.Text.SharedGlyphSdfPack</c>, decoupled from the static <c>SharedGlyphAtlas</c>
/// singleton — <see cref="TryCreate"/> is a pure function of whatever <see cref="FontAtlas"/> it is given
/// (typically <see cref="OverlayGlyphAtlasSet.MonoFont"/>), so it returns <see langword="null"/> only when the atlas
/// itself is <see langword="null"/> or carries no usable glyph bounds — never by reaching into a global. ASCII-95
/// (<see cref="FirstChar"/>..<see cref="FirstChar"/>+<see cref="GlyphCount"/>-1) is the documented v1 ceiling; a
/// wider charset is a deferred-ledger item (non-Latin glyph paging), not extended here.
/// </remarks>
public sealed class OverlayGlyphSdfPack {
    /// <summary>The first code point in the pack (space).</summary>
    public const int FirstChar = 0x20;
    /// <summary>The number of glyphs (printable ASCII 0x20-0x7E).</summary>
    public const int GlyphCount = (0x7F - 0x20);

    private readonly uint[] m_packedSdf;

    private OverlayGlyphSdfPack(int atlasCellWidth, int atlasCellHeight, float distanceRange, uint[] packedSdf) {
        AtlasCellHeight = atlasCellHeight;
        AtlasCellWidth = atlasCellWidth;
        DistanceRange = distanceRange;
        m_packedSdf = packedSdf;
    }

    /// <summary>The source cell height, in atlas texels — the vertical extent of one glyph's block in
    /// <see cref="PackedSdf"/>.</summary>
    public int AtlasCellHeight { get; }
    /// <summary>The source cell width, in atlas texels — the horizontal stride of one glyph's block in
    /// <see cref="PackedSdf"/>.</summary>
    public int AtlasCellWidth { get; }
    /// <summary>The signed-distance band width, in atlas texels — the screenPxRange numerator.</summary>
    public float DistanceRange { get; }
    /// <summary>The per-glyph contiguous SDF texels, one RGBA texel per <see cref="uint"/> (little-endian
    /// R|G|B|A). Word index of glyph g's atlas texel (x, y) is
    /// <c>((g * AtlasCellWidth * AtlasCellHeight) + (y * AtlasCellWidth) + x)</c>.</summary>
    public IReadOnlyList<uint> PackedSdf => m_packedSdf;
    /// <summary>The packed SDF words as a span — the storage-buffer upload view.</summary>
    public ReadOnlySpan<uint> SdfWords => m_packedSdf;

    /// <summary>Flattens a shared SDF atlas into the per-glyph cell pack, or returns <see langword="null"/> when
    /// <paramref name="monoFont"/> is <see langword="null"/> or carries no usable glyph bounds.</summary>
    /// <param name="monoFont">The source atlas — a uniform-grid mono atlas (typically
    /// <see cref="OverlayGlyphAtlasSet.MonoFont"/>).</param>
    public static OverlayGlyphSdfPack? TryCreate(FontAtlas? monoFont) {
        if (monoFont is not { ImageData: { } image } font) {
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
                    // and a runtime exact-EDT fallback atlas replicates its single channel so its median IS the
                    // channel value.
                    packedSdf[((glyphBase + (y * atlasCellWidth)) + x)] =
                        pixels[sourceBase]
                        | ((uint)pixels[(sourceBase + 1)] << 8)
                        | ((uint)pixels[(sourceBase + 2)] << 16)
                        | ((uint)pixels[(sourceBase + 3)] << 24);
                }
            }
        }

        return new OverlayGlyphSdfPack(
            atlasCellHeight: atlasCellHeight,
            atlasCellWidth: atlasCellWidth,
            distanceRange: font.DistanceRange,
            packedSdf: packedSdf
        );
    }

    /// <summary>The glyph index of a code point within this pack, or -1 when it is outside the printable-ASCII
    /// range.</summary>
    public static int GlyphIndex(int codePoint) =>
        (((codePoint >= FirstChar) && (codePoint < (FirstChar + GlyphCount)))
            ? (codePoint - FirstChar)
            : -1);
}
