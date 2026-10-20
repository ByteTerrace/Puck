namespace Puck.Abstractions.Windowing;

/// <summary>
/// A native window whose client-area cursor the host can change while it runs, beside the
/// <see cref="NativeWindowOptions.HideMouseCursor"/> preference the window is created with. A window that hides the
/// cursor over its client area shows the system arrow there while <see cref="ShowsSystemCursor"/> is set, as over a
/// pane that passes the pointer to an external window. Read and written on the thread that pumps the window's events.
/// </summary>
public interface INativeWindowCursor {
    /// <summary>Gets or sets whether the system arrow shows over the client area even where
    /// <see cref="NativeWindowOptions.HideMouseCursor"/> hides the cursor. A change applies at once while the pointer is
    /// over the client area. Defaults to <see langword="false"/>.</summary>
    bool ShowsSystemCursor { get; set; }
}
