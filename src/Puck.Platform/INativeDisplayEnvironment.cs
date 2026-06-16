namespace Puck.Platform;

public interface INativeDisplayEnvironment {
    PlatformID CurrentPlatform { get; }
    bool IsWindows { get; }
    string? WaylandDisplay { get; }
    string? XdgSessionType { get; }
}
