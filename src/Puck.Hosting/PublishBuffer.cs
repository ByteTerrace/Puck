namespace Puck.Hosting;

/// <summary>
/// A lock-free single-slot publish buffer: a writer (input/simulation thread) publishes an immutable frame, a reader
/// (the render thread) snapshots the latest one. A whole-reference swap per publish — no locks on the read path, no
/// partially written or torn frames. Cross-thread frame publication is centralized here as a hosting concern.
/// </summary>
/// <typeparam name="T">The immutable frame type published each write (a record/record struct snapshot).</typeparam>
public sealed class PublishBuffer<T> {
    private volatile Holder? m_latest;

    private sealed record Holder(T Frame);

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in T frame) {
        m_latest = new Holder(Frame: frame);
    }

    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    public bool TrySnapshot(out T frame) {
        var latest = m_latest;

        if (latest is null) {
            frame = default!;

            return false;
        }

        frame = latest.Frame;

        return true;
    }
}
