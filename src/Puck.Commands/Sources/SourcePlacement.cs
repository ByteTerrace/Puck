using System.Numerics;
using Puck.Abstractions.Presentation;

namespace Puck.Commands;

/// <summary>Where a source is shown: a screen on a world surface, or a pane in screen space. Either one has a face whose
/// coordinates run from 0 to 1, <c>u</c> to the right and <c>v</c> downward, and <see cref="SourceMapping"/> carries
/// the rest of the chain from that face to the source's pixels.</summary>
public abstract record SourcePlacement {
    private protected SourcePlacement() {
    }

    /// <summary>A screen on a world surface: the rectangle centred at <see cref="Origin"/> reaching
    /// <see cref="HalfWidth"/> along <see cref="Right"/> and <see cref="HalfHeight"/> along <see cref="Up"/>. Face
    /// <c>u</c> grows along <see cref="Right"/> and face <c>v</c> grows against <see cref="Up"/>, so <c>v = 0</c> is the
    /// top edge; this is the frame a world screen row authors and the SDF screen shading samples.</summary>
    /// <param name="Origin">The face's centre, in world units.</param>
    /// <param name="Right">The unit world axis face <c>u</c> increases along.</param>
    /// <param name="Up">The unit world axis face <c>v</c> decreases along, orthogonal to <paramref name="Right"/>.</param>
    /// <param name="HalfWidth">The face's half-width along <paramref name="Right"/>, in world units; positive.</param>
    /// <param name="HalfHeight">The face's half-height along <paramref name="Up"/>, in world units; positive.</param>
    public sealed record Surface(Vector3 Origin, Vector3 Right, Vector3 Up, float HalfWidth, float HalfHeight) : SourcePlacement;
    /// <summary>A pane in screen space, such as picture-in-picture or one half of a split screen.</summary>
    /// <param name="Region">The pane's rectangle in normalized display coordinates, origin top-left and <c>y</c>
    /// downward; its width and height are positive.</param>
    public sealed record Pane(NormalizedRect Region) : SourcePlacement;
}
/// <summary>How a source's image is laid onto a placement's face: one of the eight rotations and reflections of the
/// unit square. Each member names the image coordinate <c>(x, y)</c> a face point <c>(u, v)</c> shows; the four that
/// swap the axes turn the face's aspect ratio over as the image sees it.</summary>
public enum SourceUvLayout : byte {
    /// <summary>The face shows the image upright: <c>(x, y) = (u, v)</c>.</summary>
    Identity = 0,
    /// <summary>The image turned a quarter clockwise, its top edge along the face's right edge: <c>(x, y) = (v, 1 − u)</c>.</summary>
    Rotate90 = 1,
    /// <summary>The image turned upside down: <c>(x, y) = (1 − u, 1 − v)</c>.</summary>
    Rotate180 = 2,
    /// <summary>The image turned a quarter counterclockwise, its top edge along the face's left edge: <c>(x, y) = (1 − v, u)</c>.</summary>
    Rotate270 = 3,
    /// <summary>The image mirrored left to right: <c>(x, y) = (1 − u, v)</c>.</summary>
    MirrorHorizontal = 4,
    /// <summary>The image mirrored top to bottom: <c>(x, y) = (u, 1 − v)</c>.</summary>
    MirrorVertical = 5,
    /// <summary>The image reflected about the face's main diagonal: <c>(x, y) = (v, u)</c>.</summary>
    Transpose = 6,
    /// <summary>The image reflected about the face's other diagonal: <c>(x, y) = (1 − v, 1 − u)</c>.</summary>
    Transverse = 7,
}
/// <summary>How a crop whose aspect ratio differs from its face's is fitted to it.</summary>
public enum SourceFit : byte {
    /// <summary>The crop is stretched over the whole face, whatever the two aspect ratios.</summary>
    Stretch = 0,
    /// <summary>The whole crop is shown at its own aspect ratio, centred, with bars where it does not reach the face's
    /// edges; a point on a bar maps to no source pixel.</summary>
    Contain = 1,
    /// <summary>The crop fills the whole face at its own aspect ratio, centred, and the part past the face's edges is
    /// not shown.</summary>
    Cover = 2,
}
/// <summary>An integer rectangle of source pixels, origin top-left and <c>y</c> downward.</summary>
/// <param name="X">The left edge, in pixels.</param>
/// <param name="Y">The top edge, in pixels.</param>
/// <param name="Width">The width, in pixels.</param>
/// <param name="Height">The height, in pixels.</param>
public readonly record struct SourcePixelRect(int X, int Y, int Width, int Height) {
    /// <summary>Creates the rectangle covering a whole image.</summary>
    /// <param name="width">The image's width, in pixels.</param>
    /// <param name="height">The image's height, in pixels.</param>
    /// <returns>The rectangle at the origin with the image's extent.</returns>
    public static SourcePixelRect Whole(int width, int height) => new(
        Height: height,
        Width: width,
        X: 0,
        Y: 0
    );
}
