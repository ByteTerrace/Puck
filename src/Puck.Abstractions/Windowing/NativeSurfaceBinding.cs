namespace Puck.Abstractions.Windowing;

/// <summary>
/// Identifies a native surface to present into, tagged by its <see cref="DisplayKind"/> with the matching
/// windowing-system payload populated.
/// </summary>
/// <param name="DisplayKind">The windowing system that backs the surface.</param>
/// <param name="Win32">The Win32 surface payload, present when <paramref name="DisplayKind"/> is <see cref="NativeDisplayKind.Win32"/>.</param>
/// <param name="Wayland">The Wayland surface payload, present when <paramref name="DisplayKind"/> is <see cref="NativeDisplayKind.Wayland"/>.</param>
/// <param name="Xcb">The XCB surface payload, present when <paramref name="DisplayKind"/> is <see cref="NativeDisplayKind.Xcb"/>.</param>
/// <param name="Vi">The Vi surface payload, present when <paramref name="DisplayKind"/> is <see cref="NativeDisplayKind.Vi"/>.</param>
public readonly record struct NativeSurfaceBinding(
    NativeDisplayKind DisplayKind,
    Win32NativeSurfaceBinding? Win32 = null,
    WaylandNativeSurfaceBinding? Wayland = null,
    XcbNativeSurfaceBinding? Xcb = null,
    ViNativeSurfaceBinding? Vi = null
) {
    /// <summary>Gets whether the payload for <see cref="DisplayKind"/> is populated.</summary>
    public bool HasSurfacePayload => DisplayKind switch {
        NativeDisplayKind.Win32 => (Win32 is not null),
        NativeDisplayKind.Wayland => (Wayland is not null),
        NativeDisplayKind.Xcb => (Xcb is not null),
        NativeDisplayKind.Vi => (Vi is not null),
        _ => false
    };
}
