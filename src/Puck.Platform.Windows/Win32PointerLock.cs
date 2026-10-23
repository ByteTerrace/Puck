namespace Puck.Platform.Windows;

/// <summary>The decisions and arithmetic of a window's pointer lock (<c>INativeWindowCursor.LocksPointer</c>), apart from
/// the Win32 calls that carry them out: whether a window may lock, the client point the cursor is held at, where a warp
/// lands in an absolute-mode device's coordinates, and which point a device's next absolute report is measured from.
/// Every member is a pure function of its arguments.</summary>
public static class Win32PointerLock {
    /// <summary>The largest absolute-mode coordinate a raw mouse report carries on each axis (<c>RAWMOUSE</c> with
    /// <c>MOUSE_MOVE_ABSOLUTE</c> spans 0 to 65535 across its desktop).</summary>
    public const int AbsoluteExtent = 65535;
    /// <summary>How many absolute reports after a warp may still precede it before the device is taken not to follow it
    /// (a remote desktop client that does not move its own cursor to the warp never sends one from there).</summary>
    public const int WarpReportLimit = 16;

    /// <summary>Returns whether a window may lock the pointer: only the foreground window takes the mouse, and one with
    /// no client area (minimized) has no centre to hold the cursor at.</summary>
    /// <param name="foreground">Whether the window is the foreground window.</param>
    /// <param name="clientWidth">The client area's width, in pixels.</param>
    /// <param name="clientHeight">The client area's height, in pixels.</param>
    /// <returns><see langword="true"/> when the window may lock.</returns>
    public static bool CanLock(bool foreground, int clientWidth, int clientHeight) =>
        (foreground && (clientWidth > 0) && (clientHeight > 0));
    /// <summary>Returns the screen point a locked cursor is held at: the client area's centre, rounded down.</summary>
    /// <param name="originX">The client area's left edge, in screen pixels.</param>
    /// <param name="originY">The client area's top edge, in screen pixels.</param>
    /// <param name="clientWidth">The client area's width, in pixels.</param>
    /// <param name="clientHeight">The client area's height, in pixels.</param>
    /// <returns>The centre, in screen pixels.</returns>
    public static (int X, int Y) Centre(int originX, int originY, int clientWidth, int clientHeight) =>
        ((originX + (clientWidth / 2)), (originY + (clientHeight / 2)));
    /// <summary>Returns where a screen point lies in an absolute-mode device's coordinates, which span
    /// <see cref="AbsoluteExtent"/> across the desktop the device reports against: the primary monitor, or the virtual
    /// desktop.</summary>
    /// <param name="screenX">The point's x, in screen pixels.</param>
    /// <param name="screenY">The point's y, in screen pixels.</param>
    /// <param name="left">The desktop's left edge, in screen pixels.</param>
    /// <param name="top">The desktop's top edge, in screen pixels.</param>
    /// <param name="width">The desktop's width, in pixels.</param>
    /// <param name="height">The desktop's height, in pixels.</param>
    /// <returns>The point in absolute coordinates, or <see langword="null"/> for a desktop with no area.</returns>
    public static (int X, int Y)? ToAbsolute(int screenX, int screenY, int left, int top, int width, int height) =>
        (((width <= 0) || (height <= 0))
            ? null
            : (((int)((((long)(screenX - left)) * AbsoluteExtent) / width)), ((int)((((long)(screenY - top)) * AbsoluteExtent) / height))));
    /// <summary>Returns the point an absolute report's motion is measured from while a warp is awaited. A report
    /// nearer the warp's target than the last report is the first the device sent after following the warp, so it is
    /// measured from the target and the wait ends; one nearer the last report was sent before the device followed it, so
    /// it is measured from the last report, and the wait ends once <see cref="WarpReportLimit"/> such reports have
    /// arrived.</summary>
    /// <param name="report">The report, in absolute coordinates.</param>
    /// <param name="last">The last report, in absolute coordinates.</param>
    /// <param name="warp">The warp's target, in absolute coordinates.</param>
    /// <param name="reportsSinceWarp">How many reports arrived since the warp before this one.</param>
    /// <returns>The point to measure from, and whether the wait for the warp ends with this report.</returns>
    public static ((int X, int Y) From, bool WarpEnds) Baseline((int X, int Y) report, (int X, int Y) last, (int X, int Y) warp, int reportsSinceWarp) {
        static long DistanceSquared((int X, int Y) a, (int X, int Y) b) {
            var x = (((long)a.X) - b.X);
            var y = (((long)a.Y) - b.Y);

            return ((x * x) + (y * y));
        }

        if (DistanceSquared(a: report, b: warp) < DistanceSquared(a: report, b: last)) {
            return (warp, true);
        }

        return (last, ((reportsSinceWarp + 1) >= WarpReportLimit));
    }
}
