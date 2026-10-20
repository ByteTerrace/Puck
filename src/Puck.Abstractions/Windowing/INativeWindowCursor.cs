namespace Puck.Abstractions.Windowing;

/// <summary>
/// A native window whose client-area cursor the host changes while it runs, beside the
/// <see cref="NativeWindowOptions.HideMouseCursor"/> preference the window is created with: the system arrow over a
/// pane that passes the pointer to an external window (<see cref="ShowsSystemCursor"/>), and a locked pointer while a
/// drag steers a camera (<see cref="LocksPointer"/>). Read and written on the thread that pumps the window's events.
/// </summary>
public interface INativeWindowCursor {
    /// <summary>Gets or sets whether the pointer is locked to the window for a drag that reads only its motion. While
    /// locked, the window hides the cursor, holds the mouse capture, confines the cursor to its client area, reports no
    /// new pointer position, and after each pump of its events returns the cursor to the client area's centre, so the
    /// relative motion it reports never ends at a window or screen edge, absolute-mode input (a remote desktop session's
    /// mouse) included. Clearing it frees the cursor where the lock began. Losing the window's focus clears it.
    /// Defaults to <see langword="false"/>.</summary>
    bool LocksPointer { get; set; }
    /// <summary>Gets or sets whether the system arrow shows over the client area even where
    /// <see cref="NativeWindowOptions.HideMouseCursor"/> hides the cursor. A change applies at once while the pointer is
    /// over the client area. Defaults to <see langword="false"/>.</summary>
    bool ShowsSystemCursor { get; set; }
}
