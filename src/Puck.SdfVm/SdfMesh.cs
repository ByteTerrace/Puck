using System.Numerics;

namespace Puck.SdfVm;

/// <summary>An indexed triangle list in object space: opaque geometry a frame draws beside its SDF program.</summary>
/// <remarks>Triangles wind counter-clockwise seen from their front, in the right-handed convention of
/// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>. A world prototype's inline mesh becomes one, placed by its static
/// placements; no pass draws it yet.</remarks>
public sealed record SdfMesh {
    /// <summary>Creates a mesh, refusing a malformed index list or a non-finite position.</summary>
    /// <param name="positions">The object-space vertex positions.</param>
    /// <param name="indices">Three vertex indices per triangle.</param>
    /// <exception cref="ArgumentException">The index count is zero or not a multiple of three, an index names no
    /// vertex, or a position is not finite.</exception>
    public SdfMesh(ReadOnlyMemory<Vector3> positions, ReadOnlyMemory<uint> indices) {
        if (
            indices.IsEmpty ||
            ((indices.Length % 3) != 0)
        ) {
            throw new ArgumentException(
                message: "A mesh needs at least one triangle and three indices per triangle.",
                paramName: nameof(indices)
            );
        }

        foreach (var position in positions.Span) {
            if (
                !float.IsFinite(f: position.X) ||
                !float.IsFinite(f: position.Y) ||
                !float.IsFinite(f: position.Z)
            ) {
                throw new ArgumentException(
                    message: "Every mesh position must be finite.",
                    paramName: nameof(positions)
                );
            }
        }
        foreach (var index in indices.Span) {
            if (index >= ((uint)positions.Length)) {
                throw new ArgumentException(
                    message: $"Index {index} names no vertex of the {positions.Length} the mesh has.",
                    paramName: nameof(indices)
                );
            }
        }

        Positions = positions;
        Indices = indices;
    }

    /// <summary>Gets three vertex indices per triangle.</summary>
    public ReadOnlyMemory<uint> Indices { get; }
    /// <summary>Gets the object-space vertex positions.</summary>
    public ReadOnlyMemory<Vector3> Positions { get; }
    /// <summary>Gets the triangle count.</summary>
    public int TriangleCount => (Indices.Length / 3);
}
/// <summary>One placement of an <see cref="SdfMesh"/> in a frame.</summary>
/// <param name="Mesh">The triangles to draw.</param>
/// <param name="ObjectToWorld">The object-to-world transform, in the row-vector convention of
/// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>.</param>
/// <param name="Material">The material-table index the triangles shade with, as an SDF hit's material does.</param>
public readonly record struct SdfMeshDraw(SdfMesh Mesh, Matrix4x4 ObjectToWorld, int Material);
/// <summary>
/// The raw word layout of the region a frame's mesh draws upload into (<see cref="SdfWorldEngine.MeshRegionLayout"/>),
/// read as a byte-address buffer so every backend reads the same offsets: first one record a draw
/// (<see cref="DrawWords"/> words), then each distinct <see cref="SdfMesh"/> once, however many draws share it, as its
/// positions (three floats a vertex), then those meshes' indices (one word each), meshes in the order their first draw
/// names them.
/// <para>
/// A draw's record is its row-vector object-to-world matrix, row by row (words 0 to 15, <c>M11</c> first), then its
/// material, then its mesh: the mesh's first index within the index section, its index count, and its base vertex
/// within the position section (words 16 to 19), so a draw's index <c>k</c> names the position
/// <c>baseVertex + indices[firstIndex + k]</c>. The record is twenty whole words, so it has no padding a
/// structured-buffer reader could disagree on.
/// </para>
/// </summary>
public static class SdfMeshRegion {
    /// <summary>The words of one draw's record: a 4×4 matrix, a material and the draw's mesh.</summary>
    public const int DrawWords = 20;
    /// <summary>The bytes of one draw's record.</summary>
    public const int DrawBytes = (DrawWords * sizeof(uint));
    /// <summary>The bytes of one index.</summary>
    public const int IndexBytes = sizeof(uint);
    /// <summary>The bytes of one vertex position.</summary>
    public const int PositionBytes = (3 * sizeof(float));

    /// <summary>Counts the region a list of draws needs.</summary>
    /// <param name="draws">The draws.</param>
    /// <returns>One record a draw plus the distinct meshes' positions and indices, in bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> is <see langword="null"/>.</exception>
    public static ulong BytesOf(IReadOnlyList<SdfMeshDraw> draws) =>
        Plan(
            draws: draws,
            meshes: new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance)
        ).Bytes;
    /// <summary>Places each distinct mesh of a list of draws in the region and returns the region's layout.</summary>
    /// <param name="draws">The draws.</param>
    /// <param name="meshes">The scratch map the placements are written into, keyed by reference; cleared first.</param>
    /// <returns>The region's layout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> or <paramref name="meshes"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="OverflowException">The region holds more words than an <see cref="int"/> counts.</exception>
    public static SdfMeshRegionLayout Plan(IReadOnlyList<SdfMeshDraw> draws, Dictionary<SdfMesh, SdfMeshRegionMesh> meshes) {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(meshes);

        meshes.Clear();

        var vertices = 0;
        var indices = 0;

        for (var draw = 0; (draw < draws.Count); draw++) {
            var mesh = draws[draw].Mesh;

            if (meshes.TryAdd(
                key: mesh,
                value: new SdfMeshRegionMesh(
                    BaseVertex: vertices,
                    FirstIndex: indices,
                    IndexCount: mesh.Indices.Length
                )
            )) {
                vertices = checked((vertices + mesh.Positions.Length));
                indices = checked((indices + mesh.Indices.Length));
            }
        }

        _ = checked((((draws.Count * DrawWords) + (vertices * 3)) + indices));

        return new SdfMeshRegionLayout(
            DrawCount: draws.Count,
            IndexCount: indices,
            VertexCount: vertices
        );
    }
    /// <summary>Writes a list of draws into the region's words as <paramref name="layout"/> places them.</summary>
    /// <param name="draws">The draws <see cref="Plan"/> planned.</param>
    /// <param name="meshes">The placements <see cref="Plan"/> wrote for the same draws.</param>
    /// <param name="layout">The layout <see cref="Plan"/> returned for the same draws.</param>
    /// <param name="destination">The region's words; at least <see cref="SdfMeshRegionLayout.Words"/> of them, and only
    /// those are written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> or <paramref name="meshes"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the layout.</exception>
    public static void Write(IReadOnlyList<SdfMeshDraw> draws, Dictionary<SdfMesh, SdfMeshRegionMesh> meshes, SdfMeshRegionLayout layout, Span<uint> destination) {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(meshes);

        if (destination.Length < layout.Words) {
            throw new ArgumentException(
                message: $"The mesh region needs {layout.Words} words; the destination holds {destination.Length}.",
                paramName: nameof(destination)
            );
        }

        for (var draw = 0; (draw < draws.Count); draw++) {
            var (mesh, matrix, material) = draws[draw];
            var placement = meshes[mesh];
            var record = destination.Slice(
                length: DrawWords,
                start: (draw * DrawWords)
            );

            record[0] = BitConverter.SingleToUInt32Bits(value: matrix.M11);
            record[1] = BitConverter.SingleToUInt32Bits(value: matrix.M12);
            record[2] = BitConverter.SingleToUInt32Bits(value: matrix.M13);
            record[3] = BitConverter.SingleToUInt32Bits(value: matrix.M14);
            record[4] = BitConverter.SingleToUInt32Bits(value: matrix.M21);
            record[5] = BitConverter.SingleToUInt32Bits(value: matrix.M22);
            record[6] = BitConverter.SingleToUInt32Bits(value: matrix.M23);
            record[7] = BitConverter.SingleToUInt32Bits(value: matrix.M24);
            record[8] = BitConverter.SingleToUInt32Bits(value: matrix.M31);
            record[9] = BitConverter.SingleToUInt32Bits(value: matrix.M32);
            record[10] = BitConverter.SingleToUInt32Bits(value: matrix.M33);
            record[11] = BitConverter.SingleToUInt32Bits(value: matrix.M34);
            record[12] = BitConverter.SingleToUInt32Bits(value: matrix.M41);
            record[13] = BitConverter.SingleToUInt32Bits(value: matrix.M42);
            record[14] = BitConverter.SingleToUInt32Bits(value: matrix.M43);
            record[15] = BitConverter.SingleToUInt32Bits(value: matrix.M44);
            record[16] = unchecked((uint)material);
            record[17] = ((uint)placement.FirstIndex);
            record[18] = ((uint)placement.IndexCount);
            record[19] = ((uint)placement.BaseVertex);
        }

        foreach (var (mesh, placement) in meshes) {
            var positions = mesh.Positions.Span;
            var vertexWords = destination[(layout.PositionWordOffset + (placement.BaseVertex * 3))..];

            for (var vertex = 0; (vertex < positions.Length); vertex++) {
                vertexWords[(vertex * 3)] = BitConverter.SingleToUInt32Bits(value: positions[vertex].X);
                vertexWords[((vertex * 3) + 1)] = BitConverter.SingleToUInt32Bits(value: positions[vertex].Y);
                vertexWords[((vertex * 3) + 2)] = BitConverter.SingleToUInt32Bits(value: positions[vertex].Z);
            }

            mesh.Indices.Span.CopyTo(destination: destination[(layout.IndexWordOffset + placement.FirstIndex)..]);
        }
    }
}
/// <summary>Where one distinct mesh sits in the mesh region, in the units a draw's record names it by.</summary>
/// <param name="BaseVertex">The mesh's first vertex within the position section.</param>
/// <param name="FirstIndex">The mesh's first index within the index section.</param>
/// <param name="IndexCount">The mesh's index count.</param>
public readonly record struct SdfMeshRegionMesh(int BaseVertex, int FirstIndex, int IndexCount);
/// <summary>The sections of one mesh region (<see cref="SdfMeshRegion"/>), in words from the region's start.</summary>
/// <param name="DrawCount">The draw records, from word zero.</param>
/// <param name="VertexCount">The positions, three words each, from <see cref="PositionWordOffset"/>.</param>
/// <param name="IndexCount">The indices, one word each, from <see cref="IndexWordOffset"/>.</param>
public readonly record struct SdfMeshRegionLayout(int DrawCount, int VertexCount, int IndexCount) {
    /// <summary>Gets the region's size in bytes.</summary>
    public ulong Bytes => (((ulong)Words) * sizeof(uint));
    /// <summary>Gets the word the index section starts at.</summary>
    public int IndexWordOffset => (PositionWordOffset + (VertexCount * 3));
    /// <summary>Gets the word the position section starts at, past the draw records.</summary>
    public int PositionWordOffset => (DrawCount * SdfMeshRegion.DrawWords);
    /// <summary>Gets the region's size in words.</summary>
    public int Words => (IndexWordOffset + IndexCount);
}
