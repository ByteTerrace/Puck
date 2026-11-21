namespace Puck.Abstractions.Memory;

/// <summary>
/// Defers the reuse of storage that readers outside an owner's lock may still be reading: epoch-based reclamation. A
/// reader takes a <see cref="Hold"/> for the span in which it reads published storage; an owner that unlinks a piece of
/// storage (no table it publishes names it any more) retires it here, and <see cref="Reclaim"/> hands it back only once
/// every hold that entered in its retirement's epoch or earlier has closed; a hold that enters in a later epoch reads
/// only what the owner publishes after the retirement and never delays it. The storage then goes back to its pool, so a
/// reader never sees it rewritten by another use while it reads.
/// <para>
/// Two counters, by the parity of the epoch a hold entered in, carry every hold; the epoch advances only when the
/// counter of the epoch before the current one is empty, and an advance frees what was retired two epochs back. A hold
/// is lock-free (one increment and one re-read of the epoch to enter, one decrement to leave) and never waits; retiring
/// and reclaiming take the instance's own lock, so any thread may call them. A reader that keeps a published reference
/// past its hold, or reads without one, is outside this guarantee.
/// </para>
/// </summary>
/// <typeparam name="T">The retired storage.</typeparam>
public sealed class ReaderEpochs<T> where T : class {
    private readonly Lock m_gate = new();
    // Holds admitted in an even and an odd epoch.
    private readonly long[] m_active = new long[2];
    // What was retired in the last even and odd epoch, freed once both of the epochs that could reach it have no hold.
    private readonly List<T>[] m_retired = [[], []];
    private long m_epoch;

    /// <summary>Gets the number of retired items not yet reclaimed.</summary>
    public int Pending {
        get {
            lock (m_gate) { return (m_retired[0].Count + m_retired[1].Count); }
        }
    }

    /// <summary>Opens a hold for a span of reads of published storage; disposing it ends the hold.</summary>
    /// <returns>The hold.</returns>
    public Hold Enter() {
        while (true) {
            var epoch = Volatile.Read(location: ref m_epoch);
            var parity = (int)(epoch & 1L);

            _ = Interlocked.Increment(location: ref m_active[parity]);
            // An advance between the read and the increment counted no hold here; enter again under the new epoch.
            if (Volatile.Read(location: ref m_epoch) == epoch) { return new Hold(owner: this, parity: parity); }
            _ = Interlocked.Decrement(location: ref m_active[parity]);
        }
    }
    /// <summary>Retires storage that no table the owner publishes names any more.</summary>
    /// <param name="item">The storage, which the owner neither publishes nor retires again until it is reclaimed.</param>
    public void Retire(T item) {
        ArgumentNullException.ThrowIfNull(argument: item);
        lock (m_gate) { m_retired[(int)(m_epoch & 1L)].Add(item: item); }
    }
    /// <summary>Hands each retired item no open hold can reach to <paramref name="release"/>, advancing the epoch as far
    /// as the holds allow (at most twice, which frees everything when no hold is open).</summary>
    /// <param name="release">Receives each reclaimable item once, under the instance's lock; it must not enter, retire
    /// or reclaim on this instance.</param>
    /// <returns>The number of items released.</returns>
    public int Reclaim(Action<T> release) {
        ArgumentNullException.ThrowIfNull(argument: release);
        var released = 0;

        lock (m_gate) {
            for (var step = 0; (step < 2); step++) {
                var next = (int)((m_epoch + 1L) & 1L);

                // The holds that share the next epoch's counter entered two epochs back; while one is open, what was
                // retired then may still be read.
                if (Interlocked.Read(location: ref m_active[next]) != 0L) { break; }
                var freed = m_retired[next];

                foreach (var item in freed) { release(obj: item); }
                released += freed.Count;
                freed.Clear();
                Volatile.Write(location: ref m_epoch, value: (m_epoch + 1L));
            }
        }
        return released;
    }

    /// <summary>One reader's hold; disposing it ends the hold once.</summary>
    public struct Hold : IDisposable {
        private ReaderEpochs<T>? m_owner;
        private readonly int m_parity;

        internal Hold(ReaderEpochs<T> owner, int parity) { m_owner = owner; m_parity = parity; }

        /// <summary>Gets whether the hold is open.</summary>
        public readonly bool IsOpen => (m_owner is not null);

        /// <summary>Ends the hold.</summary>
        public void Dispose() {
            if (m_owner is not { } owner) { return; }
            m_owner = null;
            _ = Interlocked.Decrement(location: ref owner.m_active[m_parity]);
        }
    }
}
