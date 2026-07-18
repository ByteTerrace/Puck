using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Text;

namespace Puck.Demo.Text;

/// <summary>
/// Builds a single-channel SDF font atlas for world text (signage, cabinet marquees, the diegetic UI, engraved
/// lettering) — the <see cref="SdfCoverageAtlas"/> generator fed by the same GDI+/System.Drawing raster path the
/// diegetic terminal's <c>ConsoleGlyphFont</c> uses. Printable ASCII is rasterized into a uniform monospace grid, its
/// per-cell coverage becomes a <see cref="FontAtlas"/>, and the exact-EDT transform turns that into a
/// <see cref="FontAtlasKind.Sdf"/> atlas whose true single-channel distance the <see cref="Puck.SdfVm.SdfShapeType.Glyph"/>
/// primitive marches.
/// </summary>
/// <remarks>
/// GDI+ is Windows-only (like the whole demo), so <see cref="TryBuild"/> returns <see langword="null"/> anywhere it is
/// unavailable and the caller simply declares no world text. The output is DETERMINISTIC: the same installed font +
/// charset + cell size rasterizes to a byte-identical coverage raster, and the transform is a pure function of it, so
/// the same inputs always yield a byte-identical atlas. The higher-fidelity marchable source is the committed
/// MTSDF atlas from the font-atlas bake pipeline (true-distance by construction); this runtime path is the
/// no-toolchain fallback.
/// </remarks>
internal static class GlyphAtlasBuilder {
    private const int FirstChar = 0x20;
    private const int GlyphColumns = 16;
    private const int LastChar = 0x7E;

    /// <summary>Rasterizes printable ASCII into an SDF atlas, or returns <see langword="null"/> when GDI+ is
    /// unavailable (non-Windows) or rasterization throws (no suitable font).</summary>
    /// <param name="fontPixelSize">The em size, in pixels, to rasterize glyphs at (cell size derives from it).</param>
    /// <param name="padding">The empty margin, in pixels, around each glyph cell — kept at least the SDF distance range
    /// so the encoded band never bleeds across a cell boundary.</param>
    /// <param name="distanceRange">The SDF band width, in texels (see <see cref="SdfCoverageAtlas"/>).</param>
    public static FontAtlas? TryBuild(int fontPixelSize = 40, int padding = 8, float distanceRange = SdfCoverageAtlas.DefaultDistanceRange) {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        try {
            return SdfCoverageAtlas.Generate(coverage: BuildCoverage(fontPixelSize: fontPixelSize, padding: padding), distanceRange: distanceRange);
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[glyph-atlas] SDF font atlas unavailable ({exception.Message}); world text is disabled.");

            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static FontAtlas BuildCoverage(int fontPixelSize, int padding) {
        var glyphCount = ((LastChar - FirstChar) + 1);
        var rows = (((glyphCount + GlyphColumns) - 1) / GlyphColumns);

        using var font = CreateMonospaceFont(pixelSize: (fontPixelSize * 0.82f));
        var format = StringFormat.GenericTypographic;
        var family = font.FontFamily;
        var emHeight = family.GetEmHeight(style: FontStyle.Regular);
        var ascentPixels = (int)Math.Ceiling(a: (((double)family.GetCellAscent(style: FontStyle.Regular) / emHeight) * fontPixelSize));
        var descentPixels = (int)Math.Ceiling(a: (((double)family.GetCellDescent(style: FontStyle.Regular) / emHeight) * fontPixelSize));
        var lineHeightPixels = (int)Math.Ceiling(a: (((double)family.GetLineSpacing(style: FontStyle.Regular) / emHeight) * fontPixelSize));

        // A cell wide enough for the widest glyph advance and tall enough for the line box, plus the SDF padding margin.
        var unpaddedCellWidth = MeasureCellWidth(font: font, format: format, fontPixelSize: fontPixelSize);
        var unpaddedCellHeight = Math.Max(val1: lineHeightPixels, val2: fontPixelSize);
        var cellWidth = (unpaddedCellWidth + (padding * 2));
        var cellHeight = (unpaddedCellHeight + (padding * 2));
        var atlasWidth = (cellWidth * GlyphColumns);
        var atlasHeight = (cellHeight * rows);

        using var bitmap = new Bitmap(width: atlasWidth, height: atlasHeight, format: PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image: bitmap);

        graphics.Clear(color: Color.FromArgb(alpha: 0, red: 0, green: 0, blue: 0));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var pixelSize = (float)fontPixelSize;
        var advance = ((float)unpaddedCellWidth / pixelSize);
        var ascender = (ascentPixels / pixelSize);
        var descender = -(descentPixels / pixelSize);
        var lineHeight = (lineHeightPixels / pixelSize);
        var planeLeft = -(padding / pixelSize);
        var planeRight = ((unpaddedCellWidth + padding) / pixelSize);
        var planeTop = (ascender + (padding / pixelSize));
        var planeBottom = (descender - (padding / pixelSize));
        var glyphs = new List<FontAtlasGlyph>(capacity: glyphCount);

        for (var index = 0; (index < glyphCount); index++) {
            var column = (index % GlyphColumns);
            var row = (index / GlyphColumns);
            var cellLeft = (column * cellWidth);
            var cellTop = (row * cellHeight);

            graphics.DrawString(
                brush: Brushes.White,
                font: font,
                format: format,
                s: ((char)(FirstChar + index)).ToString(),
                x: (cellLeft + padding),
                y: (cellTop + padding)
            );

            glyphs.Add(item: new FontAtlasGlyph(
                advance: advance,
                // AtlasBounds (texels, top-down): Bottom = the LARGER row edge of the cell (see FontAtlasBounds).
                atlasBounds: new FontAtlasBounds(Bottom: (cellTop + cellHeight), Left: cellLeft, Right: (cellLeft + cellWidth), Top: cellTop),
                planeBounds: new FontAtlasBounds(Bottom: planeBottom, Left: planeLeft, Right: planeRight, Top: planeTop),
                unicode: (FirstChar + index)
            ));
        }

        return new FontAtlas(
            distanceRange: 1.0f,
            glyphs: glyphs,
            height: atlasHeight,
            imageData: ExtractRgba(bitmap: bitmap),
            imagePath: "demo:glyph-atlas",
            kerningPairs: [],
            kind: FontAtlasKind.SoftMask,
            metrics: new FontAtlasMetrics(
                Ascender: ascender,
                Descender: descender,
                LineHeight: lineHeight,
                UnderlineThickness: (1.0f / pixelSize),
                UnderlineY: (descender * 0.5f)
            ),
            size: pixelSize,
            width: atlasWidth
        );
    }
    [SupportedOSPlatform("windows")]
    private static int MeasureCellWidth(Font font, StringFormat format, int fontPixelSize) {
        using var scratch = new Bitmap(width: 1, height: 1, format: PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image: scratch);

        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var widest = fontPixelSize;

        for (var code = FirstChar; (code <= LastChar); code++) {
            var measured = (int)Math.Ceiling(a: graphics.MeasureString(text: ((char)code).ToString(), font: font, width: int.MaxValue, format: format).Width);

            if (measured > widest) {
                widest = measured;
            }
        }

        return widest;
    }
    [SupportedOSPlatform("windows")]
    private static Font CreateMonospaceFont(float pixelSize) {
        try {
            var font = new Font(familyName: "Consolas", emSize: pixelSize, style: FontStyle.Regular, unit: GraphicsUnit.Pixel);

            if (font.FontFamily.Name.Equals(value: "Consolas", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return font;
            }

            font.Dispose();
        } catch (ArgumentException) {
            // Consolas absent; fall through to the generic monospace family.
        }

        return new Font(family: FontFamily.GenericMonospace, emSize: pixelSize, style: FontStyle.Regular, unit: GraphicsUnit.Pixel);
    }
    [SupportedOSPlatform("windows")]
    private static FontAtlasImageData ExtractRgba(Bitmap bitmap) {
        var rectangle = new Rectangle(x: 0, y: 0, width: bitmap.Width, height: bitmap.Height);
        var locked = bitmap.LockBits(rect: rectangle, flags: ImageLockMode.ReadOnly, format: PixelFormat.Format32bppArgb);

        try {
            var stride = Math.Abs(value: locked.Stride);
            var source = new byte[(stride * bitmap.Height)];

            Marshal.Copy(source: locked.Scan0, destination: source, startIndex: 0, length: source.Length);

            var rgba = new byte[((bitmap.Width * bitmap.Height) * 4)];

            for (var y = 0; (y < bitmap.Height); y++) {
                for (var x = 0; (x < bitmap.Width); x++) {
                    var sourceOffset = ((y * stride) + (x * 4));      // BGRA little-endian
                    var destinationOffset = (((y * bitmap.Width) + x) * 4);

                    rgba[destinationOffset] = source[(sourceOffset + 2)];        // R
                    rgba[(destinationOffset + 1)] = source[(sourceOffset + 1)];  // G
                    rgba[(destinationOffset + 2)] = source[sourceOffset];        // B
                    rgba[(destinationOffset + 3)] = source[(sourceOffset + 3)];  // A (the AA coverage)
                }
            }

            return new FontAtlasImageData(height: bitmap.Height, rgbaPixels: rgba, width: bitmap.Width);
        } finally {
            bitmap.UnlockBits(bitmapdata: locked);
        }
    }
}
