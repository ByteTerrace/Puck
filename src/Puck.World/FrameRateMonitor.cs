namespace Puck.World;

/// <summary>
/// The world's frame-rate witness: a ring of the most recent frame deltas, sampled by
/// <see cref="Client.WorldFrameSource.CaptureFrame"/> and read by the <c>world.fps</c> verb (the project's 120 FPS desktop
/// contract, observed over the pipe).
/// </summary>
/// <remarks>Sampling and reading both run on the launcher pump thread, so no synchronization guards the queue.</remarks>
internal sealed class FrameRateMonitor {
    /// <summary>The observation horizon in presentation seconds, independent of display refresh.</summary>
    private const float WindowSeconds = 2f;

    private readonly Queue<float> m_deltas = new();
    private float m_totalSeconds;

    /// <summary>Records one frame's delta. A non-positive delta (the very first frame) is skipped.</summary>
    /// <param name="deltaSeconds">The frame's delta in seconds.</param>
    public void Sample(float deltaSeconds) {
        if (!(deltaSeconds > 0f)) {
            return;
        }

        m_deltas.Enqueue(item: deltaSeconds);
        m_totalSeconds += deltaSeconds;

        while ((m_deltas.Count > 1) && ((m_totalSeconds - m_deltas.Peek()) >= WindowSeconds)) {
            m_totalSeconds -= m_deltas.Dequeue();
        }
    }

    /// <summary>Summarizes the window: the average rate, the slowest single frame's instantaneous rate (the floor
    /// check — one hitch surfaces here before it moves the average), and the sample count.</summary>
    public (float AverageFps, float WorstFps, int FrameCount) Summarize() {
        if (m_deltas.Count == 0) {
            return (AverageFps: 0f, WorstFps: 0f, FrameCount: 0);
        }

        var worstDelta = 0f;

        foreach (var delta in m_deltas) {
            worstDelta = MathF.Max(x: worstDelta, y: delta);
        }

        return (
            AverageFps: (m_deltas.Count / m_totalSeconds),
            WorstFps: (1f / worstDelta),
            FrameCount: m_deltas.Count
        );
    }
}
