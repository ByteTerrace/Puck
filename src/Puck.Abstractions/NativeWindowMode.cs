namespace Puck.Abstractions;

/// <summary>
/// Selects what <see cref="INativeWindowFactory.Create"/> builds: a real platform window, or a headless stand-in.
/// </summary>
public enum NativeWindowMode {
    /// <summary>No OS window is created; the factory returns a headless stand-in that satisfies the
    /// <see cref="INativeWindow"/> contract (fixed extent, no events, a payload-free surface binding) so a host can
    /// render offscreen. The default.</summary>
    Headless = 0,
    /// <summary>A real native window is created for the resolved <see cref="NativeDisplayKind"/>.</summary>
    PlatformWindow
}
