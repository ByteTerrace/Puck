namespace Puck.Platform;

public sealed class NativeDisplayEnvironment : INativeDisplayEnvironment {
    public PlatformID CurrentPlatform => Environment.OSVersion.Platform;
    public bool IsWindows => OperatingSystem.IsWindows();
    public string? WaylandDisplay => Environment.GetEnvironmentVariable(variable: "WAYLAND_DISPLAY");
    public string? XdgSessionType => Environment.GetEnvironmentVariable(variable: "XDG_SESSION_TYPE");
}
