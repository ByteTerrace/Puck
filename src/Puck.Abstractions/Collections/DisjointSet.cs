using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Collections;

/// <summary>
/// A union-find over the elements <c>0</c> to <c>count - 1</c>. Every set's root is its smallest element, so a root
/// names its set the same way whatever order the unions ran in; a find halves the path it walks. Its table is rented
/// (<see cref="RentedArrays"/>) and goes back to the pool when the set is disposed; a set never disposed only leaves its
/// table to the collector. <see cref="Reset"/> makes one set serve every round of a repeated grouping without renting
/// again while its table is long enough.
/// </summary>
public sealed class DisjointSet : IDisposable {
    private readonly IAllocationObserver? m_owner;
    private int[] m_parents;

    /// <summary>Initializes a new instance of the <see cref="DisjointSet"/> class, each element its own set.</summary>
    /// <param name="count">The number of elements; not negative.</param>
    /// <param name="owner">The workspace owner charged for the table, or null for an unmetered set.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public DisjointSet(int count, IAllocationObserver? owner = null) {
        m_owner = owner;
        m_parents = RentedArrays.Rent<int>(minimumLength: count, owner: owner);
        Fill(count: count);
    }

    /// <summary>Makes each of the elements <c>0</c> to <c>count - 1</c> its own set again, renting a longer table only
    /// when <paramref name="count"/> passes the one it holds.</summary>
    /// <param name="count">The number of elements; not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public void Reset(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);
        if (m_parents.Length < count) {
            var parents = m_parents;

            m_parents = RentedArrays.Rent<int>(minimumLength: count, owner: m_owner);
            RentedArrays.Return(array: parents, owner: m_owner);
        }
        Fill(count: count);
    }
    /// <summary>Returns the root of an element's set, its smallest element.</summary>
    /// <param name="element">The element, in <c>[0, count)</c>.</param>
    /// <returns>The smallest element of the set holding <paramref name="element"/>.</returns>
    public int Find(int element) {
        while (m_parents[element] != element) {
            m_parents[element] = m_parents[m_parents[element]];
            element = m_parents[element];
        }

        return element;
    }
    /// <summary>Joins the sets holding two elements.</summary>
    /// <param name="first">The first element, in <c>[0, count)</c>.</param>
    /// <param name="second">The second element, in <c>[0, count)</c>.</param>
    public void Union(int first, int second) {
        var a = Find(element: first);
        var b = Find(element: second);

        m_parents[Math.Max(val1: a, val2: b)] = Math.Min(val1: a, val2: b);
    }
    /// <summary>Returns the set's table to the pool; the set answers nothing afterward.</summary>
    public void Dispose() {
        var parents = m_parents;

        m_parents = [];
        RentedArrays.Return(array: parents, owner: m_owner);
    }
    private void Fill(int count) {
        for (var element = 0; (element < count); element++) {
            m_parents[element] = element;
        }
    }
}
