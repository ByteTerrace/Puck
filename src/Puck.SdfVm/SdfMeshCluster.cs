using System.Numerics;
using Puck.Abstractions.Memory;
using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>
/// Simplifies triangles by vertex clustering: every vertex in one cubic cell of a grid, facing within one of the
/// <see cref="NormalBins"/> directions, becomes one vertex at the bin's mean position and normal, and a triangle whose
/// corners fall in fewer than three clusters is dropped. The output's triangle count is bounded by the surface area over
/// the cell squared, whatever the input's, so it serves what a far viewer sees of a whole world
/// (<see cref="SdfFarProduct"/>), where <see cref="SdfMeshCollapse"/>'s measured collapse, which keeps every open edge
/// and stops at its error bound, cannot bound the count. Opposite faces of a part thinner than the cell stay apart,
/// since they face into different bins. Vertices and triangles stream in (<see cref="Add"/>,
/// <see cref="AddTriangle"/>), so no merged copy of the input is ever held. The result is a pure function of what was
/// added, in the order it was added, and the cell; presentation alone. One instance is used from one thread. Its
/// tables are native storage charged to its owner, released when it is disposed.
/// </summary>
public sealed class SdfMeshCluster : IDisposable {
    /// <summary>The directions a vertex's normal is binned by: the six axes and the eight diagonals.</summary>
    public const int NormalBins = 14;

    private static readonly Vector3[] Directions = [
        Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
        Vector3.Normalize(value: new Vector3(x: 1f, y: 1f, z: 1f)), Vector3.Normalize(value: new Vector3(x: 1f, y: 1f, z: -1f)),
        Vector3.Normalize(value: new Vector3(x: 1f, y: -1f, z: 1f)), Vector3.Normalize(value: new Vector3(x: 1f, y: -1f, z: -1f)),
        Vector3.Normalize(value: new Vector3(x: -1f, y: 1f, z: 1f)), Vector3.Normalize(value: new Vector3(x: -1f, y: 1f, z: -1f)),
        Vector3.Normalize(value: new Vector3(x: -1f, y: -1f, z: 1f)), Vector3.Normalize(value: new Vector3(x: -1f, y: -1f, z: -1f)),
    ];

    private readonly float m_cell;
    private readonly float m_inverse;
    // A key wider than a word is found by a chain of exact pair lookups, each numbering the pairs it has met from zero:
    // a cluster's cell and bin by (x, y), then (that, z), then (that, bin); a kept triangle by (a, b), then (that, c),
    // then (that, material).
    private readonly OwnedLongMap<int> m_columns;
    private readonly OwnedLongMap<int> m_cells;
    private readonly OwnedLongMap<int> m_clusters;
    private readonly OwnedLongMap<int> m_edges;
    private readonly OwnedLongMap<int> m_faces;
    private readonly OwnedLongMap<byte> m_triangles;
    private readonly OwnedBuffer<Sum> m_sums;
    private readonly OwnedBuffer<uint> m_indices;
    private readonly OwnedBuffer<uint> m_materials;

    /// <summary>Initializes a new instance of the <see cref="SdfMeshCluster"/> class.</summary>
    /// <param name="cell">The grid's cell, in the positions' units, positive and finite.</param>
    /// <param name="allocator">The native allocator the tables come from, or <see langword="null"/> for the process's.</param>
    /// <param name="owner">The workspace owner charged for the tables, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> is not positive and finite.</exception>
    public SdfMeshCluster(float cell, IAllocator? allocator = null, IAllocationObserver? owner = null) {
        if (!(float.IsFinite(f: cell) && (cell > 0f))) { throw new ArgumentOutOfRangeException(paramName: nameof(cell), actualValue: cell, message: "A cell is positive and finite."); }
        m_cell = cell;
        m_inverse = (1f / cell);
        m_columns = new OwnedLongMap<int>(allocator: allocator, observer: owner);
        m_cells = new OwnedLongMap<int>(allocator: allocator, observer: owner);
        m_clusters = new OwnedLongMap<int>(allocator: allocator, observer: owner);
        m_edges = new OwnedLongMap<int>(allocator: allocator, observer: owner);
        m_faces = new OwnedLongMap<int>(allocator: allocator, observer: owner);
        m_triangles = new OwnedLongMap<byte>(allocator: allocator, observer: owner);
        m_sums = new OwnedBuffer<Sum>(allocator: allocator, observer: owner);
        m_indices = new OwnedBuffer<uint>(allocator: allocator, observer: owner);
        m_materials = new OwnedBuffer<uint>(allocator: allocator, observer: owner);
    }

    /// <summary>Gets the triangles kept so far.</summary>
    public int TriangleCount => m_materials.Length;

    /// <summary>Adds a vertex and returns the cluster it falls in.</summary>
    /// <param name="position">The vertex's position.</param>
    /// <param name="normal">The vertex's unit normal.</param>
    /// <returns>The cluster, which <see cref="AddTriangle"/> takes.</returns>
    public int Add(Vector3 position, Vector3 normal) {
        var scaled = (position * m_inverse);
        var column = Numbered(map: m_columns, high: ((int)MathF.Floor(x: scaled.X)), low: ((int)MathF.Floor(x: scaled.Y)));
        var cell = Numbered(map: m_cells, high: column, low: ((int)MathF.Floor(x: scaled.Z)));
        var cluster = Numbered(map: m_clusters, high: cell, low: BinOf(normal: normal));

        if (cluster == m_sums.Length) { m_sums.Add(item: default); }

        ref var sum = ref m_sums[cluster];

        sum = new Sum(Count: (sum.Count + 1), Normal: (sum.Normal + normal), Position: (sum.Position + position));
        return cluster;
    }
    /// <summary>Adds a triangle over three clusters: one whose corners share a cluster is dropped, and one over the same
    /// clusters and material as a triangle already kept, from whichever corner, is kept once.</summary>
    /// <param name="a">The first corner's cluster.</param>
    /// <param name="b">The second corner's cluster.</param>
    /// <param name="c">The third corner's cluster.</param>
    /// <param name="material">The triangle's material.</param>
    public void AddTriangle(int a, int b, int c, uint material) {
        if ((a == b) || (b == c) || (a == c)) { return; }

        // The triangle's corners turned so the least comes first, keeping its winding.
        var (first, second, third) = (((a < b) && (a < c)) ? (a, b, c) : ((b < c) ? (b, c, a) : (c, a, b)));
        var face = Numbered(map: m_faces, high: Numbered(map: m_edges, high: first, low: second), low: third);

        if (!m_triangles.TryAdd(key: PackedIntPair.Pack(high: face, low: unchecked((int)material)), value: 0)) { return; }
        m_indices.Add(item: ((uint)a));
        m_indices.Add(item: ((uint)b));
        m_indices.Add(item: ((uint)c));
        m_materials.Add(item: material);
    }
    /// <summary>Returns the clustered mesh: each cluster's mean position and normal, the triangles kept and their
    /// materials, its <see cref="SdfMesh.BakeCell"/> the cell, with no levels.</summary>
    /// <returns>The mesh, in arrays of its own; one of no triangles when every triangle collapsed.</returns>
    public SdfMesh Build() {
        var sums = m_sums.GetSpan();
        var positions = new Vector3[sums.Length];
        var normals = new Vector3[sums.Length];

        for (var cluster = 0; (cluster < sums.Length); cluster++) {
            var (position, normal, count) = sums[cluster];
            var length = normal.Length();

            positions[cluster] = (position / count);
            normals[cluster] = ((length > 0f) ? (normal / length) : Vector3.UnitY);
        }
        return new SdfMesh(indices: m_indices.ToArray(), normals: normals, positions: positions, triangleMaterials: m_materials.ToArray()) {
            BakeCell = new Vector3(value: m_cell),
        };
    }
    /// <summary>Releases the tables.</summary>
    public void Dispose() {
        m_columns.Dispose();
        m_cells.Dispose();
        m_clusters.Dispose();
        m_edges.Dispose();
        m_faces.Dispose();
        m_triangles.Dispose();
        m_sums.Dispose();
        m_indices.Dispose();
        m_materials.Dispose();
    }

    // The number of a pair in a map that numbers each pair it meets (OwnedLongMapNumbering.NumberOf).
    private static int Numbered(OwnedLongMap<int> map, int high, int low) => map.NumberOf(key: PackedIntPair.Pack(high: high, low: low));
    private static int BinOf(Vector3 normal) {
        var best = 0;
        var most = float.NegativeInfinity;

        for (var bin = 0; (bin < Directions.Length); bin++) {
            var alignment = Vector3.Dot(vector1: normal, vector2: Directions[bin]);

            if (alignment > most) {
                most = alignment;
                best = bin;
            }
        }
        return best;
    }

    private readonly record struct Sum(Vector3 Position, Vector3 Normal, int Count);
}
