using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// The clusters one mesh is drawn in inside its draw (<see cref="SdfMesh.Clusters"/>): a spatial partition of its own
/// triangles, each cluster with its box, which <see cref="SdfMeshCollapse"/> collapses with the places the clusters share
/// held fixed through the mesh's first <see cref="Levels"/> levels (level zero included), so any cluster may draw any of
/// those levels beside its neighbours drawing any other and the surface stays watertight; the mesh's coarser levels are
/// collapsed whole and drawn whole. Every clustered level's triangles lie cluster by cluster, so a cluster's triangles at
/// a level are one run of that level's (<see cref="FirstTriangle"/>, <see cref="Triangles"/>), counted from the level's
/// first triangle. A view's draw selection (<c>sdf-mesh-occlusion.comp.hlsl</c>) culls each cluster by the view and by
/// occlusion and gives each the level its own box needs, where the draw's own box asks for a clustered level.
/// <para>The invariant: the triangles of the clusters' runs at one level are that level's triangles, each once, and a
/// cluster's triangles at every clustered level lie within its box (a level's vertices sit at the cluster's own places,
/// the shared seam's included). <see cref="SdfMeshCollapse"/> is the one builder, which enforces both.</para>
/// <para>Presentation only, and a pure function of the mesh.</para>
/// </summary>
public sealed class SdfMeshClusters {
    /// <summary>The most clusters one mesh is drawn in (<see cref="SdfMeshRecord.MostClusters"/>).</summary>
    public const int MostClusters = SdfMeshRecord.MostClusters;
    /// <summary>The most levels clusters draw apart, level zero included (<see cref="SdfMeshRecord.MostClusterLevels"/>).</summary>
    public const int MostLevels = SdfMeshRecord.MostClusterLevels;
    /// <summary>The words of a mesh region's cluster table header (<see cref="SdfMeshRecord.ClusterHeaderWords"/>).</summary>
    public const int HeaderWords = SdfMeshRecord.ClusterHeaderWords;
    /// <summary>The words of one cluster's box in the table (<see cref="SdfMeshRecord.ClusterBoxWords"/>).</summary>
    public const int BoxWords = SdfMeshRecord.ClusterBoxWords;
    /// <summary>The words of one cluster's run at one level in the table (<see cref="SdfMeshRecord.ClusterRunWords"/>).</summary>
    public const int RunWords = SdfMeshRecord.ClusterRunWords;

    private readonly Vector3[] m_low;
    private readonly Vector3[] m_high;
    private readonly int[] m_triangles;

    /// <summary>Initializes a new instance of the <see cref="SdfMeshClusters"/> class.</summary>
    /// <param name="low">Each cluster's least corner, in object space.</param>
    /// <param name="high">Each cluster's greatest corner.</param>
    /// <param name="triangles">Each clustered level's triangles in each cluster, level by level, cluster by cluster
    /// within a level: <paramref name="low"/>'s length times the clustered levels.</param>
    /// <exception cref="ArgumentException">There are fewer than two or more than <see cref="MostClusters"/> clusters, the
    /// corners' lengths differ, or <paramref name="triangles"/> holds no whole number of levels, none, or more than
    /// <see cref="MostLevels"/>.</exception>
    public SdfMeshClusters(Vector3[] low, Vector3[] high, int[] triangles) {
        ArgumentNullException.ThrowIfNull(argument: low);
        ArgumentNullException.ThrowIfNull(argument: high);
        ArgumentNullException.ThrowIfNull(argument: triangles);
        if ((low.Length < 2) || (low.Length > MostClusters) || (high.Length != low.Length) || (triangles.Length == 0) ||
            ((triangles.Length % low.Length) != 0) || ((triangles.Length / low.Length) > MostLevels)) {
            throw new ArgumentException(message: $"Clusters number from 2 to {MostClusters}, each with a box and a run at each of 1 to {MostLevels} levels.", paramName: nameof(triangles));
        }
        m_low = low;
        m_high = high;
        m_triangles = triangles;
    }

    /// <summary>Gets the number of clusters.</summary>
    public int Count => m_low.Length;
    /// <summary>Gets the number of the mesh's levels the clusters draw apart, level zero included.</summary>
    public int Levels => (m_triangles.Length / m_low.Length);
    /// <summary>Gets the words the clusters take in a mesh region's table after the mesh's level table.</summary>
    public int TableWords => (HeaderWords + (Count * (BoxWords + (Levels * RunWords))));

    /// <summary>Returns a cluster's least corner.</summary>
    /// <param name="cluster">The cluster, below <see cref="Count"/>.</param>
    /// <returns>The corner, in object space.</returns>
    public Vector3 Low(int cluster) => m_low[cluster];
    /// <summary>Returns a cluster's greatest corner.</summary>
    /// <param name="cluster">The cluster, below <see cref="Count"/>.</param>
    /// <returns>The corner, in object space.</returns>
    public Vector3 High(int cluster) => m_high[cluster];
    /// <summary>Returns a cluster's triangles at a clustered level.</summary>
    /// <param name="level">The level, below <see cref="Levels"/>.</param>
    /// <param name="cluster">The cluster, below <see cref="Count"/>.</param>
    /// <returns>The triangles.</returns>
    public int Triangles(int level, int cluster) => m_triangles[((level * Count) + cluster)];
    /// <summary>Returns a cluster's first triangle at a clustered level, counted from the level's first triangle.</summary>
    /// <param name="level">The level, below <see cref="Levels"/>.</param>
    /// <param name="cluster">The cluster, below <see cref="Count"/>.</param>
    /// <returns>The triangle.</returns>
    public int FirstTriangle(int level, int cluster) {
        var first = 0;

        for (var before = 0; (before < cluster); before++) { first += Triangles(cluster: before, level: level); }
        return first;
    }

    /// <summary>Writes the clusters' table, as the mesh region holds it after the mesh's level table: the cluster count
    /// and clustered level count, each cluster's box (center then half extent, as floats), then each clustered level's
    /// runs cluster by cluster (first triangle among the mesh's levelled triangles, triangle count).</summary>
    /// <param name="levelFirst">The first triangle of each clustered level among the mesh's levelled triangles.</param>
    /// <param name="destination">The table's words, at least <see cref="TableWords"/>.</param>
    public void Write(ReadOnlySpan<int> levelFirst, Span<uint> destination) {
        destination[0] = ((uint)Count);
        destination[1] = ((uint)Levels);

        var at = HeaderWords;

        for (var cluster = 0; (cluster < Count); cluster++) {
            var center = ((m_low[cluster] + m_high[cluster]) * 0.5f);
            var half = ((m_high[cluster] - m_low[cluster]) * 0.5f);

            destination[at++] = BitConverter.SingleToUInt32Bits(value: center.X);
            destination[at++] = BitConverter.SingleToUInt32Bits(value: center.Y);
            destination[at++] = BitConverter.SingleToUInt32Bits(value: center.Z);
            destination[at++] = BitConverter.SingleToUInt32Bits(value: half.X);
            destination[at++] = BitConverter.SingleToUInt32Bits(value: half.Y);
            destination[at++] = BitConverter.SingleToUInt32Bits(value: half.Z);
        }
        for (var level = 0; (level < Levels); level++) {
            var first = levelFirst[level];

            for (var cluster = 0; (cluster < Count); cluster++) {
                var triangles = Triangles(cluster: cluster, level: level);

                destination[at++] = ((uint)first);
                destination[at++] = ((uint)triangles);
                first += triangles;
            }
        }
    }
}
