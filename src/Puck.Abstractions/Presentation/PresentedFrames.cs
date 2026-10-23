using System.Globalization;
using System.Text;

namespace Puck.Abstractions.Presentation;

/// <summary>How a capture reads the render chain's frames.</summary>
public enum FrameCaptureMode : byte {
    /// <summary>A still: the capture holds presentation from the frame it is armed in until it is served, the renderer
    /// keeps rendering that held frame with no presentation time passing, and temporal views settle or converge over the
    /// held renders before one is read back. Clean and repeatable, and blind to what changes from frame to frame.</summary>
    Settled,
    /// <summary>What the display showed: the capture holds nothing and settles nothing, and reads back each frame the
    /// render chain presents while it is armed, as composed for the display that frame, with the frame's jitter,
    /// temporal history and motion. Shows flicker, ghosting and trails that a settled capture hides.</summary>
    Presented,
}
/// <summary>A rectangle of a captured frame a presented burst measures its frame-to-frame change over, in fractions of
/// the frame's width and height with the origin at the top left, as <c>view.annotate</c> measures its outlines.</summary>
/// <param name="X">The left edge, in [0, 1).</param>
/// <param name="Y">The top edge, in [0, 1).</param>
/// <param name="Width">The width, in (0, 1 - <paramref name="X"/>].</param>
/// <param name="Height">The height, in (0, 1 - <paramref name="Y"/>].</param>
public sealed record FrameCaptureRegion(double X, double Y, double Width, double Height) {
    /// <summary>Returns why a rectangle is not a region of a frame, or <see langword="null"/> when it is one.</summary>
    /// <param name="x">The left edge, as a fraction of the frame's width.</param>
    /// <param name="y">The top edge, as a fraction of the frame's height.</param>
    /// <param name="width">The width, as a fraction of the frame's width.</param>
    /// <param name="height">The height, as a fraction of the frame's height.</param>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    public static string? RefusalOf(double x, double y, double width, double height) => ((
        double.IsFinite(d: x) && double.IsFinite(d: y) && double.IsFinite(d: width) && double.IsFinite(d: height) &&
        (x >= 0d) && (y >= 0d) && (width > 0d) && (height > 0d) && ((x + width) <= 1d) && ((y + height) <= 1d))
        ? null
        : "a region is x y width height in fractions of the frame, each edge inside it (0 <= x, y; 0 < width <= 1 - x; 0 < height <= 1 - y)"
    );
    /// <summary>Returns the pixels the region covers in a frame of an extent: at least one pixel each way, inside the
    /// frame.</summary>
    /// <param name="width">The frame's width, in pixels.</param>
    /// <param name="height">The frame's height, in pixels.</param>
    /// <returns>The covered rectangle's left, top, width and height, in pixels.</returns>
    public (int Left, int Top, int Width, int Height) PixelsOf(int width, int height) {
        var left = Math.Clamp(value: ((int)Math.Floor(d: (X * width))), min: 0, max: Math.Max(val1: (width - 1), val2: 0));
        var top = Math.Clamp(value: ((int)Math.Floor(d: (Y * height))), min: 0, max: Math.Max(val1: (height - 1), val2: 0));
        var right = Math.Clamp(value: ((int)Math.Ceiling(a: ((X + Width) * width))), min: (left + 1), max: width);
        var bottom = Math.Clamp(value: ((int)Math.Ceiling(a: ((Y + Height) * height))), min: (top + 1), max: height);

        return (left, top, (right - left), (bottom - top));
    }
    /// <inheritdoc/>
    public override string ToString() => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"{X:0.####} {Y:0.####} {Width:0.####} {Height:0.####}"
    );
}
/// <summary>How one presented frame differs from the one presented before it, over the burst's region or the whole
/// frame: the pixels whose red, green or blue differ at all, and the absolute change of their Rec. 709 luma, on the
/// 8-bit display values as they were read back (0 to 255).</summary>
/// <param name="Changed">The pixels whose red, green or blue value differs.</param>
/// <param name="Pixels">The pixels compared.</param>
/// <param name="MeanLumaDelta">The mean absolute luma change over every compared pixel.</param>
/// <param name="MaxLumaDelta">The largest absolute luma change of one pixel.</param>
public sealed record PresentedFrameDelta(long Changed, long Pixels, double MeanLumaDelta, double MaxLumaDelta) {
    /// <summary>Compares two RGBA8 frames of one extent over a region, or over the whole frame.</summary>
    /// <param name="previous">The earlier frame's pixels, four bytes a pixel, rows top to bottom.</param>
    /// <param name="current">The later frame's pixels, in the same layout.</param>
    /// <param name="width">The frames' width, in pixels.</param>
    /// <param name="height">The frames' height, in pixels.</param>
    /// <param name="region">The region compared, or <see langword="null"/> for the whole frame.</param>
    /// <returns>The difference.</returns>
    /// <exception cref="ArgumentException">A frame holds fewer than four bytes for each pixel of the extent.</exception>
    public static PresentedFrameDelta Of(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current, int width, int height, FrameCaptureRegion? region) {
        var bytes = (((long)width) * height * 4L);

        if ((previous.Length < bytes) || (current.Length < bytes)) {
            throw new ArgumentException(message: "Both frames must hold four bytes for each pixel of the extent.");
        }

        var (left, top, columns, rows) = (region?.PixelsOf(height: height, width: width) ?? (0, 0, width, height));
        var changed = 0L;
        var total = 0L;
        var maximum = 0;

        for (var row = top; (row < (top + rows)); row++) {
            var start = (((row * width) + left) * 4);
            var before = previous.Slice(length: (columns * 4), start: start);
            var after = current.Slice(length: (columns * 4), start: start);

            for (var offset = 0; (offset < before.Length); offset += 4) {
                var red = (after[offset] - before[offset]);
                var green = (after[(offset + 1)] - before[(offset + 1)]);
                var blue = (after[(offset + 2)] - before[(offset + 2)]);

                if ((red | green | blue) == 0) {
                    continue;
                }

                // Rec. 709 luma in units of 1/10000 of a display step.
                var luma = Math.Abs(value: ((2126 * red) + (7152 * green) + (722 * blue)));

                changed++;
                total += luma;
                maximum = Math.Max(val1: maximum, val2: luma);
            }
        }

        var pixels = (((long)columns) * rows);

        return new PresentedFrameDelta(
            Changed: changed,
            MaxLumaDelta: (maximum / 10000d),
            MeanLumaDelta: ((total / 10000d) / pixels),
            Pixels: pixels
        );
    }
}
/// <summary>One frame of a presented capture, as the display showed it.</summary>
/// <param name="Index">The frame's position in the burst, from zero.</param>
/// <param name="Path">The PNG the frame was written to.</param>
/// <param name="Presentation">The render chain's index of the frame the display presented: consecutive presentations
/// differ by one, so a gap names presentations the burst could not read.</param>
/// <param name="Rendered">The presentation the shown image was rendered on: equal to <paramref name="Presentation"/> when
/// the frame rendered it, earlier when the display showed an image standing from an earlier frame.</param>
/// <param name="Tick">The simulation tick the shown image was rendered from, or <see langword="null"/> when the renderer
/// names none.</param>
/// <param name="Width">The frame's width, in pixels.</param>
/// <param name="Height">The frame's height, in pixels.</param>
/// <param name="Delta">How the frame differs from the one before it, or <see langword="null"/> for the first frame and for
/// a frame of another extent than the one before it.</param>
public sealed record PresentedFrame(int Index, string Path, long Presentation, long Rendered, ulong? Tick, int Width, int Height, PresentedFrameDelta? Delta);
/// <summary>Writes the report of a presented capture's frames: one line for the burst, one for each frame and one
/// summing the differences, the text <c>world.screenshot … presented</c> prints and <c>puck_capture_frame</c>
/// answers.</summary>
public static class PresentedFrameReport {
    /// <summary>Formats the report.</summary>
    /// <param name="frames">The frames, in order.</param>
    /// <param name="region">The region the differences measure, or <see langword="null"/> for the whole frame.</param>
    /// <returns>The report, lines separated by <c>\n</c>.</returns>
    public static string Format(IReadOnlyList<PresentedFrame> frames, FrameCaptureRegion? region) {
        ArgumentNullException.ThrowIfNull(argument: frames);

        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        if (frames.Count == 0) {
            return "[capture: presented 0 frames]";
        }

        var first = frames[0];

        _ = text.Append(culture, $"[capture: presented {frames.Count} frame{((frames.Count == 1) ? "" : "s")} {first.Width}x{first.Height}, differences over {((region is null) ? "the whole frame" : $"region {region} ({DescribePixels(region: region, frame: first)})")}]");

        var gaps = 0L;
        var repeats = 0;
        var pairs = 0;
        var changedFrames = 0;
        var maximumChanged = 0L;
        var maximumMean = 0d;

        foreach (var frame in frames) {
            _ = text.Append(culture, $"\nframe {frame.Index} presentation {frame.Presentation} rendered {frame.Rendered} tick {frame.Tick?.ToString(provider: culture) ?? "none"}");

            if (frame.Rendered < frame.Presentation) {
                repeats++;
                _ = text.Append(value: " (standing)");
            }
            if (frame.Delta is { } delta) {
                pairs++;
                changedFrames += ((delta.Changed > 0) ? 1 : 0);
                maximumChanged = Math.Max(val1: maximumChanged, val2: delta.Changed);
                maximumMean = Math.Max(val1: maximumMean, val2: delta.MeanLumaDelta);
                _ = text.Append(culture, $" changed {delta.Changed} px ({((100d * delta.Changed) / delta.Pixels):0.###}%) mean |dY| {delta.MeanLumaDelta:0.####} max |dY| {delta.MaxLumaDelta:0.##}");
            }
            if (frame.Index > 0) {
                gaps += Math.Max(val1: 0L, val2: (frame.Presentation - frames[(frame.Index - 1)].Presentation - 1L));
            }

            _ = text.Append(value: ' ').Append(value: frame.Path);
        }

        _ = text.Append(culture, $"\n[capture: {pairs} pair{((pairs == 1) ? "" : "s")} compared, {changedFrames} changed, most changed {maximumChanged} px, largest mean |dY| {maximumMean:0.####}; {repeats} standing frame{((repeats == 1) ? "" : "s")}; {gaps} presentation{((gaps == 1) ? "" : "s")} skipped]");

        return text.ToString();
    }

    private static string DescribePixels(FrameCaptureRegion region, PresentedFrame frame) {
        var (left, top, width, height) = region.PixelsOf(height: frame.Height, width: frame.Width);

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"{width}x{height} px at {left},{top}");
    }
}
