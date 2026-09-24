namespace Puck.World;

/// <summary>
/// The world's frame-rate witness: a ring of the most recent frame deltas, sampled by
/// <c>WorldFramePresenter.CaptureFrame</c> and read by the <c>world.fps</c> verb (the project's 120 FPS desktop
/// contract, observed over the pipe).
/// </summary>
/// <remarks>
/// Sampling and reading both run on the launcher pump thread, so no synchronization guards the queue. The window is
/// anchored to the reading instant, not to the last sample: time since the last composed frame counts as a frame
/// still in progress, so a render loop that stops producing frames reads as stalled rather than replaying its last
/// two seconds.
/// </remarks>
/// <param name="timeProvider">The clock the open gap since the last sample is measured on; the system clock when
/// omitted.</param>
public sealed class FrameRateMonitor(TimeProvider? timeProvider = null) {
    /// <summary>The observation horizon in presentation seconds, independent of display refresh.</summary>
    private const float WindowSeconds = 2f;

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
            (m_deltas.Count > 1) &&
            ((m_totalSeconds - m_deltas.Peek()) >= WindowSeconds)
        ) {
            m_totalSeconds -= m_deltas.Dequeue();
        }
    }
    /// <summary>Summarizes the window ending now: the average rate, the slowest single frame's instantaneous rate (the
    /// floor check — one hitch surfaces here before it moves the average), and the count of frames composed inside
    /// the window. The open gap since the last sample is the frame in progress: it can be the slowest frame, it
    /// lengthens the window the average divides by, and once it spans the whole window no composed frame remains.</summary>
    public (float AverageFps, float WorstFps, int FrameCount) Summarize() {
        if (m_deltas.Count == 0) {
            return (AverageFps: 0f, WorstFps: 0f, FrameCount: 0);
        }

        var pendingSeconds = ((float)m_timeProvider.GetElapsedTime(startingTimestamp: m_lastSampleTimestamp).TotalSeconds);
        var keptSeconds = m_totalSeconds;
        var dropped = 0;

        // The oldest frames fall out of the window the open gap has pushed forward.
        foreach (var delta in m_deltas) {
            if ((keptSeconds + pendingSeconds) <= WindowSeconds) {
                break;
            }

            keptSeconds -= delta;
            dropped++;
        }

        var count = (m_deltas.Count - dropped);
        var worstDelta = pendingSeconds;
        var index = 0;

        foreach (var delta in m_deltas) {
            if (index++ >= dropped) {
                worstDelta = MathF.Max(
                    x: worstDelta,
                    y: delta
                );
            }
        }

        if (count == 0) {
            return (AverageFps: 0f, WorstFps: (1f / worstDelta), FrameCount: 0);
        }

        return (
            AverageFps: (count / (keptSeconds + pendingSeconds)),
            WorstFps: (1f / worstDelta),
            FrameCount: count
        );
    }
}
