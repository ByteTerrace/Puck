using System.Numerics;
using Puck.SignedDistance;
using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>An indexed triangle list in object space: opaque geometry a frame draws beside its SDF program.</summary>
/// <remarks>Triangles wind counter-clockwise seen from their front, in the right-handed convention of
/// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>. A world prototype's inline mesh becomes one, placed by its
/// placements and stamps, and so does each group the mesh-path bake contours (<see cref="SdfMeshPathBake"/>), which also
/// carries each vertex's normal and each triangle's palette entry, and, for a group whose shapes ride slots of their own,
/// each vertex's member (<see cref="Members"/>). The mesh pass rasterizes it.</remarks>
public sealed record SdfMesh {
    /// <summary>Creates a mesh, refusing a malformed index list, a non-finite vertex, or a vertex or triangle attribute
    /// of the wrong length.</summary>
    /// <param name="positions">The object-space vertex positions.</param>
    /// <param name="indices">Three vertex indices per triangle.</param>
    /// <param name="normals">One object-space normal per vertex, which the hit's normal interpolates across a triangle,
    /// or empty for each triangle's face normal.</param>
    /// <param name="triangleMaterials">One palette entry per triangle, added to its draw's material, or empty for the
    /// draw's material on every triangle.</param>
    /// <param name="members">The slots besides its draw's frame slot whose motion carries some of its vertices, at most
    /// <see cref="MaxMembers"/>, or none.</param>
    /// <param name="vertexMembers">One entry per vertex: the index in <paramref name="members"/> of the member it rides, or
    /// <see cref="NoMember"/> for one that rides its draw; empty when every vertex rides its draw.</param>
    /// <exception cref="ArgumentException">The index count is zero or not a multiple of three, the mesh has more than
    /// <see cref="MaxTriangles"/> triangles, an index names no vertex, a position, normal or member pose is not finite,
    /// an attribute is neither empty nor one per vertex (one per triangle for <paramref name="triangleMaterials"/>), the
    /// mesh has more than <see cref="MaxMembers"/> members, or a vertex names no member.</exception>
    public SdfMesh(ReadOnlyMemory<Vector3> positions, ReadOnlyMemory<uint> indices, ReadOnlyMemory<Vector3> normals = default, ReadOnlyMemory<uint> triangleMaterials = default, ReadOnlyMemory<SdfMeshMember> members = default, ReadOnlyMemory<byte> vertexMembers = default) {
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
            if (!VectorFunctions.IsFinite(vector: position)) {
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
            count: triangleMaterials.Length,
            expected: (indices.Length / 3),
            name: nameof(triangleMaterials),
            per: "triangle"
        );

        foreach (var normal in normals.Span) {
            if (!VectorFunctions.IsFinite(vector: normal)) {
                throw new ArgumentException(
                    message: "Every mesh normal must be finite.",
                    paramName: nameof(normals)
                );
            }
        }

        RequireAttribute(
            count: vertexMembers.Length,
            expected: positions.Length,
            name: nameof(vertexMembers),
            per: "vertex"
        );
        if (members.Length > MaxMembers) {
            throw new ArgumentException(
                message: $"A mesh's vertices ride at most {MaxMembers} members; this one has {members.Length}.",
                paramName: nameof(members)
            );
        }
        foreach (var member in members.Span) {
            if (!VectorFunctions.IsFinite(vector: member.Position) || !VectorFunctions.IsFinite(rotation: member.Orientation)) {
                throw new ArgumentException(
                    message: "Every member's pose must be finite.",
                    paramName: nameof(members)
                );
            }
        }
        foreach (var member in vertexMembers.Span) {
            if ((member != NoMember) && (member >= members.Length)) {
                throw new ArgumentException(
                    message: $"Vertex member {member} names none of the {members.Length} the mesh has.",
                    paramName: nameof(vertexMembers)
                );
            }
        }

        Positions = positions;
        Indices = indices;
        Normals = normals;
        TriangleMaterials = triangleMaterials;
        Members = members;
        VertexMembers = vertexMembers;
    }

    /// <summary>The most triangles a mesh holds: the mesh pass writes a covered pixel's triangle as a float, which holds
    /// every whole number up to 2^24 exactly, so the last triangle is 2^24 - 1.</summary>
    public const int MaxTriangles = (1 << 24);
    /// <summary>The most members a mesh's vertices ride: a vertex names its member in a byte, and this value is
    /// <see cref="NoMember"/>.</summary>
    public const int MaxMembers = byte.MaxValue;
    /// <summary>The vertex member of a vertex that rides its draw rather than a member.</summary>
    public const byte NoMember = byte.MaxValue;

    /// <summary>Gets three vertex indices per triangle.</summary>
    public ReadOnlyMemory<uint> Indices { get; }
    /// <summary>Gets one object-space normal per vertex, or none.</summary>
    public ReadOnlyMemory<Vector3> Normals { get; }
    /// <summary>Gets the object-space vertex positions.</summary>
    public ReadOnlyMemory<Vector3> Positions { get; }
    /// <summary>Gets the triangle count.</summary>
    public int TriangleCount => (Indices.Length / 3);
    /// <summary>Gets one palette entry per triangle, added to its draw's material, or none.</summary>
    public ReadOnlyMemory<uint> TriangleMaterials { get; }
    /// <summary>Gets the slots besides its draw's frame slot whose motion carries some of its vertices, or none.</summary>
    public ReadOnlyMemory<SdfMeshMember> Members { get; }
    /// <summary>Gets each vertex's index in <see cref="Members"/>, or <see cref="NoMember"/> for a vertex that rides its
    /// draw; none when every vertex rides its draw.</summary>
    public ReadOnlyMemory<byte> VertexMembers { get; }

    private static void RequireAttribute(int count, int expected, string name, string per) {
        if ((count != 0) && (count != expected)) {
            throw new ArgumentException(
                message: $"A mesh's {name} hold one entry per {per} ({expected}) or none; there are {count}.",
                paramName: name
            );
        }
    }
}
/// <summary>A slot whose motion carries some of a mesh's vertices (<see cref="SdfMesh.Members"/>): a shape of a baked group
/// riding a slot of its own beside the group's.</summary>
/// <param name="SlotOffset">The slot's number less the draw's frame slot (<see cref="SdfMeshDraw.FrameSlot"/>), which one
/// creation's emission keeps in every program.</param>
/// <param name="Position">Where the slot stood in the mesh's object space when the mesh was baked.</param>
/// <param name="Orientation">How it was turned there. A vertex riding the member is carried into the slot's frame by the
/// inverse of this pose, then out by the slot's transform in the frame that draws it.</param>
public readonly record struct SdfMeshMember(int SlotOffset, Vector3 Position, Quaternion Orientation);
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
    /// <summary>Gets whether the draw belongs to a moving body or animated placement, which the irradiance volume's
    /// static scene leaves out (<see cref="SdfMeshIrradianceScene.IsStatic"/>).</summary>
    public bool IsDynamic { get; init; }
    /// <summary>Gets whether the draw is glass (<see cref="SdfMeshBakeGroup.Glass"/>): the glass pass rasterizes it over
    /// the opaque image, and the mesh pass, the shadow cascades and the irradiance volume's static scene leave it
    /// out.</summary>
    public bool Glass { get; init; }
    /// <summary>Gets the dynamic-transform slot whose frame the mesh's object space is, or <see langword="null"/> for a
    /// mesh in no slot's frame: the shading places a material's layers in that frame and reads its lanes, as it does for
    /// a field hit of a shape riding the slot.</summary>
    public int? FrameSlot { get; init; }
    /// <summary>Gets the rotation of the rigid carry every world position and normal the draw resolves goes through
    /// last, after its matrix or its members' slots: identity for a draw drawn where it stands, a portal pair's turn
    /// for the copy of a body drawn through the pair.</summary>
    public Quaternion CarryRotation { get; init; } = Quaternion.Identity;
    /// <summary>Gets the translation of the rigid carry, applied after <see cref="CarryRotation"/>.</summary>
    public Vector3 CarryTranslation { get; init; }
    /// <summary>Gets the world-space plane the draw is clipped by, <c>(n, d)</c>: a point where <c>dot(n, x) + d</c> is
    /// negative is not drawn, in any view or shadow. Zero, the default, draws everything; a body straddling a portal's
    /// glass is drawn clipped to the glass's front on each side.</summary>
    public Vector4 ClipPlane { get; init; }
}
/// <summary>
/// The raw word layout of the region a frame's mesh draws upload into, read as a structured buffer of uints so every
/// backend reads the same word offsets: first one record a draw (<see cref="SdfMeshRecord.DrawWords"/> words), then each distinct
/// <see cref="SdfMesh"/> once, however many draws share it, as its vertices (<see cref="SdfMeshRecord.VertexWords"/> words each:
/// position and normal), its triangle materials (one word each, for a mesh that has them) and its
/// indices (one word each). <see cref="Plan"/> lays the meshes out section by section, all vertices, then all triangle
/// materials, then all indices, in the order their first draw names them; a residency's tables place each mesh whole
/// past a reserve of draw records, where it stays until the arena packs again (<see cref="SdfMeshRegionArena"/>). A record names
/// its mesh's words either way.
/// <para>
/// A draw's record is its row-vector object-to-world matrix, row by row (words 0 to 15, <c>M11</c> first), then its
/// material, then its mesh: the word its first index sits at, its index count, the word its first vertex sits at, its
/// attribute flags (<see cref="SdfMeshRecord.NormalsFlag"/>, <see cref="SdfMeshRecord.MaterialsFlag"/>), and the word its first triangle material
/// sits at (words 16 to 21), then its normal matrix, the inverse transpose of the matrix's upper 3×3, row by row (words
/// 22 to 30), which carries an object-space normal to world space under any scale, nonuniform and mirrored included
/// (a singular matrix, which draws no area, writes its own upper 3×3 instead). Word 31 is the dynamic-transform slot
/// whose frame the mesh is stored in (<see cref="SdfMeshDraw.FrameSlot"/>), or -1. Its word offsets are counted from
/// the region's start, so a reader needs nothing but the record to find a draw's triangles: its index <c>k</c> is the
/// word at <c>indexWord + k</c>, and names the vertex whose six words start at <c>vertexWord + (6 * index)</c>. A
/// vertex without a normal holds zeros there, and the flags say which a mesh has. Word 32 is the word a mesh with
/// members (<see cref="SdfMeshRecord.SkinFlag"/>) keeps them at, past its indices: their count, then <see cref="SdfMeshRecord.MemberWords"/> words a
/// member (its slot offset, then its position and orientation), then one word a vertex naming its member. Words 33 to 39
/// are the draw's carry (<see cref="SdfMeshDraw.CarryRotation"/> as x, y, z, w, then
/// <see cref="SdfMeshDraw.CarryTranslation"/>), which every world position and normal the draw resolves goes through
/// last, and words 40 to 43 its clip plane (<see cref="SdfMeshDraw.ClipPlane"/>). The record is whole words, so it has no
/// padding a structured-buffer reader could disagree on.
/// </para>
/// </summary>
public static class SdfMeshRegion {
    /// <summary>The bytes of one draw's record (<see cref="SdfMeshRecord.DrawWords"/> words).</summary>
    public const int DrawBytes = (SdfMeshRecord.DrawWords * sizeof(uint));
    /// <summary>The most draws one region holds: the mesh pass pushes a draw's index in the bits below
    /// <see cref="SdfKernelInterfaces.MeshViewShift"/>, and writes a covered pixel's draw plus one as a float, which holds
    /// 2^24 exactly.</summary>
    public const int MaxDraws = (1 << SdfKernelInterfaces.MeshViewShift);
    /// <summary>The bytes of one index.</summary>
    public const int IndexBytes = sizeof(uint);
    /// <summary>The bytes of one vertex (<see cref="SdfMeshRecord.VertexWords"/> words).</summary>
    public const int VertexBytes = (SdfMeshRecord.VertexWords * sizeof(float));

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
        var skins = 0;

        for (var draw = 0; (draw < draws.Count); draw++) {
            var mesh = draws[draw].Mesh;

            if (meshes.TryAdd(
                key: mesh,
                value: new SdfMeshRegionMesh(
                    IndexCount: mesh.Indices.Length,
                    IndexWord: indices,
                    MaterialWord: materials,
                    VertexWord: vertices,
                    SkinWord: skins
                )
            )) {
                vertices = checked((vertices + mesh.Positions.Length));
                materials = checked((materials + mesh.TriangleMaterials.Length));
                indices = checked((indices + mesh.Indices.Length));
                skins = checked((skins + SkinWordsOf(mesh: mesh)));
            }
        }

        _ = checked((((((draws.Count * SdfMeshRecord.DrawWords) + (vertices * SdfMeshRecord.VertexWords)) + materials) + indices) + skins));

        var layout = new SdfMeshRegionLayout(
            DrawCount: draws.Count,
            IndexCount: indices,
            MaterialCount: materials,
            VertexCount: vertices,
            SkinCount: skins
        );

        // The first pass counted each mesh's place within its section; a placement names absolute words.
        foreach (var mesh in meshes.Keys.ToArray()) {
            var counted = meshes[mesh];

            meshes[mesh] = new SdfMeshRegionMesh(
                IndexCount: counted.IndexCount,
                IndexWord: (layout.IndexWordOffset + counted.IndexWord),
                MaterialWord: (layout.MaterialWordOffset + counted.MaterialWord),
                VertexWord: (layout.VertexWordOffset + (counted.VertexWord * SdfMeshRecord.VertexWords)),
                SkinWord: (mesh.VertexMembers.IsEmpty ? 0 : (layout.SkinWordOffset + counted.SkinWord))
            );
        }

        return layout;
    }
    /// <summary>The words one mesh takes when its vertices, triangle materials, indices and members lie together, as a
    /// mesh region arena places it (<see cref="SdfMeshRegionArena"/>).</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Its words.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static int WordsOf(SdfMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        return checked(((((mesh.Positions.Length * SdfMeshRecord.VertexWords) + mesh.TriangleMaterials.Length) + mesh.Indices.Length) + SkinWordsOf(mesh: mesh)));
    }
    /// <summary>The words a mesh's members take: none for a mesh without them, otherwise their count, each member's
    /// <see cref="SdfMeshRecord.MemberWords"/> words and one word a vertex.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Its members' words.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static int SkinWordsOf(SdfMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        return (mesh.VertexMembers.IsEmpty ? 0 : checked(((1 + (mesh.Members.Length * SdfMeshRecord.MemberWords)) + mesh.VertexMembers.Length)));
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
        RequireDestination(destination: destination, layout: layout);
        WriteDraws(
            destination: destination,
            draws: draws,
            meshes: meshes
        );
        WriteMeshes(
            destination: destination,
            layout: layout,
            meshes: meshes
        );
    }
    /// <summary>Writes only the draw records of a list of draws, the region's first <see cref="SdfMeshRecord.DrawWords"/> words a draw,
    /// as <see cref="Write"/> writes them. A list whose draws move while the meshes they name and those meshes'
    /// placements stand rewrites only these words.</summary>
    /// <param name="draws">The draws.</param>
    /// <param name="meshes">The placements of every mesh the draws name.</param>
    /// <param name="destination">The draw records' words; at least <see cref="SdfMeshRecord.DrawWords"/> a draw.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> or <paramref name="meshes"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the records.</exception>
    /// <exception cref="KeyNotFoundException">A draw names a mesh <paramref name="meshes"/> does not place.</exception>
    public static void WriteDraws(IReadOnlyList<SdfMeshDraw> draws, Dictionary<SdfMesh, SdfMeshRegionMesh> meshes, Span<uint> destination) {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(meshes);
        if (destination.Length < (draws.Count * SdfMeshRecord.DrawWords)) {
            throw new ArgumentException(message: $"{draws.Count} draw records need {(draws.Count * SdfMeshRecord.DrawWords)} words; the destination holds {destination.Length}.", paramName: nameof(destination));
        }

        for (var draw = 0; (draw < draws.Count); draw++) {
            var (mesh, matrix, material, _) = draws[draw];
            var placement = meshes[mesh];
            var record = destination.Slice(
                length: SdfMeshRecord.DrawWords,
                start: (draw * SdfMeshRecord.DrawWords)
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
            record[SdfMeshRecord.MaterialWord] = unchecked((uint)material);
            record[SdfMeshRecord.IndexWord] = ((uint)placement.IndexWord);
            record[SdfMeshRecord.IndexCountWord] = ((uint)placement.IndexCount);
            record[SdfMeshRecord.VertexWord] = ((uint)placement.VertexWord);
            record[SdfMeshRecord.FlagsWord] = (mesh.Normals.IsEmpty ? 0u : SdfMeshRecord.NormalsFlag) | (mesh.TriangleMaterials.IsEmpty ? 0u : SdfMeshRecord.MaterialsFlag) | (mesh.VertexMembers.IsEmpty ? 0u : SdfMeshRecord.SkinFlag);
            record[SdfMeshRecord.TriangleMaterialWord] = ((uint)placement.MaterialWord);
            WriteNormalMatrix(
                matrix: matrix,
                record: record[SdfMeshRecord.NormalMatrixWord..]
            );
            record[SdfMeshRecord.FrameSlotWord] = unchecked((uint)(draws[draw].FrameSlot ?? SdfProgram.NoDynamicTransformSlot));
            record[SdfMeshRecord.SkinWord] = ((uint)placement.SkinWord);

            var carry = draws[draw].CarryRotation;
            var shift = draws[draw].CarryTranslation;
            var clip = draws[draw].ClipPlane;

            record[SdfMeshRecord.CarryRotationWord] = BitConverter.SingleToUInt32Bits(value: carry.X);
            record[(SdfMeshRecord.CarryRotationWord + 1)] = BitConverter.SingleToUInt32Bits(value: carry.Y);
            record[(SdfMeshRecord.CarryRotationWord + 2)] = BitConverter.SingleToUInt32Bits(value: carry.Z);
            record[(SdfMeshRecord.CarryRotationWord + 3)] = BitConverter.SingleToUInt32Bits(value: carry.W);
            record[SdfMeshRecord.CarryTranslationWord] = BitConverter.SingleToUInt32Bits(value: shift.X);
            record[(SdfMeshRecord.CarryTranslationWord + 1)] = BitConverter.SingleToUInt32Bits(value: shift.Y);
            record[(SdfMeshRecord.CarryTranslationWord + 2)] = BitConverter.SingleToUInt32Bits(value: shift.Z);
            record[SdfMeshRecord.ClipPlaneWord] = BitConverter.SingleToUInt32Bits(value: clip.X);
            record[(SdfMeshRecord.ClipPlaneWord + 1)] = BitConverter.SingleToUInt32Bits(value: clip.Y);
            record[(SdfMeshRecord.ClipPlaneWord + 2)] = BitConverter.SingleToUInt32Bits(value: clip.Z);
            record[(SdfMeshRecord.ClipPlaneWord + 3)] = BitConverter.SingleToUInt32Bits(value: clip.W);
        }
    }
    /// <summary>Writes only the meshes a list of draws names, every word past the draw records, as <see cref="Write"/>
    /// writes them.</summary>
    /// <param name="meshes">The placements <see cref="Plan"/> wrote.</param>
    /// <param name="layout">The layout <see cref="Plan"/> returned for the same draws.</param>
    /// <param name="destination">The region's words; at least <see cref="SdfMeshRegionLayout.Words"/> of them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="meshes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the layout.</exception>
    public static void WriteMeshes(Dictionary<SdfMesh, SdfMeshRegionMesh> meshes, SdfMeshRegionLayout layout, Span<uint> destination) {
        ArgumentNullException.ThrowIfNull(meshes);
        RequireDestination(destination: destination, layout: layout);

        foreach (var (mesh, placement) in meshes) {
            WriteMesh(
                destination: destination,
                mesh: mesh,
                placement: placement
            );
        }
    }
    /// <summary>Writes one mesh's vertices, triangle materials and indices at the words its placement names.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="placement">Where the mesh's sections lie, in words from the start of
    /// <paramref name="destination"/>.</param>
    /// <param name="destination">The words the placement counts from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static void WriteMesh(SdfMesh mesh, SdfMeshRegionMesh placement, Span<uint> destination) {
        ArgumentNullException.ThrowIfNull(mesh);

        var positions = mesh.Positions.Span;
        var normals = mesh.Normals.Span;
        var vertexWords = destination[placement.VertexWord..];

        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            var words = vertexWords.Slice(
                length: SdfMeshRecord.VertexWords,
                start: (vertex * SdfMeshRecord.VertexWords)
            );
            var normal = (normals.IsEmpty ? Vector3.Zero : normals[vertex]);

            words[0] = BitConverter.SingleToUInt32Bits(value: positions[vertex].X);
            words[1] = BitConverter.SingleToUInt32Bits(value: positions[vertex].Y);
            words[2] = BitConverter.SingleToUInt32Bits(value: positions[vertex].Z);
            words[3] = BitConverter.SingleToUInt32Bits(value: normal.X);
            words[4] = BitConverter.SingleToUInt32Bits(value: normal.Y);
            words[5] = BitConverter.SingleToUInt32Bits(value: normal.Z);
        }

        mesh.TriangleMaterials.Span.CopyTo(destination: destination[placement.MaterialWord..]);
        mesh.Indices.Span.CopyTo(destination: destination[placement.IndexWord..]);
        if (mesh.VertexMembers.IsEmpty) { return; }

        var members = mesh.Members.Span;
        var skin = destination[placement.SkinWord..];

        skin[0] = ((uint)members.Length);
        for (var member = 0; (member < members.Length); member++) {
            var words = skin.Slice(length: SdfMeshRecord.MemberWords, start: (1 + (member * SdfMeshRecord.MemberWords)));

            var (offset, position, orientation) = members[member];

            words[0] = unchecked((uint)offset);
            words[1] = BitConverter.SingleToUInt32Bits(value: position.X);
            words[2] = BitConverter.SingleToUInt32Bits(value: position.Y);
            words[3] = BitConverter.SingleToUInt32Bits(value: position.Z);
            words[4] = BitConverter.SingleToUInt32Bits(value: orientation.X);
            words[5] = BitConverter.SingleToUInt32Bits(value: orientation.Y);
            words[6] = BitConverter.SingleToUInt32Bits(value: orientation.Z);
            words[7] = BitConverter.SingleToUInt32Bits(value: orientation.W);
        }

        var vertexMembers = mesh.VertexMembers.Span;
        var perVertex = skin[(1 + (members.Length * SdfMeshRecord.MemberWords))..];

        for (var vertex = 0; (vertex < vertexMembers.Length); vertex++) {
            perVertex[vertex] = ((vertexMembers[vertex] == SdfMesh.NoMember) ? uint.MaxValue : vertexMembers[vertex]);
        }
    }

    private static void RequireDestination(Span<uint> destination, SdfMeshRegionLayout layout) {
        if (destination.Length < layout.Words) {
            throw new ArgumentException(
                message: $"The mesh region needs {layout.Words} words; the destination holds {destination.Length}.",
                paramName: nameof(destination)
            );
        }
    }
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
/// <summary>Where one distinct mesh sits in the mesh region, in the words a draw's record names it by, counted from the
/// region's start.</summary>
/// <param name="VertexWord">The word the mesh's first vertex starts at.</param>
/// <param name="MaterialWord">The word the mesh's first triangle material sits at.</param>
/// <param name="IndexWord">The word the mesh's first index sits at.</param>
/// <param name="IndexCount">The mesh's index count.</param>
/// <param name="SkinWord">The word the mesh's members start at, or zero for a mesh without them.</param>
public readonly record struct SdfMeshRegionMesh(int VertexWord, int MaterialWord, int IndexWord, int IndexCount, int SkinWord = 0);
/// <summary>The sections of one mesh region (<see cref="SdfMeshRegion"/>), in words from the region's start.</summary>
/// <param name="DrawCount">The draw records, from word zero.</param>
/// <param name="VertexCount">The vertices, <see cref="SdfMeshRecord.VertexWords"/> words each, from
/// <see cref="VertexWordOffset"/>.</param>
/// <param name="MaterialCount">The triangle materials, one word each, from <see cref="MaterialWordOffset"/>.</param>
/// <param name="IndexCount">The indices, one word each, from <see cref="IndexWordOffset"/>.</param>
/// <param name="SkinCount">The meshes' members' words (<see cref="SdfMeshRegion.SkinWordsOf"/>), from
/// <see cref="SkinWordOffset"/>.</param>
public readonly record struct SdfMeshRegionLayout(int DrawCount, int VertexCount, int MaterialCount, int IndexCount, int SkinCount = 0) {
    /// <summary>Gets the region's size in bytes.</summary>
    public ulong Bytes => (((ulong)Words) * sizeof(uint));
    /// <summary>Gets the word the index section starts at.</summary>
    public int IndexWordOffset => (MaterialWordOffset + MaterialCount);
    /// <summary>Gets the word the triangle-material section starts at, past the vertices.</summary>
    public int MaterialWordOffset => (VertexWordOffset + (VertexCount * SdfMeshRecord.VertexWords));
    /// <summary>Gets the word the vertex section starts at, past the draw records.</summary>
    public int VertexWordOffset => (DrawCount * SdfMeshRecord.DrawWords);
    /// <summary>Gets the word the members section starts at, past the indices.</summary>
    public int SkinWordOffset => (IndexWordOffset + IndexCount);
    /// <summary>Gets the region's size in words.</summary>
    public int Words => (SkinWordOffset + SkinCount);
}
