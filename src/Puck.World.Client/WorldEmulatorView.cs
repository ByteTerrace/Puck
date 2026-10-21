using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>Where an engaged screen's emulator view draws its source: the screen route's display
/// (<see cref="WorldScreenDisplay"/>) cropped from the source, scaled to the display aspect its pixel aspect gives, and
/// fitted whole and centred inside the seat's view, with the fill colour around it. Presentation only, in single
/// precision; nothing here reaches the simulation.</summary>
public static class WorldEmulatorView {
    /// <summary>Fits a display's crop of a source inside a frame of the display.</summary>
    /// <param name="display">The screen route's display, or <see langword="null"/> for
    /// <see cref="WorldScreenDisplay.Neutral"/>.</param>
    /// <param name="sourceWidth">The source's width, in pixels; positive.</param>
    /// <param name="sourceHeight">The source's height, in pixels; positive.</param>
    /// <param name="frame">The seat's view, in normalized display coordinates.</param>
    /// <param name="displayWidth">The display's width, in pixels; positive.</param>
    /// <param name="displayHeight">The display's height, in pixels; positive.</param>
    /// <param name="drawn">The rectangle the crop is drawn over, in normalized display coordinates, centred in
    /// <paramref name="frame"/> and reaching its edges on its limiting axis.</param>
    /// <param name="crop">The crop, in source pixels: the whole source when the display's crop leaves none of it.</param>
    /// <returns><see langword="false"/> when an extent is not positive or the frame covers no area; the outputs are then
    /// default.</returns>
    public static bool TryFit(WorldScreenDisplay? display, int sourceWidth, int sourceHeight, NormalizedRect frame, int displayWidth, int displayHeight, out NormalizedRect drawn, out SourcePixelRect crop) {
        drawn = default;
        crop = default;

        if (
            (sourceWidth <= 0) ||
            (sourceHeight <= 0) ||
            (displayWidth <= 0) ||
            (displayHeight <= 0) ||
            !(frame.Width > 0f) ||
            !(frame.Height > 0f)
        ) {
            return false;
        }

        var shown = (display ?? WorldScreenDisplay.Neutral);
        var edges = (shown.Crop ?? new WorldScreenDisplayCrop());
        var width = ((long)sourceWidth - edges.Left - edges.Right);
        var height = ((long)sourceHeight - edges.Top - edges.Bottom);

        crop = (((width > 0) && (height > 0))
            ? new SourcePixelRect(
                Height: ((int)height),
                Width: ((int)width),
                X: edges.Left,
                Y: edges.Top
            )
            : SourcePixelRect.Whole(
                height: sourceHeight,
                width: sourceWidth
            ));

        var aspect = ((((double)crop.Width) * shown.PixelAspect) / crop.Height);
        var frameWidth = (((double)frame.Width) * displayWidth);
        var frameHeight = (((double)frame.Height) * displayHeight);
        var (drawnWidth, drawnHeight) = (((frameHeight * aspect) <= frameWidth)
            ? ((frameHeight * aspect), frameHeight)
            : (frameWidth, (frameWidth / aspect)));
        var normalizedWidth = (drawnWidth / displayWidth);
        var normalizedHeight = (drawnHeight / displayHeight);

        drawn = new NormalizedRect(
            Height: ((float)normalizedHeight),
            Width: ((float)normalizedWidth),
            X: ((float)(frame.X + (0.5 * (frame.Width - normalizedWidth)))),
            Y: ((float)(frame.Y + (0.5 * (frame.Height - normalizedHeight))))
        );

        return true;
    }
    /// <summary>Returns a display's fill colour as the place pass writes it: <c>#RRGGBB</c> straight through
    /// <c>/255</c>, opaque, as every authored colour reaches the display (<see cref="HexColor"/>).</summary>
    /// <param name="display">The screen route's display, or <see langword="null"/> for black.</param>
    /// <returns>The colour; black for an unauthored or malformed fill, which the validator refuses.</returns>
    public static Vector4 FillOf(WorldScreenDisplay? display) => (HexColor.TryParse(
        rgb: out var rgb,
        value: (display?.Fill ?? WorldScreenDisplay.DefaultFill)
    )
        ? new Vector4(value: rgb, w: 1f)
        : new Vector4(x: 0f, y: 0f, z: 0f, w: 1f));
}
