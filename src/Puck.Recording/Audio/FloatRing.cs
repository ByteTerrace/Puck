namespace Puck.Recording.Audio;

/// <summary>
/// A single-producer/single-consumer growable ring of interleaved float samples — the per-source jitter buffer
/// that lets the <see cref="OpusAudioLane"/> align differently sized reads from several capture sources before
/// mixing. Capacity grows once at start-up sizing and is stable in steady state (the lane drains what it enqueues
/// each pump), so it does not allocate after warm-up.
/// </summary>
internal sealed class FloatRing {
    private float[] m_buffer;
    private int m_count;
    private int m_head;

    /// <summary>Initializes a new instance of the <see cref="FloatRing"/> class.</summary>
    /// <param name="capacity">The initial sample capacity.</param>
    public FloatRing(int capacity) {
        m_buffer = new float[Math.Max(val1: 1, val2: capacity)];
    }

    /// <summary>Gets the number of samples currently buffered.</summary>
    public int Count =>
        m_count;

    /// <summary>Enqueues interleaved samples, growing the backing store if necessary.</summary>
    /// <param name="samples">The samples to append.</param>
    public void Enqueue(ReadOnlySpan<float> samples) {
        EnsureCapacity(required: (m_count + samples.Length));

        var tail = ((m_head + m_count) % m_buffer.Length);

        foreach (var sample in samples) {
            m_buffer[tail] = sample;
            tail = ((tail + 1) % m_buffer.Length);
        }

        m_count += samples.Length;
    }

    /// <summary>Copies up to <paramref name="destination"/>.Length samples out and removes them.</summary>
    /// <param name="destination">The destination span.</param>
    /// <returns>The number of samples dequeued.</returns>
    public int Dequeue(Span<float> destination) {
        var taken = Math.Min(val1: destination.Length, val2: m_count);

        for (var index = 0; (index < taken); index++) {
            destination[index] = m_buffer[m_head];
            m_head = ((m_head + 1) % m_buffer.Length);
        }

        m_count -= taken;

        return taken;
    }

    /// <summary>Reads a sample at an offset from the head without removing it.</summary>
    /// <param name="offset">The sample offset from the head.</param>
    /// <returns>The sample value.</returns>
    public float Peek(int offset) =>
        m_buffer[((m_head + offset) % m_buffer.Length)];

    /// <summary>Drops the oldest <paramref name="count"/> samples.</summary>
    /// <param name="count">The number of samples to drop.</param>
    public void Drop(int count) {
        var dropped = Math.Min(val1: count, val2: m_count);

        m_head = ((m_head + dropped) % m_buffer.Length);
        m_count -= dropped;
    }

    private void EnsureCapacity(int required) {
        if (required <= m_buffer.Length) {
            return;
        }

        var grown = new float[Math.Max(val1: required, val2: (m_buffer.Length * 2))];

        for (var index = 0; (index < m_count); index++) {
            grown[index] = m_buffer[((m_head + index) % m_buffer.Length)];
        }

        m_buffer = grown;
        m_head = 0;
    }
}
