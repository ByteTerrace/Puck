namespace Puck.Platform.Windows.Recording;

// A single-producer/single-consumer float ring with drop-oldest overflow — the device capture thread writes, the
// recording session's audio thread reads. Only the producer advances the write index and only the consumer advances
// the read index (each index has one writer), so no lock is needed. On overflow the producer keeps writing (never
// blocking a device thread); the consumer detects it lagged past the window, jumps its read index forward to the
// freshest `capacity` samples, and counts the skipped ones as dropped. Absolute sample indices are preserved across
// drops so timestamps stay aligned to the wall clock.
internal sealed class AudioSampleRing {
    private readonly float[] m_buffer;
    private readonly int m_capacity;
    private readonly int m_mask;
    private long m_dropped;
    private long m_readIndex;
    private long m_writeIndex;

    public AudioSampleRing(int capacity) {
        // Round up to a power of two so index wrapping is a mask.
        var rounded = 1;

        while (rounded < capacity) {
            rounded <<= 1;
        }

        m_capacity = rounded;
        m_mask = (rounded - 1);
        m_buffer = new float[rounded];
    }

    /// <summary>The interleaved samples dropped to overflow since construction.</summary>
    public long DroppedSampleCount => Volatile.Read(location: ref m_dropped);

    /// <summary>The absolute index (in interleaved samples) of the next sample the consumer will read.</summary>
    public long ReadIndex => Volatile.Read(location: ref m_readIndex);

    /// <summary>Writes interleaved samples from a device thread; overwrites the oldest unread on overflow.</summary>
    /// <param name="samples">The interleaved float samples the device produced.</param>
    public void Write(ReadOnlySpan<float> samples) {
        var write = m_writeIndex; // producer-owned; plain read is safe.

        for (var i = 0; (i < samples.Length); i++) {
            m_buffer[(int)(write & m_mask)] = samples[i];
            write++;
        }

        // Publish the new write position; the consumer reads it with acquire semantics.
        Volatile.Write(location: ref m_writeIndex, value: write);
    }

    /// <summary>Reads up to <paramref name="destination"/>.Length interleaved samples, resyncing past any overflow.</summary>
    /// <param name="destination">The consumer's buffer for interleaved samples.</param>
    /// <returns>The number of samples copied and the absolute index of the first copied sample.</returns>
    public (int Count, long FirstSampleIndex) Read(Span<float> destination) {
        var write = Volatile.Read(location: ref m_writeIndex);
        var read = m_readIndex; // consumer-owned.
        var available = (write - read);

        // The producer only guarantees the freshest `capacity` samples remain intact; if the consumer fell behind,
        // jump forward and count the skipped span as dropped.
        if (available > m_capacity) {
            var skipped = (available - m_capacity);

            read += skipped;
            Volatile.Write(location: ref m_dropped, value: (Volatile.Read(location: ref m_dropped) + skipped));
            available = m_capacity;
        }

        var toCopy = (int)Math.Min(val1: available, val2: (long)destination.Length);
        var firstSampleIndex = read;

        for (var i = 0; (i < toCopy); i++) {
            destination[i] = m_buffer[(int)(read & m_mask)];
            read++;
        }

        Volatile.Write(location: ref m_readIndex, value: read);

        return (toCopy, firstSampleIndex);
    }
}
