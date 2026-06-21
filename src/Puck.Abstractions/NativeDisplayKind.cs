namespace Puck.Abstractions;

/// <summary>
/// Specifies the windowing system that backs a <see cref="NativeSurfaceBinding"/>.
/// </summary>
public enum NativeDisplayKind {
    /// <summary>The display kind is selected automatically for the current platform.</summary>
    Auto = 0,
    /// <summary>No display; rendering is headless.</summary>
    Headless,
    /// <summary>The Win32 windowing system.</summary>
    Win32,
    /// <summary>The Wayland windowing system.</summary>
    Wayland,
    /// <summary>The XCB windowing system.</summary>
    Xcb,
    /// <summary>The Xlib windowing system.</summary>
    Xlib,
    /// <summary>The Vi windowing system.</summary>
    Vi,
    /// <summary>An unsupported windowing system.</summary>
    Unsupported
}
