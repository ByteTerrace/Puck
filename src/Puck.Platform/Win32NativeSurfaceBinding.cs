namespace Puck.Platform;

public readonly record struct Win32NativeSurfaceBinding(
    nint InstanceHandle,
    nint WindowHandle
);
