using System.Numerics;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.Commands;

/// <summary>Where a point mapped onto a source's pixels goes.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(StrictEnumConverter<SourceDestination>))]
public enum SourceDestination : byte {
    /// <summary>Hover and highlight only. Nothing reaches state, and a host may find the point by GPU picking instead
    /// of the mapping.</summary>
    Presentation = 0,
    /// <summary>The simulation, such as a light gun aimed at an emulated game: the ray arrives as command values in a
    /// tick's snapshot (<see cref="SourcePointerCommands"/>) and is mapped in fixed point from document data, so every
    /// run maps it to the same pixel.</summary>
    Simulation = 1,
    /// <summary>An external window on the host, such as an editor captured into a pane: pointer and keyboard events
    /// reach the window in its client coordinates (<see cref="SourcePassthrough"/>). Only a source the local user opened
    /// on their own machine may take this destination; a world document never can.</summary>
    Passthrough = 2,
}
/// <summary>Who opened a source, which decides whether it may take <see cref="SourceDestination.Passthrough"/>.</summary>
public enum SourceOpener : byte {
    /// <summary>A world document declared it, whether authored locally or arrived through a portal.</summary>
    Document = 0,
    /// <summary>The local user opened it on their own machine.</summary>
    LocalUser = 1,
}
/// <summary>The external window a <see cref="SourceDestination.Passthrough"/> source shows, as its host reports it. A
/// window capture shows the window's whole frame, borders and title bar included, so the client area sits inside the
/// captured image rather than filling it.</summary>
/// <param name="FrameWidth">The captured frame's width, in physical pixels, which the source image spans; positive.</param>
/// <param name="FrameHeight">The captured frame's height, in physical pixels, which the source image spans; positive.</param>
/// <param name="Client">The client area inside the frame, in physical pixels from the frame's top-left corner; not
/// empty.</param>
/// <param name="DpiScale">Physical pixels per pixel of the window's own client coordinates: 1 for a window aware of its
/// monitor's DPI, and the factor the system stretches it by otherwise, such as 1.5 for a DPI-unaware window on a
/// 144-DPI monitor; finite and positive.</param>
public readonly record struct SourcePassthroughWindow(int FrameWidth, int FrameHeight, SourcePixelRect Client, float DpiScale);
/// <summary>A point in an external window's client area, top-left origin and <c>y</c> downward.</summary>
/// <param name="Physical">The point in physical pixels.</param>
/// <param name="Logical">The point in the window's own client coordinates, <paramref name="Physical"/> divided by the
/// window's DPI scale, which is what the window's pointer messages carry.</param>
/// <param name="InClient">Whether the point lies inside the client area; a point on the captured frame's border or title
/// bar lies outside it.</param>
public readonly record struct SourcePassthroughPoint(Vector2 Physical, Vector2 Logical, bool InClient);
/// <summary>The host passthrough rule and its coordinate contract. Passthrough touches no state, so its coordinates
/// are floats.</summary>
public static class SourcePassthrough {
    /// <summary>Returns whether a source opened by <paramref name="opener"/> may take
    /// <see cref="SourceDestination.Passthrough"/>: only one the local user opened.</summary>
    /// <param name="opener">Who opened the source.</param>
    /// <returns><see langword="true"/> for <see cref="SourceOpener.LocalUser"/>.</returns>
    public static bool IsPermitted(SourceOpener opener) => (opener == SourceOpener.LocalUser);
    /// <summary>Maps a source pixel coordinate into the window's client area. The source image spans the window's
    /// captured frame, possibly at another extent than the frame's, so each axis scales by the frame extent over the
    /// source extent before the client area's offset inside the frame is taken off.</summary>
    /// <param name="coordinate">The source pixel coordinate, as <see cref="SourceHit.Coordinate"/> reports it.</param>
    /// <param name="sourceWidth">The source's width, in pixels; positive.</param>
    /// <param name="sourceHeight">The source's height, in pixels; positive.</param>
    /// <param name="window">The window the source shows.</param>
    /// <returns>The client-area point in physical pixels and in the window's own coordinates, and whether it lies inside
    /// the client area.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceWidth"/> or <paramref name="sourceHeight"/>
    /// is not positive, or the window's frame extent, client extent or DPI scale is not positive.</exception>
    public static SourcePassthroughPoint ToClient(FixedVector2 coordinate, int sourceWidth, int sourceHeight, SourcePassthroughWindow window) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: window.FrameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: window.FrameHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: window.Client.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: window.Client.Height);

        if (
            !float.IsFinite(f: window.DpiScale) ||
            (window.DpiScale <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: window.DpiScale,
                message: "The window's DPI scale must be finite and positive.",
                paramName: nameof(window)
            );
        }

        var physical = new Vector2(
            x: ((float)((((double)coordinate.X) * (window.FrameWidth / ((double)sourceWidth))) - window.Client.X)),
            y: ((float)((((double)coordinate.Y) * (window.FrameHeight / ((double)sourceHeight))) - window.Client.Y))
        );

        return new SourcePassthroughPoint(
            InClient: (
                (physical.X >= 0f) &&
                (physical.Y >= 0f) &&
                (physical.X < window.Client.Width) &&
                (physical.Y < window.Client.Height)
            ),
            Logical: (physical / window.DpiScale),
            Physical: physical
        );
    }
}
