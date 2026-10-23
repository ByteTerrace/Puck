namespace Puck.Platform.Windows;

/// <summary>The decisions and arithmetic of a window's pointer lock (<c>INativeWindowCursor.LocksPointer</c>), apart from
/// the Win32 calls that carry them out: whether a window may lock and the client point a relative cursor is held at.
/// Every member is a pure function of its arguments.</summary>
public static class Win32PointerLock {
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
}
