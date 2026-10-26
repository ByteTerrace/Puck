using System.Numerics;

namespace Puck.SdfVm;

/// <summary>Partitions a static contour along its existing edges before planar merging and collapse. Every piece's
/// open edges stay fixed through both operations, including every collinear seam vertex, so independently chosen levels
/// meet edge for edge. No new surface or clipping plane is introduced.</summary>
public static class SdfMeshChunks {
    /// <summary>The side of a spatial bin in metres. Triangles belong to their centroid's bin; its box includes their
    /// complete extent, so bin boundaries never clip a triangle.</summary>
    public const float Side = 8f;

    /// <summary>Merges and levels a static union in spatial pieces when it spans more than one chunk.</summary>
    /// <param name="mesh">The contour, with its actual bake cell.</param>
    /// <param name="preserveFeatures">Whether coarser levels limit crease and material-edge error to the largest bake-cell dimension divided by 8.</param>
    /// <returns>The merged mesh, holding independently levelled chunks when larger than one chunk.</returns>
    public static SdfMesh Bake(SdfMesh mesh, bool preserveFeatures = false) {
        ArgumentNullException.ThrowIfNull(mesh);
        var positions = mesh.Positions.Span;
        var low = positions[0];
        var high = low;

        foreach (var position in positions) {
            low = Vector3.Min(value1: low, value2: position);
            high = Vector3.Max(value1: high, value2: position);
        }
        var size = (high - low);

        if (MathF.Max(x: size.X, y: MathF.Max(x: size.Y, y: size.Z)) <= Side) {
            return Merge(mesh: mesh, preserveFeatures: preserveFeatures);
        }

        var bins = new Dictionary<(int X, int Y, int Z), List<int>>();
        var indices = mesh.Indices.Span;

        for (var triangle = 0; (triangle < mesh.TriangleCount); triangle++) {
            var centre = (((((positions[((int)indices[(3 * triangle)])] + positions[((int)indices[((3 * triangle) + 1)])]) + positions[((int)indices[((3 * triangle) + 2)])]) / 3f) - low) / Side);
            var key = (((int)MathF.Floor(x: centre.X)), ((int)MathF.Floor(x: centre.Y)), ((int)MathF.Floor(x: centre.Z)));

            if (!bins.TryGetValue(key: key, value: out var triangles)) {
                triangles = [];
                bins.Add(key: key, value: triangles);
            }
            triangles.Add(item: triangle);
        }
        if (bins.Count == 1) { return Merge(mesh: mesh, preserveFeatures: preserveFeatures); }

        var chunks = new List<SdfMesh>(capacity: bins.Count);

        foreach (var bin in bins.Values) {
            var vertices = new Dictionary<uint, uint>();
            var points = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new uint[(3 * bin.Count)];
            var materials = new uint[(mesh.TriangleMaterials.IsEmpty ? 0 : bin.Count)];

            for (var at = 0; (at < bin.Count); at++) {
                var triangle = bin[at];

                if (materials.Length != 0) { materials[at] = mesh.TriangleMaterials.Span[triangle]; }
                for (var corner = 0; (corner < 3); corner++) {
                    var index = indices[((3 * triangle) + corner)];

                    if (!vertices.TryGetValue(key: index, value: out var local)) {
                        local = ((uint)points.Count);
                        vertices.Add(key: index, value: local);
                        points.Add(item: positions[((int)index)]);
                        if (!mesh.Normals.IsEmpty) { normals.Add(item: mesh.Normals.Span[((int)index)]); }
                    }
                    triangles[((3 * at) + corner)] = local;
                }
            }
            var part = new SdfMesh(positions: points.ToArray(), indices: triangles, normals: normals.ToArray(), triangleMaterials: materials) { BakeCell = mesh.BakeCell };

            chunks.Add(item: Merge(mesh: part, preserveFeatures: preserveFeatures));
        }
        return Join(chunks: chunks);
    }

    private static SdfMesh Merge(SdfMesh mesh, bool preserveFeatures) {
        var merged = SdfMeshPlanarMerge.Merge(mesh: mesh);

        return SdfMeshCollapse.Levels(mesh: merged, preserveFeatures: preserveFeatures);
    }

    /// <summary>Joins chunks' merged surfaces for whole-group queries. Only chunks are stored and drawn; the joined
    /// arrays are a CPU query view, never another resident GPU mesh.</summary>
    /// <param name="chunks">The nonempty list of chunks.</param>
    /// <returns>The whole-group mesh.</returns>
    public static SdfMesh Join(IReadOnlyList<SdfMesh> chunks) {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0) { throw new ArgumentException(message: "A chunked mesh needs chunks.", paramName: nameof(chunks)); }
        var vertices = chunks.Sum(selector: static chunk => chunk.Positions.Length);
        var triangles = chunks.Sum(selector: static chunk => chunk.TriangleCount);
        var positions = new Vector3[vertices];
        var normals = new Vector3[(chunks[0].Normals.IsEmpty ? 0 : vertices)];
        var indices = new uint[(3 * triangles)];
        var materials = new uint[(chunks[0].TriangleMaterials.IsEmpty ? 0 : triangles)];
        var vertex = 0;
        var triangle = 0;

        foreach (var chunk in chunks) {
            chunk.Positions.Span.CopyTo(destination: positions.AsSpan(start: vertex));
            if (normals.Length != 0) { chunk.Normals.Span.CopyTo(destination: normals.AsSpan(start: vertex)); }
            if (materials.Length != 0) { chunk.TriangleMaterials.Span.CopyTo(destination: materials.AsSpan(start: triangle)); }
            for (var index = 0; (index < chunk.Indices.Length); index++) { indices[((3 * triangle) + index)] = (chunk.Indices.Span[index] + ((uint)vertex)); }
            vertex += chunk.Positions.Length;
            triangle += chunk.TriangleCount;
        }
        var joined = new SdfMesh(positions: positions, indices: indices, normals: normals, triangleMaterials: materials) { BakeCell = chunks[0].BakeCell };

        return joined with { Chunks = chunks };
    }
}
