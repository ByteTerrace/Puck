namespace Puck.Launcher;

/// <summary>Startup options parsed from the command line that steer the terminal itself (as opposed to the
/// window surface, which <see cref="Puck.Abstractions.Windowing.NativeWindowOptions"/> carries).</summary>
public sealed class LauncherOptions {
    /// <summary>Gets the wall-clock duration after which the terminal auto-exits (for headless or scripted
    /// runs), or <see langword="null"/> to run until closed.</summary>
    public TimeSpan? ExitAfter { get; init; }
    /// <summary>Gets the target render rate in Hz, or <see langword="null"/> to uncap the framerate (e.g. for VRR). Defaults to 60.</summary>
    public uint? TargetRenderRate { get; init; } = 60U;
    /// <summary>Gets whether the genlock phase-aligner is enabled (the former <c>PUCK_GENLOCK</c> toggle, whose
    /// <c>=0</c> value disabled it). Defaults to enabled.</summary>
    public bool GenlockEnabled { get; init; } = true;
    /// <summary>Gets whether to periodically log the measured present interval / genlock phase error (the former
    /// <c>PUCK_PRESENT_TIMING=1</c> diagnostics opt-in). Off by default so a shipped run is not noisy.</summary>
    public bool LogPresentTiming { get; init; }
    /// <summary>Gets the delay, in seconds, before a one-shot synthetic device-loss is injected to exercise recovery
    /// (the former <c>PUCK_TEST_DEVICE_LOSS</c> test hook), or <see langword="null"/> to leave it off.</summary>
    public double? SyntheticDeviceLossSeconds { get; init; }
}
