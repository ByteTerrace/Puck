using System.Numerics;
using Puck.Abstractions.Memory;

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
    /// <param name="owner">The workspace owner charged for the merge's, the binning's and each collapse's tables, or <see langword="null"/> for none.</param>
    /// <returns>The merged mesh, holding independently levelled chunks when larger than one chunk.</returns>
    /// <param name="allocator">The native allocator the merge's, the binning's and each collapse's lists come from, so a background bake's scratch never reaches the collector, or <see langword="null"/> to rent them.</param>
    public static SdfMesh Bake(SdfMesh mesh, bool preserveFeatures = false, IAllocationObserver? owner = null, IAllocator? allocator = null) {
        ArgumentNullException.ThrowIfNull(mesh);
        // A split introduces open edges that must remain fixed for independent levels. Merge the original planar
        // surface first, so a flat face does not retain an artificial row of contour vertices at every spatial seam.
        mesh = SdfMeshPlanarMerge.Merge(allocator: allocator, mesh: mesh, owner: owner);
        if (mesh.TriangleCount == 0) { return mesh; }
        var positions = mesh.Positions.Span;
        var low = mesh.PositionLow;
        var size = (mesh.PositionHigh - low);

        if (MathF.Max(x: size.X, y: MathF.Max(x: size.Y, y: size.Z)) <= Side) {
            return SdfMeshCollapse.Levels(allocator: allocator, mesh: mesh, preserveFeatures: preserveFeatures, owner: owner);
        }

        using var scope = new RentedScope(allocator: allocator, owner: owner);
        var triangleCount = mesh.TriangleCount;
        var bins = new Dictionary<(int X, int Y, int Z), int>();
        var binOf = scope.Rent<int>(minimumLength: triangleCount);
        var indices = mesh.Indices.Span;

        // A mesh spread wider than MostChunks bins (the world group's far ground) takes bins of twice the side until it
        // fills no more: at 8 m it split into 160,000 draws, and every pass culls and levels each. A bin is numbered as
        // its first triangle is met, and binOf names each triangle's bin.
        for (var side = Side; ; side *= 2f) {
            bins.Clear();

            var filled = true;

            for (var triangle = 0; (triangle < triangleCount); triangle++) {
                var centre = (((((positions[((int)indices[(3 * triangle)])] + positions[((int)indices[((3 * triangle) + 1)])]) + positions[((int)indices[((3 * triangle) + 2)])]) / 3f) - low) / side);
                var key = (((int)MathF.Floor(x: centre.X)), ((int)MathF.Floor(x: centre.Y)), ((int)MathF.Floor(x: centre.Z)));

                if (!bins.TryGetValue(key: key, value: out var bin)) {
                    if (bins.Count >= MostChunks) {
                        filled = false;
                        break;
                    }
                    bin = bins.Count;
                    bins.Add(key: key, value: bin);
                }
                binOf[triangle] = bin;
            }
            if (filled) { break; }
        }
        if (bins.Count == 1) { return SdfMeshCollapse.Levels(allocator: allocator, mesh: mesh, preserveFeatures: preserveFeatures, owner: owner); }

        // Each bin's triangles, in triangle order: its first triangle's place in `order` is starts[bin].
        var parts = bins.Count;
        var starts = scope.Rent<int>(minimumLength: (parts + 1), clear: true);
        var order = scope.Rent<int>(minimumLength: triangleCount);

        for (var triangle = 0; (triangle < triangleCount); triangle++) { starts[(binOf[triangle] + 1)]++; }
        for (var part = 0; (part < parts); part++) { starts[(part + 1)] += starts[part]; }

        var filledTo = scope.Rent<int>(minimumLength: parts, clear: true);

        for (var triangle = 0; (triangle < triangleCount); triangle++) {
            var part = binOf[triangle];

            order[(starts[part] + filledTo[part]++)] = triangle;
        }

        var vertexCounts = new int[parts];
        var seen = scope.Rent<int>(minimumLength: positions.Length, clear: true);
        var vertexCount = 0;
        for (var part = 0; part < parts; part++) {
            for (var at = starts[part]; (at < starts[(part + 1)]); at++) {
                foreach (var source in indices.Slice(3 * order[at], 3)) {
                    if (seen[source] == part + 1) { continue; }
                    seen[source] = part + 1;
                    vertexCounts[part]++;
                }
            }
            vertexCount += vertexCounts[part];
        }

        var joinedPositions = new Vector3[vertexCount];
        var joinedNormals = new Vector3[mesh.Normals.IsEmpty ? 0 : vertexCount];
        var joinedIndices = new uint[indices.Length];
        var joinedMaterials = new uint[mesh.TriangleMaterials.Length];
        var chunks = new SdfMesh[parts];
        var localOf = scope.Rent<int>(minimumLength: positions.Length);
        var sourceSites = mesh.Sites();
        var siteOf = scope.Rent<int>(minimumLength: positions.Length);
        siteOf.AsSpan(start: 0, length: positions.Length).Fill(value: -1);
        seen.AsSpan(start: 0, length: positions.Length).Clear();
        var vertexBase = 0;
        var triangleBase = 0;
        for (var part = 0; part < parts; part++) {
            var binTriangles = (starts[(part + 1)] - starts[part]);
            var localIndices = new uint[3 * binTriangles];
            var localSites = new int[vertexCounts[part]];
            var count = 0;
            for (var at = 0; at < binTriangles; at++) {
                var triangle = order[(starts[part] + at)];
                if (joinedMaterials.Length > 0) { joinedMaterials[triangleBase + at] = mesh.TriangleMaterials.Span[triangle]; }
                for (var corner = 0; corner < 3; corner++) {
                    var source = indices[3 * triangle + corner];
                    if (seen[source] != part + 1) {
                        seen[source] = part + 1;
                        localOf[source] = count;
                        var vertex = vertexBase + count;
                        ref var site = ref siteOf[sourceSites[source]];
                        if (site < vertexBase) { site = vertex; }
                        localSites[count++] = site - vertexBase;
                        joinedPositions[vertex] = positions[(int)source];
                        if (joinedNormals.Length > 0) { joinedNormals[vertex] = mesh.Normals.Span[(int)source]; }
                    }
                    localIndices[3 * at + corner] = (uint)localOf[source];
                    joinedIndices[3 * (triangleBase + at) + corner] = (uint)(vertexBase + localOf[source]);
                }
            }
            chunks[part] = new SdfMesh(
                joinedPositions.AsMemory(vertexBase, count), localIndices,
                joinedNormals.Length == 0 ? default : joinedNormals.AsMemory(vertexBase, count),
                joinedMaterials.Length == 0 ? default : joinedMaterials.AsMemory(triangleBase, binTriangles)) {
                BakeCell = mesh.BakeCell, BakedSites = localSites,
            };
            vertexBase += count;
            triangleBase += binTriangles;
        }
        Parallel.For(0, chunks.Length, part => {
            chunks[part] = SdfMeshCollapse.Levels(chunks[part], preserveFeatures, owner, allocator) with { BakedSites = null };
        });
        return new SdfMesh(joinedPositions, joinedIndices, joinedNormals, joinedMaterials) { BakeCell = mesh.BakeCell, Chunks = chunks };
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
