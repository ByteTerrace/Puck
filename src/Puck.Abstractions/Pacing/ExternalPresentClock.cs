namespace Puck.Abstractions.Pacing;

/// <summary>
/// The external-clock ingestion seam for genlock (latency phase-align): an asynchronous frame producer (a live camera's
/// grabber thread) publishes each frame's ARRIVAL timestamp — <see cref="System.Diagnostics.Stopwatch"/> ticks, stamped
/// at the publish site so it shares the pacer's QPC clock domain — and the render pacer reads the latest one to bias its
/// deadline toward the arrivals (present a fresh frame with minimum latency while rendering at full VRR rate). One
/// writer at a time is assumed (multiple sources devolve to last-writer-wins); reads and writes take a brief internal
/// lock so the (timestamp, version) pair is always observed consistently.
/// </summary>
public sealed class ExternalPresentClock {
    private readonly Lock m_gate = new();
    private long m_arrivalTimestamp;
    private long m_frameVersion;

    /// <summary>Publishes a frame arrival (called from the producer's own thread, or forwarded at a coarser cadence —
    /// the version lets a reader recover the true per-frame period even when forwarding skips frames).</summary>
    /// <param name="arrivalTimestamp">The arrival time in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <param name="frameVersion">The producer's monotonically increasing frame counter at this arrival.</param>
    public void Publish(long arrivalTimestamp, long frameVersion) {
        lock (m_gate) {
            m_arrivalTimestamp = arrivalTimestamp;
            m_frameVersion = frameVersion;
        }
    }

    /// <summary>Reads the most recent arrival, or returns <see langword="false"/> when no producer has published yet.</summary>
    /// <param name="arrivalTimestamp">The most recent arrival time in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
    /// <param name="frameVersion">The producer's frame counter at that arrival.</param>
    /// <returns><see langword="true"/> if a producer has published at least one arrival.</returns>
    public bool TryRead(out long arrivalTimestamp, out long frameVersion) {
        lock (m_gate) {
            arrivalTimestamp = m_arrivalTimestamp;
            frameVersion = m_frameVersion;
        }

        return (0 != arrivalTimestamp);
    }
}
