using System.Numerics;

namespace Puck.Demo.Forge.Bake;

/// <summary>One view's expanded preview cell: the quantized result back in RGB (palette colours expanded 5→8 bits),
/// with the transparency mask so a sprite cell composes over the checkerboard.</summary>
/// <param name="Rgba">The cell pixels (RGBA8, alpha 255).</param>
/// <param name="Mask">The foreground mask (sprites), or null (backgrounds, fully opaque).</param>
/// <param name="Width">The cell width.</param>
/// <param name="Height">The cell height.</param>
internal sealed record PreviewCell(byte[] Rgba, bool[]? Mask, int Width, int Height);

/// <summary>
/// Composes a bake's quantized views into ONE preview image — the exact pixels the hardware would show, laid out as
/// a grid (a background is its single 160×144 view; a sprite is a facings-down/poses-across grid on a checkerboard).
/// Overlay mode 1 appends a palette strip + warning tick row; mode 2 additionally rules the 8×8 tile grid.
/// </summary>
internal static class PreviewComposer {
    private const int SwatchEdge = 6;
    private const int StripHeight = (SwatchEdge + 4);
    private const byte CheckerLight = 0xB4;
    private const byte CheckerDark = 0x8C;

    /// <summary>Composes the preview.</summary>
    /// <param name="cells">The per-view preview cells, in plan view order.</param>
    /// <param name="rows">The grid row count (4 facings for a sprite; 1 for a background).</param>
    /// <param name="diagnostics">The bake diagnostics (the overlay reads palettes + warnings).</param>
    /// <param name="overlayMode">0 = bare, 1 = palette strip + warning ticks, 2 = also rule the tile grid.</param>
    /// <returns>The composed image, its extent, and its average colour.</returns>
    public static (byte[] Rgba, int Width, int Height, Vector3 AverageColor) Compose(IReadOnlyList<PreviewCell> cells, int rows, BakeDiagnostics diagnostics, int overlayMode) {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var columns = Math.Max(val1: 1, val2: ((cells.Count + rows) - 1) / Math.Max(val1: 1, val2: rows));
        var cellWidth = ((cells.Count > 0) ? cells[0].Width : 8);
        var cellHeight = ((cells.Count > 0) ? cells[0].Height : 8);
        var width = (columns * cellWidth);
        var gridHeight = (rows * cellHeight);
        var height = (gridHeight + ((overlayMode >= 1) ? StripHeight : 0));
        var rgba = new byte[width * height * 4];

        for (var index = 0; (index < cells.Count); index++) {
            PaintCell(cell: cells[index], destination: rgba, destinationWidth: width, originX: ((index % columns) * cellWidth), originY: ((index / columns) * cellHeight));
        }

        if (overlayMode >= 2) {
            RuleTileGrid(rgba: rgba, width: width, gridHeight: gridHeight);
        }

        if (overlayMode >= 1) {
            PaintStrip(diagnostics: diagnostics, rgba: rgba, stripTop: gridHeight, width: width);
        }

        return (rgba, width, height, Average(rgba: rgba));
    }

    /// <summary>Expands one quantized view into its preview cell: each index through its tile's palette, transparent
    /// pixels flagged for the checkerboard.</summary>
    /// <param name="view">The quantized view.</param>
    /// <param name="palettes">The fitted palettes (display order, as the fitter emitted them).</param>
    /// <param name="transparentSlotReserved">Whether index 0 is the transparent OBJ slot (indices 1.. map onto the
    /// palette's colours).</param>
    /// <returns>The preview cell.</returns>
    public static PreviewCell ExpandView(QuantizedView view, IReadOnlyList<ushort[]> palettes, bool transparentSlotReserved) {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(palettes);

        var rgba = new byte[view.Width * view.Height * 4];
        var tilesWide = (view.Width / 8);

        for (var y = 0; (y < view.Height); y++) {
            for (var x = 0; (x < view.Width); x++) {
                var pixel = ((y * view.Width) + x);
                var index = view.Indices[pixel];
                var offset = (pixel * 4);

                if (transparentSlotReserved && (index == 0)) {
                    rgba[offset + 3] = 0xFF;

                    continue;
                }

                var palette = palettes[view.TilePalettes[((y / 8) * tilesWide) + (x / 8)]];
                var colourIndex = (transparentSlotReserved ? (index - 1) : index);
                var colour = palette[Math.Clamp(value: colourIndex, max: (palette.Length - 1), min: 0)];

                rgba[offset] = BakeColor.Expand8(value5: BakeColor.Channel(channel: 0, colour: colour));
                rgba[offset + 1] = BakeColor.Expand8(value5: BakeColor.Channel(channel: 1, colour: colour));
                rgba[offset + 2] = BakeColor.Expand8(value5: BakeColor.Channel(channel: 2, colour: colour));
                rgba[offset + 3] = 0xFF;
            }
        }

        return new PreviewCell(Height: view.Height, Mask: view.Mask, Rgba: rgba, Width: view.Width);
    }

    private static void PaintCell(PreviewCell cell, byte[] destination, int destinationWidth, int originX, int originY) {
        for (var y = 0; (y < cell.Height); y++) {
            for (var x = 0; (x < cell.Width); x++) {
                var pixel = ((y * cell.Width) + x);
                var source = (pixel * 4);
                var target = ((((originY + y) * destinationWidth) + (originX + x)) * 4);

                if (cell.Mask is { } mask && !mask[pixel]) {
                    // The classic transparency checkerboard, in 4×4 blocks — position-based, deterministic.
                    var shade = (((((originX + x) / 4) + ((originY + y) / 4)) % 2) == 0) ? CheckerLight : CheckerDark;

                    destination[target] = shade;
                    destination[target + 1] = shade;
                    destination[target + 2] = shade;
                } else {
                    destination[target] = cell.Rgba[source];
                    destination[target + 1] = cell.Rgba[source + 1];
                    destination[target + 2] = cell.Rgba[source + 2];
                }

                destination[target + 3] = 0xFF;
            }
        }
    }

    // Overlay 2: darken every 8th row/column over the grid area so tile boundaries read at a glance.
    private static void RuleTileGrid(byte[] rgba, int width, int gridHeight) {
        for (var y = 0; (y < gridHeight); y++) {
            for (var x = 0; (x < width); x++) {
                if (((x % 8) != 0) && ((y % 8) != 0)) {
                    continue;
                }

                var offset = (((y * width) + x) * 4);

                rgba[offset] = (byte)((rgba[offset] * 3) / 4);
                rgba[offset + 1] = (byte)((rgba[offset + 1] * 3) / 4);
                rgba[offset + 2] = (byte)((rgba[offset + 2] * 3) / 4);
            }
        }
    }

    // Overlay 1: the palette strip (four swatches per palette, a gap between palettes) and, right-aligned, one red
    // tick per warning.
    private static void PaintStrip(BakeDiagnostics diagnostics, byte[] rgba, int stripTop, int width) {
        var x = 2;

        for (var colour = 0; (colour < diagnostics.Palettes.Count); colour++) {
            if ((x + SwatchEdge) > width) {
                break;
            }

            PaintSwatch(colour: diagnostics.Palettes[colour], rgba: rgba, width: width, x: x, y: (stripTop + 2));
            x += (SwatchEdge + (((colour % 4) == 3) ? 3 : 1));
        }

        var tickX = (width - 2 - SwatchEdge);

        for (var warning = 0; (warning < diagnostics.Warnings.Count); warning++) {
            if (tickX < x) {
                break;
            }

            PaintSwatch(colour: 0xE03028u, rgba: rgba, width: width, x: tickX, y: (stripTop + 2));
            tickX -= (SwatchEdge + 2);
        }
    }

    private static void PaintSwatch(uint colour, byte[] rgba, int width, int x, int y) {
        for (var dy = 0; (dy < SwatchEdge); dy++) {
            for (var dx = 0; (dx < SwatchEdge); dx++) {
                var offset = ((((y + dy) * width) + (x + dx)) * 4);

                rgba[offset] = (byte)((colour >> 16) & 0xFF);
                rgba[offset + 1] = (byte)((colour >> 8) & 0xFF);
                rgba[offset + 2] = (byte)(colour & 0xFF);
                rgba[offset + 3] = 0xFF;
            }
        }
    }

    private static Vector3 Average(byte[] rgba) {
        if (rgba.Length == 0) {
            return Vector3.Zero;
        }

        var sums = Vector3.Zero;
        var pixels = (rgba.Length / 4);

        for (var offset = 0; (offset < rgba.Length); offset += 4) {
            sums += new Vector3(rgba[offset], rgba[offset + 1], rgba[offset + 2]);
        }

        return (sums / (pixels * 255f));
    }
}
