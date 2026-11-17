namespace Puck.Abstractions.Gpu;

/// <summary>An intrusive index from a reference key to the rows that name it. Each row has exactly one key; setting
/// or removing it updates both links and the head in constant time, without one collection per key.</summary>
/// <typeparam name="T">The reference key's type.</typeparam>
public sealed class GpuRowIndex<T> where T : notnull {
    private readonly Dictionary<T, int> m_heads;

    private bool[] m_present = [];
    private T?[] m_keys = [];
    private int[] m_next = [];
    private int[] m_previous = [];

    /// <summary>Creates an index using the key's own equality unless a comparer is supplied.</summary>
    /// <param name="comparer">The equality of keys.</param>
    public GpuRowIndex(IEqualityComparer<T>? comparer = null) => m_heads = new(comparer: comparer);

    /// <summary>Gets the logical payload of retained row arrays, excluding headers and the head dictionary.</summary>
    public long StorageBytes => (((long)m_keys.Length) * ((System.Runtime.CompilerServices.Unsafe.SizeOf<T>() + (2 * sizeof(int))) + sizeof(bool)));

    /// <summary>Forgets every chain, releasing its keys while retaining row storage.</summary>
    public void Clear() { m_heads.Clear(); Array.Clear(array: m_keys); Array.Clear(array: m_present); }
    /// <summary>Names a key's first row, or -1 when no row names it.</summary>
    /// <param name="key">The key.</param><returns>The first row.</returns>
    public int First(T key) => (m_heads.TryGetValue(key: key, value: out var row) ? row : -1);
    /// <summary>Names the next row on the same chain, or -1.</summary>
    /// <param name="row">A row on a chain.</param><returns>The next row.</returns>
    public int Next(int row) => m_next[row];
    /// <summary>Changes a row's key.</summary>
    /// <param name="row">The nonnegative row.</param><param name="key">Its key.</param>
    public void Set(int row, T key) {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        if (row >= m_keys.Length) {
            var capacity = Math.Max(val1: (row + 1), val2: Math.Max(val1: 4, val2: (2 * m_keys.Length)));

            Array.Resize(array: ref m_keys, newSize: capacity);
            Array.Resize(array: ref m_next, newSize: capacity);
            Array.Resize(array: ref m_previous, newSize: capacity);
            Array.Resize(array: ref m_present, newSize: capacity);
        }
        if (m_present[row] && m_heads.Comparer.Equals(x: m_keys[row]!, y: key)) { return; }
        Remove(row: row);
        m_keys[row] = key;
        m_present[row] = true;
        m_previous[row] = -1;
        m_next[row] = First(key: key);
        if (m_next[row] >= 0) { m_previous[m_next[row]] = row; }
        m_heads[key] = row;
    }
    /// <summary>Removes a row from its chain, if present.</summary>
    /// <param name="row">The nonnegative row.</param>
    public void Remove(int row) {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        if ((row < m_keys.Length) && m_present[row]) {
            var old = m_keys[row]!;
            var previous = m_previous[row];
            var next = m_next[row];

            if (previous >= 0) { m_next[previous] = next; } else if (next >= 0) { m_heads[old] = next; } else { _ = m_heads.Remove(key: old); }
            if (next >= 0) { m_previous[next] = previous; }
            m_keys[row] = default;
            m_present[row] = false;
        }
    }
}
