namespace Puck.Abstractions;

/// <summary>
/// An optional presenter capability that reports when the display ACTUALLY showed a presented frame, so the host pacer
/// can phase-lock its render cadence to the real present rhythm (closing the loop) instead of free-accumulating a
/// computed period that drifts from the display. A presenter returns <see cref="PresentTimingSample.Unavailable"/> when
/// it cannot determine the timing this frame (the extension/feature is absent, statistics are momentarily disjoint after
/// a resize, or nothing has been presented yet), so the pacer degrades cleanly to its open-loop behaviour.
/// </summary>
public interface IPresentTimingFeedback {
    /// <summary>Gets the most recent display-confirmed present timing, or <see cref="PresentTimingSample.Unavailable"/> when none is readable.</summary>
    PresentTimingSample LastPresentTiming { get; }
}

/// <summary>
/// A display-confirmed present timing: a monotonic present counter and the timestamp (in <see cref="System.Diagnostics.Stopwatch"/>
/// ticks — equal to the QPC clock on Windows, so directly comparable to the pacer's timeline) at which that present was
/// shown. A default (zero-timestamp) value means "unavailable"; <see cref="IsAvailable"/> distinguishes it.
/// </summary>
/// <param name="PresentCount">A monotonically increasing count of confirmed presents — a change signals a NEW present.</param>
/// <param name="PresentTimestampTicks">The <see cref="System.Diagnostics.Stopwatch"/>-tick timestamp the present was shown at.</param>
public readonly record struct PresentTimingSample(uint PresentCount, long PresentTimestampTicks) {
    /// <summary>The "unavailable" sample — the pacer treats it as "no present-timing information" and stays open-loop.</summary>
    public static PresentTimingSample Unavailable => default;
    /// <summary>Whether a usable present timestamp was determined (a positive timestamp).</summary>
    public bool IsAvailable =>
        (PresentTimestampTicks > 0L);
}
