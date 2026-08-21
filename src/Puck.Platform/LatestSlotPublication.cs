using System.Diagnostics;

namespace Puck.Platform;

/// <summary>The slot-ring counterpart of <see cref="LatestFrameBuffer"/>: a single producer publishes which
/// consumer-owned shared texture holds the newest completed frame; the consumer reads the slot and version without
/// copying or blocking.</summary>
public sealed class LatestSlotPublication {
    private volatile int m_latestSlot = -1;
    private long m_timestamp;
    private long m_version;

    /// <summary>Gets the most recently published slot, or <c>-1</c> before the first publication.</summary>
    public int LatestSlot => m_latestSlot;
    /// <summary>Gets the <see cref="Stopwatch"/> timestamp of the most recent publication.</summary>
    public long Timestamp => Interlocked.Read(location: ref m_timestamp);
    /// <summary>Gets a monotonically increasing count of publications.</summary>
    public long Version => Interlocked.Read(location: ref m_version);

    /// <summary>Returns the slot the producer writes next: the one after the latest, round-robin.</summary>
    /// <param name="targetCount">The ring size.</param>
    /// <returns>The next slot index.</returns>
    public int NextSlot(int targetCount) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetCount);

        return ((m_latestSlot + 1) % targetCount);
    }
    /// <summary>Publishes a completed slot (called from the producer thread).</summary>
    /// <param name="slot">The slot whose copy has completed.</param>
    public void Publish(int slot) {
        m_latestSlot = slot;
        _ = Interlocked.Exchange(location1: ref m_timestamp, value: Stopwatch.GetTimestamp());
        _ = Interlocked.Increment(location: ref m_version);
    }
}
