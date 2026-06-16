namespace Puck.Launcher;

/// <summary>Startup options parsed from the command line that steer the terminal itself (as opposed to the
/// window surface, which <see cref="Puck.Platform.NativeWindowOptions"/> carries).</summary>
public sealed class LauncherOptions {
    /// <summary>Gets the wall-clock seconds after which the terminal auto-exits (for headless or scripted
    /// runs), or <see langword="null"/> to run until closed.</summary>
    public double? ExitAfterSeconds { get; init; }
}
