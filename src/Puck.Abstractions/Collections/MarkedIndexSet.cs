namespace Puck.Abstractions.Collections;

/// <summary>A set of non-negative indices that keeps its members in the order they were first added, so adding,
/// testing, enumerating and clearing cost what the set holds, never the range its indices span. A change carrier
/// (the rows a window wrote, the slots a pass dirtied) clears it every window without zeroing a table sized to
/// every index it has ever seen.</summary>
/// <remarks>Not thread-safe. The mark table grows to the largest index added and is kept across
/// <see cref="Clear"/>.</remarks>
public sealed class MarkedIndexSet {
    private bool[] m_marked = [];
    private int[] m_members = [];
    private int m_count;

    /// <summary>Gets the number of members.</summary>
    public int Count => m_count;
    /// <summary>Gets the members in the order they were first added since the last <see cref="Clear"/>; valid until
    /// the set next changes.</summary>
    public ReadOnlySpan<int> Members => m_members.AsSpan(start: 0, length: m_count);

    /// <summary>Returns whether <paramref name="index"/> is a member.</summary>
    /// <param name="index">The index to test; a negative index is never a member.</param>
    /// <returns><see langword="true"/> when the index was added since the last <see cref="Clear"/>.</returns>
    public bool Contains(int index) => (((uint)index) < ((uint)m_marked.Length)) && m_marked[index];

    /// <summary>Adds <paramref name="index"/> once, growing the mark table to hold it.</summary>
    /// <param name="index">The non-negative index to add.</param>
    /// <returns><see langword="true"/> when the index was not already a member.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public bool Add(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        if (index >= m_marked.Length) {
            Array.Resize(array: ref m_marked, newSize: (int)Math.Max(val1: 16u, val2: System.Numerics.BitOperations.RoundUpToPowerOf2(value: (uint)(index + 1))));
        }
        if (m_marked[index]) { return false; }
        m_marked[index] = true;
        if (m_count == m_members.Length) {
            Array.Resize(array: ref m_members, newSize: Math.Max(val1: 16, val2: (m_members.Length * 2)));
        }
        m_members[m_count++] = index;
        return true;
    }

    /// <summary>Removes every member, unmarking only the members themselves.</summary>
    public void Clear() {
        foreach (var index in Members) { m_marked[index] = false; }
        m_count = 0;
    }
}
