namespace Puck.Abstractions;

/// <summary>
/// One native OS window (or the headless stand-in): the surface a presenter binds its swapchain to, plus the event
/// pump that feeds the resize/visibility/input state. Creation, <see cref="PollEvents"/>, and destruction all happen
/// on ONE thread — the thread that created the window owns its platform event queue — and every property reflects
/// only what that thread has pumped so far.
/// </summary>
public interface INativeWindow : IDisposable {
    /// <summary>The windowing system that actually backs this window — always a concrete kind, never
    /// <see cref="NativeDisplayKind.Auto"/> (<see cref="NativeDisplayKind.Headless"/> for the headless stand-in).</summary>
    NativeDisplayKind DisplayKind { get; }
    /// <summary>Whether the window has painted at least once (Win32: the first <c>WM_PAINT</c> was processed;
    /// headless: the window was shown) — the gate smoke runners poll before autoclosing.</summary>
    bool HasPainted { get; }
    /// <summary>The current client-area height, in pixels — initialized from <see cref="NativeWindowOptions.Height"/>
    /// and updated as resize events are pumped.</summary>
    uint Height { get; }
    /// <summary>Whether the native window still exists: <see langword="false"/> once its destruction has been pumped
    /// (e.g. the user closed it) or the object was disposed. The host run loop exits when this goes false.</summary>
    bool IsOpen { get; }
    /// <summary>Whether the window is currently shown — tracks <see cref="Show"/> and the platform's visibility
    /// events.</summary>
    bool IsVisible { get; }
    /// <summary>A monotonically increasing count of client-size changes, so a presenter can detect a resize (and
    /// rebuild its swapchain) by comparing against the last value it observed instead of diffing extents. Constant on
    /// providers that cannot resize (headless).</summary>
    ulong ResizeCount { get; }
    /// <summary>The title the window was created with.</summary>
    string Title { get; }
    /// <summary>The current client-area width, in pixels — initialized from <see cref="NativeWindowOptions.Width"/>
    /// and updated as resize events are pumped.</summary>
    uint Width { get; }

    /// <summary>Destroys the native window, after which <see cref="IsOpen"/> is <see langword="false"/>. Safe to call
    /// when already closed; the object still requires <see cref="IDisposable.Dispose"/>.</summary>
    void Close();
    /// <summary>Creates the <see cref="NativeSurfaceBinding"/> that identifies this window's native surface, tagged
    /// with the window's <see cref="DisplayKind"/> and the matching windowing-system payload populated.</summary>
    /// <returns>The surface binding a presenter can create a swapchain against.</returns>
    NativeSurfaceBinding CreateSurfaceBinding();
    /// <summary>Pumps every pending windowing-system event (resize, visibility, close, paint, keyboard/pointer input)
    /// and folds the frame's accumulated pointer motion into the input queue. Called once per frame, on the thread
    /// that created the window.</summary>
    void PollEvents();
    /// <summary>Makes the window visible. On Win32 this is also where the best-effort
    /// <see cref="NativeWindowOptions.StartFullscreen"/> and <see cref="NativeWindowOptions.HideMouseCursor"/>
    /// preferences are applied.</summary>
    void Show();
}
