namespace Puck.Maths;

/// <summary>
/// A set of slots kept in Hilbert-curve order of the grid cell each one stands in, re-sorted incrementally: a slot whose
/// cell is unchanged costs one comparison per <see cref="Sync"/>, and only the slots that joined or changed cell are
/// sorted and merged back.
/// </summary>
/// <remarks>
/// The order is a pure function of the members and their cells: ascending <see cref="HilbertCurve"/> distance, ties
/// (several slots in one cell) by ascending slot. It never depends on the history of syncs, so two indexes fed the same
/// members and cells agree on every machine and at every worker count. All arithmetic is integer. Use it to cut a
/// per-tick walk, a delivery stream or a streaming queue into runs that are spatially compact: a run of consecutive
/// entries covers a contiguous stretch of the curve, so its members are near one another.
/// </remarks>
public sealed class CurveOrderedIndex {
    private readonly long[] m_cellX;
    private readonly long[] m_cellY;
    private readonly Comparison<int> m_compare;
    private readonly ulong[] m_key;
    private readonly int[] m_order;
    private readonly bool[] m_ordered;
    private readonly int[] m_pending;
    private readonly int[] m_stamp;
    private int m_count;
    private int m_epoch;

    /// <summary>Initializes a new instance of the <see cref="CurveOrderedIndex"/> class with no members.</summary>
    /// <param name="capacity">The number of slots; members are slot indices in <c>[0, capacity)</c>.</param>
    /// <param name="order">The curve order; the grid is <c>2^order</c> cells square and centred on the origin. In
    /// <c>[1, 31]</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative or
    /// <paramref name="order"/> is outside <c>[1, 31]</c>.</exception>
    public CurveOrderedIndex(int capacity, int order = 24) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: order,
            other: 1,
            paramName: nameof(order)
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: order,
            other: 31,
            paramName: nameof(order)
        );

        m_cellX = new long[capacity];
        m_cellY = new long[capacity];
        m_key = new ulong[capacity];
        m_order = new int[capacity];
        m_ordered = new bool[capacity];
        m_pending = new int[capacity];
        m_stamp = new int[capacity];
        Order = order;
        m_compare = (a, b) => {
            var byKey = m_key[a].CompareTo(value: m_key[b]);

            return ((byKey != 0) ? byKey : a.CompareTo(value: b));
        };
    }

    /// <summary>Gets the curve order of the grid the cells index.</summary>
    public int Order { get; }
    /// <summary>Gets the members in curve order, valid until the next <see cref="Sync"/>.</summary>
    public ReadOnlySpan<int> Sorted => m_order.AsSpan(start: 0, length: m_count);

    /// <summary>Returns the curve distance a member was last ordered under.</summary>
    /// <param name="slot">The member's slot, which must be in the last synced set.</param>
    /// <returns>The distance along the curve of the slot's cell.</returns>
    public ulong KeyOf(int slot) => m_key[slot];
    /// <summary>Makes the index hold exactly <paramref name="members"/>, ordered by the cell each stands in now.</summary>
    /// <param name="members">The slots to hold, each once and in any order.</param>
    /// <param name="cellX">Each slot's cell x index, read at <c>cellX[slot]</c> for every member.</param>
    /// <param name="cellY">Each slot's cell y index, read at <c>cellY[slot]</c> for every member.</param>
    /// <returns>The number of members that joined or changed cell and so were sorted back in.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A member is outside <c>[0, capacity)</c>.</exception>
    public int Sync(ReadOnlySpan<int> members, ReadOnlySpan<long> cellX, ReadOnlySpan<long> cellY) {
        if (m_epoch == int.MaxValue) {
            Array.Clear(array: m_stamp);
            m_epoch = 0;
        }

        var epoch = ++m_epoch;
        var pending = 0;

        // A member whose cell moved leaves the order here and re-enters below, as one that joined.
        foreach (var slot in members) {
            var x = cellX[slot];
            var y = cellY[slot];

            m_stamp[slot] = epoch;
            if (m_ordered[slot] && ((m_cellX[slot] != x) || (m_cellY[slot] != y))) {
                m_ordered[slot] = false;
            }
            if (!m_ordered[slot]) {
                m_cellX[slot] = x;
                m_cellY[slot] = y;
                m_key[slot] = HilbertCurve.EncodeCentered(
                    order: Order,
                    x: x,
                    y: y
                );
                m_pending[pending++] = slot;
            }
        }

        // Compact the survivors in place: a slot that left, or whose cell moved, drops out.
        var kept = 0;

        for (var at = 0; (at < m_count); at++) {
            var slot = m_order[at];

            if ((m_stamp[slot] == epoch) && m_ordered[slot]) {
                m_order[kept++] = slot;
            } else {
                m_ordered[slot] = false;
            }
        }
        if (pending > 0) {
            m_pending.AsSpan(start: 0, length: pending).Sort(comparison: m_compare);

            // Merge from the back, so the survivors shift once and nothing is copied aside.
            var write = (kept + pending);
            var survivor = (kept - 1);
            var joiner = (pending - 1);

            while (joiner >= 0) {
                if ((survivor >= 0) && (m_compare(m_order[survivor], m_pending[joiner]) > 0)) {
                    m_order[--write] = m_order[survivor--];
                } else {
                    var slot = m_pending[joiner--];

                    m_order[--write] = slot;
                    m_ordered[slot] = true;
                }
            }
        }

        m_count = (kept + pending);

        return pending;
    }
}
