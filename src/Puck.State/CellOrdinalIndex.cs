namespace Puck.State;

// A read-local index over a store's current membership. Only ordinals live in scratch; values and authored order
// stay in the store. No reference survives the read, so mutable row lists and transferred frame zones stay fresh.
internal readonly ref struct CellOrdinalIndex {
    private readonly StateStore? m_store;
    private readonly StateRow m_row;
    private readonly int m_rowOrdinal;
    private readonly Span<int> m_buckets;

    internal int Find(CellName key) {
        var bucket = key.GetHashCode() & (m_buckets.Length - 1);

        while (m_buckets[bucket] is >= 0 and var index) {
            if (
                TryKey(
                index: index,
                key: out var candidate
            ) &&
                (candidate == key)
            ) { return index; }
            bucket = (bucket + 1) & (m_buckets.Length - 1);
        }
        return -1;
    }
    internal static int ScratchLength(int count) => checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2(value: ((uint)Math.Max(
        val1: 1,
        val2: checked((count * 2))
    ))));

    private bool TryKey(int index, out CellName key) {
        if (m_store is not null) {
            return ((m_rowOrdinal >= 0)
                ? m_store.TryKeyAt(
                    index: index,
                    key: out key,
                    rowOrdinal: m_rowOrdinal
                )
                : m_store.TryKeyAt(
                    index: index,
                    key: out key,
                    row: m_row
                )
            );
        }
        key = m_row.Cells![index].Key;
        return true;
    }

    internal CellOrdinalIndex(StateStore? store, StateRow row, Span<int> scratch, int rowOrdinal = -1) {
        m_store = store;
        m_row = row;
        m_rowOrdinal = rowOrdinal;
        m_buckets = scratch;
        scratch.Fill(value: -1);
        var count = ((store is null)
            ? (row.Cells?.Count ?? 0)
            : ((rowOrdinal >= 0)
                ? store.CellCount(rowOrdinal: rowOrdinal)
                : store.CellCount(row: row)
        ));

        for (var index = 0; (index < count); index++) {
            if (!TryKey(
                index: index,
                key: out var key
            )) { continue; }
            var bucket = key.GetHashCode() & (scratch.Length - 1);

            while (scratch[bucket] >= 0) {
                bucket = (bucket + 1) & (scratch.Length - 1);
            }
            scratch[bucket] = index;
        }
    }
}
