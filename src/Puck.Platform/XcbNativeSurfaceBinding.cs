namespace Puck.Platform;

public readonly record struct XcbNativeSurfaceBinding(
    nint Connection,
    uint Window
);
