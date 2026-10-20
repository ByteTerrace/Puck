namespace Puck.SignedDistance.Baking;

/// <summary>
/// A union-find over the elements <c>0</c> to <c>count - 1</c>. Every set's root is its smallest element, so a root
/// names its set the same way whatever order the unions ran in; a find halves the path it walks.
/// </summary>
public sealed class SdfDisjointSet {
    private readonly int[] m_parents;

    /// <summary>Initializes a new instance of the <see cref="SdfDisjointSet"/> class, each element its own set.</summary>
    /// <param name="count">The number of elements; not negative.</param>
    public SdfDisjointSet(int count) {
        m_parents = new int[count];

        for (var element = 0; (element < count); element++) {
            m_parents[element] = element;
        }
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
}
