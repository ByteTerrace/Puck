namespace Puck.World.Server;

/// <summary>A sorted-by-due-tick queue of opaque tokens. Insertion keeps the backing arrays ascending; a sweep
/// drains only the entries whose due tick has arrived, from the front, so a caller with nothing due pays for one
/// comparison rather than a walk of everything it owns. Backing storage is two arrays that only grow — steady-state
/// <see cref="Add"/>, <see cref="Remove"/>, and <see cref="TryDequeueDue"/> never allocate once the table has
/// reached its working size, and none of the three uses LINQ.</summary>
/// <typeparam name="TToken">The opaque payload an owner reads back when its entry comes due — a key, an index, or
/// whatever else identifies which of the owner's own things this deadline belongs to.</typeparam>
public sealed class WorldDeadlineTable<TToken> {
    private long[] m_dueTicks = [];
    private TToken[] m_tokens = [];
    private int m_count;

    /// <summary>The number of live (not yet dequeued) entries.</summary>
    public int Count => m_count;

    /// <summary>Removes every entry.</summary>
    public void Clear() => m_count = 0;

    /// <summary>Registers one deadline, keeping the backing arrays sorted ascending by due tick. Two entries may
    /// share a due tick; ties dequeue in the order they were added.</summary>
    /// <param name="dueTick">The tick at or after which the entry is due.</param>
    /// <param name="token">The owner's own payload for this entry.</param>
    public void Add(long dueTick, TToken token) {
        if (m_count == m_dueTicks.Length) {
            Grow();
        }

        var index = m_count;

        while ((index > 0) && (m_dueTicks[index - 1] > dueTick)) {
            m_dueTicks[index] = m_dueTicks[index - 1];
            m_tokens[index] = m_tokens[index - 1];
            index--;
        }

        m_dueTicks[index] = dueTick;
        m_tokens[index] = token;
        m_count++;
    }

    /// <summary>Removes the first entry equal to <paramref name="token"/>, wherever it sits, ahead of its own
    /// deadline — the cancellation half of a table an owner adds to eagerly (a lease that commits or aborts before
    /// it expires). A no-op when no such entry is live.</summary>
    /// <param name="token">The payload of the entry to cancel.</param>
    /// <returns><see langword="true"/> when a matching entry was found and removed.</returns>
    public bool Remove(TToken token) {
        var comparer = EqualityComparer<TToken>.Default;

        for (var index = 0; (index < m_count); index++) {
            if (!comparer.Equals(m_tokens[index], token)) {
                continue;
            }

            var tail = ((m_count - index) - 1);

            if (tail > 0) {
                Array.Copy(sourceArray: m_dueTicks, sourceIndex: (index + 1), destinationArray: m_dueTicks, destinationIndex: index, length: tail);
                Array.Copy(sourceArray: m_tokens, sourceIndex: (index + 1), destinationArray: m_tokens, destinationIndex: index, length: tail);
            }

            m_count--;
            m_tokens[m_count] = default!;

            return true;
        }

        return false;
    }

    /// <summary>Pops the earliest entry when its due tick has arrived, leaving the remaining entries sorted. A
    /// caller sweeps by looping this until it returns <see langword="false"/>.</summary>
    /// <param name="tick">The current tick, compared against the earliest entry's due tick.</param>
    /// <param name="token">The dequeued entry's payload, or <see langword="default"/> when nothing was due.</param>
    /// <returns><see langword="true"/> when an entry was due and has been removed.</returns>
    public bool TryDequeueDue(long tick, out TToken token) {
        if ((m_count == 0) || (m_dueTicks[0] > tick)) {
            token = default!;

            return false;
        }

        token = m_tokens[0];

        var tail = (m_count - 1);

        if (tail > 0) {
            Array.Copy(sourceArray: m_dueTicks, sourceIndex: 1, destinationArray: m_dueTicks, destinationIndex: 0, length: tail);
            Array.Copy(sourceArray: m_tokens, sourceIndex: 1, destinationArray: m_tokens, destinationIndex: 0, length: tail);
        }

        m_count--;
        m_tokens[m_count] = default!;

        return true;
    }

    private void Grow() {
        var capacity = Math.Max(4, (m_dueTicks.Length * 2));

        Array.Resize(array: ref m_dueTicks, newSize: capacity);
        Array.Resize(array: ref m_tokens, newSize: capacity);
    }
}

public sealed partial class WorldServer {
    // Ownership escrow reclaim, transfer-lease expiry, and contribution-tenure retraction all evaluate on the same
    // tick-driven, replay-deterministic terms (see ReclaimExpiredEscrows' own remarks) — one call keeps that shared
    // shape visible at the call site instead of three adjacent lines that could drift apart. Park reclaim shares the
    // shape too but stays a separate call in WorldServer.Step: it runs after SweepPlacementResponses, and folding it
    // in here would silently reorder it ahead of a sweep whose own comment ties it to running after StepFields.
    private void SweepDeadlines(ulong tick) {
        ReclaimExpiredEscrows(tick: tick);
        m_transferEscrow.ReclaimExpired(tick: tick);
        SweepContributionTenure(tick: tick);
    }
}
