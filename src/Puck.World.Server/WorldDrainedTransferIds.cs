namespace Puck.World;

/// <summary>The transfer ids one instance lifetime has drained, as the source of each: every id below a dense
/// watermark, plus the drained ids above it. A transfer is a duplicate exactly when its id is drained here, so an id
/// minted and applied out of enqueue order (a pose following its traveller, a non-atomic party splitting into one
/// transfer per member) never makes an older id still waiting in the queue read as already applied: the watermark
/// passes an id only once that id itself has drained. The set above the watermark is bounded by the work drained ahead
/// of something still queued, never by the lifetime's crossing count.</summary>
/// <remarks>Held by its <see cref="WorldInstance"/>, so a name reused after a stop starts a new lifetime with nothing
/// drained rather than inheriting its predecessor's ids.</remarks>
public sealed class WorldDrainedTransferIds {
    private readonly SortedSet<ulong> m_above = [];
    private ulong m_watermark;

    /// <summary>Gets the highest id at or below which every id has drained, or <see langword="null"/> while id 0 has
    /// not.</summary>
    public ulong? HighWater => ((m_watermark == 0UL)
        ? null
        : (m_watermark - 1UL));
    /// <summary>Gets the drained ids above <see cref="HighWater"/>, ascending.</summary>
    public IReadOnlyCollection<ulong> Above => m_above;

    /// <summary>Returns whether <paramref name="transferId"/> has drained.</summary>
    /// <param name="transferId">The id.</param>
    /// <returns><see langword="true"/> when the id has drained.</returns>
    public bool IsDrained(ulong transferId) => ((transferId < m_watermark) || m_above.Contains(item: transferId));
    /// <summary>Marks <paramref name="transferId"/> drained unless it already was.</summary>
    /// <param name="transferId">The id.</param>
    /// <returns><see langword="false"/> when the id had already drained: a duplicate.</returns>
    public bool TryDrain(ulong transferId) {
        if (IsDrained(transferId: transferId)) {
            return false;
        }

        _ = m_above.Add(item: transferId);
        Advance();

        return true;
    }
    /// <summary>Returns a drained id to the queue: a retryable refusal re-queues its transfer under the same id.</summary>
    /// <param name="transferId">The id, drained by the attempt being retried.</param>
    public void Undrain(ulong transferId) {
        if (m_above.Remove(item: transferId) || (transferId >= m_watermark)) {
            return;
        }

        // The watermark passed the id when it drained; every id it passed since then stays drained.
        for (var id = (transferId + 1UL); (id < m_watermark); id++) {
            _ = m_above.Add(item: id);
        }

        m_watermark = transferId;
    }
    /// <summary>Marks every id at or below <paramref name="transferId"/> drained — a recovered departure record, whose
    /// lifetime queues nothing that recovery could still drain.</summary>
    /// <param name="transferId">The recovered id.</param>
    public void DrainThrough(ulong transferId) {
        if (transferId >= m_watermark) {
            m_watermark = checked((transferId + 1UL));
            m_above.RemoveWhere(match: id => (id < m_watermark));
            Advance();
        }
    }
    /// <summary>Replaces this table with a checkpoint's.</summary>
    /// <param name="highWater">Every id at or below it drained, or <see langword="null"/> for none.</param>
    /// <param name="above">The drained ids above it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="above"/> is <see langword="null"/>.</exception>
    public void Restore(ulong? highWater, IEnumerable<ulong> above) {
        ArgumentNullException.ThrowIfNull(argument: above);
        m_above.Clear();
        m_watermark = ((highWater is { } through)
            ? checked((through + 1UL))
            : 0UL);

        foreach (var id in above) {
            if (id >= m_watermark) {
                _ = m_above.Add(item: id);
            }
        }

        Advance();
    }

    private void Advance() {
        while (m_above.Remove(item: m_watermark)) {
            m_watermark++;
        }
    }
}
