namespace Puck.Platform.Probes;

/// <summary>
/// A single-producer, multi-reader triple buffer for <see cref="ProbeReading"/>. Each of the three slots carries
/// its own seqlock — a 64-bit sequence counter that is odd while a write is in progress and even once it
/// completes — so a reader that races a publish detects the torn window and retries instead of returning a mixed
/// record. The producer writes round-robin over the two slots that are not the current latest, so it never
/// overwrites the slot a reader may be reading. This is the layout an out-of-process shared-memory ring will reuse
/// for a model probe: a seqlock, unlike a managed reference swap, is cross-process safe, and a 64-bit counter
/// cannot wrap back to a value a suspended reader already sampled.
/// </summary>
public sealed class ProbeReadingRing {
    private const int SlotCount = 3;

    private readonly long[] m_sequences = new long[SlotCount];
    private readonly ProbeReading[] m_slots = new ProbeReading[SlotCount];

    private long m_publishCount;
    private volatile int m_latestSlot = -1;

    /// <summary>Gets the number of publications so far.</summary>
    public long Version => Interlocked.Read(location: ref m_publishCount);

    /// <summary>Publishes a reading from the single producer. Never blocks and never allocates.</summary>
    /// <param name="reading">The reading to publish.</param>
    public void Publish(in ProbeReading reading) {
        var writeSlot = ((m_latestSlot + 1) % SlotCount);

        ref var sequence = ref m_sequences[writeSlot];

        Volatile.Write(location: ref sequence, value: (Volatile.Read(location: ref sequence) + 1L));
        m_slots[writeSlot] = reading;
        Volatile.Write(location: ref sequence, value: (sequence + 1L));
        m_latestSlot = writeSlot;
        _ = Interlocked.Increment(location: ref m_publishCount);
    }
    /// <summary>Tries to read the most recently published reading. Never blocks; retries internally if a read
    /// raced a concurrent publish rather than returning a torn record.</summary>
    /// <param name="reading">When this returns <see langword="true"/>, a torn-free copy of the latest
    /// reading.</param>
    /// <returns><see langword="true"/> once at least one reading has been published.</returns>
    public bool TryReadLatest(out ProbeReading reading) {
        while (true) {
            var slot = m_latestSlot;

            if (slot < 0) {
                reading = default;

                return false;
            }

            var before = Volatile.Read(location: ref m_sequences[slot]);

            if ((before & 1L) != 0L) {
                continue;
            }

            var candidate = m_slots[slot];
            var after = Volatile.Read(location: ref m_sequences[slot]);

            if (before != after) {
                continue;
            }

            reading = candidate;

            return true;
        }
    }
}
