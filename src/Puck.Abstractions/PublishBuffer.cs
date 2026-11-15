namespace Puck.Abstractions;

/// <summary>
/// The one latest-value handoff between threads: writers publish a value, readers snapshot the newest one, and
/// nothing is queued or retained, so a reader skips every publication it was too slow to see. The slot is a seqlock:
/// a sequence counter that is zero before the first publish, odd while a write is in progress and even once a value is
/// published. A reader copies the value and keeps the copy only when the counter was even and unchanged across the
/// copy, so it never observes a torn value (a copy whose words come from two publications), and it retries otherwise.
/// <para>
/// The fences carry the invariant on every memory model, not only on x86. A writer's two interlocked increments are
/// full fences, so the odd counter is visible before any word of the value changes and the even counter after the
/// last. A reader's full fence between its copy and its second read of the counter keeps the copy from being
/// reordered past that read. Readers take no lock; writers serialize on one lock, so two writers can never interleave
/// their halves of the counter and any number of threads may publish. A reader that finds a write in progress yields
/// through a <see cref="SpinWait"/>, which gives up its time slice after a short spin. A publish copies the value into
/// the slot and allocates nothing.
/// </para>
/// </summary>
/// <typeparam name="T">The value type published each write, copied by assignment: a record or record struct snapshot.</typeparam>
public sealed class PublishBuffer<T> {
    private readonly Lock m_writer = new();
    private T m_value = default!;

    // Twice the number of completed publishes, plus one while a write is in progress.
    private long m_sequence;

    /// <summary>Gets the number of completed publications.</summary>
    public long Version =>
        (Volatile.Read(location: ref m_sequence) >> 1);

    /// <summary>Publishes a value from any thread.</summary>
    /// <param name="value">The value to publish.</param>
    public void Publish(in T value) {
        lock (m_writer) {
            _ = Interlocked.Increment(location: ref m_sequence);
            m_value = value;
            _ = Interlocked.Increment(location: ref m_sequence);
        }
    }
    /// <summary>Copies the latest published value, when one exists.</summary>
    /// <param name="value">The latest value, when published; otherwise <see langword="default"/>.</param>
    /// <returns><see langword="true"/> when a value has been published.</returns>
    public bool TrySnapshot(out T value) {
        var spinner = new SpinWait();

        while (true) {
            var before = Volatile.Read(location: ref m_sequence);

            if (before == 0) {
                value = default!;

                return false;
            }
            if ((before & 1) == 0) {
                value = m_value;
                Interlocked.MemoryBarrier();

                if (Volatile.Read(location: ref m_sequence) == before) {
                    return true;
                }
            }

            spinner.SpinOnce();
        }
    }
}
