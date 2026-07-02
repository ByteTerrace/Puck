namespace Puck.Abstractions.Windowing;

/// <summary>
/// The XCB windowing-system payload of a <see cref="NativeSurfaceBinding"/>.
/// </summary>
/// <param name="Connection">The native <c>xcb_connection_t</c> handle.</param>
/// <param name="Window">The XCB window identifier (<c>xcb_window_t</c>).</param>
public readonly record struct XcbNativeSurfaceBinding(
    nint Connection,
    uint Window
);
