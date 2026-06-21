namespace Puck.Abstractions;

/// <summary>
/// The Wayland windowing-system payload of a <see cref="NativeSurfaceBinding"/>.
/// </summary>
/// <param name="Display">The native <c>wl_display</c> handle.</param>
/// <param name="Surface">The native <c>wl_surface</c> handle.</param>
public readonly record struct WaylandNativeSurfaceBinding(
    nint Display,
    nint Surface
);
