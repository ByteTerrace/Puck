using System.Diagnostics;

namespace Puck.Platform;

/// <summary>The slot-ring counterpart of <see cref="LatestFrameBuffer"/>: a single producer publishes which
/// consumer-owned shared texture holds the newest completed frame; the consumer reads the slot and version without
/// copying or blocking.</summary>
public sealed class LatestSlotPublication {
    private volatile int m_latestSlot = -1;
    private int[]? m_readers;
    private long m_timestamp;
    private long m_version;

    /// <summary>Gets the most recently published slot, or <c>-1</c> before the first publication. Observers may read
    /// this for readiness/diagnostics; asynchronous consumption must use <see cref="TryAcquireLatest"/>.</summary>
    public int LatestSlot => m_latestSlot;
    /// <summary>Gets the <see cref="Stopwatch"/> timestamp of the most recent publication.</summary>
    public long Timestamp => Interlocked.Read(location: ref m_timestamp);
    /// <summary>Gets a monotonically increasing count of publications.</summary>
    public long Version => Interlocked.Read(location: ref m_version);

    /// <summary>Configures the fixed target count before the producer or consumer uses the publication.</summary>
    /// <param name="targetCount">The ring size. At least two slots are required so the published slot is never also
    /// the write target.</param>
    public void Configure(int targetCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetCount, 2);

        var readers = new int[targetCount];
        var existing = Interlocked.CompareExchange(location1: ref m_readers, value: readers, comparand: null);

        if ((existing is not null) && (existing.Length != targetCount)) {
            throw new InvalidOperationException(message: $"the slot publication is already configured for {existing.Length} targets");
        }
    }
    /// <summary>Tries to reserve the next unleased slot for the single producer, round-robin after the latest. The
    /// current published slot is never returned, even when it has no readers, because a consumer may acquire it until
    /// the next publication.</summary>
    /// <param name="slot">When this returns <see langword="true"/>, the slot the producer may write.</param>
    /// <returns>Whether a writable slot was available. A false result drops this producer frame rather than racing a
    /// consumer that still samples every other target.</returns>
    public bool TryReserveWriteSlot(out int slot) {
        var readers = (Volatile.Read(location: ref m_readers) ?? throw new InvalidOperationException(message: "the slot publication has not been configured"));
        var latest = m_latestSlot;
        var candidateCount = ((latest < 0) ? readers.Length : (readers.Length - 1));

        for (var offset = 1; (offset <= candidateCount); offset++) {
            var candidate = ((latest + offset) % readers.Length);

            if (0 == Volatile.Read(location: ref readers[candidate])) {
                slot = candidate;

                return true;
            }
        }

        slot = -1;

        return false;
    }
    /// <summary>Acquires the latest completed slot for asynchronous consumption. The caller must pair a successful
    /// acquisition with <see cref="Release"/> after the GPU work that samples the slot has retired.</summary>
    /// <param name="slot">When this returns <see langword="true"/>, the stable slot to consume.</param>
    /// <returns>Whether a frame has been published.</returns>
    public bool TryAcquireLatest(out int slot) {
        var readers = Volatile.Read(location: ref m_readers);

        if (readers is null) {
            slot = -1;

            return false;
        }

        while (true) {
            var latest = m_latestSlot;

            if (latest < 0) {
                slot = -1;

                return false;
            }

            _ = Interlocked.Increment(location: ref readers[latest]);

            if (latest == m_latestSlot) {
                slot = latest;

                return true;
            }

            _ = Interlocked.Decrement(location: ref readers[latest]);
        }
    }
    /// <summary>Releases a slot acquired with <see cref="TryAcquireLatest"/>.</summary>
    /// <param name="slot">The acquired slot.</param>
    public void Release(int slot) {
        var readers = (Volatile.Read(location: ref m_readers) ?? throw new InvalidOperationException(message: "the slot publication has not been configured"));

        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, readers.Length);

        if (Interlocked.Decrement(location: ref readers[slot]) < 0) {
            _ = Interlocked.Increment(location: ref readers[slot]);

            throw new InvalidOperationException(message: $"slot {slot} has no outstanding acquisition");
        }
    }
    /// <summary>Publishes a completed slot (called from the producer thread).</summary>
    /// <param name="slot">The slot whose copy has completed.</param>
    public void Publish(int slot) {
        var readers = (Volatile.Read(location: ref m_readers) ?? throw new InvalidOperationException(message: "the slot publication has not been configured"));

        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, readers.Length);

        if ((slot == m_latestSlot) || (0 != Volatile.Read(location: ref readers[slot]))) {
            throw new InvalidOperationException(message: $"slot {slot} is not writable");
        }

        m_latestSlot = slot;
        _ = Interlocked.Exchange(location1: ref m_timestamp, value: Stopwatch.GetTimestamp());
        _ = Interlocked.Increment(location: ref m_version);
    }
}
