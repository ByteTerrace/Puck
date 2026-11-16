namespace Puck.Abstractions;

/// <summary>
/// The latest-image handoff for an image filled in place (a table too large to copy as one value): a writer fills
/// <see cref="Back"/> and publishes it, a reader takes the latest published image, and neither ever waits on the other
/// or retries. The reader skips every publication it was too slow to see.
/// <para>Three slots change hands by ownership. The writer owns <see cref="Back"/>, the reader owns the slot
/// <see cref="Take"/> last returned, and the third sits between them, marked fresh once a writer has published into it.
/// Each side changes hands only by one <see cref="Interlocked.Exchange(ref int, int)"/> with that middle slot, so the
/// three always remain one slot each, and no slot is ever written while the other side may read it. The exchanges are
/// full fences: everything the writer wrote before <see cref="Publish"/> is visible to the reader that takes it.</para>
/// <para>One writer at a time and one reader at a time: callers that write from several threads serialize their writes
/// themselves (a delivery gate), and so do callers that read from several threads. A reader never takes a writer's
/// serialization, so a writer stalled mid-image leaves the last complete image to take. Every slot starts as an image
/// <paramref name="create"/> returns, so the reader's first image is already complete.</para>
/// </summary>
/// <typeparam name="T">The image type, mutated in place by the writer.</typeparam>
/// <param name="create">Creates each of the three initial images; each must describe the same initial state.</param>
public sealed class TripleBuffer<T>(Func<T> create) where T : class {
    private const int Fresh = 4, Index = 3;

    private readonly T[] m_slots = [create(), create(), create()];

    // m_back is the writer's, m_front the reader's, m_middle the third slot's index, with Fresh set by a publication
    // the reader has not taken.
    private int m_back = 0;
    private int m_front = 2;
    private int m_middle = 1;

    /// <summary>Gets the image the writer fills next; only the writer touches it until <see cref="Publish"/>.</summary>
    public T Back => m_slots[m_back];

    /// <summary>Publishes <see cref="Back"/> as the latest complete image and hands the writer the slot it replaced,
    /// which holds an older image the writer overwrites.</summary>
    public void Publish() => m_back = (Interlocked.Exchange(location1: ref m_middle, value: (m_back | Fresh)) & Index);
    /// <summary>Takes the latest published image, or keeps the one taken last when nothing newer was published. The
    /// image stays the reader's own, unchanged by any writer, until its next call.</summary>
    /// <returns>The latest complete image.</returns>
    public T Take() {
        if ((Volatile.Read(location: ref m_middle) & Fresh) != 0) {
            m_front = (Interlocked.Exchange(location1: ref m_middle, value: m_front) & Index);
        }
        return m_slots[m_front];
    }
}
