namespace Puck.Platform;

/// <summary>Probes the process environment for the facts display-kind selection needs (the current platform and the
/// Unix session variables) — a seam so <see cref="NativeWindowPlatformSupport"/> can be exercised without real
/// environment state.</summary>
internal interface INativeDisplayEnvironment {
    /// <summary>The operating-system platform the process is running on.</summary>
    PlatformID CurrentPlatform { get; }
    /// <summary>The <c>WAYLAND_DISPLAY</c> environment variable, or <see langword="null"/> when absent.</summary>
    string? WaylandDisplay { get; }
    /// <summary>The <c>XDG_SESSION_TYPE</c> environment variable, or <see langword="null"/> when absent.</summary>
    string? XdgSessionType { get; }
}
