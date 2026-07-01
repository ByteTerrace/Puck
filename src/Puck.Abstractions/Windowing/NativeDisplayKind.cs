namespace Puck.Abstractions.Windowing;

/// <summary>
/// Specifies the windowing system that backs a <see cref="NativeSurfaceBinding"/>, and doubles as the
/// request/detection vocabulary for window creation.
/// </summary>
public enum NativeDisplayKind {
    /// <summary>Request-only: "detect the display kind for the current platform". Valid in
    /// <see cref="NativeWindowOptions.DisplayKind"/>; it is resolved to a concrete kind before any window or
    /// binding is created, so it never describes an actual window.</summary>
    Auto = 0,
    /// <summary>Binding-tag-only: no OS window exists and rendering is headless. Tags the surface binding the
    /// headless window stand-in produces, which carries no windowing-system payload.</summary>
    Headless,
    /// <summary>The Win32 windowing system (Windows).</summary>
    Win32,
    /// <summary>The Wayland windowing system (Linux).</summary>
    Wayland,
    /// <summary>The XCB windowing system (Linux/X11).</summary>
    Xcb,
    /// <summary>The Nintendo Switch Vi (<c>nn::vi</c>) windowing system. Never auto-detected — it must be requested
    /// explicitly, and requires the licensed Switch backend to be registered.</summary>
    Vi,
    /// <summary>Detection-only: the environment has no supported windowing system (e.g. macOS today). Returned by
    /// platform detection; never requested, and never backs a window.</summary>
    Unsupported
}
