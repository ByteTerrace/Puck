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
/// <summary>The external window a <see cref="SourceDestination.Passthrough"/> source shows, as its host reports it.</summary>
/// <param name="ClientWidth">The window's client width, in physical pixels; positive.</param>
/// <param name="ClientHeight">The window's client height, in physical pixels; positive.</param>
/// <param name="DpiScale">The window's physical pixels per logical pixel, such as 1.5 at 144 DPI; positive.</param>
public readonly record struct SourcePassthroughWindow(int ClientWidth, int ClientHeight, float DpiScale);
/// <summary>A point in an external window's client area, top-left origin and <c>y</c> downward.</summary>
/// <param name="Physical">The point in physical pixels, what a DPI-aware window's pointer messages carry.</param>
/// <param name="Logical">The point in logical pixels, <paramref name="Physical"/> divided by the window's DPI scale,
/// what a window the system scales for DPI expects.</param>
public readonly record struct SourcePassthroughPoint(Vector2 Physical, Vector2 Logical);
/// <summary>The host passthrough rule and its coordinate contract. Passthrough touches no state, so its coordinates
/// are floats.</summary>
public static class SourcePassthrough {
    /// <summary>Returns whether a source opened by <paramref name="opener"/> may take
    /// <see cref="SourceDestination.Passthrough"/>: only one the local user opened.</summary>
    /// <param name="opener">Who opened the source.</param>
    /// <returns><see langword="true"/> for <see cref="SourceOpener.LocalUser"/>.</returns>
    public static bool IsPermitted(SourceOpener opener) => (opener == SourceOpener.LocalUser);
    /// <summary>Maps a source pixel coordinate into the window's client area. The source may be captured at another
    /// extent than the window's client area, so each axis scales by the client extent over the source extent.</summary>
    /// <param name="coordinate">The source pixel coordinate, as <see cref="SourceHit.Coordinate"/> reports it.</param>
    /// <param name="sourceWidth">The source's width, in pixels; positive.</param>
    /// <param name="sourceHeight">The source's height, in pixels; positive.</param>
    /// <param name="window">The window the source shows.</param>
    /// <returns>The client-area point in physical and logical pixels.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceWidth"/> or <paramref name="sourceHeight"/>
    /// is not positive, or the window's client extent or DPI scale is not positive.</exception>
    public static SourcePassthroughPoint ToClient(FixedVector2 coordinate, int sourceWidth, int sourceHeight, SourcePassthroughWindow window) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: window.ClientWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: window.ClientHeight);

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
            x: ((float)(((double)coordinate.X) * (window.ClientWidth / ((double)sourceWidth)))),
            y: ((float)(((double)coordinate.Y) * (window.ClientHeight / ((double)sourceHeight))))
        );

        return new SourcePassthroughPoint(
            Logical: (physical / window.DpiScale),
            Physical: physical
        );
    }
}
