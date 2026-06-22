namespace Puck.Abstractions;

/// <summary>
/// A window/display capability that reports the monitor's refresh characteristics so the host's pacer can clamp its
/// render cadence into the display's variable-refresh-rate (VRR) window — capping just below the maximum and flooring at
/// the minimum. A provider returns <see cref="DisplayRefreshRange.Unknown"/> when it cannot determine the range, so the
/// pacer degrades to its fixed configured period rather than mis-clamping.
/// </summary>
public interface IDisplayRefreshInfo {
    /// <summary>Queries the refresh characteristics of the display the window currently occupies.</summary>
    /// <returns>The display's refresh range, or <see cref="DisplayRefreshRange.Unknown"/> when unavailable.</returns>
    DisplayRefreshRange QueryRefreshRange();
    /// <summary>A monotonically increasing version that changes whenever the refresh range may have — because the display
    /// mode/topology changed (e.g. a resolution or refresh-rate change) or the window moved to a different monitor. The
    /// host pacer polls this and re-runs <see cref="QueryRefreshRange"/> only when it advances, so a once-at-startup query
    /// does not go stale when the window is dragged between a 60 Hz and a 240 Hz panel. A provider that cannot detect
    /// changes returns a constant (the pacer then keeps its initial range).</summary>
    ulong RefreshConfigurationVersion { get; }
}

/// <summary>
/// A display's refresh characteristics, in Hertz: the current mode rate and the inclusive <c>[Minimum, Maximum]</c> range
/// the display advertises. A default (all-zero) value means "unknown"; <see cref="IsKnown"/> distinguishes it.
/// </summary>
/// <param name="CurrentHertz">The display's current refresh rate, in Hz.</param>
/// <param name="MinimumHertz">The lowest refresh rate the display advertises at the current resolution, in Hz.</param>
/// <param name="MaximumHertz">The highest refresh rate the display advertises at the current resolution, in Hz.</param>
public readonly record struct DisplayRefreshRange(double CurrentHertz, double MinimumHertz, double MaximumHertz) {
    /// <summary>The "unknown" range — the pacer treats it as "no display information" and keeps its fixed period.</summary>
    public static DisplayRefreshRange Unknown => default;
    /// <summary>Whether a usable range was determined (a positive maximum).</summary>
    public bool IsKnown =>
        (MaximumHertz > 0.0);
    /// <summary>Whether the display advertises more than one refresh rate (a necessary, not sufficient, sign of VRR headroom).</summary>
    public bool HasVariableRange =>
        (MaximumHertz > MinimumHertz);
}
