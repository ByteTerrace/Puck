namespace Puck.Abstractions.Windowing;

/// <summary>Where a native window stands (<see cref="INativeWindowPlacement.Placement"/>).</summary>
/// <param name="Width">The client width, in pixels.</param>
/// <param name="Height">The client height, in pixels.</param>
/// <param name="Client">The client area on the desktop, or <see langword="null"/> where the windowing system tells a
/// client nothing of where its window stands (Wayland).</param>
/// <param name="Monitor">The monitor's name, or <see langword="null"/> where none is known.</param>
/// <param name="MonitorArea">The monitor's whole area, or <see langword="null"/> where none is known.</param>
/// <param name="WorkArea">The part of the monitor windows may use (the monitor less its task bars), which
/// <see cref="INativeWindowPlacement.TryCenter"/> centres in, or <see langword="null"/> where none is known.</param>
/// <param name="Fullscreen">Whether the window fills its monitor without a frame.</param>
public readonly record struct NativeWindowPlacement(
    uint Width,
    uint Height,
    NativeDesktopArea? Client,
    string? Monitor,
    NativeDesktopArea? MonitorArea,
    NativeDesktopArea? WorkArea,
    bool Fullscreen
) {
    /// <summary>The largest client width or height a window takes, in pixels: the largest extent every windowing
    /// system's resize report carries (a Win32 <c>WM_SIZE</c> word read as signed, an X11 <c>INT16</c>
    /// coordinate).</summary>
    public const uint MaxClientExtent = 32767;

    /// <summary>Returns why a client size cannot be asked for, or <see langword="null"/> when it can.</summary>
    /// <param name="width">The client width, in pixels.</param>
    /// <param name="height">The client height, in pixels.</param>
    /// <returns>The refusal, or <see langword="null"/> when both extents lie from 1 to <see cref="MaxClientExtent"/>.</returns>
    public static string? RefuseClientSize(uint width, uint height) => (((width is 0 or > MaxClientExtent) || (height is 0 or > MaxClientExtent))
        ? $"a client size is 1 to {MaxClientExtent} pixels on each side, not {width}x{height}"
        : null);
    /// <summary>Answers <see cref="INativeWindowPlacement.TrySetFullscreen"/> for a window with no frame-filling mode:
    /// it is already a window, and refuses to fill its monitor.</summary>
    /// <param name="fullscreen">Whether the caller asked the window to fill its monitor.</param>
    /// <param name="kind">The windowing system backing the window.</param>
    /// <param name="refusal">The refusal, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="fullscreen"/> is <see langword="false"/>.</returns>
    public static bool WithoutFullscreen(bool fullscreen, NativeDisplayKind kind, out string? refusal) {
        refusal = (fullscreen ? $"the {kind} window has no fullscreen mode" : null);

        return !fullscreen;
    }
}
