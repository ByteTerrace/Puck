using System.Buffers.Binary;
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
/// <see cref="TryCreate"/> is a pure function of whatever <see cref="FontAtlas"/> it is given
/// (typically <see cref="OverlayGlyphAtlasSet.MonoFont"/>), so it returns <see langword="null"/> only when the atlas
/// itself is <see langword="null"/> or carries no usable glyph bounds — never by reaching into a global. ASCII-95
/// (<see cref="FirstChar"/>..<see cref="FirstChar"/>+<see cref="GlyphCount"/>-1) is the v1 ceiling; a wider charset
/// would page glyphs (non-Latin), not extend this one.
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

        // Probe the first glyph that HAS atlas bounds for the uniform cell size — SPACE carries no bounds in the
        // committed atlas (no ink, no cell); boundless glyphs stay blank cells in the pack.
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

    // ---- the prepacked artifact ---------------------------------------------------------------------------------
    // The pack is ~1.4 MiB of already-flattened cells, but building it from the atlas decodes the WHOLE combined
    // MTSDF PNG (4435x4440 RGBA ≈ 79 MiB, ≥150 MiB transient with the decoder's scanlines) at every startup. The
    // binary artifact below persists the finished pack beside the atlas, keyed by the SHA-256 of the source PNG and
    // mono-layout JSON bytes, so a warm start reads 1.4 MiB and never touches the image decoder. Written on first
    // run (a rebake changes the key and rebuilds); OverlayGlyphAtlasSet.LoadOverlayPack orchestrates.

    // 'P','O','G','P' + format version. Bump the version on any layout change — the key check then misses cleanly.
    private const uint PackMagic = 0x50474F50u;
    private const uint PackVersion = 1u;
    private const int PackHashBytes = 32;
    private const int PackHeaderBytes = ((sizeof(uint) * 5) + sizeof(float) + (PackHashBytes * 2));

    /// <summary>Reads a prepacked artifact, returning <see langword="null"/> when the file is absent, malformed, a
    /// different format version, or keyed to different source bytes (a rebaked atlas).</summary>
    /// <param name="path">The artifact path.</param>
    /// <param name="pngHash">The SHA-256 of the current combined-atlas PNG bytes.</param>
    /// <param name="jsonHash">The SHA-256 of the current mono layout JSON bytes.</param>
    internal static OverlayGlyphSdfPack? TryReadPack(string path, ReadOnlySpan<byte> pngHash, ReadOnlySpan<byte> jsonHash) {
        byte[] bytes;

        try {
            if (!File.Exists(path: path)) {
                return null;
            }

            bytes = File.ReadAllBytes(path: path);
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }

        if (bytes.Length < PackHeaderBytes) {
            return null;
        }

        var span = bytes.AsSpan();

        if ((BinaryPrimitives.ReadUInt32LittleEndian(source: span) != PackMagic) ||
            (BinaryPrimitives.ReadUInt32LittleEndian(source: span[4..]) != PackVersion)) {
            return null;
        }

        var cellWidth = BinaryPrimitives.ReadInt32LittleEndian(source: span[8..]);
        var cellHeight = BinaryPrimitives.ReadInt32LittleEndian(source: span[12..]);
        var distanceRange = BinaryPrimitives.ReadSingleLittleEndian(source: span[16..]);
        var wordCount = BinaryPrimitives.ReadInt32LittleEndian(source: span[20..]);

        if (!span.Slice(start: 24, length: PackHashBytes).SequenceEqual(other: pngHash) ||
            !span.Slice(start: (24 + PackHashBytes), length: PackHashBytes).SequenceEqual(other: jsonHash)) {
            return null;
        }

        if ((cellWidth <= 0) || (cellHeight <= 0) ||
            (wordCount != (GlyphCount * cellWidth * cellHeight)) ||
            (bytes.Length != (PackHeaderBytes + (wordCount * sizeof(uint))))) {
            return null;
        }

        var words = new uint[wordCount];
        var payload = span[PackHeaderBytes..];

        for (var index = 0; (index < wordCount); index++) {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(source: payload[(index * sizeof(uint))..]);
        }

        return new OverlayGlyphSdfPack(
            atlasCellHeight: cellHeight,
            atlasCellWidth: cellWidth,
            distanceRange: distanceRange,
            packedSdf: words
        );
    }

    /// <summary>Writes this pack as the prepacked artifact (atomic temp-then-move, so a concurrent reader never sees
    /// a torn file). A write failure is narrated once and swallowed — the cache is an optimization, never a gate.</summary>
    /// <param name="path">The artifact path.</param>
    /// <param name="pngHash">The SHA-256 of the source combined-atlas PNG bytes.</param>
    /// <param name="jsonHash">The SHA-256 of the source mono layout JSON bytes.</param>
    internal void WritePack(string path, ReadOnlySpan<byte> pngHash, ReadOnlySpan<byte> jsonHash) {
        var bytes = new byte[(PackHeaderBytes + (m_packedSdf.Length * sizeof(uint)))];
        var span = bytes.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(destination: span, value: PackMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: span[4..], value: PackVersion);
        BinaryPrimitives.WriteInt32LittleEndian(destination: span[8..], value: AtlasCellWidth);
        BinaryPrimitives.WriteInt32LittleEndian(destination: span[12..], value: AtlasCellHeight);
        BinaryPrimitives.WriteSingleLittleEndian(destination: span[16..], value: DistanceRange);
        BinaryPrimitives.WriteInt32LittleEndian(destination: span[20..], value: m_packedSdf.Length);
        pngHash.CopyTo(destination: span.Slice(start: 24, length: PackHashBytes));
        jsonHash.CopyTo(destination: span.Slice(start: (24 + PackHashBytes), length: PackHashBytes));

        var payload = span[PackHeaderBytes..];

        for (var index = 0; (index < m_packedSdf.Length); index++) {
            BinaryPrimitives.WriteUInt32LittleEndian(destination: payload[(index * sizeof(uint))..], value: m_packedSdf[index]);
        }

        try {
            var temporary = (path + $".{Environment.ProcessId}.tmp");

            File.WriteAllBytes(path: temporary, bytes: bytes);
            File.Move(sourceFileName: temporary, destFileName: path, overwrite: true);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine(value: $"[Puck.Overlays] could not persist the overlay glyph pack '{path}' ({exception.Message}); the next start decodes the full atlas again.");
        }
    }
}
