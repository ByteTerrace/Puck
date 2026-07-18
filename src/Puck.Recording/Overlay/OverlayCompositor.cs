using System.Globalization;
using Puck.Abstractions.Presentation;
using Puck.Recording.Document;

namespace Puck.Recording.Overlay;

/// <summary>
/// The CPU alpha-over compositor for capture-only overlays: it draws the document's overlay rows (text, rectangle,
/// timecode) straight onto the copied CPU frame after the capture tap, so they exist in the recording and never in
/// the game window. Text is rendered from the crisp integer-scaled <see cref="BitmapFont"/>. Composition is zero
/// cost when there are no rows.
/// </summary>
public sealed class OverlayCompositor {
    private const long NanosecondsPerMillisecond = 1_000_000L;
    private const int OutlineThickness = 2;

    private static string FormatTimecode(long nanoseconds) {
        var totalMilliseconds = Math.Max(val1: 0L, val2: (nanoseconds / NanosecondsPerMillisecond));
        var milliseconds = (totalMilliseconds % 1000L);
        var totalSeconds = (totalMilliseconds / 1000L);
        var seconds = (totalSeconds % 60L);
        var minutes = ((totalSeconds / 60L) % 60L);
        var hours = (totalSeconds / 3600L);

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{hours:D2}:{minutes:D2}:{seconds:D2}.{milliseconds:D3}"
        );
    }

    private readonly OverlayRow[] m_overlays;

    /// <summary>Initializes a new instance of the <see cref="OverlayCompositor"/> class.</summary>
    /// <param name="overlays">The overlay rows to composite, in back-to-front order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="overlays"/> is <see langword="null"/>.</exception>
    public OverlayCompositor(IReadOnlyList<OverlayRow> overlays) {
        ArgumentNullException.ThrowIfNull(argument: overlays);

        m_overlays = [.. overlays];
    }

    /// <summary>Gets whether there are any rows to composite.</summary>
    public bool HasOverlays =>
        (m_overlays.Length > 0);

    /// <summary>Composites every overlay row onto the frame in place.</summary>
    /// <param name="pixels">The tightly packed CPU pixels (four bytes per pixel).</param>
    /// <param name="format">The pixel format (RGBA or BGRA byte order).</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <param name="sessionTimeNanoseconds">The wall-clock session time for a session timecode.</param>
    /// <param name="simTimeNanoseconds">The simulation time for a sim timecode.</param>
    public void Composite(Span<byte> pixels, SurfaceFormat format, int width, int height, long sessionTimeNanoseconds, long simTimeNanoseconds) {
        foreach (var row in m_overlays) {
            switch (row.Kind) {
                case OverlayKind.Rect:
                    DrawRect(pixels: pixels, format: format, width: width, height: height, row: row);

                    break;
                case OverlayKind.Text:
                    DrawText(pixels: pixels, format: format, width: width, height: height, row: row, text: (row.Content ?? string.Empty));

                    break;
                case OverlayKind.Timecode:
                    DrawText(pixels: pixels, format: format, width: width, height: height, row: row, text: FormatTimecode(nanoseconds: (row.Clock == OverlayClock.Sim)
                        ? simTimeNanoseconds
                        : sessionTimeNanoseconds));

                    break;
                default:
                    break;
            }
        }
    }

    private static void DrawText(Span<byte> pixels, SurfaceFormat format, int width, int height, OverlayRow row, string text) {
        if (!Rgba32.TryParse(value: row.Color, color: out var color) || (text.Length == 0)) {
            return;
        }

        var scale = BitmapFont.ScaleFor(pixelHeight: row.PixelHeight);
        var advance = (BitmapFont.GlyphSize * scale);
        var textWidth = (text.Length * advance);
        var textHeight = (BitmapFont.GlyphSize * scale);
        var originX = (AnchorX(normalized: row.X, frame: width, extent: textWidth, anchor: row.Anchor));
        var originY = (AnchorY(normalized: row.Y, frame: height, extent: textHeight, anchor: row.Anchor));

        for (var index = 0; (index < text.Length); index++) {
            var glyph = BitmapFont.Glyph(value: text[index]);
            var cellX = (originX + (index * advance));

            for (var gy = 0; (gy < BitmapFont.GlyphSize); gy++) {
                for (var gx = 0; (gx < BitmapFont.GlyphSize); gx++) {
                    if (!BitmapFont.IsSet(glyph: glyph, column: gx, row: gy)) {
                        continue;
                    }

                    FillBlock(
                        color: color,
                        format: format,
                        height: height,
                        pixels: pixels,
                        size: scale,
                        width: width,
                        x: (cellX + (gx * scale)),
                        y: (originY + (gy * scale))
                    );
                }
            }
        }
    }

    private static void DrawRect(Span<byte> pixels, SurfaceFormat format, int width, int height, OverlayRow row) {
        if (!Rgba32.TryParse(value: row.Color, color: out var fill)) {
            return;
        }

        var rectWidth = Math.Max(val1: 0, val2: (int)MathF.Round(x: (row.Width * width)));
        var rectHeight = Math.Max(val1: 0, val2: (int)MathF.Round(x: (row.Height * height)));
        var originX = AnchorX(normalized: row.X, frame: width, extent: rectWidth, anchor: row.Anchor);
        var originY = AnchorY(normalized: row.Y, frame: height, extent: rectHeight, anchor: row.Anchor);

        for (var y = 0; (y < rectHeight); y++) {
            for (var x = 0; (x < rectWidth); x++) {
                Blend(pixels: pixels, format: format, width: width, height: height, x: (originX + x), y: (originY + y), color: fill);
            }
        }

        if ((row.OutlineColor is { } outlineText) && Rgba32.TryParse(value: outlineText, color: out var outline)) {
            for (var t = 0; (t < OutlineThickness); t++) {
                for (var x = 0; (x < rectWidth); x++) {
                    Blend(pixels: pixels, format: format, width: width, height: height, x: (originX + x), y: (originY + t), color: outline);
                    Blend(pixels: pixels, format: format, width: width, height: height, x: (originX + x), y: ((originY + rectHeight) - 1 - t), color: outline);
                }

                for (var y = 0; (y < rectHeight); y++) {
                    Blend(pixels: pixels, format: format, width: width, height: height, x: (originX + t), y: (originY + y), color: outline);
                    Blend(pixels: pixels, format: format, width: width, height: height, x: ((originX + rectWidth) - 1 - t), y: (originY + y), color: outline);
                }
            }
        }
    }

    private static void FillBlock(Span<byte> pixels, SurfaceFormat format, int width, int height, int x, int y, int size, Rgba32 color) {
        for (var dy = 0; (dy < size); dy++) {
            for (var dx = 0; (dx < size); dx++) {
                Blend(pixels: pixels, format: format, width: width, height: height, x: (x + dx), y: (y + dy), color: color);
            }
        }
    }

    private static void Blend(Span<byte> pixels, SurfaceFormat format, int width, int height, int x, int y, Rgba32 color) {
        if ((x < 0) || (y < 0) || (x >= width) || (y >= height) || (color.A == 0)) {
            return;
        }

        var offset = (((y * width) + x) * 4);
        int alpha = color.A;
        var inverse = (255 - alpha);
        int redIndex;
        int blueIndex;

        if (format == SurfaceFormat.B8G8R8A8Unorm) {
            blueIndex = offset;
            redIndex = (offset + 2);
        } else {
            redIndex = offset;
            blueIndex = (offset + 2);
        }

        pixels[redIndex] = (byte)(((color.R * alpha) + (pixels[redIndex] * inverse) + 127) / 255);
        pixels[(offset + 1)] = (byte)(((color.G * alpha) + (pixels[(offset + 1)] * inverse) + 127) / 255);
        pixels[blueIndex] = (byte)(((color.B * alpha) + (pixels[blueIndex] * inverse) + 127) / 255);
    }

    private static int AnchorX(float normalized, int frame, int extent, OverlayAnchor anchor) {
        var origin = (int)MathF.Round(x: (normalized * frame));

        return anchor switch {
            OverlayAnchor.TopCenter or OverlayAnchor.Center or OverlayAnchor.BottomCenter => (origin - (extent / 2)),
            OverlayAnchor.TopRight or OverlayAnchor.MiddleRight or OverlayAnchor.BottomRight => (origin - extent),
            _ => origin,
        };
    }

    private static int AnchorY(float normalized, int frame, int extent, OverlayAnchor anchor) {
        var origin = (int)MathF.Round(x: (normalized * frame));

        return anchor switch {
            OverlayAnchor.MiddleLeft or OverlayAnchor.Center or OverlayAnchor.MiddleRight => (origin - (extent / 2)),
            OverlayAnchor.BottomLeft or OverlayAnchor.BottomCenter or OverlayAnchor.BottomRight => (origin - extent),
            _ => origin,
        };
    }
}
