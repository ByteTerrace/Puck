using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.Demo.DevConsole;

/// <summary>
/// A fixed-cell monospace glyph atlas for the on-screen developer console, rasterized once at startup via GDI+ (the
/// same technique as the reference terminal's SystemDrawing font generator). Printable ASCII (0x20–0x7E) is drawn
/// into uniform cells and the per-pixel coverage is packed four bytes to a <see cref="uint"/> so the console overlay
/// can carry the whole font in its storage buffer — no second texture binding, so the overlay keeps the proven
/// single-sampler shape of the binding bar. GDI+ is Windows-only; <see cref="TryCreate"/> returns
/// <see langword="null"/> anywhere it is unavailable and the console falls back to the terminal.
/// </summary>
internal sealed class ConsoleGlyphFont {
    /// <summary>The first code point in the atlas (space).</summary>
    public const int FirstChar = 0x20;
    /// <summary>The number of glyphs (printable ASCII 0x20–0x7E).</summary>
    public const int GlyphCount = 0x7F - 0x20;

    private ConsoleGlyphFont(int cellWidth, int cellHeight, uint[] packedCoverage) {
        CellHeight = cellHeight;
        CellWidth = cellWidth;
        PackedCoverage = packedCoverage;
    }

    /// <summary>The glyph cell width in pixels.</summary>
    public int CellWidth { get; }
    /// <summary>The glyph cell height in pixels.</summary>
    public int CellHeight { get; }
    /// <summary>The coverage bytes (one per glyph pixel, atlas-row-major per glyph), packed four to a
    /// <see cref="uint"/> (little-endian). Byte index of glyph g's pixel (x, y) is
    /// <c>((g * CellWidth * CellHeight) + (y * CellWidth) + x)</c>.</summary>
    public IReadOnlyList<uint> PackedCoverage { get; }

    /// <summary>Rasterizes the atlas, or returns <see langword="null"/> when GDI+ is unavailable (non-Windows) or the
    /// rasterization throws (no suitable font).</summary>
    /// <param name="cellWidth">The glyph cell width in pixels.</param>
    /// <param name="cellHeight">The glyph cell height in pixels.</param>
    public static ConsoleGlyphFont? TryCreate(int cellWidth = 9, int cellHeight = 16) {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }

        try {
            return BuildWindows(cellWidth: cellWidth, cellHeight: cellHeight);
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[console] glyph atlas unavailable ({exception.Message}); the on-screen console is disabled (the terminal console still works).");

            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static ConsoleGlyphFont BuildWindows(int cellWidth, int cellHeight) {
        var coverage = new byte[ConsoleGlyphFont.GlyphCount * cellWidth * cellHeight];
        // A small canvas per glyph, rendered white on transparent so the alpha channel IS the anti-aliased coverage.
        using var bitmap = new Bitmap(width: cellWidth, height: cellHeight, format: PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image: bitmap);

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // Consolas is the canonical Windows monospace; fall back to the generic monospace family if it is absent.
        using var font = CreateMonospaceFont(pixelSize: (cellHeight * 0.82f));
        // GenericTypographic drops the extra padding DrawString adds, so the glyph sits tight in the cell.
        var format = StringFormat.GenericTypographic;
        var stride = (cellWidth * cellHeight);
        var rowBytes = new byte[cellWidth * cellHeight * 4];

        for (var index = 0; (index < ConsoleGlyphFont.GlyphCount); index++) {
            graphics.Clear(color: Color.Transparent);
            graphics.DrawString(
                brush: Brushes.White,
                font: font,
                format: format,
                point: new PointF(x: 0f, y: 0f),
                s: ((char)(ConsoleGlyphFont.FirstChar + index)).ToString()
            );

            var locked = bitmap.LockBits(rect: new Rectangle(x: 0, y: 0, width: cellWidth, height: cellHeight), flags: ImageLockMode.ReadOnly, format: PixelFormat.Format32bppArgb);

            try {
                Marshal.Copy(source: locked.Scan0, destination: rowBytes, startIndex: 0, length: (locked.Stride * cellHeight));

                for (var y = 0; (y < cellHeight); y++) {
                    for (var x = 0; (x < cellWidth); x++) {
                        // BGRA little-endian: the alpha byte is at +3; it holds the AA coverage of the white glyph.
                        coverage[(index * stride) + (y * cellWidth) + x] = rowBytes[(y * locked.Stride) + (x * 4) + 3];
                    }
                }
            } finally {
                bitmap.UnlockBits(bitmapdata: locked);
            }
        }

        return new ConsoleGlyphFont(cellWidth: cellWidth, cellHeight: cellHeight, packedCoverage: Pack(coverage: coverage));
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
            // Consolas is not installed; fall through to the generic monospace family.
        }

        return new Font(family: FontFamily.GenericMonospace, emSize: pixelSize, style: FontStyle.Regular, unit: GraphicsUnit.Pixel);
    }

    // Packs the coverage bytes four-to-a-uint (little-endian), padding the final word with zeros.
    private static uint[] Pack(byte[] coverage) {
        var packed = new uint[(coverage.Length + 3) / 4];

        for (var index = 0; (index < coverage.Length); index++) {
            packed[index >> 2] |= ((uint)coverage[index] << ((index & 3) * 8));
        }

        return packed;
    }
}
