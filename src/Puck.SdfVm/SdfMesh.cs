using System.Numerics;

namespace Puck.SdfVm;

/// <summary>An indexed triangle list in object space: opaque geometry a frame draws beside its SDF program.</summary>
/// <remarks>Triangles wind counter-clockwise seen from their front, in the right-handed convention of
/// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>. A world prototype's inline mesh becomes one, placed by its
/// placements and stamps, and so does a prototype's baked mesh, which also carries each vertex's normal and texture
/// coordinate, each triangle's palette entry, and its surface textures. The mesh pass rasterizes it.</remarks>
public sealed record SdfMesh {
    /// <summary>Creates a mesh, refusing a malformed index list, a non-finite vertex, or a vertex or triangle attribute
    /// of the wrong length.</summary>
    /// <param name="positions">The object-space vertex positions.</param>
    /// <param name="indices">Three vertex indices per triangle.</param>
    /// <param name="normals">One object-space normal per vertex, which the hit's normal interpolates across a triangle,
    /// or empty for each triangle's face normal.</param>
    /// <param name="uvs">One texture coordinate per vertex, or empty for none.</param>
    /// <param name="triangleMaterials">One palette entry per triangle, added to its draw's material, or empty for the
    /// draw's material on every triangle.</param>
    /// <param name="textures">The surface textures the texture coordinates address, or <see langword="null"/> for
    /// none.</param>
    /// <exception cref="ArgumentException">The index count is zero or not a multiple of three, the mesh has more than
    /// <see cref="MaxTriangles"/> triangles, an index names no vertex, a position, normal or texture coordinate is not
    /// finite, an attribute is neither empty nor one per vertex (one per triangle for
    /// <paramref name="triangleMaterials"/>), or the mesh has textures but no texture coordinates.</exception>
    public SdfMesh(ReadOnlyMemory<Vector3> positions, ReadOnlyMemory<uint> indices, ReadOnlyMemory<Vector3> normals = default, ReadOnlyMemory<Vector2> uvs = default, ReadOnlyMemory<uint> triangleMaterials = default, SdfMeshTextures? textures = null) {
        if (
            indices.IsEmpty ||
            ((indices.Length % 3) != 0)
        ) {
            throw new ArgumentException(
                message: "A mesh needs at least one triangle and three indices per triangle.",
                paramName: nameof(indices)
            );
        }
        if ((indices.Length / 3) > MaxTriangles) {
            throw new ArgumentException(
                message: $"A mesh holds at most {MaxTriangles} triangles, each numbered by a value the mesh target's float holds exactly; this one has {(indices.Length / 3)}.",
                paramName: nameof(indices)
            );
        }

        foreach (var position in positions.Span) {
            if (!IsFinite(value: position)) {
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

        RequireAttribute(
            count: normals.Length,
            expected: positions.Length,
            name: nameof(normals),
            per: "vertex"
        );
        RequireAttribute(
            count: uvs.Length,
            expected: positions.Length,
            name: nameof(uvs),
            per: "vertex"
        );
        RequireAttribute(
            count: triangleMaterials.Length,
            expected: (indices.Length / 3),
            name: nameof(triangleMaterials),
            per: "triangle"
        );

        foreach (var normal in normals.Span) {
            if (!IsFinite(value: normal)) {
                throw new ArgumentException(
                    message: "Every mesh normal must be finite.",
                    paramName: nameof(normals)
                );
            }
        }
        foreach (var uv in uvs.Span) {
            if (!(float.IsFinite(f: uv.X) && float.IsFinite(f: uv.Y))) {
                throw new ArgumentException(
                    message: "Every mesh texture coordinate must be finite.",
                    paramName: nameof(uvs)
                );
            }
        }

        if ((textures is not null) && uvs.IsEmpty) {
            throw new ArgumentException(
                message: "A mesh with surface textures carries a texture coordinate per vertex to address them.",
                paramName: nameof(textures)
            );
        }

        Positions = positions;
        Indices = indices;
        Normals = normals;
        Uvs = uvs;
        TriangleMaterials = triangleMaterials;
        Textures = textures;
    }

    /// <summary>The most triangles a mesh holds: the mesh pass writes a covered pixel's triangle as a float, which holds
    /// every whole number up to 2^24 exactly, so the last triangle is 2^24 - 1.</summary>
    public const int MaxTriangles = (1 << 24);

    /// <summary>Gets three vertex indices per triangle.</summary>
    public ReadOnlyMemory<uint> Indices { get; }
    /// <summary>Gets one object-space normal per vertex, or none.</summary>
    public ReadOnlyMemory<Vector3> Normals { get; }
    /// <summary>Gets the object-space vertex positions.</summary>
    public ReadOnlyMemory<Vector3> Positions { get; }
    /// <summary>Gets the triangle count.</summary>
    public int TriangleCount => (Indices.Length / 3);
    /// <summary>Gets the surface textures the texture coordinates address, or <see langword="null"/>.</summary>
    public SdfMeshTextures? Textures { get; }
    /// <summary>Gets one palette entry per triangle, added to its draw's material, or none.</summary>
    public ReadOnlyMemory<uint> TriangleMaterials { get; }
    /// <summary>Gets one texture coordinate per vertex, or none.</summary>
    public ReadOnlyMemory<Vector2> Uvs { get; }

    private static bool IsFinite(Vector3 value) => (
        float.IsFinite(f: value.X) &&
        float.IsFinite(f: value.Y) &&
        float.IsFinite(f: value.Z)
    );
    private static void RequireAttribute(int count, int expected, string name, string per) {
        if ((count != 0) && (count != expected)) {
            throw new ArgumentException(
                message: $"A mesh's {name} hold one entry per {per} ({expected}) or none; there are {count}.",
                paramName: name
            );
        }
    }
}
/// <summary>One placement of an <see cref="SdfMesh"/> in a frame.</summary>
/// <param name="Mesh">The triangles to draw.</param>
/// <param name="ObjectToWorld">The object-to-world transform, in the row-vector convention of
/// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>.</param>
/// <param name="Material">The material-table index the triangles shade with, as an SDF hit's material does; a mesh
/// with <see cref="SdfMesh.TriangleMaterials"/> adds each triangle's entry to it.</param>
/// <param name="Identity">What the draw is across frames, compared with <see cref="object.Equals(object)"/>: the draw
/// at an index whose identity differs from the one staged there before has no motion history, so the tables seed its
/// previous object-to-world from this frame's rather than reading another draw's pose as motion.</param>
public readonly record struct SdfMeshDraw(SdfMesh Mesh, Matrix4x4 ObjectToWorld, int Material, object Identity) {
    /// <summary>Gets the draw's level of detail when it is one of a baked placement's two representations, the mesh or the
    /// impostor (<see cref="SdfMeshLod"/>): a view records the draw of the pair its projected size selects. Null for a draw
    /// every view records.</summary>
    public SdfMeshLod? Lod { get; init; }
    /// <summary>Gets the impostor a card draw shows: the draw's mesh is then the card (<see cref="SdfMeshCard.Mesh"/>),
    /// which the mesh pass places in front of the impostor's sphere facing each view, and the hit passes find the surface
    /// behind each of its pixels in the impostor's views. Null for a mesh draw.</summary>
    public SdfMeshImpostor? Impostor { get; init; }
}
/// <summary>The mesh every impostor draw uses: a unit quad of two triangles, whose vertex positions are the card's corners
/// as fractions of its half extent. The mesh pass reads only their signs, so one mesh serves every impostor.</summary>
public static class SdfMeshCard {
    /// <summary>Gets the card's mesh: corners <c>(-1, -1)</c>, <c>(1, -1)</c>, <c>(1, 1)</c> and <c>(-1, 1)</c> in the plane
    /// of zero <c>z</c>, wound counter-clockwise.</summary>
    public static SdfMesh Mesh { get; } = new(
        indices: new uint[] { 0u, 1u, 2u, 0u, 2u, 3u },
        positions: new Vector3[] {
            new(x: -1f, y: -1f, z: 0f),
            new(x: 1f, y: -1f, z: 0f),
            new(x: 1f, y: 1f, z: 0f),
            new(x: -1f, y: 1f, z: 0f),
        }
    );
}
/// <summary>
/// The raw word layout of the region a frame's mesh draws upload into (<see cref="SdfWorldTables.MeshRegionLayout"/>),
/// read as a structured buffer of uints so every backend reads the same word offsets: first one record a draw
/// (<see cref="DrawWords"/> words), then each distinct <see cref="SdfMesh"/> once, however many draws share it, as its
/// vertices (<see cref="VertexWords"/> words each: position, normal and texture coordinate), then those meshes' triangle
/// materials (one word each, for a mesh that has them), then their indices (one word each), meshes in the order their
/// first draw names them.
/// <para>
/// A draw's record is its row-vector object-to-world matrix, row by row (words 0 to 15, <c>M11</c> first), then its
/// material, then its mesh: the word its first index sits at, its index count, the word its first vertex sits at, its
/// attribute flags (<see cref="NormalsFlag"/>, <see cref="MaterialsFlag"/>, <see cref="TexturesFlag"/>), and the word
/// its first triangle material
/// sits at (words 16 to 21), then its normal matrix, the inverse transpose of the matrix's upper 3×3, row by row (words
/// 22 to 30), which carries an object-space normal to world space under any scale, nonuniform and mirrored included
/// (a singular matrix, which draws no area, writes its own upper 3×3 instead), then its impostor (words 31 to 40, zero
/// for a draw without one, <see cref="ImpostorFlag"/> clear): the bounding sphere's object-space center (31 to 33) and
/// radius (34), the views along each side of the view grid (35), the texels along each side of a view (36), and where
/// the impostor's rectangle sits in the impostor atlases (37 to 40, the scale then the offset,
/// <see cref="SdfMeshAtlas.Placement"/>). Its word offsets are counted from the
/// region's start, so a reader needs nothing but the record to find a draw's triangles: its index <c>k</c> is the word at <c>indexWord + k</c>, and names the vertex whose eight words
/// start at <c>vertexWord + (8 * index)</c>. A vertex without a normal or texture coordinate holds zeros there, and
/// the flags say which a mesh has. A mesh whose textures the mesh atlases hold (<see cref="SdfMeshAtlas"/>) carries
/// <see cref="TexturesFlag"/>, and its vertices' texture coordinates are written moved into the atlases, so a reader
/// samples them as they stand. The record is forty-one whole words, so it has no padding a structured-buffer reader
/// could disagree on.
/// </para>
/// </summary>
public static class SdfMeshRegion {
    /// <summary>The words of one draw's record: a 4×4 matrix, a material, the draw's mesh, its normal matrix and its
    /// impostor.</summary>
    public const int DrawWords = 41;
    /// <summary>The bytes of one draw's record.</summary>
    public const int DrawBytes = (DrawWords * sizeof(uint));
    /// <summary>The most draws one region holds: the mesh pass pushes a draw's index in the bits below
    /// <see cref="SdfKernelInterfaces.MeshViewShift"/>, and writes a covered pixel's draw plus one as a float, which holds
    /// 2^24 exactly.</summary>
    public const int MaxDraws = (1 << SdfKernelInterfaces.MeshViewShift);
    /// <summary>The bytes of one index.</summary>
    public const int IndexBytes = sizeof(uint);
    /// <summary>The words of one vertex: its position, its normal and its texture coordinate, as floats.</summary>
    public const int VertexWords = 8;
    /// <summary>The bytes of one vertex.</summary>
    public const int VertexBytes = (VertexWords * sizeof(float));
    /// <summary>The flag a record carries when its mesh has <see cref="SdfMesh.Normals"/>.</summary>
    public const uint NormalsFlag = 1u;
    /// <summary>The flag a record carries when its mesh has <see cref="SdfMesh.TriangleMaterials"/>.</summary>
    public const uint MaterialsFlag = 2u;
    /// <summary>The flag a record carries when the mesh atlases hold its mesh's <see cref="SdfMesh.Textures"/>.</summary>
    public const uint TexturesFlag = 4u;
    /// <summary>The flag a record carries when it is an impostor card (<see cref="SdfMeshDraw.Impostor"/>) whose views the
    /// impostor atlases hold.</summary>
    public const uint ImpostorFlag = 8u;

    /// <summary>Counts the region a list of draws needs.</summary>
    /// <param name="draws">The draws.</param>
    /// <returns>One record a draw plus the distinct meshes' vertices, triangle materials and indices, in bytes.</returns>
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
    /// <exception cref="ArgumentException">The list holds more than <see cref="MaxDraws"/> draws.</exception>
    /// <exception cref="OverflowException">The region holds more words than an <see cref="int"/> counts.</exception>
    public static SdfMeshRegionLayout Plan(IReadOnlyList<SdfMeshDraw> draws, Dictionary<SdfMesh, SdfMeshRegionMesh> meshes) {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(meshes);

        if (draws.Count > MaxDraws) {
            throw new ArgumentException(
                message: $"A mesh region holds at most {MaxDraws} draws; the list holds {draws.Count}.",
                paramName: nameof(draws)
            );
        }


        meshes.Clear();

        var vertices = 0;
        var materials = 0;
        var indices = 0;

        for (var draw = 0; (draw < draws.Count); draw++) {
            var mesh = draws[draw].Mesh;

            if (meshes.TryAdd(
                key: mesh,
                value: new SdfMeshRegionMesh(
                    BaseVertex: vertices,
                    FirstIndex: indices,
                    FirstMaterial: materials,
                    IndexCount: mesh.Indices.Length
                )
            )) {
                vertices = checked((vertices + mesh.Positions.Length));
                materials = checked((materials + mesh.TriangleMaterials.Length));
                indices = checked((indices + mesh.Indices.Length));
            }
        }

        _ = checked(((((draws.Count * DrawWords) + (vertices * VertexWords)) + materials) + indices));

        return new SdfMeshRegionLayout(
            DrawCount: draws.Count,
            IndexCount: indices,
            MaterialCount: materials,
            VertexCount: vertices
        );
    }
    /// <summary>Writes a list of draws into the region's words as <paramref name="layout"/> places them.</summary>
    /// <param name="draws">The draws <see cref="Plan"/> planned.</param>
    /// <param name="meshes">The placements <see cref="Plan"/> wrote for the same draws.</param>
    /// <param name="layout">The layout <see cref="Plan"/> returned for the same draws.</param>
    /// <param name="destination">The region's words; at least <see cref="SdfMeshRegionLayout.Words"/> of them, and only
    /// those are written.</param>
    /// <param name="atlas">The mesh atlases the frame binds, or <see langword="null"/> when it binds none: a mesh whose
    /// textures they hold is written with <see cref="TexturesFlag"/> and its texture coordinates moved into them.</param>
    /// <param name="impostors">The impostor atlases the frame binds, or <see langword="null"/> when it binds none: a card
    /// draw whose impostor they hold is written with <see cref="ImpostorFlag"/> and its atlas rectangle.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> or <paramref name="meshes"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the layout.</exception>
    public static void Write(IReadOnlyList<SdfMeshDraw> draws, Dictionary<SdfMesh, SdfMeshRegionMesh> meshes, SdfMeshRegionLayout layout, Span<uint> destination, SdfMeshAtlas? atlas = null, SdfMeshAtlas? impostors = null) {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(meshes);

        if (destination.Length < layout.Words) {
            throw new ArgumentException(
                message: $"The mesh region needs {layout.Words} words; the destination holds {destination.Length}.",
                paramName: nameof(destination)
            );
        }

        for (var draw = 0; (draw < draws.Count); draw++) {
            var (mesh, matrix, material, _) = draws[draw];
            var impostor = (((draws[draw].Impostor is { } shown) && (impostors?.Holds(textures: shown) ?? false))
                ? shown
                : null);
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
            record[17] = ((uint)(layout.IndexWordOffset + placement.FirstIndex));
            record[18] = ((uint)placement.IndexCount);
            record[19] = ((uint)(layout.VertexWordOffset + (placement.BaseVertex * VertexWords)));
            record[20] = (mesh.Normals.IsEmpty ? 0u : NormalsFlag) | (mesh.TriangleMaterials.IsEmpty ? 0u : MaterialsFlag) | (Textured(atlas: atlas, mesh: mesh) ? TexturesFlag : 0u) | ((impostor is null) ? 0u : ImpostorFlag);
            record[21] = ((uint)(layout.MaterialWordOffset + placement.FirstMaterial));
            WriteNormalMatrix(
                matrix: matrix,
                record: record[22..]
            );
            WriteImpostor(
                atlas: impostors,
                impostor: impostor,
                record: record[31..]
            );
        }

        foreach (var (mesh, placement) in meshes) {
            var positions = mesh.Positions.Span;
            var normals = mesh.Normals.Span;
            var uvs = mesh.Uvs.Span;
            var vertexWords = destination[(layout.VertexWordOffset + (placement.BaseVertex * VertexWords))..];
            var atlasPlacement = (Textured(atlas: atlas, mesh: mesh)
                ? atlas!.Placement(textures: mesh.Textures!)
                : new Vector4(w: 0f, x: 1f, y: 1f, z: 0f));
            var uvScale = new Vector2(x: atlasPlacement.X, y: atlasPlacement.Y);
            var uvOffset = new Vector2(x: atlasPlacement.Z, y: atlasPlacement.W);

            for (var vertex = 0; (vertex < positions.Length); vertex++) {
                var words = vertexWords.Slice(
                    length: VertexWords,
                    start: (vertex * VertexWords)
                );
                var normal = (normals.IsEmpty ? Vector3.Zero : normals[vertex]);
                var uv = (uvs.IsEmpty ? Vector2.Zero : ((uvs[vertex] * uvScale) + uvOffset));

                words[0] = BitConverter.SingleToUInt32Bits(value: positions[vertex].X);
                words[1] = BitConverter.SingleToUInt32Bits(value: positions[vertex].Y);
                words[2] = BitConverter.SingleToUInt32Bits(value: positions[vertex].Z);
                words[3] = BitConverter.SingleToUInt32Bits(value: normal.X);
                words[4] = BitConverter.SingleToUInt32Bits(value: normal.Y);
                words[5] = BitConverter.SingleToUInt32Bits(value: normal.Z);
                words[6] = BitConverter.SingleToUInt32Bits(value: uv.X);
                words[7] = BitConverter.SingleToUInt32Bits(value: uv.Y);
            }

            mesh.TriangleMaterials.Span.CopyTo(destination: destination[(layout.MaterialWordOffset + placement.FirstMaterial)..]);
            mesh.Indices.Span.CopyTo(destination: destination[(layout.IndexWordOffset + placement.FirstIndex)..]);
        }
    }

    // A draw's impostor words: the sphere, the view grid and the atlas rectangle, or zeros.
    private static void WriteImpostor(SdfMeshImpostor? impostor, SdfMeshAtlas? atlas, Span<uint> record) {
        record[..10].Clear();

        if ((impostor is null) || (atlas is null)) {
            return;
        }

        var placement = atlas.Placement(textures: impostor);

        record[0] = BitConverter.SingleToUInt32Bits(value: impostor.Center.X);
        record[1] = BitConverter.SingleToUInt32Bits(value: impostor.Center.Y);
        record[2] = BitConverter.SingleToUInt32Bits(value: impostor.Center.Z);
        record[3] = BitConverter.SingleToUInt32Bits(value: impostor.Radius);
        record[4] = ((uint)impostor.Views);
        record[5] = ((uint)impostor.ViewTexels);
        record[6] = BitConverter.SingleToUInt32Bits(value: placement.X);
        record[7] = BitConverter.SingleToUInt32Bits(value: placement.Y);
        record[8] = BitConverter.SingleToUInt32Bits(value: placement.Z);
        record[9] = BitConverter.SingleToUInt32Bits(value: placement.W);
    }
    // Whether a mesh's textures are in the atlases a frame binds.
    private static bool Textured(SdfMesh mesh, SdfMeshAtlas? atlas) =>
        ((mesh.Textures is not null) && (atlas is not null) && atlas.Holds(textures: mesh.Textures));
    // A draw's normal matrix: the inverse transpose of its matrix's upper 3×3, row by row, in the row-vector convention
    // (n_world = n.x row0 + n.y row1 + n.z row2). A singular matrix writes its own upper 3×3.
    private static void WriteNormalMatrix(Matrix4x4 matrix, Span<uint> record) {
        var upper = new Matrix4x4(
            m11: matrix.M11, m12: matrix.M12, m13: matrix.M13, m14: 0f,
            m21: matrix.M21, m22: matrix.M22, m23: matrix.M23, m24: 0f,
            m31: matrix.M31, m32: matrix.M32, m33: matrix.M33, m34: 0f,
            m41: 0f, m42: 0f, m43: 0f, m44: 1f
        );
        var normal = (Matrix4x4.Invert(matrix: upper, result: out var inverse)
            ? Matrix4x4.Transpose(matrix: inverse)
            : upper);

        record[0] = BitConverter.SingleToUInt32Bits(value: normal.M11);
        record[1] = BitConverter.SingleToUInt32Bits(value: normal.M12);
        record[2] = BitConverter.SingleToUInt32Bits(value: normal.M13);
        record[3] = BitConverter.SingleToUInt32Bits(value: normal.M21);
        record[4] = BitConverter.SingleToUInt32Bits(value: normal.M22);
        record[5] = BitConverter.SingleToUInt32Bits(value: normal.M23);
        record[6] = BitConverter.SingleToUInt32Bits(value: normal.M31);
        record[7] = BitConverter.SingleToUInt32Bits(value: normal.M32);
        record[8] = BitConverter.SingleToUInt32Bits(value: normal.M33);
    }
}
/// <summary>Where one distinct mesh sits in the mesh region, in the units a draw's record names it by.</summary>
/// <param name="BaseVertex">The mesh's first vertex within the vertex section.</param>
/// <param name="FirstIndex">The mesh's first index within the index section.</param>
/// <param name="FirstMaterial">The mesh's first triangle material within the triangle-material section.</param>
/// <param name="IndexCount">The mesh's index count.</param>
public readonly record struct SdfMeshRegionMesh(int BaseVertex, int FirstIndex, int FirstMaterial, int IndexCount);
/// <summary>The sections of one mesh region (<see cref="SdfMeshRegion"/>), in words from the region's start.</summary>
/// <param name="DrawCount">The draw records, from word zero.</param>
/// <param name="VertexCount">The vertices, <see cref="SdfMeshRegion.VertexWords"/> words each, from
/// <see cref="VertexWordOffset"/>.</param>
/// <param name="MaterialCount">The triangle materials, one word each, from <see cref="MaterialWordOffset"/>.</param>
/// <param name="IndexCount">The indices, one word each, from <see cref="IndexWordOffset"/>.</param>
public readonly record struct SdfMeshRegionLayout(int DrawCount, int VertexCount, int MaterialCount, int IndexCount) {
    /// <summary>Gets the region's size in bytes.</summary>
    public ulong Bytes => (((ulong)Words) * sizeof(uint));
    /// <summary>Gets the word the index section starts at.</summary>
    public int IndexWordOffset => (MaterialWordOffset + MaterialCount);
    /// <summary>Gets the word the triangle-material section starts at, past the vertices.</summary>
    public int MaterialWordOffset => (VertexWordOffset + (VertexCount * SdfMeshRegion.VertexWords));
    /// <summary>Gets the word the vertex section starts at, past the draw records.</summary>
    public int VertexWordOffset => (DrawCount * SdfMeshRegion.DrawWords);
    /// <summary>Gets the region's size in words.</summary>
    public int Words => (IndexWordOffset + IndexCount);
}
