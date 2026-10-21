namespace Puck.Abstractions.Windowing;

/// <summary>
/// A native window whose client-area cursor the host changes while it runs, beside the
/// <see cref="NativeWindowOptions.HideMouseCursor"/> preference the window is created with: the system arrow over a
/// pane that passes the pointer to an external window (<see cref="ShowsSystemCursor"/>), and a locked pointer while a
/// drag steers a camera (<see cref="LocksPointer"/>). Read and written on the thread that pumps the window's events.
/// </summary>
public interface INativeWindowCursor {
    /// <summary>Gets or sets whether the pointer is locked to the window for a drag that reads only its motion. While
    /// locked, the window hides the cursor, holds the mouse capture wherever the cursor roams, reports no
    /// new pointer position, and returns the cursor to the client area's centre whenever it wanders far from it, so the
    /// relative motion it reports never ends at a window or screen edge, absolute-mode input (a remote desktop session's
    /// mouse) included. Clearing it frees the cursor where the lock began. Losing the window's focus clears it.
    /// Defaults to <see langword="false"/>.</summary>
    bool LocksPointer { get; set; }
    /// <summary>Gets or sets whether the system arrow shows over the client area even where
    /// <see cref="NativeWindowOptions.HideMouseCursor"/> hides the cursor. A change applies at once while the pointer is
    /// over the client area. Defaults to <see langword="false"/>.</summary>
    bool ShowsSystemCursor { get; set; }

    /// <summary>Moves the cursor to a point of the client area, as a pad driving the pointer does, so the window reports
    /// the pointer there as it would a mouse's move. Only the foreground window moves the cursor, and never while the
    /// pointer is locked, so the user's cursor never moves while they work elsewhere.</summary>
    /// <param name="x">The client-area x coordinate, in client pixels from the left edge.</param>
    /// <param name="y">The client-area y coordinate, in client pixels from the top edge.</param>
    /// <returns><see langword="true"/> when the cursor moved.</returns>
    bool WarpPointer(int x, int y);
}
