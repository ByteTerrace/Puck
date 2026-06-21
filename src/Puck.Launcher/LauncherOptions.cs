namespace Puck.Launcher;

/// <summary>Startup options parsed from the command line that steer the terminal itself (as opposed to the
/// window surface, which <see cref="Puck.Abstractions.NativeWindowOptions"/> carries).</summary>
public sealed class LauncherOptions {
    /// <summary>Gets the wall-clock duration after which the terminal auto-exits (for headless or scripted
    /// runs), or <see langword="null"/> to run until closed.</summary>
    public TimeSpan? ExitAfter { get; init; }
    /// <summary>Gets the target render rate in Hz, or <see langword="null"/> to uncap the framerate (e.g. for VRR). Defaults to 60.</summary>
    public uint? TargetRenderRate { get; init; } = 60U;
}
