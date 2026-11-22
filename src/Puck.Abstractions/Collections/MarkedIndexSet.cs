using Puck.Abstractions.Memory;
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
    /// <summary>Gets the members in the order they were first added since the last <see cref="Clear"/>, or ascending
    /// after <see cref="Sort"/>; valid until the set next changes.</summary>
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
        ArrayCapacity.EnsureLength(ref m_marked, checked(index + 1));
        if (m_marked[index]) { return false; }
        m_marked[index] = true;
        ArrayCapacity.EnsureLength(ref m_members, m_count + 1);
        m_members[m_count++] = index;
        return true;
    }

    /// <summary>Gets the bytes the set's mark table and member list retain, excluding array headers.</summary>
    public long StorageBytes => (((long)m_marked.Length) + (((long)m_members.Length) * sizeof(int)));

    /// <summary>Puts the members in ascending order, so a consumer that writes a table at each visits it in increasing
    /// order and its writes coalesce into runs.</summary>
    public void Sort() => m_members.AsSpan(start: 0, length: m_count).Sort();
    /// <summary>Removes every member, unmarking only the members themselves.</summary>
    public void Clear() {
        foreach (var index in Members) { m_marked[index] = false; }
        m_count = 0;
    }
    /// <summary>Moves every member into a caller's array, in the order they were added, and empties the set, so a
    /// consumer that visits the members can add to the set again while it visits them (a work list whose items may
    /// queue themselves for the next pass).</summary>
    /// <param name="members">The array the members are written to from index zero, grown when it is too short.</param>
    /// <returns>The number of members written.</returns>
    public int Drain(ref int[] members) {
        var count = m_count;

        _ = ArrayCapacity.EnsureScratch(ref members, count);
        Members.CopyTo(destination: members);
        Clear();

        return count;
    }
}
