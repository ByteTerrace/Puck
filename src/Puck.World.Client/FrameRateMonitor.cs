namespace Puck.World;

/// <summary>One reading of the <see cref="FrameRateMonitor"/> window.</summary>
/// <param name="AverageFps">The composed frames' rate over the window, or 0 when no frame was sampled or the renderer is
/// stalled.</param>
/// <param name="WorstFps">The slowest single frame's instantaneous rate, the frame still in progress included: the
/// floor check, where one hitch surfaces before it moves the average. 0 when no frame was sampled.</param>
/// <param name="FrameCount">The composed frames the window holds.</param>
/// <param name="StalledSeconds">How long no frame has been composed, when that gap is a stall rather than a slow frame;
/// 0 otherwise.</param>
public readonly record struct FrameRateSummary(float AverageFps, float WorstFps, int FrameCount, float StalledSeconds) {
    /// <summary>Gets whether the renderer has stopped composing frames.</summary>
    public bool Stalled => (StalledSeconds > 0f);
}
/// <summary>
/// The world's frame-rate witness: a ring of the most recent frame deltas, sampled by
/// <c>WorldFramePresenter.CaptureFrame</c> and read by the <c>world.fps</c> verb (the project's 120 FPS desktop
/// contract, observed over the pipe).
/// </summary>
/// <remarks>
/// Sampling and reading both run on the launcher pump thread, so no synchronization guards the queue. The window ends
/// at the reading instant and spans two presentation seconds, reaching further back when it must to hold four composed
/// frames, so a renderer composing one frame every few seconds still reads as its rate. Time since the last composed
/// frame is the frame in progress: it is the slowest frame when it lasts longest, it lowers the average once it
/// outlasts a typical frame, and it reads as a stall once it outlasts both the window and four times the longest
/// composed frame in it.
/// </remarks>
/// <param name="timeProvider">The clock the open gap since the last sample is measured on; the system clock when
/// omitted.</param>
public sealed class FrameRateMonitor(TimeProvider? timeProvider = null) {
    /// <summary>The observation horizon in presentation seconds, independent of display refresh.</summary>
    private const float WindowSeconds = 2f;
    /// <summary>The fewest composed frames the window holds, whatever their length.</summary>
    private const int MinimumFrames = 4;
    /// <summary>How many times the longest composed frame the open gap must reach, beyond the window, to be a
    /// stall.</summary>
    private const float StallRatio = 4f;

    private readonly Queue<float> m_deltas = new();
    private readonly TimeProvider m_timeProvider = (timeProvider ?? TimeProvider.System);

    private long m_lastSampleTimestamp;
    private float m_totalSeconds;

    /// <summary>Records one frame's delta. A non-positive delta (the very first frame) is skipped.</summary>
    /// <param name="deltaSeconds">The frame's delta in seconds.</param>
    public void Sample(float deltaSeconds) {
        if (!(deltaSeconds > 0f)) {
            return;
        }

        m_lastSampleTimestamp = m_timeProvider.GetTimestamp();
        m_deltas.Enqueue(item: deltaSeconds);
        m_totalSeconds += deltaSeconds;

        while (
            (m_deltas.Count > MinimumFrames) &&
            ((m_totalSeconds - m_deltas.Peek()) >= WindowSeconds)
        ) {
            m_totalSeconds -= m_deltas.Dequeue();
        }
    }
    /// <summary>Summarizes the window ending now (see the type's remarks).</summary>
    /// <returns>The reading.</returns>
    public FrameRateSummary Summarize() {
        if (m_deltas.Count == 0) {
            return default;
        }

        var pendingSeconds = ((float)m_timeProvider.GetElapsedTime(startingTimestamp: m_lastSampleTimestamp).TotalSeconds);
        var keptSeconds = m_totalSeconds;
        var dropped = 0;

        // The oldest frames fall out of the window the open gap has pushed forward, down to the fewest it holds.
        foreach (var delta in m_deltas) {
            if (
                ((m_deltas.Count - dropped) <= MinimumFrames) ||
                ((keptSeconds + pendingSeconds) <= WindowSeconds)
            ) {
                break;
            }

            keptSeconds -= delta;
            dropped++;
        }

        var count = (m_deltas.Count - dropped);
        var longestDelta = 0f;
        var index = 0;

        foreach (var delta in m_deltas) {
            if (index++ >= dropped) {
                longestDelta = MathF.Max(
                    x: longestDelta,
                    y: delta
                );
            }
        }

        if (
            (pendingSeconds > WindowSeconds) &&
            (pendingSeconds > (StallRatio * longestDelta))
        ) {
            return new FrameRateSummary(
                AverageFps: 0f,
                FrameCount: count,
                StalledSeconds: pendingSeconds,
                WorstFps: (1f / pendingSeconds)
            );
        }

        // A frame in progress no longer than a typical frame is ordinary; only its overrun lowers the rate.
        var overrunSeconds = MathF.Max(
            x: 0f,
            y: (pendingSeconds - (keptSeconds / count))
        );

        return new FrameRateSummary(
            AverageFps: (count / (keptSeconds + overrunSeconds)),
            FrameCount: count,
            StalledSeconds: 0f,
            WorstFps: (1f / MathF.Max(
                x: longestDelta,
                y: pendingSeconds
            ))
        );
    }
}
