namespace Puck.Abstractions;

/// <summary>
/// The Wayland windowing-system payload of a <see cref="NativeSurfaceBinding"/>.
/// </summary>
/// <param name="DisplayHandle">The native <c>wl_display</c> handle.</param>
/// <param name="SurfaceHandle">The native <c>wl_surface</c> handle.</param>
public readonly record struct WaylandNativeSurfaceBinding(
    nint DisplayHandle,
    nint SurfaceHandle
);
