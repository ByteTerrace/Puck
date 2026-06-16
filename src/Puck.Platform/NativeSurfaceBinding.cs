namespace Puck.Platform;

public readonly record struct NativeSurfaceBinding(
    NativeDisplayKind DisplayKind,
    Win32NativeSurfaceBinding? Win32 = null,
    WaylandNativeSurfaceBinding? Wayland = null,
    XcbNativeSurfaceBinding? Xcb = null,
    ViNativeSurfaceBinding? Vi = null
) {
    /// <summary>True when the binding carries the platform payload matching its
    /// <see cref="DisplayKind"/> (so a Vulkan surface can be created from it). The
    /// headless and auto kinds carry no payload and are always false.</summary>
    public bool HasSurfacePayload => DisplayKind switch {
        NativeDisplayKind.Win32 => (Win32 is not null),
        NativeDisplayKind.Wayland => (Wayland is not null),
        NativeDisplayKind.Xcb => (Xcb is not null),
        NativeDisplayKind.Vi => (Vi is not null),
        _ => false
    };
}
