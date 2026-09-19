namespace Puck.State;

/// <summary>One row's map from a cell key's ordinal to the column slot that holds the cell.</summary>
/// <remarks>A row's keys are interned close together, so the map is an array over the span of ordinals the row uses
/// and a read is one bounds check and one load. A row whose keys are scattered across the key table would make that
/// array mostly holes; past <see cref="MaxSpanPerCell"/> times its cell count plus <see cref="SpanSlack"/> it keeps
/// a dictionary instead, which costs a hash per read and no more memory than the row has cells.</remarks>
public sealed class ArenaSlotMap {
    /// <summary>How many times wider than the cells it maps the array may be, before <see cref="SpanSlack"/>.</summary>
    public const int MaxSpanPerCell = 4;
    /// <summary>The ordinals of span every map is allowed however few cells it holds.</summary>
    public const int SpanSlack = 64;

    private int m_count;
    // Slots are stored plus one, so a cleared array reads as holding nothing.
    private int[] m_dense = [];
    private int m_first;
    private Dictionary<int, int>? m_sparse;

    private bool TryWiden(int key) {
        if (m_count == 0) {
            m_first = key;

            if (m_dense.Length == 0) {
                m_dense = new int[16];
            }

            return true;
        }

        var first = Math.Min(
            val1: m_first,
            val2: key
        );
        var last = Math.Max(
            val1: ((((long)m_first) + m_dense.Length) - 1L),
            val2: ((long)key)
        );
        var span = ((last - first) + 1L);

        if (span > ((((long)(m_count + 1)) * MaxSpanPerCell) + SpanSlack)) {
            return false;
        }

        var widened = new int[((int)Math.Max(
            val1: span,
            val2: Math.Min(
                val1: (((long)m_dense.Length) * 2L),
                val2: ((((long)(m_count + 1)) * MaxSpanPerCell) + SpanSlack)
            )
        ))];

        m_dense.CopyTo(
            array: widened,
            index: (m_first - first)
        );
        m_dense = widened;
        m_first = first;

        return true;
    }

    /// <summary>Gets a value indicating whether the map has left its array for a dictionary.</summary>
    public bool IsSparse => (m_sparse is not null);

    /// <summary>Sets the slot a key's ordinal maps to.</summary>
    /// <param name="key">The key's ordinal in the owning arena's key table.</param>
    public int this[int key] {
        set {
            if (m_sparse is { } sparse) {
                sparse[key] = value;

                return;
            }

            var index = (((long)key) - m_first);

            // A cleared map keeps the span it had: a row that reindexes maps the same keys again, in whatever
            // order its members now stand.
            if (
                (((ulong)index) >= ((ulong)m_dense.Length)) &&
                !TryWiden(key: key)
            ) {
                sparse = new Dictionary<int, int>(capacity: (m_count + 1));

                for (var held = 0; (held < m_dense.Length); held++) {
                    if (m_dense[held] != 0) {
                        sparse[(m_first + held)] = (m_dense[held] - 1);
                    }
                }

                sparse[key] = value;
                m_dense = [];
                m_sparse = sparse;

                return;
            }

            ref var cell = ref m_dense[(key - m_first)];

            if (cell == 0) {
                m_count++;
            }

            cell = (value + 1);
        }
    }

    /// <summary>Forgets every mapping, keeping the array for the row's next keys.</summary>
    public void Clear() {
        m_count = 0;
        m_sparse = null;
        Array.Clear(array: m_dense);
    }
    /// <summary>Determines whether a key's ordinal is mapped.</summary>
    /// <param name="key">The key's ordinal.</param>
    /// <returns><see langword="true"/> when the key maps to a slot.</returns>
    public bool ContainsKey(int key) => TryGetValue(
        key: key,
        value: out _
    );
    /// <summary>Returns the slot a key's ordinal maps to.</summary>
    /// <param name="key">The key's ordinal.</param>
    /// <param name="value">The slot on success; otherwise <c>-1</c>.</param>
    /// <returns><see langword="true"/> when the key maps to a slot.</returns>
    public bool TryGetValue(int key, out int value) {
        var index = (((long)key) - m_first);

        if (((ulong)index) < ((ulong)m_dense.Length)) {
            value = (m_dense[index] - 1);

            return (value >= 0);
        }

        if (
            (m_sparse is { } sparse) &&
            sparse.TryGetValue(
                key: key,
                value: out value
            )
        ) {
            return true;
        }

        value = -1;

        return false;
    }
}
