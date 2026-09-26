namespace Puck.Hosting;

/// <summary>
/// A single-slot publish buffer: a writer (input/simulation thread) publishes a frame, a reader (the render thread)
/// snapshots the latest one. The slot is guarded by a sequence counter that is odd while a write is in progress: a
/// reader copies the frame and keeps the copy only when the counter was even and unchanged across it, so it never
/// sees a partially written frame, and retries otherwise. Readers take no lock; writers serialize on one uncontended
/// lock, so two writers can never interleave their halves of the counter. A publish copies the frame into the slot and
/// allocates nothing. Cross-thread frame publication is centralized here as a hosting concern.
/// </summary>
/// <typeparam name="T">The immutable frame type published each write (a record/record struct snapshot).</typeparam>
public sealed class PublishBuffer<T> {
    private readonly Lock m_writer = new();
    private T m_frame = default!;

    // Zero before the first publish, odd while a write is in progress, even and non-zero once a frame is published.
    private long m_sequence;

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in T frame) {
        lock (m_writer) {
            _ = Interlocked.Increment(location: ref m_sequence);
            m_frame = frame;
            _ = Interlocked.Increment(location: ref m_sequence);
        }
    }
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    public bool TrySnapshot(out T frame) {
        var spinner = new SpinWait();

        while (true) {
            var before = Volatile.Read(location: ref m_sequence);

            if (before == 0) {
                frame = default!;

                return false;
            }
            if ((before & 1) == 0) {
                frame = m_frame;
                Interlocked.MemoryBarrier();

                if (Volatile.Read(location: ref m_sequence) == before) {
                    return true;
                }
            }

            spinner.SpinOnce();
        }
    }
}
