namespace Puck.State;

// A read-local index over a store's current membership. Only ordinals live in scratch; values and authored order
// stay in the store. No reference survives the read, so mutable row lists and transferred frame zones stay fresh.
internal readonly ref struct CellOrdinalIndex {
    private readonly StateStore? m_store;
    private readonly StateRow m_row;
    private readonly Span<int> m_buckets;

    internal static int ScratchLength(int count) => checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, checked(count * 2))));

    internal CellOrdinalIndex(StateStore? store, StateRow row, Span<int> scratch) {
        m_store = store;
        m_row = row;
        m_buckets = scratch;
        scratch.Fill(-1);
        var count = store?.CellCount(row) ?? row.Cells?.Count ?? 0;
        for (var index = 0; index < count; index++) {
            if (!TryKey(index, out var key)) { continue; }
            var bucket = key.GetHashCode() & (scratch.Length - 1);
            while (scratch[bucket] >= 0) {
                bucket = (bucket + 1) & (scratch.Length - 1);
            }
            scratch[bucket] = index;
        }
    }

    internal int Find(CellName key) {
        var bucket = key.GetHashCode() & (m_buckets.Length - 1);
        while (m_buckets[bucket] is >= 0 and var index) {
            if (TryKey(index, out var candidate) && candidate == key) { return index; }
            bucket = (bucket + 1) & (m_buckets.Length - 1);
        }
        return -1;
    }

    private bool TryKey(int index, out CellName key) {
        if (m_store is not null) { return m_store.TryKeyAt(m_row, index, out key); }
        key = m_row.Cells![index].Key;
        return true;
    }
}
