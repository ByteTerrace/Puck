namespace Puck.Platform;

public readonly record struct WaylandNativeSurfaceBinding(
    nint Display,
    nint Surface
);
