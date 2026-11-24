using System.Numerics;

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
/// added, in the order it was added, and the cell; presentation alone. One instance is used from one thread.
/// </summary>
public sealed class SdfMeshCluster {
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
    private readonly Dictionary<(int X, int Y, int Z, int Bin), int> m_clusters = [];
    private readonly List<(Vector3 Position, Vector3 Normal, int Count)> m_sums = [];
    private readonly HashSet<(int A, int B, int C, uint Material)> m_triangles = [];
    private readonly List<uint> m_indices = [];
    private readonly List<uint> m_materials = [];

    /// <summary>Initializes a new instance of the <see cref="SdfMeshCluster"/> class.</summary>
    /// <param name="cell">The grid's cell, in the positions' units, positive and finite.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cell"/> is not positive and finite.</exception>
    public SdfMeshCluster(float cell) {
        if (!(float.IsFinite(f: cell) && (cell > 0f))) { throw new ArgumentOutOfRangeException(paramName: nameof(cell), actualValue: cell, message: "A cell is positive and finite."); }
        m_cell = cell;
        m_inverse = (1f / cell);
    }

    /// <summary>Gets the triangles kept so far.</summary>
    public int TriangleCount => m_materials.Count;

    /// <summary>Adds a vertex and returns the cluster it falls in.</summary>
    /// <param name="position">The vertex's position.</param>
    /// <param name="normal">The vertex's unit normal.</param>
    /// <returns>The cluster, which <see cref="AddTriangle"/> takes.</returns>
    public int Add(Vector3 position, Vector3 normal) {
        var scaled = (position * m_inverse);
        var key = (((int)MathF.Floor(x: scaled.X)), ((int)MathF.Floor(x: scaled.Y)), ((int)MathF.Floor(x: scaled.Z)), BinOf(normal: normal));

        if (!m_clusters.TryGetValue(key: key, value: out var cluster)) {
            cluster = m_sums.Count;
            m_clusters.Add(key: key, value: cluster);
            m_sums.Add(item: default);
        }

        var sum = m_sums[cluster];

        m_sums[cluster] = ((sum.Position + position), (sum.Normal + normal), (sum.Count + 1));
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
        if (!m_triangles.Add(item: Rotated(a: a, b: b, c: c, material: material))) { return; }
        m_indices.Add(item: ((uint)a));
        m_indices.Add(item: ((uint)b));
        m_indices.Add(item: ((uint)c));
        m_materials.Add(item: material);
    }
    /// <summary>Returns the clustered mesh: each cluster's mean position and normal, the triangles kept and their
    /// materials, its <see cref="SdfMesh.BakeCell"/> the cell, with no levels.</summary>
    /// <returns>The mesh; one of no triangles when every triangle collapsed.</returns>
    public SdfMesh Build() {
        var positions = new Vector3[m_sums.Count];
        var normals = new Vector3[m_sums.Count];

        for (var cluster = 0; (cluster < m_sums.Count); cluster++) {
            var (position, normal, count) = m_sums[cluster];
            var length = normal.Length();

            positions[cluster] = (position / count);
            normals[cluster] = ((length > 0f) ? (normal / length) : Vector3.UnitY);
        }
        return new SdfMesh(indices: m_indices.ToArray(), normals: normals, positions: positions, triangleMaterials: m_materials.ToArray()) {
            BakeCell = new Vector3(value: m_cell),
        };
    }

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
    // The triangle's corners turned so the least comes first, keeping its winding.
    private static (int, int, int, uint) Rotated(int a, int b, int c, uint material) => ((a < b) && (a < c))
        ? (a, b, c, material)
        : ((b < c) ? (b, c, a, material) : (c, a, b, material));
}
