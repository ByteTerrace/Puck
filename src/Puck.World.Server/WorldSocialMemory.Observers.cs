using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldSocialMemory {
    private readonly record struct ObserverState(int ImpressionHead, int ReceiptHead, int ImpressionCount, int ReceiptCount);

    private ObserverState Owner(WorldEntityAddress observer) => m_observers.TryGetValue(observer, out var owner) ? owner : new(-1, -1, 0, 0);

    private void SetOwner(WorldEntityAddress observer, ObserverState owner) {
        if (owner.ImpressionCount == 0 && owner.ReceiptCount == 0) { m_observers.Remove(observer); }
        else { m_observers[observer] = owner; }
    }

    /// <summary>Removes all memory owned by one incarnation, including its duplicate receipts. Other observers'
    /// memories of that individual remain untouched. This is ownership retirement, not ordinary forgetting.</summary>
    /// <remarks>The caller must secure any required durable copy and resolve ownership before removal; this method
    /// does not implement transfer, rollback, or admission. Never call it for temporary separation or an ambiguous
    /// handoff. It visits only the owner's records, removes each receipt from the indexed expiry heap in logarithmic
    /// time, and allocates nothing. Clock, work counters, and the next admission ordinal are unchanged.</remarks>
    /// <param name="observer">The exact original mobility incarnation whose locally owned memory is retired.</param>
    /// <returns>Whether any impression or receipt was removed; false for a valid unknown incarnation.</returns>
    /// <exception cref="ArgumentException">The observer address is invalid.</exception>
    /// <exception cref="InvalidOperationException">The observer is frozen; only its matching transfer may thaw or retire it.</exception>
    public bool RemoveObserver(WorldEntityAddress observer) {
        if (!Valid(observer)) { throw new ArgumentException("invalid social observer address", nameof(observer)); }
        if (IsObserverFrozen(observer)) { throw new InvalidOperationException("social observer is frozen for transfer"); }
        return RemoveObserverCore(observer);
    }

    private bool RemoveObserverCore(WorldEntityAddress observer) {
        var owner = Owner(observer);
        for (var node = owner.ImpressionHead; node >= 0;) {
            var next = m_impressionOwners.Next(node);
            ForgetCore(m_impressionOwners.Key(node));
            node = next;
        }
        for (var node = owner.ReceiptHead; node >= 0;) {
            var next = m_receiptOwners.Next(node);
            RemoveReceipt(m_receiptOwners.Key(node));
            node = next;
        }
        return owner.ImpressionCount != 0 || owner.ReceiptCount != 0;
    }

    /// <summary>Copies only one observer's impressions and exact receipt ledger, including receipts for forgotten
    /// impressions. Traversal visits only that observer's records; canonicalization sorts only those records.</summary>
    /// <remarks>The detached checkpoint retains the source policy. By default it retains the source clock; a supplied
    /// destination clock rebases aging anchors while preserving exact age and immutable event timestamps. This is a
    /// logical instantaneous cutover, not an estimate of transit time. It does not transfer ownership or reserve storage.
    /// Receipt ordinals are compacted while preserving forgetting boundaries;
    /// unrelated observers' event counts and work counters do not enter the result. Restoring this checkpoint gives
    /// an independent bank with a fresh ingestion allowance, not the original authority's full work state.</remarks>
    /// <param name="observer">The original mobility incarnation, never a recyclable destination slot.</param>
    /// <param name="engineTick">Optional destination engine-clock boundary; null retains the source clock.</param>
    /// <returns>A detached checkpoint containing only the selected observer's memory; empty for an unknown observer.</returns>
    /// <exception cref="ArgumentException">The observer address is invalid.</exception>
    /// <exception cref="OverflowException">Rebasing an aging anchor would exceed the signed 128-bit time representation.</exception>
    public WorldSocialMemoryCheckpoint CaptureObserver(WorldEntityAddress observer, ulong? engineTick = null) {
        if (!Valid(observer)) { throw new ArgumentException("invalid social observer address", nameof(observer)); }
        return CaptureObserverCore(observer, EngineTick, engineTick ?? EngineTick);
    }

    private WorldSocialMemoryCheckpoint CaptureObserverCore(WorldEntityAddress observer, ulong sourceClock, ulong targetClock) {
        var clockDelta = (Int128)targetClock - sourceClock;
        var owner = Owner(observer);
        var impressions = new WorldSocialImpressionCheckpoint[owner.ImpressionCount];
        var receipts = new WorldSocialReceiptCheckpoint[owner.ReceiptCount];
        var output = 0;
        for (var node = owner.ImpressionHead; node >= 0; node = m_impressionOwners.Next(node)) {
            var key = m_impressionOwners.Key(node);
            var state = m_impressions[key];
            impressions[output++] = new(key, state.Value, state.Weight, state.Uncertainty, checked(state.UpdatedAt + clockDelta), state.IndependentEvents, state.FirstReceiptOrdinal);
        }
        output = 0;
        for (var node = owner.ReceiptHead; node >= 0; node = m_receiptOwners.Next(node)) {
            var key = m_receiptOwners.Key(node);
            var state = m_receipts[key];
            receipts[output++] = new(key.Impression, key.Event, state.OccurredAt, checked(state.LocalOccurredAt + clockDelta), state.Ordinal, state.Value,
                state.Weight, state.Direct, state.ConflictSeen, state.OriginalSource, state.OriginalValue);
        }
        Array.Sort(impressions, static (left, right) => Compare(left.Key, right.Key));
        Array.Sort(receipts, static (left, right) => left.Ordinal.CompareTo(right.Ordinal));

        // A receipt rank preserves every old-receipt < impression-birth test. Leave enough ordinal space after
        // each birth for its independent-event count, including events whose receipts have already expired. Since
        // rank <= the original birth ordinal, a valid source bank cannot overflow this sum.
        var nextOrdinal = (ulong)receipts.Length;
        for (var index = 0; index < impressions.Length; index++) {
            var original = impressions[index];
            var first = ReceiptBoundary(receipts, original.FirstReceiptOrdinal);
            nextOrdinal = Math.Max(nextOrdinal, checked((ulong)first + original.IndependentEvents));
            impressions[index] = original with { FirstReceiptOrdinal = (ulong)first };
        }
        for (var index = 0; index < receipts.Length; index++) { receipts[index] = receipts[index] with { Ordinal = (ulong)index }; }
        return new(Policy.Identity, targetClock, 0, 0, nextOrdinal, impressions, receipts);
    }

    private static int ReceiptBoundary(WorldSocialReceiptCheckpoint[] receipts, ulong ordinal) {
        var first = 0;
        var end = receipts.Length;
        while (first < end) {
            var middle = first + ((end - first) / 2);
            if (receipts[middle].Ordinal < ordinal) { first = middle + 1; }
            else { end = middle; }
        }
        return first;
    }

    // A preallocated intrusive list pool. The dictionaries still own logical values; these recyclable node indices
    // are only acceleration metadata, excluded from checkpoints and state hashes. Removing an arbitrary record
    // patches its two neighbors in O(1), so forgetting/expiry never scans one observer or the whole population.
    private sealed class ObserverLinks<TKey>(int capacity) {
        private struct Node {
            public TKey Key;
            public int Previous;
            public int Next;
        }
        private readonly Node[] m_nodes = new Node[capacity];
        private int m_unused;
        private int m_free = -1;

        public TKey Key(int node) => m_nodes[node].Key;
        public int Next(int node) => m_nodes[node].Next;

        public int Add(TKey key, int head) {
            var node = m_free;
            if (node < 0) {
                if (m_unused == m_nodes.Length) { throw new InvalidOperationException("social observer index exceeded its reserved capacity"); }
                node = m_unused++;
            } else { m_free = m_nodes[node].Next; }
            m_nodes[node] = new Node { Key = key, Previous = -1, Next = head };
            if (head >= 0) { m_nodes[head].Previous = node; }
            return node;
        }

        public int Remove(int node, int head) {
            var previous = m_nodes[node].Previous;
            var next = m_nodes[node].Next;
            if (previous >= 0) { m_nodes[previous].Next = next; }
            else { head = next; }
            if (next >= 0) { m_nodes[next].Previous = previous; }
            m_nodes[node] = new Node { Key = default!, Previous = -1, Next = m_free };
            m_free = node;
            return head;
        }
    }
}
