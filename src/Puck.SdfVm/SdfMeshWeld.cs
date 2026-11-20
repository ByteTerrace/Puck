using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SdfVm;

// The first vertex at each position in vertex order. Vector3 equality and its hash agree for signed zeros.
internal sealed class SdfMeshWeld(int capacity) {
    private readonly Dictionary<Vector3, int> m_first = new(capacity);

    public int Site(Vector3 position, int vertex) {
        ref var first = ref CollectionsMarshal.GetValueRefOrAddDefault(m_first, position, out var found);
        if (!found) { first = vertex; }
        return first;
    }

    public bool TryFind(Vector3 position, out int vertex) => m_first.TryGetValue(position, out vertex);

    public static int[] Sites(ReadOnlySpan<Vector3> positions) {
        var weld = new SdfMeshWeld(positions.Length);
        var sites = new int[positions.Length];
        for (var vertex = 0; vertex < sites.Length; vertex++) { sites[vertex] = weld.Site(positions[vertex], vertex); }
        return sites;
    }
}
