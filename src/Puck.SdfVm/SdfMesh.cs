using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Puck.Maths;

namespace Puck.SdfVm;

/// <summary>An indexed triangle list in object space: opaque geometry a frame draws beside its SDF program.</summary>
/// <remarks>Triangles wind counter-clockwise seen from their front, in the right-handed convention of
/// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>. A world prototype's inline mesh becomes one, placed by its
/// placements and stamps, and so does each group the mesh-path bake contours (<see cref="SdfMeshPathBake"/>), which also
/// carries each vertex's normal and each triangle's palette entry, and, for a group whose shapes ride slots of their own,
/// each vertex's member (<see cref="Members"/>). The mesh pass rasterizes it.</remarks>
public sealed partial record SdfMesh {
    /// <summary>The actual contour lattice spacing in object space; zero for an authored triangle mesh.</summary>
    public Vector3 BakeCell { get; init; }
    /// <summary>Gets the independently drawn pieces of a large static union; empty for a single mesh. The mesh's own
    /// triangles concatenate their merged surfaces for whole-group queries; only the pieces enter the mesh region.</summary>
    public IReadOnlyList<SdfMesh> Chunks { get; init; } = [];
    /// <summary>Gets whether the triangles are outward-wound pieces of closed surfaces, so a face turned from a light is
    /// where that light leaves a solid: true for every baked mesh, and for an authored mesh whose triangles close a volume
    /// (<c>WorldPlacementStamper.MeshOf</c>); false for an authored sheet or soup, which is drawn in both windings and
    /// bounds no solid.</summary>
    public bool Encloses { get; init; } = true;

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

        var low = new Vector3(value: float.PositiveInfinity);
        var high = new Vector3(value: float.NegativeInfinity);

        foreach (var position in positions.Span) {
            if (!VectorFunctions.IsFinite(vector: position)) {
                throw new ArgumentException(
                    message: "Every mesh position must be finite.",
                    paramName: nameof(positions)
                );
            }

            low = Vector3.Min(value1: low, value2: position);
            high = Vector3.Max(value1: high, value2: position);
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
        PositionLow = low;
        PositionHigh = high;
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
    /// <summary>Gets the least corner of the box the positions span, object space.</summary>
    public Vector3 PositionLow { get; }
    /// <summary>Gets the greatest corner of the box the positions span, object space.</summary>
    public Vector3 PositionHigh { get; }
    /// <summary>Gets the triangle count.</summary>
    public int TriangleCount => (Indices.Length / 3);
    /// <summary>Gets one palette entry per triangle, added to its draw's material, or none.</summary>
    public ReadOnlyMemory<uint> TriangleMaterials { get; }
    /// <summary>Gets the slots besides its draw's frame slot whose motion carries some of its vertices, or none.</summary>
    public ReadOnlyMemory<SdfMeshMember> Members { get; }
    /// <summary>Gets each vertex's index in <see cref="Members"/>, or <see cref="NoMember"/> for a vertex that rides its
    /// draw; none when every vertex rides its draw.</summary>
    public ReadOnlyMemory<byte> VertexMembers { get; }
    /// <summary>Gets the mesh's coarser levels of detail, finest first, each a triangle list over this mesh's own vertices
    /// (<see cref="SdfMeshCollapse"/>); none for a mesh drawn at its own triangles alone. The mesh's own triangles are
    /// its level zero, which the irradiance trace and every query read.</summary>
    /// <exception cref="ArgumentException">A level's triangles name no vertex, its triangle materials are not one per
    /// triangle when the mesh carries them (or present when it does not), its error is negative, not finite or smaller
    /// than a finer level's, or the levels and the mesh together hold more than <see cref="MaxTriangles"/>
    /// triangles.</exception>
    public IReadOnlyList<SdfMeshLevel> Levels {
        get => m_levels;
        init {
            ArgumentNullException.ThrowIfNull(value);

            var triangles = ((long)TriangleCount);
            var error = 0f;

            foreach (var level in value) {
                ArgumentNullException.ThrowIfNull(level);

                if (level.Indices.IsEmpty || ((level.Indices.Length % 3) != 0) || (level.TriangleMaterials.Length != (TriangleMaterials.IsEmpty ? 0 : (level.Indices.Length / 3)))) {
                    throw new ArgumentException(message: "A level holds at least one triangle, three indices a triangle and one triangle material a triangle when its mesh carries them.", paramName: nameof(Levels));
                }
                if (!float.IsFinite(f: level.Error) || (level.Error < error)) {
                    throw new ArgumentException(message: $"A level's error is finite and no smaller than a finer level's; {level.Error} follows {error}.", paramName: nameof(Levels));
                }
                foreach (var index in level.Indices.Span) {
                    if (index >= ((uint)Positions.Length)) {
                        throw new ArgumentException(message: $"Level index {index} names no vertex of the {Positions.Length} the mesh has.", paramName: nameof(Levels));
                    }
                }

                error = level.Error;
                triangles += (level.Indices.Length / 3);
            }
            if (triangles > MaxTriangles) {
                throw new ArgumentException(message: $"A mesh and its levels hold at most {MaxTriangles} triangles, each numbered by a value the mesh target's float holds exactly; these hold {triangles}.", paramName: nameof(Levels));
            }

            m_levels = value;
            m_levelledTriangleCount = ((int)triangles);
        }
    }
    /// <summary>Gets the triangles every level holds together, the mesh's own first: the triangles its region words
    /// hold, numbered from zero in that order.</summary>
    public int LevelledTriangleCount => ((m_levels.Count == 0) ? TriangleCount : m_levelledTriangleCount);

    /// <summary>Returns a level's range among this mesh's triangles, the base mesh first and coarser levels after it.</summary>
    /// <param name="level">Zero for the base mesh, one for its first coarser level; a level past the coarsest selects
    /// the coarsest.</param>
    /// <returns>The first triangle in the concatenated levels and the chosen level's triangle count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is negative.</exception>
    public (int FirstTriangle, int TriangleCount) LevelOf(int level) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: level);
        var levels = Levels;
        var chosen = Math.Min(val1: level, val2: levels.Count);
        var first = 0;
        var count = TriangleCount;
        for (var at = 0; at < chosen; at++) {
            first += count;
            count = levels[at].TriangleCount;
        }
        return (first, count);
    }

    private readonly IReadOnlyList<SdfMeshLevel> m_levels = [];
    // The mesh's and its levels' triangles together, set with the levels.
    private readonly int m_levelledTriangleCount;

    private static void RequireAttribute(int count, int expected, string name, string per) {
        if ((count != 0) && (count != expected)) {
            throw new ArgumentException(
                message: $"A mesh's {name} hold one entry per {per} ({expected}) or none; there are {count}.",
                paramName: name
            );
        }
    }
}
/// <summary>One coarser level of detail of a baked mesh (<see cref="SdfMesh.Levels"/>): a triangle list over the mesh's
/// own vertices, which a pass draws in the mesh's place where its error cannot show (<see cref="SdfMeshDetail"/>).</summary>
/// <param name="Indices">Three vertex indices of the mesh per triangle.</param>
/// <param name="TriangleMaterials">One palette entry per triangle, as the mesh's own are, or none for a mesh without
/// them.</param>
/// <param name="Error">The largest distance, in object units, from any vertex of the mesh's own triangles to the
/// level's surface, measured to a triangle of the vertex's material within <see cref="SdfMeshCollapse"/>'s normal
/// bound, and from any vertex or edge midpoint of a crease or a material's edge to that line as the level draws it: the
/// most the level moves the surface, a crease or a material's edge.</param>
public sealed record SdfMeshLevel(ReadOnlyMemory<uint> Indices, ReadOnlyMemory<uint> TriangleMaterials, float Error) {
    /// <summary>Gets the level's triangle count.</summary>
    public int TriangleCount => (Indices.Length / 3);
}
/// <summary>A slot whose motion carries some of a mesh's vertices (<see cref="SdfMesh.Members"/>): a shape of a baked group
/// riding a slot of its own beside the group's.</summary>
/// <param name="SlotOffset">The slot's number less the draw's frame slot (<see cref="SdfMeshDraw.FrameSlot"/>), which one
/// creation's emission keeps in every program.</param>
/// <param name="Position">Where the slot stood in the mesh's object space when the mesh was baked.</param>
/// <param name="Orientation">How it was turned there. A vertex riding the member is carried into the slot's frame by the
/// inverse of this pose, then out by the slot's transform in the frame that draws it.</param>
public readonly record struct SdfMeshMember(int SlotOffset, Vector3 Position, Quaternion Orientation);
/// <summary>The heights a height field's draw is displaced by as it is drawn (<see cref="SdfHeightField"/>): the handle
/// the frame's sampled table holds them at (<see cref="SdfProgram.HeightFields"/>), and the band of object-space heights
/// every displaced point lies in, which the draw's box holds in place of its baked reference's.</summary>
/// <param name="Handle">The heights' handle in the frame's program.</param>
/// <param name="Low">The least object-space height a displaced point takes.</param>
/// <param name="High">The greatest object-space height a displaced point takes.</param>
public readonly record struct SdfMeshDisplacement(int Handle, float Low, float High) {
    /// <summary>Returns the displacement of a program's height field.</summary>
    /// <param name="binding">The height field's binding.</param>
    /// <returns>The displacement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    public static SdfMeshDisplacement Of(SdfHeightFieldBinding binding) {
        ArgumentNullException.ThrowIfNull(argument: binding);

        return new(Handle: binding.Handle, High: binding.Field.DrawnHigh, Low: binding.Field.DrawnLow);
    }
}
/// <summary>One placement of an <see cref="SdfMesh"/> in a frame.</summary>
/// <param name="Mesh">The triangles to draw.</param>
/// <param name="ObjectToWorld">The object-to-world transform, in the row-vector convention of
/// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>.</param>
/// <param name="Material">The material-table index the triangles shade with, as an SDF hit's material does; a mesh
/// with <see cref="SdfMesh.TriangleMaterials"/> adds each triangle's entry to it.</param>
/// <param name="Identity">The host's picking identity, also the fallback motion key when <see cref="MotionIdentity"/>
/// is absent. <see cref="SdfMeshHistory"/> observes the stable key and mesh at each staged index to seed previous
/// matrices and invalidate GPU visibility/detail history together.</param>
public readonly record struct SdfMeshDraw(SdfMesh Mesh, Matrix4x4 ObjectToWorld, int Material, object Identity) {
    /// <summary>Gets a stable surface identity for motion history when the picking identity also carries changing pose metadata.</summary>
    public object? MotionIdentity { get; init; }
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
    /// <summary>Gets whether materials use the root/member frame. False keeps material coordinates in world space,
    /// independently of the root slot used by skinning and portal membership.</summary>
    public bool UsesRootMaterialFrame { get; init; } = true;

    /// <summary>Gets the rotation of the carry every world position and normal the draw resolves goes through last,
    /// after its matrix or its members' slots: identity for a draw drawn where it stands, a portal pair's turn for the
    /// copy of a body drawn through the pair.</summary>
    public Quaternion CarryRotation { get; init; } = Quaternion.Identity;

    /// <summary>Gets the translation of the carry, applied after <see cref="CarryRotation"/> and
    /// <see cref="CarryScale"/>.</summary>
    public Vector3 CarryTranslation { get; init; }

    /// <summary>Gets the carry's uniform scale, applied to a position after <see cref="CarryRotation"/>: one for a draw
    /// drawn where it stands, a portal pair's scale for the copy of a body drawn through a pair of two sizes. Normals
    /// only turn.</summary>
    public float CarryScale { get; init; } = 1f;

    /// <summary>Gets the world-space plane the draw is clipped by, <c>(n, d)</c>: a point where <c>dot(n, x) + d</c> is
    /// negative is not drawn, in any view or shadow. Zero, the default, draws everything; a body straddling a portal's
    /// glass is drawn clipped to the glass's front on each side.</summary>
    public Vector4 ClipPlane { get; init; }
    /// <summary>Gets the heights the draw's mesh, a height field's baked reference, is displaced by as it is drawn, or
    /// <see langword="null"/> for a draw drawn as its mesh was baked.</summary>
    public SdfMeshDisplacement? Displacement { get; init; }
    /// <summary>Returns whether two draws stage the same GPU record, bounds, picking identity and motion interpretation,
    /// including the exact bits of each floating-point field.</summary>
    /// <param name="other">The draw to compare.</param>
    /// <returns>Whether staging either draw gives the same content.</returns>
    public bool SameStagedContent(in SdfMeshDraw other) {
        if (
            !ReferenceEquals(objA: this.Mesh, objB: other.Mesh) ||
            (this.Material != other.Material) ||
            (this.Glass != other.Glass) ||
            (this.IsDynamic != other.IsDynamic) ||
            (this.FrameSlot != other.FrameSlot) ||
            (this.UsesRootMaterialFrame != other.UsesRootMaterialFrame) ||
            !Equals(objA: this.MotionIdentity, objB: other.MotionIdentity) ||
            (BitConverter.SingleToInt32Bits(value: this.CarryScale) != BitConverter.SingleToInt32Bits(value: other.CarryScale)) ||
            (this.Displacement != other.Displacement) ||
            !Equals(objA: this.Identity, objB: other.Identity)
        ) {
            return false;
        }

        var leftMatrix = this.ObjectToWorld;
        var rightMatrix = other.ObjectToWorld;
        var leftRotation = this.CarryRotation;
        var rightRotation = other.CarryRotation;
        var leftShift = this.CarryTranslation;
        var rightShift = other.CarryTranslation;
        var leftClip = this.ClipPlane;
        var rightClip = other.ClipPlane;

        return (
            MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in leftMatrix)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in rightMatrix))) &&
            MemoryMarshal.AsBytes(span: new ReadOnlySpan<Quaternion>(reference: in leftRotation)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Quaternion>(reference: in rightRotation))) &&
            MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector3>(reference: in leftShift)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector3>(reference: in rightShift))) &&
            MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector4>(reference: in leftClip)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector4>(reference: in rightClip)))
        );
    }

    /// <summary>Gets whether the draw's faces turned from a light are exactly where that light leaves a solid: its mesh
    /// encloses (<see cref="SdfMesh.Encloses"/>), no clip plane cuts it open, and its matrix keeps its winding. A shadow
    /// cascade's exit layer draws such a draw's faces turned from the light alone and every other draw whole, as a thin
    /// caster.</summary>
    public bool EnclosesSolid => (Mesh.Encloses && (ClipPlane == Vector4.Zero) &&
        ((((ObjectToWorld.M11 * ((ObjectToWorld.M22 * ObjectToWorld.M33) - (ObjectToWorld.M23 * ObjectToWorld.M32))) -
          (ObjectToWorld.M12 * ((ObjectToWorld.M21 * ObjectToWorld.M33) - (ObjectToWorld.M23 * ObjectToWorld.M31)))) +
          (ObjectToWorld.M13 * ((ObjectToWorld.M21 * ObjectToWorld.M32) - (ObjectToWorld.M22 * ObjectToWorld.M31)))) > 0f));
}
/// <summary>The CPU work needed to update one retained packed draw, independently of device upload deltas.</summary>
/// <param name="PackedWords">The record words assigned; retained equal words are not assigned.</param>
/// <param name="NormalComputed">Whether the normal matrix was derived.</param>
/// <param name="SelectionComputed">Whether the world-space selection box was derived.</param>
/// <param name="ChangedWords">One bit per word that changed in the retained record.</param>
public readonly record struct SdfMeshDrawPacking(int PackedWords, bool NormalComputed, bool SelectionComputed, ulong ChangedWords);

/// <summary>
/// The raw word layout of the region a frame's mesh draws upload into, read as a structured buffer of uints so every
/// backend reads the same word offsets: first one record a draw (<see cref="SdfMeshRecord.DrawWords"/> words), then each distinct
/// <see cref="SdfMesh"/> once, however many draws share it, as its vertices (<see cref="SdfMeshRecord.VertexWords"/> words each:
/// position and normal), its triangle materials (one word each, for a mesh that has them) and its
/// indices (one word each), its own triangles' first and then each of its levels' (<see cref="SdfMesh.Levels"/>), so the
/// triangles of every level are numbered together and a draw chooses its level by the level it pushes above its index
/// (<see cref="SdfKernelInterfaces.MeshLevelShift"/>, <see cref="SdfMesh.LevelOf"/>). <see cref="Plan"/> lays the meshes out section by section, all vertices, then all triangle
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
/// member (its slot offset, then its position and orientation), then one word a vertex naming its member. Words 33 to 40
/// are the draw's carry (<see cref="SdfMeshDraw.CarryRotation"/> as x, y, z, w, then
/// <see cref="SdfMeshDraw.CarryTranslation"/> and <see cref="SdfMeshDraw.CarryScale"/>), which every world position and
/// normal the draw resolves goes through last, words 41 to 44 its clip plane (<see cref="SdfMeshDraw.ClipPlane"/>), and
/// words 45 and 46 the first triangle of its mesh's first and second coarser levels (<see cref="SdfMesh.LevelOf"/>), which a draw
/// of a level adds to the triangle its vertex id names. The record is whole words, so it has no padding a
/// structured-buffer reader could disagree on.
/// </para>
/// </summary>
public static class SdfMeshRegion {
    /// <summary>Reads the mesh placement already encoded in a complete draw record.</summary>
    /// <param name="record">The retained draw's words.</param>
    /// <returns>The placement used to pack the draw.</returns>
    public static SdfMeshRegionMesh ReadPlacement(ReadOnlySpan<uint> record) => new(
        IndexWord: (int)record[SdfMeshRecord.IndexWord],
        IndexCount: (int)record[SdfMeshRecord.IndexCountWord],
        VertexWord: (int)record[SdfMeshRecord.VertexWord],
        MaterialWord: (int)record[SdfMeshRecord.TriangleMaterialWord],
        SkinWord: (int)record[SdfMeshRecord.SkinWord]);

    /// <summary>The bytes of one draw's record (<see cref="SdfMeshRecord.DrawWords"/> words).</summary>
    public const int DrawBytes = (SdfMeshRecord.DrawWords * sizeof(uint));
    /// <summary>The most draws one region holds: the mesh pass pushes a draw's index in the bits below
    /// <see cref="SdfKernelInterfaces.MeshLevelShift"/>, and writes a covered pixel's draw plus one as a float, which holds
    /// 2^24 exactly.</summary>
    public const int MaxDraws = (1 << SdfKernelInterfaces.MeshLevelShift);
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
                materials = checked((materials + MaterialWordsOf(mesh: mesh)));
                indices = checked((indices + IndexWordsOf(mesh: mesh)));
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

        return checked(((((mesh.Positions.Length * SdfMeshRecord.VertexWords) + MaterialWordsOf(mesh: mesh)) + IndexWordsOf(mesh: mesh)) + SkinWordsOf(mesh: mesh)));
    }
    /// <summary>The index section's words: all levelled triangles' indices, then one three-word row per level, including
    /// zero, holding its first triangle, vertex count and error.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Its index words.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static int IndexWordsOf(SdfMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        return checked(((3 * mesh.LevelledTriangleCount) + (3 * (mesh.Levels.Count + 1))));
    }
    /// <summary>The triangle-material words a mesh takes: one a triangle of every level, laid out as its indices are, or
    /// none for a mesh without them.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Its triangle-material words.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static int MaterialWordsOf(SdfMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        return (mesh.TriangleMaterials.IsEmpty ? 0 : mesh.LevelledTriangleCount);
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
    /// <param name="generations">Each staged index's explicit nonzero history generation.</param>
    /// <param name="destination">The region's words; at least <see cref="SdfMeshRegionLayout.Words"/> of them, and only
    /// those are written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> or <paramref name="meshes"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the layout.</exception>
    public static void Write(IReadOnlyList<SdfMeshDraw> draws, Dictionary<SdfMesh, SdfMeshRegionMesh> meshes, SdfMeshRegionLayout layout, ReadOnlySpan<uint> generations, Span<uint> destination) {
        RequireDestination(destination: destination, layout: layout);
        WriteDraws(
            destination: destination,
            draws: draws,
            generations: generations,
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
    /// <param name="generations">Each staged index's explicit nonzero history generation.</param>
    /// <param name="destination">The draw records' words; at least <see cref="SdfMeshRecord.DrawWords"/> a draw.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draws"/> or <paramref name="meshes"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the records.</exception>
    /// <exception cref="KeyNotFoundException">A draw names a mesh <paramref name="meshes"/> does not place.</exception>
    public static void WriteDraws(IReadOnlyList<SdfMeshDraw> draws, Dictionary<SdfMesh, SdfMeshRegionMesh> meshes, ReadOnlySpan<uint> generations, Span<uint> destination) {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(meshes);
        if (generations.Length != draws.Count) { throw new ArgumentException(message: "Every draw needs its explicitly observed generation.", paramName: nameof(generations)); }
        if (destination.Length < (draws.Count * SdfMeshRecord.DrawWords)) {
            throw new ArgumentException(message: $"{draws.Count} draw records need {(draws.Count * SdfMeshRecord.DrawWords)} words; the destination holds {destination.Length}.", paramName: nameof(destination));
        }

        for (var draw = 0; (draw < draws.Count); draw++) {
            var item = draws[draw];

            WriteDraw(
                draw: in item,
                placement: meshes[item.Mesh],
                generation: generations[draw],
                record: destination.Slice(
                    length: SdfMeshRecord.DrawWords,
                    start: (draw * SdfMeshRecord.DrawWords)
                )
            );
        }
    }
    /// <summary>Writes one draw's record, as <see cref="WriteDraws"/> writes each.</summary>
    /// <param name="draw">The draw.</param>
    /// <param name="placement">Where the draw's mesh lies in the region.</param>
    /// <param name="generation">The explicit nonzero generation observed for this staged index.</param>
    /// <param name="record">The record's words; at least <see cref="SdfMeshRecord.DrawWords"/>.</param>
    /// <exception cref="ArgumentException">The mesh's levels exceed the indirect identity's level field.</exception>
    public static void WriteDraw(in SdfMeshDraw draw, SdfMeshRegionMesh placement, uint generation, Span<uint> record) {
        var previous = default(SdfMeshDraw);

        _ = WriteDrawCore(draw: in draw, previous: in previous, placement: placement, generation: generation, record: record, retained: false);
    }

    /// <summary>Updates a retained packed draw, deriving only fields whose inputs changed. The record must still
    /// contain the full packing of <paramref name="previous"/>; changed placement addresses are compared directly
    /// with its words. Identity changes alone do not rewrite GPU data.</summary>
    /// <param name="draw">The current draw.</param>
    /// <param name="previous">The draw whose complete record is retained.</param>
    /// <param name="placement">The current mesh placement.</param>
    /// <param name="generation">The nonzero observed surface generation.</param>
    /// <param name="record">The retained record, updated in place.</param>
    /// <returns>The CPU words assigned and the expensive field derivations performed, independently of upload deltas.</returns>
    public static SdfMeshDrawPacking WriteDrawIncremental(in SdfMeshDraw draw, in SdfMeshDraw previous, SdfMeshRegionMesh placement, uint generation, Span<uint> record) =>
        WriteDrawCore(draw: in draw, previous: in previous, placement: placement, generation: generation, record: record, retained: true);

    // Full and retained packing share every field writer. Exact input bits are the dependency key: numeric equality
    // would forget signed zero and NaN payload changes that the packed record preserves.
    private static SdfMeshDrawPacking WriteDrawCore(in SdfMeshDraw draw, in SdfMeshDraw previous, SdfMeshRegionMesh placement, uint generation, Span<uint> record, bool retained) {
        var (mesh, matrix, material, _) = draw;

        if (generation == 0) { throw new ArgumentOutOfRangeException(paramName: nameof(generation), message: "A staged instance generation is nonzero."); }
        if (mesh.Levels.Count >= (1 << (32 - SdfKernelInterfaces.MeshLevelShift))) {
            throw new ArgumentException(message: "The mesh's levels exceed the indirect draw identity's level field.", paramName: nameof(draw));
        }

        var previousMatrix = previous.ObjectToWorld;
        var carry = draw.CarryRotation;
        var previousCarry = previous.CarryRotation;
        var shift = draw.CarryTranslation;
        var previousShift = previous.CarryTranslation;
        var clip = draw.ClipPlane;
        var previousClip = previous.ClipPlane;
        var meshChanged = (!retained || !ReferenceEquals(objA: mesh, objB: previous.Mesh));
        var matrixChanged = (!retained || !SameDrawBits(left: in matrix, right: in previousMatrix));
        var normalChanged = (!retained || !SameNormalBasis(left: in matrix, right: in previousMatrix));
        var boxChanged = (meshChanged || normalChanged || (draw.Displacement != previous.Displacement) || !SameDrawTranslation(left: in matrix, right: in previousMatrix) || !SameDrawBits(left: in carry, right: in previousCarry) ||
            !SameDrawBits(left: in shift, right: in previousShift) ||
            (BitConverter.SingleToUInt32Bits(draw.CarryScale) != BitConverter.SingleToUInt32Bits(previous.CarryScale)));
        var writer = new DrawRecordWriter(record[..SdfMeshRecord.DrawWords], retained);

        if (matrixChanged) { WriteDrawMatrix(matrix: in matrix, record: ref writer); }
        if (normalChanged) { WriteNormalMatrix(matrix: matrix, record: ref writer); }

        writer[SdfMeshRecord.MaterialWord] = unchecked((uint)material);
        writer[SdfMeshRecord.IndexWord] = ((uint)placement.IndexWord);
        writer[SdfMeshRecord.IndexCountWord] = ((uint)placement.IndexCount);
        writer[SdfMeshRecord.VertexWord] = ((uint)placement.VertexWord);
        writer[SdfMeshRecord.FlagsWord] =
            (mesh.Normals.IsEmpty ? 0u : SdfMeshRecord.NormalsFlag) | (mesh.TriangleMaterials.IsEmpty ? 0u : SdfMeshRecord.MaterialsFlag) | (mesh.VertexMembers.IsEmpty ? 0u : SdfMeshRecord.SkinFlag) | (draw.UsesRootMaterialFrame ? 0u : SdfMeshRecord.WorldMaterialFlag);
        writer[SdfMeshRecord.TriangleMaterialWord] = ((uint)placement.MaterialWord);
        writer[SdfMeshRecord.FrameSlotWord] = unchecked((uint)(draw.FrameSlot ?? SdfProgram.NoDynamicTransformSlot));
        writer[SdfMeshRecord.SkinWord] = ((uint)placement.SkinWord);
        writer[SdfMeshRecord.CarryRotationWord] = BitConverter.SingleToUInt32Bits(carry.X);
        writer[SdfMeshRecord.CarryRotationWord + 1] = BitConverter.SingleToUInt32Bits(carry.Y);
        writer[SdfMeshRecord.CarryRotationWord + 2] = BitConverter.SingleToUInt32Bits(carry.Z);
        writer[SdfMeshRecord.CarryRotationWord + 3] = BitConverter.SingleToUInt32Bits(carry.W);
        writer[SdfMeshRecord.CarryTranslationWord] = BitConverter.SingleToUInt32Bits(shift.X);
        writer[SdfMeshRecord.CarryTranslationWord + 1] = BitConverter.SingleToUInt32Bits(shift.Y);
        writer[SdfMeshRecord.CarryTranslationWord + 2] = BitConverter.SingleToUInt32Bits(shift.Z);
        writer[SdfMeshRecord.CarryScaleWord] = BitConverter.SingleToUInt32Bits(draw.CarryScale);
        writer[SdfMeshRecord.ClipPlaneWord] = BitConverter.SingleToUInt32Bits(clip.X);
        writer[SdfMeshRecord.ClipPlaneWord + 1] = BitConverter.SingleToUInt32Bits(clip.Y);
        writer[SdfMeshRecord.ClipPlaneWord + 2] = BitConverter.SingleToUInt32Bits(clip.Z);
        writer[SdfMeshRecord.ClipPlaneWord + 3] = BitConverter.SingleToUInt32Bits(clip.W);
        writer[SdfMeshRecord.LevelTableWord] = ((uint)(placement.IndexWord + (3 * mesh.LevelledTriangleCount)));
        writer[SdfMeshRecord.LevelTableWord + 1] = ((uint)mesh.Levels.Count);
        writer[SdfMeshRecord.DisplacementWord] = ((draw.Displacement is { } displacement) ? ((uint)displacement.Handle) : SdfFrameSampled.None);

        var selection = record.Slice(length: 16, start: SdfMeshRecord.SelectionWord);
        var boxed = (retained && ((selection[3] & 1u) == 0u));

        if (boxChanged) {
            boxed = SdfMeshBox.TryOf(box: out var box, draw: in draw);
            WriteSelectionVector(words: ref writer, offset: SdfMeshRecord.SelectionWord + 0, value: box.Center);
            WriteSelectionVector(words: ref writer, offset: SdfMeshRecord.SelectionWord + 4, value: box.HalfX);
            writer[SdfMeshRecord.SelectionWord + 7] = BitConverter.SingleToUInt32Bits(value: box.Stretch);
            WriteSelectionVector(words: ref writer, offset: SdfMeshRecord.SelectionWord + 8, value: box.HalfY);
            WriteSelectionVector(words: ref writer, offset: SdfMeshRecord.SelectionWord + 12, value: box.HalfZ);
        }

        var enclosed = ((!meshChanged && !normalChanged && SameDrawBits(left: in clip, right: in previousClip))
            ? ((selection[3] & 4u) != 0u)
            : draw.EnclosesSolid);
        writer[SdfMeshRecord.SelectionWord + 3] = (boxed ? 0u : 1u) | (draw.Glass ? 2u : 0u) | (enclosed ? 4u : 0u);
        writer[SdfMeshRecord.SelectionWord + 11] = generation;
        writer[SdfMeshRecord.SelectionWord + 15] = ((uint)mesh.Levels.Count);

        return new(PackedWords: writer.PackedWords, NormalComputed: normalChanged, SelectionComputed: boxChanged, ChangedWords: writer.ChangedWords);
    }

    // Every packed write passes this door: a changed-word mask cannot miss an assigned field.
    private ref struct DrawRecordWriter {
        private readonly Span<uint> m_words;
        private readonly bool m_retained;
        public int PackedWords;
        public ulong ChangedWords;

        public DrawRecordWriter(Span<uint> words, bool retained) {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(words.Length, 64);
            m_words = words;
            m_retained = retained;
        }
        public uint this[int index] {
            set {
                if (m_retained && m_words[index] == value) { return; }
                m_words[index] = value;
                ChangedWords |= 1UL << index;
                PackedWords++;
            }
        }
    }

    private static bool SameDrawBits<T>(in T left, in T right) where T : unmanaged =>
        MemoryMarshal.AsBytes(span: new ReadOnlySpan<T>(reference: in left)).SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<T>(reference: in right)));

    private static bool SameNormalBasis(in Matrix4x4 left, in Matrix4x4 right) {
        var before = MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in left));
        var after = MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in right));

        return before[..12].SequenceEqual(other: after[..12]) &&
            before.Slice(start: 16, length: 12).SequenceEqual(other: after.Slice(start: 16, length: 12)) &&
            before.Slice(start: 32, length: 12).SequenceEqual(other: after.Slice(start: 32, length: 12));
    }

    private static bool SameDrawTranslation(in Matrix4x4 left, in Matrix4x4 right) =>
        MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in left)).Slice(start: 48, length: 12)
            .SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in right)).Slice(start: 48, length: 12));

    private static void WriteDrawMatrix(in Matrix4x4 matrix, ref DrawRecordWriter record) {
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
    }

    private static void WriteSelectionVector(ref DrawRecordWriter words, int offset, Vector3 value) {
        words[offset + 0] = BitConverter.SingleToUInt32Bits(value: value.X);
        words[offset + 1] = BitConverter.SingleToUInt32Bits(value: value.Y);
        words[offset + 2] = BitConverter.SingleToUInt32Bits(value: value.Z);
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

        var materialWord = (placement.MaterialWord + mesh.TriangleMaterials.Length);
        var indexWord = (placement.IndexWord + mesh.Indices.Length);

        foreach (var level in mesh.Levels) {
            level.TriangleMaterials.Span.CopyTo(destination: destination[materialWord..]);
            level.Indices.Span.CopyTo(destination: destination[indexWord..]);
            materialWord += level.TriangleMaterials.Length;
            indexWord += level.Indices.Length;
        }
        // A shared table after this mesh's indices, independent of draw count and of the baker's current two levels.
        var firstTriangle = 0;

        for (var level = 0; (level <= mesh.Levels.Count); level++) {
            var triangles = ((level == 0) ? mesh.TriangleCount : mesh.Levels[(level - 1)].TriangleCount);

            destination[indexWord++] = ((uint)firstTriangle);
            destination[indexWord++] = ((uint)(3 * triangles));
            destination[indexWord++] = BitConverter.SingleToUInt32Bits(value: ((level == 0) ? 0f : mesh.Levels[(level - 1)].Error));
            firstTriangle += triangles;
        }
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
    private static void WriteNormalMatrix(Matrix4x4 matrix, ref DrawRecordWriter record) {
        var upper = new Matrix4x4(
            m11: matrix.M11, m12: matrix.M12, m13: matrix.M13, m14: 0f,
            m21: matrix.M21, m22: matrix.M22, m23: matrix.M23, m24: 0f,
            m31: matrix.M31, m32: matrix.M32, m33: matrix.M33, m34: 0f,
            m41: 0f, m42: 0f, m43: 0f, m44: 1f
        );
        var normal = (Matrix4x4.Invert(matrix: upper, result: out var inverse)
            ? Matrix4x4.Transpose(matrix: inverse)
            : upper);

        record[SdfMeshRecord.NormalMatrixWord + 0] = BitConverter.SingleToUInt32Bits(value: normal.M11);
        record[SdfMeshRecord.NormalMatrixWord + 1] = BitConverter.SingleToUInt32Bits(value: normal.M12);
        record[SdfMeshRecord.NormalMatrixWord + 2] = BitConverter.SingleToUInt32Bits(value: normal.M13);
        record[SdfMeshRecord.NormalMatrixWord + 3] = BitConverter.SingleToUInt32Bits(value: normal.M21);
        record[SdfMeshRecord.NormalMatrixWord + 4] = BitConverter.SingleToUInt32Bits(value: normal.M22);
        record[SdfMeshRecord.NormalMatrixWord + 5] = BitConverter.SingleToUInt32Bits(value: normal.M23);
        record[SdfMeshRecord.NormalMatrixWord + 6] = BitConverter.SingleToUInt32Bits(value: normal.M31);
        record[SdfMeshRecord.NormalMatrixWord + 7] = BitConverter.SingleToUInt32Bits(value: normal.M32);
        record[SdfMeshRecord.NormalMatrixWord + 8] = BitConverter.SingleToUInt32Bits(value: normal.M33);
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
