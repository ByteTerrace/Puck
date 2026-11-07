using System.Numerics;

namespace Puck.SdfVm;

/// <summary>Merges a static contour's planar regions, then partitions it along existing edges before collapse. Every piece's
/// open edges stay fixed through collapse, including every collinear seam vertex, so independently chosen levels
/// meet edge for edge. No new surface or clipping plane is introduced.</summary>
public static class SdfMeshChunks {
    /// <summary>The side of a spatial bin in metres. Triangles belong to their centroid's bin; its box includes their
    /// complete extent, so bin boundaries never clip a triangle.</summary>
    public const float Side = 8f;
    /// <summary>The contour triangles past which one static shape's mesh is chunked as a union's is, so its collapse
    /// runs in pieces at once rather than on one worker for seconds. Below it a chunk's locked seams would only
    /// add triangles to its coarser levels (a terrain sector's first level tripled).</summary>
    public const int WholeTriangles = 131_072;
    /// <summary>The most chunks one mesh is cut into: a mesh that would fill more bins of <see cref="Side"/> takes bins of
    /// twice the side, until it fills no more.</summary>
    public const int MostChunks = 64;

    /// <summary>Merges a static group's planar regions before introducing spatial seams, then levels its pieces at once
    /// when it spans more than one chunk.</summary>
    /// <param name="mesh">The contour, with its actual bake cell.</param>
    /// <param name="preserveFeatures">Whether coarser levels limit crease and material-edge error to the largest bake-cell dimension divided by 8.</param>
    /// <returns>The merged mesh, holding independently levelled chunks when larger than one chunk.</returns>
    public static SdfMesh Bake(SdfMesh mesh, bool preserveFeatures = false) {
        ArgumentNullException.ThrowIfNull(mesh);
        // A split introduces open edges that must remain fixed for independent levels. Merge the original planar
        // surface first, so a flat face does not retain an artificial row of contour vertices at every spatial seam.
        mesh = SdfMeshPlanarMerge.Merge(mesh: mesh);
        if (mesh.TriangleCount == 0) { return mesh; }
        var positions = mesh.Positions.Span;
        var low = mesh.PositionLow;
        var size = (mesh.PositionHigh - low);

        if (MathF.Max(x: size.X, y: MathF.Max(x: size.Y, y: size.Z)) <= Side) {
            return SdfMeshCollapse.Levels(mesh: mesh, preserveFeatures: preserveFeatures);
        }

        var bins = new Dictionary<(int X, int Y, int Z), List<int>>();
        var indices = mesh.Indices.Span;

        // A mesh spread wider than MostChunks bins (the world group's far ground) takes bins of twice the side until it
        // fills no more: at 8 m it split into 160,000 draws, and every pass culls and levels each.
        for (var side = Side; ; side *= 2f) {
            bins.Clear();
            for (var triangle = 0; (triangle < mesh.TriangleCount); triangle++) {
                var centre = (((((positions[((int)indices[(3 * triangle)])] + positions[((int)indices[((3 * triangle) + 1)])]) + positions[((int)indices[((3 * triangle) + 2)])]) / 3f) - low) / side);
                var key = (((int)MathF.Floor(x: centre.X)), ((int)MathF.Floor(x: centre.Y)), ((int)MathF.Floor(x: centre.Z)));

                if (!bins.TryGetValue(key: key, value: out var triangles)) {
                    if (bins.Count >= MostChunks) { break; }
                    triangles = [];
                    bins.Add(key: key, value: triangles);
                }
                triangles.Add(item: triangle);
            }
            if (bins.Values.Sum(selector: static triangles => triangles.Count) == mesh.TriangleCount) { break; }
        }
        if (bins.Count == 1) { return SdfMeshCollapse.Levels(mesh: mesh, preserveFeatures: preserveFeatures); }

        var parts = new List<SdfMesh>(capacity: bins.Count);

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
            parts.Add(item: new SdfMesh(positions: points.ToArray(), indices: triangles, normals: normals.ToArray(), triangleMaterials: materials) { BakeCell = mesh.BakeCell });
        }

        // Each piece's open edges stay fixed, so the pieces level independently, at once, each into its own
        // place in the bins' order.
        var chunks = new SdfMesh[parts.Count];

        _ = Parallel.For(fromInclusive: 0, toExclusive: parts.Count, body: index => chunks[index] = SdfMeshCollapse.Levels(mesh: parts[index], preserveFeatures: preserveFeatures));

        return Join(chunks: chunks);
    }

    /// <summary>Merges a contour into its planar polygons (<see cref="SdfMeshPlanarMerge"/>) and, unless
    /// <paramref name="levels"/> is false, collapses the merged mesh into its coarser levels (<see cref="SdfMeshCollapse"/>):
    /// what every landed mesh, and each chunk of a chunked one, is made of.</summary>
    /// <param name="mesh">The contour, with its actual bake cell.</param>
    /// <param name="preserveFeatures">Whether coarser levels limit crease and material-edge error to the largest bake-cell
    /// dimension divided by 8.</param>
    /// <param name="levels">Whether the mesh takes coarser levels; glass, which its pass draws at level zero alone, does
    /// not.</param>
    /// <returns>The merged mesh, levelled or not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static SdfMesh Merge(SdfMesh mesh, bool preserveFeatures, bool levels = true) {
        ArgumentNullException.ThrowIfNull(argument: mesh);

        var merged = SdfMeshPlanarMerge.Merge(mesh: mesh);

        return (levels ? SdfMeshCollapse.Levels(mesh: merged, preserveFeatures: preserveFeatures) : merged);
    }

    /// <summary>Joins chunks' merged surfaces for whole-group queries. Only chunks are stored and drawn; the joined
    /// arrays are a CPU query view, never another resident GPU mesh. The joined mesh holds the vertex and triangle
    /// attributes once: its chunks are views of its positions, normals and triangle materials, in order, each keeping its
    /// own indices and levels, and a native copy keeps that sharing and drops the joined indices
    /// (<see cref="SdfMesh.StorageBytes"/>, <see cref="SdfMesh.Indices"/>).</summary>
    /// <param name="chunks">The nonempty list of chunks, none of them skinned.</param>
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
        var views = new SdfMesh[chunks.Count];

        vertex = 0;
        triangle = 0;
        for (var index = 0; (index < views.Length); index++) {
            var chunk = chunks[index];

            views[index] = new SdfMesh(
                positions: positions.AsMemory(start: vertex, length: chunk.Positions.Length),
                indices: chunk.Indices,
                normals: ((normals.Length == 0) ? default : normals.AsMemory(start: vertex, length: chunk.Positions.Length)),
                triangleMaterials: ((materials.Length == 0) ? default : materials.AsMemory(start: triangle, length: chunk.TriangleCount))) {
                BakeCell = chunk.BakeCell,
                Levels = chunk.Levels,
            };
            vertex += chunk.Positions.Length;
            triangle += chunk.TriangleCount;
        }

        return new SdfMesh(positions: positions, indices: indices, normals: normals, triangleMaterials: materials) { BakeCell = chunks[0].BakeCell, Chunks = views };
    }
}
