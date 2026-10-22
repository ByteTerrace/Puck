using System.Globalization;
using Puck.Assets;

namespace Puck.Cli.Motion;

/// <summary>One frame of a contact sheet: which still and camera it shows and where it and its label are drawn.</summary>
/// <param name="Frame">The index of the still, in capture order.</param>
/// <param name="Camera">The index of the camera, in row order.</param>
/// <param name="X">The left edge of the image, in pixels.</param>
/// <param name="Y">The top edge of the image, in pixels, below its label.</param>
/// <param name="LabelY">The top edge of the label strip above the image, in pixels.</param>
public sealed record MotionSheetCell(int Frame, int Camera, int X, int Y, int LabelY);
/// <summary>The geometry of a contact sheet: its size and every cell.</summary>
/// <param name="Width">The sheet's width, in pixels.</param>
/// <param name="Height">The sheet's height, in pixels.</param>
/// <param name="CellWidth">The width of each image, in pixels.</param>
/// <param name="CellHeight">The height of each image, in pixels.</param>
/// <param name="LabelHeight">The height of the label strip above each image, in pixels.</param>
/// <param name="Cells">The cells, by frame and then by camera.</param>
public sealed record MotionSheetLayout(int Width, int Height, int CellWidth, int CellHeight, int LabelHeight, IReadOnlyList<MotionSheetCell> Cells) {
    /// <summary>The pixels between neighbouring cells and around the sheet's edge.</summary>
    public const int Gutter = 6;

    /// <summary>Lays frames out left to right in bands of at most <paramref name="columns"/>, each band one row per camera,
    /// so a camera's frames read in order across the sheet and its next band starts below the last camera's row.</summary>
    /// <param name="frames">How many stills the sheet shows.</param>
    /// <param name="cameras">How many cameras each still has.</param>
    /// <param name="columns">The most frames in a band.</param>
    /// <param name="cellWidth">The width each image is scaled to, in pixels.</param>
    /// <param name="cellHeight">The height each image is scaled to, in pixels.</param>
    /// <param name="labelHeight">The height of the label strip above each image, in pixels.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count or a size is less than one.</exception>
    public static MotionSheetLayout Plan(int frames, int cameras, int columns, int cellWidth, int cellHeight, int labelHeight) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: frames, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: cameras, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: columns, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: cellWidth, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: cellHeight, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: labelHeight, other: 1);

        var across = Math.Min(val1: frames, val2: columns);
        var bands = (((frames + columns) - 1) / columns);
        var rowHeight = (labelHeight + cellHeight);
        var bandHeight = ((cameras * rowHeight) + Gutter);
        var cells = new List<MotionSheetCell>(capacity: (frames * cameras));

        for (var frame = 0; (frame < frames); frame++) {
            for (var camera = 0; (camera < cameras); camera++) {
                var top = ((Gutter + ((frame / columns) * bandHeight)) + (camera * rowHeight));

                cells.Add(item: new MotionSheetCell(
                    Camera: camera,
                    Frame: frame,
                    LabelY: top,
                    X: (Gutter + ((frame % columns) * (cellWidth + Gutter))),
                    Y: (top + labelHeight)
                ));
            }
        }

        return new MotionSheetLayout(
            CellHeight: cellHeight,
            CellWidth: cellWidth,
            Cells: cells,
            Height: (Gutter + (bands * bandHeight)),
            LabelHeight: labelHeight,
            Width: (Gutter + (across * (cellWidth + Gutter)))
        );
    }
}
/// <summary>An RGBA image decoded from a still, or composed into a sheet.</summary>
/// <param name="Width">The width, in pixels.</param>
/// <param name="Height">The height, in pixels.</param>
/// <param name="Rgba">The pixels, four bytes each, rows top to bottom.</param>
public sealed record MotionImage(int Width, int Height, byte[] Rgba);
/// <summary>How two same-sized images differ.</summary>
/// <param name="Count">The pixels where any channel differs.</param>
/// <param name="Max">The largest difference of any channel, from 0 to 255.</param>
/// <param name="Mean">The mean difference over every channel of every pixel.</param>
public sealed record MotionPixelDifference(long Count, int Max, double Mean);
/// <summary>Reads, scales, labels and writes the images <c>puck motion</c> composes, in C# with no external tool.</summary>
public static class MotionSheets {
    private static readonly byte[] LabelBackground = [24, 26, 32, 255];
    private static readonly byte[] LabelText = [236, 238, 242, 255];
    private static readonly byte[] SheetBackground = [12, 13, 16, 255];

    /// <summary>Reads a PNG.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The image.</returns>
    public static MotionImage Read(string path) {
        var image = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: path));

        return new MotionImage(Height: image.Height, Rgba: image.RgbaPixels, Width: image.Width);
    }
    /// <summary>Writes a PNG.</summary>
    /// <param name="path">The file.</param>
    /// <param name="image">The image.</param>
    public static void Write(string path, MotionImage image) => PngEncoder.Write(height: image.Height, path: path, rgba: image.Rgba, width: image.Width);
    /// <summary>Compares two images pixel by pixel.</summary>
    /// <param name="left">The first image.</param>
    /// <param name="right">The second image.</param>
    /// <returns>The difference, or <see langword="null"/> when the images differ in size.</returns>
    public static MotionPixelDifference? Compare(MotionImage left, MotionImage right) {
        if ((left.Width != right.Width) || (left.Height != right.Height)) {
            return null;
        }

        var count = 0L;
        var max = 0;
        var sum = 0L;

        for (var pixel = 0; (pixel < (left.Width * left.Height)); pixel++) {
            var differs = false;

            for (var channel = 0; (channel < 4); channel++) {
                var delta = Math.Abs(value: (left.Rgba[((pixel * 4) + channel)] - right.Rgba[((pixel * 4) + channel)]));

                sum += delta;
                max = Math.Max(val1: max, val2: delta);
                differs |= (delta != 0);
            }

            if (differs) {
                count++;
            }
        }

        return new MotionPixelDifference(Count: count, Max: max, Mean: (sum / ((4d * left.Width) * left.Height)));
    }
    /// <summary>Scales an image down to a width, averaging the source pixels each destination pixel covers.</summary>
    /// <param name="source">The image.</param>
    /// <param name="width">The width to scale to; the height keeps the aspect.</param>
    /// <returns>The scaled image.</returns>
    public static MotionImage ScaleToWidth(MotionImage source, int width) {
        var height = Math.Max(val1: 1, val2: ((int)Math.Round(a: ((((double)source.Height) * width) / source.Width))));
        var rgba = new byte[((width * height) * 4)];

        for (var y = 0; (y < height); y++) {
            var top = ((y * source.Height) / height);
            var bottom = Math.Max(val1: (top + 1), val2: (((y + 1) * source.Height) / height));

            for (var x = 0; (x < width); x++) {
                var left = ((x * source.Width) / width);
                var right = Math.Max(val1: (left + 1), val2: (((x + 1) * source.Width) / width));
                var count = ((right - left) * (bottom - top));
                var sums = new int[4];

                for (var row = top; (row < bottom); row++) {
                    for (var column = left; (column < right); column++) {
                        for (var channel = 0; (channel < 4); channel++) {
                            sums[channel] += source.Rgba[((((row * source.Width) + column) * 4) + channel)];
                        }
                    }
                }

                for (var channel = 0; (channel < 4); channel++) {
                    rgba[((((y * width) + x) * 4) + channel)] = ((byte)((sums[channel] + (count / 2)) / count));
                }
            }
        }

        return new MotionImage(Height: height, Rgba: rgba, Width: width);
    }
    /// <summary>Composes a contact sheet.</summary>
    /// <param name="layout">The geometry, from <see cref="MotionSheetLayout.Plan"/>.</param>
    /// <param name="frames">The images, indexed <c>[frame][camera]</c> and each already scaled to the layout's cell size.</param>
    /// <param name="labels">The label for each cell, indexed <c>[frame][camera]</c>.</param>
    /// <returns>The sheet.</returns>
    public static MotionImage Compose(MotionSheetLayout layout, IReadOnlyList<IReadOnlyList<MotionImage>> frames, IReadOnlyList<IReadOnlyList<string>> labels) {
        var sheet = new MotionImage(Height: layout.Height, Rgba: new byte[((layout.Width * layout.Height) * 4)], Width: layout.Width);

        Fill(image: sheet, color: SheetBackground, height: layout.Height, left: 0, top: 0, width: layout.Width);

        foreach (var cell in layout.Cells) {
            var image = frames[cell.Frame][cell.Camera];

            Fill(image: sheet, color: LabelBackground, height: layout.LabelHeight, left: cell.X, top: cell.LabelY, width: layout.CellWidth);
            DrawText(color: LabelText, image: sheet, left: (cell.X + 4), text: labels[cell.Frame][cell.Camera], top: (cell.LabelY + 3), height: (layout.LabelHeight - 6));

            for (var row = 0; ((row < image.Height) && (row < layout.CellHeight)); row++) {
                Buffer.BlockCopy(
                    count: (Math.Min(val1: image.Width, val2: layout.CellWidth) * 4),
                    dst: sheet.Rgba,
                    dstOffset: ((((cell.Y + row) * sheet.Width) + cell.X) * 4),
                    src: image.Rgba,
                    srcOffset: ((row * image.Width) * 4)
                );
            }
        }

        return sheet;
    }
    /// <summary>Returns the label strip height that fits one line of text.</summary>
    public static int LabelHeightFor(int scale) => ((BitmapFont.GlyphSize * scale) + 6);
    /// <summary>Returns how many characters of text fit in a width at a scale.</summary>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="scale">The integer glyph scale.</param>
    /// <returns>The character count.</returns>
    public static int CharactersThatFit(int width, int scale) => Math.Max(val1: 0, val2: ((width - 8) / (BitmapFont.GlyphSize * scale)));
    /// <summary>Formats the label of a still: the camera, the tick offset from the scenario's tick 0 and the absolute tick.</summary>
    /// <param name="camera">The camera's name; a leading <c>motion-</c> is dropped.</param>
    /// <param name="offset">The capture tick, counted from the scenario's tick 0.</param>
    /// <param name="tick">The absolute World tick.</param>
    /// <returns>The label.</returns>
    public static string Label(string camera, int offset, ulong tick) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{(camera.StartsWith(comparisonType: StringComparison.Ordinal, value: "motion-") ? camera["motion-".Length..] : camera)} t+{offset} tick {tick}"
    );

    private static void Fill(MotionImage image, byte[] color, int left, int top, int width, int height) {
        for (var row = top; (row < (top + height)); row++) {
            for (var column = left; (column < (left + width)); column++) {
                Buffer.BlockCopy(count: 4, dst: image.Rgba, dstOffset: (((row * image.Width) + column) * 4), src: color, srcOffset: 0);
            }
        }
    }
    private static void DrawText(MotionImage image, string text, int left, int top, int height, byte[] color) {
        var scale = Math.Max(val1: 1, val2: (height / BitmapFont.GlyphSize));
        var advance = (BitmapFont.GlyphSize * scale);

        for (var index = 0; (index < text.Length); index++) {
            var glyph = BitmapFont.Glyph(value: text[index]);

            for (var gy = 0; (gy < BitmapFont.GlyphSize); gy++) {
                for (var gx = 0; (gx < BitmapFont.GlyphSize); gx++) {
                    if (!BitmapFont.IsSet(column: gx, glyph: glyph, row: gy)) {
                        continue;
                    }

                    for (var sy = 0; (sy < scale); sy++) {
                        for (var sx = 0; (sx < scale); sx++) {
                            var x = (((left + (index * advance)) + (gx * scale)) + sx);
                            var y = ((top + (gy * scale)) + sy);

                            if ((x < 0) || (y < 0) || (x >= image.Width) || (y >= image.Height)) {
                                continue;
                            }

                            Buffer.BlockCopy(count: 4, dst: image.Rgba, dstOffset: (((y * image.Width) + x) * 4), src: color, srcOffset: 0);
                        }
                    }
                }
            }
        }
    }
}
