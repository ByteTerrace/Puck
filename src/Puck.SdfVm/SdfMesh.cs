using System.Numerics;
using System.Runtime.CompilerServices;
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
    // A cut or merge can pass its weld to the next bake stage. Published meshes need only their planes.
    internal int[]? BakedSites { get; init; }
    internal int[] Sites() => BakedSites ?? SdfMeshWeld.Sites(Positions.Span);

    /// <summary>The actual contour lattice spacing in object space; zero for an authored triangle mesh.</summary>
    public Vector3 BakeCell { get; init; }
    /// <summary>Gets the independently drawn pieces of a large static union; empty for a single mesh. Only the pieces
    /// enter the mesh region, the irradiance scene and the store. An offline mesh's own triangles concatenate their merged
    /// surfaces for whole-group queries; a published one keeps no joined indices (<see cref="Indices"/>).</summary>
    public IReadOnlyList<SdfMesh> Chunks { get; init; } = [];
    /// <summary>Gets how many independently drawn meshes the mesh is: its <see cref="Chunks"/>, or one, itself.</summary>
    public int PieceCount => Math.Max(val1: 1, val2: Chunks.Count);

    /// <summary>Returns one independently drawn mesh of the mesh: a chunk, or the mesh itself when it has none.</summary>
    /// <param name="index">The piece, below <see cref="PieceCount"/>.</param>
    /// <returns>The piece.</returns>
    public SdfMesh Piece(int index) => ((Chunks.Count == 0) ? this : Chunks[index]);

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
    /// mesh has more than <see cref="MaxMembers"/> members, a vertex names no member, or an influence is not
    /// canonical.</exception>
    /// <param name="influences">One second-owner tuple per skinned vertex, each canonical
    /// (<see cref="SdfMeshInfluence.Canonical"/>), or empty for rigid members.</param>
    public SdfMesh(ReadOnlyMemory<Vector3> positions, ReadOnlyMemory<uint> indices, ReadOnlyMemory<Vector3> normals = default, ReadOnlyMemory<uint> triangleMaterials = default, ReadOnlyMemory<SdfMeshMember> members = default, ReadOnlyMemory<byte> vertexMembers = default, ReadOnlyMemory<SdfMeshInfluence> influences = default) {
        if (!influences.IsEmpty && ((influences.Length != positions.Length) || (vertexMembers.Length != positions.Length))) { throw new ArgumentException(message: "Influences require one complete member tuple per vertex.", paramName: nameof(influences)); }
        foreach (var influence in influences.Span) {
            if (!influence.Canonical) { throw new ArgumentException(message: "A vertex whose share leaves one owner rides that owner first, with the rigid influence (SdfMeshSkinTuple.Of).", paramName: nameof(influences)); }
            if ((influence.Second != NoMember) && (influence.Second >= members.Length)) { throw new ArgumentException(message: "An influence names no member.", paramName: nameof(influences)); }
            if (!float.IsFinite(f: influence.Gradient.LengthSquared())) { throw new ArgumentException(message: "An influence gradient must be finite.", paramName: nameof(influences)); }
        }
        Influences = influences;
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
        m_indices = indices;
        m_triangleCount = (indices.Length / 3);
        Normals = normals;
        TriangleMaterials = triangleMaterials;
        Members = members;
        VertexMembers = vertexMembers;
    }

    /// <summary>The most triangles a mesh holds: the mesh pass writes a covered pixel's triangle as a float, which holds
    /// every whole number up to 2^24 exactly, so the last triangle is 2^24 - 1.</summary>
    public const int MaxTriangles = (1 << 24);
    /// <summary>The most members a mesh's vertices ride (<see cref="SdfSkinLimits.MaxMembers"/>): a vertex names its
    /// member in a byte, and this value is <see cref="NoMember"/>.</summary>
    public const int MaxMembers = SdfSkinLimits.MaxMembers;
    /// <summary>The vertex member of a vertex that rides its draw rather than a member.</summary>
    public const byte NoMember = byte.MaxValue;

    /// <summary>Gets three vertex indices per triangle.</summary>
    /// <exception cref="InvalidOperationException">The mesh is a published chunked mesh, which keeps no joined indices:
    /// its chunks hold its triangles, and only they are drawn, traced and stored.</exception>
    public ReadOnlyMemory<uint> Indices => (m_joinedIndicesOmitted
        ? throw new InvalidOperationException(message: "A published chunked mesh keeps no joined indices; read its chunks' triangles.")
        : m_indices);
    /// <summary>Gets one object-space normal per vertex, or none.</summary>
    public ReadOnlyMemory<Vector3> Normals { get; }
    /// <summary>Gets the object-space vertex positions.</summary>
    public ReadOnlyMemory<Vector3> Positions { get; }
    /// <summary>Gets the least corner of the box the positions span, object space.</summary>
    public Vector3 PositionLow { get; }
    /// <summary>Gets the greatest corner of the box the positions span, object space.</summary>
    public Vector3 PositionHigh { get; }
    /// <summary>Gets the triangle count; a chunked mesh's is its chunks' together.</summary>
    public int TriangleCount => m_triangleCount;
    /// <summary>Gets one palette entry per triangle, added to its draw's material, or none.</summary>
    public ReadOnlyMemory<uint> TriangleMaterials { get; }
    /// <summary>Gets the slots besides its draw's frame slot whose motion carries some of its vertices, or none.</summary>
    public ReadOnlyMemory<SdfMeshMember> Members { get; }
    /// <summary>Gets each vertex's index in <see cref="Members"/>, or <see cref="NoMember"/> for a vertex that rides its
    /// draw; none when every vertex rides its draw.</summary>
    public ReadOnlyMemory<byte> VertexMembers { get; }
    /// <summary>Gets the second-owner tuple per skinned vertex; empty for rigid skinning.</summary>
    public ReadOnlyMemory<SdfMeshInfluence> Influences { get; }
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

        for (var at = 0; (at < chosen); at++) {
            first += count;
            count = levels[at].TriangleCount;
        }
        return (first, count);
    }

    private readonly IReadOnlyList<SdfMeshLevel> m_levels = [];
    // The mesh's and its levels' triangles together, set with the levels.
    private readonly int m_levelledTriangleCount;
    private readonly ReadOnlyMemory<uint> m_indices;
    private readonly int m_triangleCount;
    // Whether this is a published chunked mesh, whose joined indices were never copied (SdfMesh.Storage.cs).
    private readonly bool m_joinedIndicesOmitted;

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
/// <remarks>A draw's placement is read through one door, <see cref="PlacementIn"/>: a draw placed by its slot
/// (<see cref="PlacedBySlot"/>) carries its scale and no pose, since its frame slot's row in the frame's dynamic
/// transforms places it on the CPU as the kernels place it on the GPU (<c>sdfMeshPoseOf</c>). Such a draw compares
/// equal, and stages the same content, wherever its slot moves, so a body that only moved changes no draw.</remarks>
public readonly record struct SdfMeshDraw {
    /// <summary>Initializes a new instance of the <see cref="SdfMeshDraw"/> struct.</summary>
    /// <param name="mesh">The triangles to draw.</param>
    /// <param name="objectToWorld">The object-to-world transform, in the row-vector convention of
    /// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>. A draw placed by its slot (<see cref="PlacedBySlot"/>) keeps
    /// only its scale, the length of its first row; its pose is its slot's row.</param>
    /// <param name="material">The material-table index the triangles shade with, as an SDF hit's material does; a mesh
    /// with <see cref="SdfMesh.TriangleMaterials"/> adds each triangle's entry to it.</param>
    /// <param name="identity">The host's picking identity, also the fallback motion key when <see cref="MotionIdentity"/>
    /// is absent. <see cref="SdfMeshHistory"/> observes the stable key and mesh at each staged index to seed previous
    /// matrices and invalidate GPU visibility/detail history together.</param>
    public SdfMeshDraw(SdfMesh mesh, Matrix4x4 objectToWorld, int material, object identity) {
        Mesh = mesh;
        Matrix = objectToWorld;
        Material = material;
        Identity = identity;
    }

    /// <summary>Gets the triangles to draw.</summary>
    public SdfMesh Mesh { get; init; }
    /// <summary>Gets the material-table index the triangles shade with.</summary>
    public int Material { get; init; }
    /// <summary>Gets the host's picking identity, also the fallback motion key when <see cref="MotionIdentity"/> is
    /// absent.</summary>
    public object Identity { get; init; }
    // The matrix the producer gave: the placement of a draw not placed by its slot, and only the scale of one that is.
    // Read through PlacementIn, FixedPlacement or SlotScale alone.
    private Matrix4x4 Matrix { get; init; }

    /// <summary>Gets whether the draw is placed by its slot: it rides its frame slot (<see cref="RidesSlot"/>) and moves
    /// (<see cref="IsDynamic"/>), so its slot's row is its placement and no pose of its own is kept or compared. A static
    /// draw riding a slot keeps its placement, which the static scene reads (<see cref="FixedPlacement"/>), and the
    /// producer moves the draw when it moves.</summary>
    public bool PlacedBySlot => (RidesSlot && IsDynamic);

    /// <summary>Returns the draw's object-to-world matrix in a frame: for a draw riding its frame slot, the slot's row
    /// at the draw's scale (the scale first, then the row's orientation and position, as <c>sdfMeshPoseOf</c> poses it
    /// and <see cref="SdfMeshBox.Ridden"/> places its box); for any other, the matrix it was given. This is the one door a
    /// CPU reader of a draw's placement goes through.</summary>
    /// <param name="transforms">The frame's dynamic transforms, as the region's transform rows hold them.</param>
    /// <returns>The matrix, in the row-vector convention of <see cref="Puck.Abstractions.Cameras.ViewProjection"/>. A slot
    /// outside <paramref name="transforms"/> places a draw riding it at the origin at its scale, which no kernel reads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transforms"/> is <see langword="null"/>.</exception>
    public Matrix4x4 PlacementIn(IReadOnlyList<DynamicTransform> transforms) {
        ArgumentNullException.ThrowIfNull(argument: transforms);

        if (FrameSlot is not { } slot || (slot < 0)) {
            return Matrix;
        }

        var scale = Matrix4x4.CreateScale(scale: SlotScale);

        if (slot >= transforms.Count) {
            return scale;
        }

        var row = transforms[slot];

        return ((scale * Matrix4x4.CreateFromQuaternion(quaternion: row.Orientation)) * Matrix4x4.CreateTranslation(position: row.Position));
    }
    /// <summary>Gets the matrix of a draw not placed by its slot, which needs no frame to place it: a fixed placement,
    /// a static draw riding a slot (whose producer moves the draw when it moves), or an animated placement whose producer
    /// writes its matrix each frame. A static scene and an offline tool read it.</summary>
    /// <exception cref="InvalidOperationException">The draw is placed by its slot (<see cref="PlacedBySlot"/>): its
    /// placement is its slot's row, read through <see cref="PlacementIn"/>.</exception>
    public Matrix4x4 FixedPlacement => (PlacedBySlot
        ? throw new InvalidOperationException(message: "A draw placed by its slot is placed by its slot's row: read its placement through PlacementIn.")
        : Matrix);
    /// <summary>Returns the draw at another matrix: for a draw placed by its slot, at that matrix's scale.</summary>
    /// <param name="objectToWorld">The object-to-world transform.</param>
    /// <returns>The draw.</returns>
    public SdfMeshDraw PlacedAt(Matrix4x4 objectToWorld) => (this with { Matrix = objectToWorld });

    /// <summary>Returns whether two draws are the same placement of the same content: every field, except that a draw
    /// placed by its slot compares only its scale, its pose being its slot's row.</summary>
    /// <param name="other">The draw to compare.</param>
    /// <returns>Whether the draws are equal.</returns>
    public bool Equals(SdfMeshDraw other) =>
        ReferenceEquals(objA: Mesh, objB: other.Mesh) && (Material == other.Material) && Equals(objA: Identity, objB: other.Identity) &&
        Equals(objA: MotionIdentity, objB: other.MotionIdentity) && (IsDynamic == other.IsDynamic) && (Tier == other.Tier) &&
        ReferenceEquals(objA: Trace, objB: other.Trace) && (Piece == other.Piece) && (Glass == other.Glass) && (FrameSlot == other.FrameSlot) &&
        (UsesRootMaterialFrame == other.UsesRootMaterialFrame) && CarryRotation.Equals(other: other.CarryRotation) &&
        CarryTranslation.Equals(other: other.CarryTranslation) && CarryScale.Equals(obj: other.CarryScale) && ClipPlane.Equals(other: other.ClipPlane) &&
        (Displacement == other.Displacement) && (PlacedBySlot ? SlotScale.Equals(obj: other.SlotScale) : Matrix.Equals(other: other.Matrix));
    /// <summary>Returns a hash of the fields <see cref="Equals(SdfMeshDraw)"/> compares.</summary>
    /// <returns>The hash.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(RuntimeHelpers.GetHashCode(o: Mesh), Material, Identity, MotionIdentity, IsDynamic, FrameSlot, Glass, Piece),
            (PlacedBySlot ? SlotScale.GetHashCode() : Matrix.GetHashCode()),
            CarryTranslation,
            CarryScale,
            ClipPlane
        );

    /// <summary>Gets a stable surface identity for motion history when the picking identity also carries changing pose metadata.</summary>
    public object? MotionIdentity { get; init; }
    /// <summary>Gets whether the draw's placement moves (a moving body or an animated placement), which the static scene
    /// leaves out (<see cref="IsStatic"/>).</summary>
    public bool IsDynamic { get; init; }
    /// <summary>Gets whether the draw belongs to the static scene, which the irradiance volume's probes and the distant
    /// shadow map hold while nothing in its staged record changes: an opaque draw of a mesh whose placement is not
    /// <see cref="IsDynamic"/> and whose surface reads nothing that moves outside its record. A skinned mesh
    /// (<see cref="SdfMesh.VertexMembers"/>), which reads its members' live slots, and a displaced draw
    /// (<see cref="Displacement"/>), which reads the frame's sampled heights, are never static, whatever
    /// <see cref="IsDynamic"/> says. Glass neither blocks nor bounces the probes' rays, and a draw without a mesh draws
    /// nothing.</summary>
    public bool IsStatic => ((Mesh is { } mesh) && mesh.VertexMembers.IsEmpty && (Displacement is null) && !IsDynamic && !Glass);
    /// <summary>Gets the detail tier the drawn mesh was baked at (<see cref="SdfMeshTier"/>), or -1 for a mesh no bake
    /// tiered (an authored mesh). It reports only, in the selection counts
    /// (<see cref="SdfWorldTables.DescribeSelection"/>): no staged record or pass reads it.</summary>
    public int Tier { get; init; } = -1;
    /// <summary>Gets the mesh the irradiance volume's static scene holds in place of the drawn mesh of the draw's
    /// placement: its bake group's floor where the bake admits the floor as the group's trace mesh
    /// (<see cref="SdfMeshPathBake"/>), so a draw whose mesh moves from tier to tier with the view or the memory budget
    /// leaves the scene standing; <see langword="null"/> holds the drawn mesh. Every piece of the placement names it,
    /// and the scene takes it once, at the placement's first piece (<see cref="Piece"/>). No staged record or pass reads
    /// it.</summary>
    public SdfMesh? Trace { get; init; }
    /// <summary>Gets which piece of its bake group's drawn mesh the draw draws (<see cref="SdfMesh.Piece"/>): zero for
    /// a whole mesh and for the first piece of a chunked one.</summary>
    public int Piece { get; init; }
    /// <summary>Gets whether the draw is glass (<see cref="SdfMeshBakeGroup.Glass"/>): the glass pass rasterizes it over
    /// the opaque image, the cascades integrate its authored shadow density, and the opaque mesh pass and the
    /// irradiance volume's static scene leave it out.</summary>
    public bool Glass { get; init; }
    /// <summary>Gets the dynamic-transform slot whose frame the mesh's object space is, or <see langword="null"/> for a
    /// mesh in no slot's frame: the shading places a material's layers in that frame and reads its lanes, as it does for
    /// a field hit of a shape riding the slot. A draw with a frame slot rides it (<see cref="RidesSlot"/>): it is placed
    /// at the slot's pose in the frame at <see cref="SlotScale"/>, scale first, then the slot's orientation and position
    /// (<see cref="PlacementIn"/>), which is what its staged record keeps instead of a matrix.</summary>
    public int? FrameSlot { get; init; }
    /// <summary>Gets whether the draw rides its frame slot (<see cref="FrameSlot"/>): its record holds its scale and
    /// not its matrix, and the kernels pose it from the slot's row, so the draw moving with its slot changes no
    /// record.</summary>
    public bool RidesSlot => (FrameSlot is >= 0);
    /// <summary>Gets the uniform scale of the draw's matrix, its first row's length: for a draw riding its slot, the
    /// scale its record holds and its slot's pose is taken at.</summary>
    public float SlotScale => new Vector3(x: Matrix.M11, y: Matrix.M12, z: Matrix.M13).Length();

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
    public bool SameStagedContent(in SdfMeshDraw other) => Same(other: in other, groupMesh: false);
    /// <summary>Returns whether two draws place the same caster: the same staged content, except that a mesh may differ
    /// where both draws name one bake group's mesh at two tiers (the same non-null <see cref="Trace"/>, the group's floor).
    /// A held map whose texel is far coarser than a tier's step (the distant shadow map) stands across such a swap.</summary>
    /// <param name="other">The draw to compare.</param>
    /// <returns>Whether either draw places the same caster.</returns>
    public bool SameCaster(in SdfMeshDraw other) => Same(other: in other, groupMesh: true);
    /// <summary>Returns how a draw's staged content differs from another's: not at all, in its matrix alone, in the pose
    /// of a slot a static draw rides at one scale, or elsewhere, comparing exact bits as <see cref="SameStagedContent"/>
    /// does. A draw placed by its slot (<see cref="PlacedBySlot"/>) differs in its matrix only where its scale does.</summary>
    /// <param name="other">The draw to compare, usually the one staged before.</param>
    /// <returns>The difference.</returns>
    public SdfMeshDrawDifference StagedDifference(in SdfMeshDraw other) {
        if (!SameExceptMatrix(other: in other, groupMesh: false)) {
            return SdfMeshDrawDifference.Content;
        }
        if (SamePlacement(other: in other)) {
            return SdfMeshDrawDifference.None;
        }

        return ((RidesSlot && (BitConverter.SingleToUInt32Bits(value: SlotScale) == BitConverter.SingleToUInt32Bits(value: other.SlotScale)))
            ? SdfMeshDrawDifference.Slot
            : SdfMeshDrawDifference.Matrix);
    }

    private bool Same(in SdfMeshDraw other, bool groupMesh) => (SameExceptMatrix(other: in other, groupMesh: groupMesh) && SamePlacement(other: in other));
    // Whether two draws that agree in everything but their matrix place alike, by exact bits: a draw placed by its slot
    // by its scale alone (both draws are or are not, since SameExceptMatrix compares the slot and IsDynamic).
    private bool SamePlacement(in SdfMeshDraw other) {
        if (PlacedBySlot) {
            return (BitConverter.SingleToUInt32Bits(value: SlotScale) == BitConverter.SingleToUInt32Bits(value: other.SlotScale));
        }

        var leftMatrix = this.Matrix;
        var rightMatrix = other.Matrix;

        return Bitwise.Equal(left: in leftMatrix, right: in rightMatrix);
    }
    private bool SameExceptMatrix(in SdfMeshDraw other, bool groupMesh) {
        if (
            (!ReferenceEquals(objA: this.Mesh, objB: other.Mesh) && !(groupMesh && (this.Trace is { } trace) && ReferenceEquals(objA: trace, objB: other.Trace))) ||
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

        var leftRotation = this.CarryRotation;
        var rightRotation = other.CarryRotation;
        var leftShift = this.CarryTranslation;
        var rightShift = other.CarryTranslation;
        var leftClip = this.ClipPlane;
        var rightClip = other.ClipPlane;

        return (
            Bitwise.Equal(left: in leftRotation, right: in rightRotation) &&
            Bitwise.Equal(left: in leftShift, right: in rightShift) &&
            Bitwise.Equal(left: in leftClip, right: in rightClip)
        );
    }

    /// <summary>Gets whether the draw's faces turned from a light are exactly where that light leaves a solid: its mesh
    /// encloses (<see cref="SdfMesh.Encloses"/>), no clip plane cuts it open, and its matrix keeps its winding. A shadow
    /// cascade's exit layer draws such a draw's faces turned from the light alone and every other draw whole, as a thin
    /// caster.</summary>
    public bool EnclosesSolid => (Mesh.Encloses && (ClipPlane == Vector4.Zero) && (RidesSlot ? (SlotScale > 0f) : (Matrix.GetDeterminant() > 0f)));
}
/// <summary>How a draw's staged content differs from the draw staged before it (<see cref="SdfMeshDraw.StagedDifference"/>).</summary>
public enum SdfMeshDrawDifference : byte {
    /// <summary>The draws stage the same content.</summary>
    None,
    /// <summary>The draws differ in their object-to-world matrix alone: the same mesh, identities, flags, carry and clip,
    /// and a record that holds the matrix.</summary>
    Matrix,
    /// <summary>The draws ride one frame slot at one scale and differ only in the slot's pose: their records are the
    /// same, since a draw riding its slot is posed from the slot's row.</summary>
    Slot,
    /// <summary>The draws differ in something besides their matrix.</summary>
    Content,
}
/// <summary>The CPU work needed to update one retained packed draw, independently of device upload deltas.</summary>
/// <param name="PackedWords">The record words assigned; retained equal words are not assigned.</param>
/// <param name="SelectionComputed">Whether the selection box was derived.</param>
/// <param name="ChangedWords">One bit per word that changed in the retained record.</param>
public readonly record struct SdfMeshDrawPacking(int PackedWords, bool SelectionComputed, ulong ChangedWords);
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
/// A draw's record is <see cref="SdfMeshRecord"/>'s layout. Its word offsets are counted from the region's start, so a
/// reader needs nothing but the record to find a draw's triangles: its index <c>k</c> is the word at
/// <c>indexWord + k</c>, and names the vertex whose six words start at <c>vertexWord + (6 * index)</c>. A vertex without a
/// normal holds zeros there, and the flags say which a mesh has. A mesh with members (<see cref="SdfMeshRecord.SkinFlag"/>)
/// keeps them past its indices: their count, then <see cref="SdfMeshRecord.MemberWords"/> words a member (its slot
/// offset, then its position and orientation), then <see cref="SdfMeshRecord.SkinVertexWords"/> per vertex carrying its
/// owners, share and gradient. A draw riding its frame slot (<see cref="SdfMeshDraw.RidesSlot"/>) holds its scale as its
/// pose and its box in the slot's frame (<see cref="SdfMeshRecord.SlotBoxFlag"/>); any other holds its affine matrix and
/// its world box. No record holds a normal matrix: the kernels derive it from the pose. The record is whole words, so it
/// has no padding a structured-buffer reader could disagree on.
/// </para>
/// </summary>
public static class SdfMeshRegion {
    /// <summary>Packs a row that has no standing geometry: zero placement and generation, with only the selection
    /// absent bit set. The selection and hit doors can reject it without reading any mesh words.</summary>
    /// <param name="record">Exactly one draw record.</param>
    public static void WriteAbsent(Span<uint> record) {
        record.Clear();
        record[(SdfMeshRecord.SelectionWord + 3)] = SdfMeshRecord.AbsentFlag;
    }
    /// <summary>Reads back what a packed record's selection words say of its draw: whether a pass may draw it at all,
    /// the world box it is selected by, and whether it is glass. The box is the one <see cref="WriteDraw"/> wrote
    /// (<see cref="SdfMeshBox.TryOfFixed"/>), or, for a draw riding its frame slot (<see cref="SdfMeshRecord.SlotBoxFlag"/>),
    /// the box the kernels derive from the slot's row (<see cref="SdfMeshBox.Ridden"/>), so a reader judges a draw by the
    /// same box every pass selects it by under the same transforms.</summary>
    /// <param name="record">The record, <see cref="SdfMeshRecord.DrawWords"/> words that a write of this type packed.</param>
    /// <param name="transforms">The frame's dynamic transforms, as the region's transform rows hold them.</param>
    /// <param name="box">The draw's box, or <see langword="null"/> for a draw no host box bounds, which every pass
    /// draws, and for a slot outside <paramref name="transforms"/>.</param>
    /// <param name="glass">Whether the draw is glass (<see cref="SdfMeshDraw.Glass"/>).</param>
    /// <returns><see langword="false"/> for an absent record (<see cref="WriteAbsent"/>), which no pass draws.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transforms"/> is <see langword="null"/>.</exception>
    public static bool ReadSelection(ReadOnlySpan<uint> record, IReadOnlyList<DynamicTransform> transforms, out SdfMeshBox? box, out bool glass) {
        ArgumentNullException.ThrowIfNull(argument: transforms);
        var selection = record.Slice(length: SelectionWords, start: SdfMeshRecord.SelectionWord);
        var flags = selection[3];

        glass = ((flags & 2u) != 0u);
        box = null;
        if ((flags & SdfMeshRecord.AbsentFlag) != 0u) {
            return false;
        }
        if ((flags & 1u) != 0u) {
            return true;
        }
        if ((flags & SdfMeshRecord.SlotBoxFlag) != 0u) {
            var slot = unchecked((int)record[SdfMeshRecord.FrameSlotWord]);

            if ((slot >= 0) && (slot < transforms.Count)) {
                box = SdfMeshBox.Ridden(
                    carryRotation: new Quaternion(x: Float(words: record, word: SdfMeshRecord.CarryRotationWord), y: Float(words: record, word: (SdfMeshRecord.CarryRotationWord + 1)),
                        z: Float(words: record, word: (SdfMeshRecord.CarryRotationWord + 2)), w: Float(words: record, word: (SdfMeshRecord.CarryRotationWord + 3))),
                    carryScale: Float(words: record, word: SdfMeshRecord.CarryScaleWord),
                    carryTranslation: ReadSelectionVector(offset: SdfMeshRecord.CarryTranslationWord, words: record),
                    center: ReadSelectionVector(words: selection, offset: 0),
                    half: new Vector3(x: BitConverter.UInt32BitsToSingle(value: selection[4]), y: BitConverter.UInt32BitsToSingle(value: selection[9]),
                        z: BitConverter.UInt32BitsToSingle(value: selection[14])),
                    scale: Float(words: record, word: SdfMeshRecord.ScaleWord),
                    slot: transforms[slot]
                );
            }

            return true;
        }
        box = new SdfMeshBox(
            Center: ReadSelectionVector(words: selection, offset: 0),
            HalfX: ReadSelectionVector(words: selection, offset: 4),
            HalfY: ReadSelectionVector(words: selection, offset: 8),
            HalfZ: ReadSelectionVector(words: selection, offset: 12)
        ) {
            Stretch = BitConverter.UInt32BitsToSingle(value: selection[7]),
        };

        return true;

        static float Float(ReadOnlySpan<uint> words, int word) => BitConverter.UInt32BitsToSingle(value: words[word]);
    }
    /// <summary>The words of a record's selection rows, from <see cref="SdfMeshRecord.SelectionWord"/>.</summary>
    public const int SelectionWords = 16;
    /// <summary>Writes the box a draw is selected by into a record's selection words, as <see cref="ReadSelection"/> reads
    /// it, leaving every other selection word as it stands: a world box's center and half axes and its stretch, or the
    /// unboxed flag for a draw no host box bounds, which every pass draws. A skinned draw's record takes its host bound
    /// (<see cref="SdfMeshBox.TryOfSkin"/>) here, whenever its members move.</summary>
    /// <param name="selection">The record's <see cref="SelectionWords"/> selection words.</param>
    /// <param name="box">The box, or <see langword="null"/> for none.</param>
    public static void WriteSelectionBox(Span<uint> selection, in SdfMeshBox? box) {
        selection[3] &= ~SdfMeshRecord.SlotBoxFlag;
        if (box is not { } bounded) {
            selection[3] |= 1u;

            return;
        }

        WriteVector(offset: 0, selection: selection, value: bounded.Center);
        WriteVector(offset: 4, selection: selection, value: bounded.HalfX);
        selection[7] = BitConverter.SingleToUInt32Bits(value: bounded.Stretch);
        WriteVector(offset: 8, selection: selection, value: bounded.HalfY);
        WriteVector(offset: 12, selection: selection, value: bounded.HalfZ);
        selection[3] &= ~1u;

        static void WriteVector(Span<uint> selection, int offset, Vector3 value) {
            selection[offset] = BitConverter.SingleToUInt32Bits(value: value.X);
            selection[(offset + 1)] = BitConverter.SingleToUInt32Bits(value: value.Y);
            selection[(offset + 2)] = BitConverter.SingleToUInt32Bits(value: value.Z);
        }
    }
    private static Vector3 ReadSelectionVector(ReadOnlySpan<uint> words, int offset) => new(
        x: BitConverter.UInt32BitsToSingle(value: words[offset]),
        y: BitConverter.UInt32BitsToSingle(value: words[(offset + 1)]),
        z: BitConverter.UInt32BitsToSingle(value: words[(offset + 2)])
    );

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
    /// <summary>The bytes a baked mesh takes of a region whose arena places it (<see cref="SdfMeshRegionArena"/>): its
    /// own words, or each of its chunks' where it is chunked, since the chunks are what draws name.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Its bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static long PlacedBytesOf(SdfMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var words = ((mesh.Chunks.Count == 0) ? ((long)WordsOf(mesh: mesh)) : 0L);

        foreach (var chunk in mesh.Chunks) { words += WordsOf(mesh: chunk); }

        return (words * sizeof(uint));
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
    /// <see cref="SdfMeshRecord.MemberWords"/> words and SkinVertexWords per vertex.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns>Its members' words.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static int SkinWordsOf(SdfMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        return (mesh.VertexMembers.IsEmpty ? 0 : checked(((1 + (mesh.Members.Length * SdfMeshRecord.MemberWords)) + (SdfMeshRecord.SkinVertexWords * mesh.VertexMembers.Length))));
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

        _ = WriteDrawCore(draw: in draw, generation: generation, placement: placement, previous: in previous, record: record, retained: false);
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
        WriteDrawCore(draw: in draw, generation: generation, placement: placement, previous: in previous, record: record, retained: true);
    /// <summary>Updates a retained packed draw whose draw differs from the one it holds in its matrix alone
    /// (<see cref="SdfMeshDrawDifference.Matrix"/>): the pose words, the selection box and the selection flags, the only
    /// words such a draw changes. The record must still contain the full packing of <paramref name="previous"/>, so it
    /// ends as <see cref="WriteDrawIncremental"/> would leave it. A draw riding its slot whose pose alone moved
    /// (<see cref="SdfMeshDrawDifference.Slot"/>) changes no word.</summary>
    /// <param name="draw">The current draw.</param>
    /// <param name="previous">The draw whose complete record is retained; it differs from <paramref name="draw"/> only
    /// in its matrix.</param>
    /// <param name="record">The retained record, updated in place.</param>
    /// <returns>The CPU words assigned and the expensive field derivations performed.</returns>
    public static SdfMeshDrawPacking WriteDrawPose(in SdfMeshDraw draw, in SdfMeshDraw previous, Span<uint> record) {
        var writer = new DrawRecordWriter(record[..SdfMeshRecord.DrawWords], true);
        var boxChanged = WritePose(draw: in draw, meshChanged: false, previous: in previous, record: record, retained: true, writer: ref writer);

        return new(ChangedWords: writer.ChangedWords, PackedWords: writer.PackedWords, SelectionComputed: boxChanged);
    }

    // Full and retained packing share every field writer. Exact input bits are the dependency key: numeric equality
    // would forget signed zero and NaN payload changes that the packed record preserves.
    private static SdfMeshDrawPacking WriteDrawCore(in SdfMeshDraw draw, in SdfMeshDraw previous, SdfMeshRegionMesh placement, uint generation, Span<uint> record, bool retained) {
        var (mesh, material) = (draw.Mesh, draw.Material);

        if (generation == 0) { throw new ArgumentOutOfRangeException(paramName: nameof(generation), message: "A staged instance generation is nonzero."); }
        if (mesh.Levels.Count >= (1 << (32 - SdfKernelInterfaces.MeshLevelShift))) {
            throw new ArgumentException(message: "The mesh's levels exceed the indirect draw identity's level field.", paramName: nameof(draw));
        }

        var carry = draw.CarryRotation;
        var shift = draw.CarryTranslation;
        var clip = draw.ClipPlane;
        var writer = new DrawRecordWriter(record[..SdfMeshRecord.DrawWords], retained);
        var boxChanged = WritePose(draw: in draw, meshChanged: (!retained || !ReferenceEquals(objA: mesh, objB: previous.Mesh)), previous: in previous,
            record: record, retained: retained, writer: ref writer);

        writer[SdfMeshRecord.MaterialWord] = unchecked((uint)material);
        writer[SdfMeshRecord.IndexWord] = ((uint)placement.IndexWord);
        writer[SdfMeshRecord.IndexCountWord] = ((uint)placement.IndexCount);
        writer[SdfMeshRecord.VertexWord] = ((uint)placement.VertexWord);
        writer[SdfMeshRecord.FlagsWord] =
            (mesh.Normals.IsEmpty ? 0u : SdfMeshRecord.NormalsFlag) | (mesh.TriangleMaterials.IsEmpty ? 0u : SdfMeshRecord.MaterialsFlag) | (mesh.VertexMembers.IsEmpty ? 0u : SdfMeshRecord.SkinFlag) | (draw.UsesRootMaterialFrame ? 0u : SdfMeshRecord.WorldMaterialFlag);
        writer[SdfMeshRecord.TriangleMaterialWord] = ((uint)placement.MaterialWord);
        writer[SdfMeshRecord.FrameSlotWord] = unchecked((uint)(draw.FrameSlot ?? SdfProgram.NoDynamicTransformSlot));
        writer[SdfMeshRecord.SkinWord] = ((uint)placement.SkinWord);
        writer[SdfMeshRecord.CarryRotationWord] = BitConverter.SingleToUInt32Bits(value: carry.X);
        writer[(SdfMeshRecord.CarryRotationWord + 1)] = BitConverter.SingleToUInt32Bits(value: carry.Y);
        writer[(SdfMeshRecord.CarryRotationWord + 2)] = BitConverter.SingleToUInt32Bits(value: carry.Z);
        writer[(SdfMeshRecord.CarryRotationWord + 3)] = BitConverter.SingleToUInt32Bits(value: carry.W);
        writer[SdfMeshRecord.CarryTranslationWord] = BitConverter.SingleToUInt32Bits(value: shift.X);
        writer[(SdfMeshRecord.CarryTranslationWord + 1)] = BitConverter.SingleToUInt32Bits(value: shift.Y);
        writer[(SdfMeshRecord.CarryTranslationWord + 2)] = BitConverter.SingleToUInt32Bits(value: shift.Z);
        writer[SdfMeshRecord.CarryScaleWord] = BitConverter.SingleToUInt32Bits(value: draw.CarryScale);
        writer[SdfMeshRecord.ClipPlaneWord] = BitConverter.SingleToUInt32Bits(value: clip.X);
        writer[(SdfMeshRecord.ClipPlaneWord + 1)] = BitConverter.SingleToUInt32Bits(value: clip.Y);
        writer[(SdfMeshRecord.ClipPlaneWord + 2)] = BitConverter.SingleToUInt32Bits(value: clip.Z);
        writer[(SdfMeshRecord.ClipPlaneWord + 3)] = BitConverter.SingleToUInt32Bits(value: clip.W);
        writer[SdfMeshRecord.LevelTableWord] = ((uint)(placement.IndexWord + (3 * mesh.LevelledTriangleCount)));
        writer[(SdfMeshRecord.LevelTableWord + 1)] = ((uint)mesh.Levels.Count);
        writer[SdfMeshRecord.DisplacementWord] = ((draw.Displacement is { } displacement) ? ((uint)displacement.Handle) : SdfFrameSampled.None);
        writer[(SdfMeshRecord.SelectionWord + 11)] = generation;
        writer[(SdfMeshRecord.SelectionWord + 15)] = ((uint)mesh.Levels.Count);

        return new(ChangedWords: writer.ChangedWords, PackedWords: writer.PackedWords, SelectionComputed: boxChanged);
    }
    // The words a draw's placement derives: its pose words, its selection box and the selection flags (bounded, glass,
    // enclosing, dynamic, slot box), each written only where its inputs' bits moved in a retained record. A draw riding
    // its slot writes its scale and its box in the slot's frame, which the slot moving leaves standing; any other writes
    // its matrix and its world box. Returns whether the box was derived.
    private static bool WritePose(in SdfMeshDraw draw, in SdfMeshDraw previous, Span<uint> record, bool retained, bool meshChanged, ref DrawRecordWriter writer) {
        var rides = draw.RidesSlot;
        var modeChanged = (!retained || (rides != previous.RidesSlot));
        var matrix = (rides ? default : draw.FixedPlacement);
        var previousMatrix = (previous.RidesSlot ? default : previous.FixedPlacement);
        var scale = draw.SlotScale;
        var carry = draw.CarryRotation;
        var previousCarry = previous.CarryRotation;
        var shift = draw.CarryTranslation;
        var previousShift = previous.CarryTranslation;
        var clip = draw.ClipPlane;
        var previousClip = previous.ClipPlane;
        var carryChanged = (!Bitwise.Equal(left: in carry, right: in previousCarry) || !Bitwise.Equal(left: in shift, right: in previousShift) ||
            (BitConverter.SingleToUInt32Bits(value: draw.CarryScale) != BitConverter.SingleToUInt32Bits(value: previous.CarryScale)));
        // Whether the map a normal goes through changed (the winding the enclosing flag reads), and the pose words' inputs.
        bool basisChanged;
        bool poseChanged;
        bool boxChanged;

        if (rides) {
            basisChanged = (modeChanged || (BitConverter.SingleToUInt32Bits(value: scale) != BitConverter.SingleToUInt32Bits(value: previous.SlotScale)));
            poseChanged = basisChanged;
            boxChanged = (meshChanged || basisChanged || carryChanged || (draw.Displacement != previous.Displacement));
        } else {
            basisChanged = (modeChanged || !SameNormalBasis(left: in matrix, right: in previousMatrix));
            poseChanged = (modeChanged || !Bitwise.Equal(left: in matrix, right: in previousMatrix));
            boxChanged = (meshChanged || basisChanged || carryChanged || (draw.Displacement != previous.Displacement) || !SameDrawTranslation(left: in matrix, right: in previousMatrix));
        }
        if (poseChanged) { WritePoseWords(matrix: in matrix, record: ref writer, rides: rides, scale: scale); }

        var selection = record.Slice(length: SelectionWords, start: SdfMeshRecord.SelectionWord);
        var boxed = (retained && ((selection[3] & 1u) == 0u));

        if (boxChanged && rides) {
            // The box in the slot's frame, as the kernels place it (sdfSelectionBox) and the host reads it (ReadSelection).
            boxed = SdfMeshBox.TryOfObject(center: out var center, draw: in draw, half: out var half);
            WriteSelectionVector(words: ref writer, offset: (SdfMeshRecord.SelectionWord + 0), value: center);
            WriteSelectionVector(words: ref writer, offset: (SdfMeshRecord.SelectionWord + 4), value: new Vector3(x: half.X, y: 0f, z: 0f));
            writer[(SdfMeshRecord.SelectionWord + 7)] = BitConverter.SingleToUInt32Bits(value: (scale * MathF.Abs(x: draw.CarryScale)));
            WriteSelectionVector(words: ref writer, offset: (SdfMeshRecord.SelectionWord + 8), value: new Vector3(x: 0f, y: half.Y, z: 0f));
            WriteSelectionVector(words: ref writer, offset: (SdfMeshRecord.SelectionWord + 12), value: new Vector3(x: 0f, y: 0f, z: half.Z));
        } else if (boxChanged) {
            boxed = SdfMeshBox.TryOfFixed(box: out var box, draw: in draw);
            WriteSelectionVector(words: ref writer, offset: (SdfMeshRecord.SelectionWord + 0), value: box.Center);
            WriteSelectionVector(words: ref writer, offset: (SdfMeshRecord.SelectionWord + 4), value: box.HalfX);
            writer[(SdfMeshRecord.SelectionWord + 7)] = BitConverter.SingleToUInt32Bits(value: box.Stretch);
            WriteSelectionVector(words: ref writer, offset: (SdfMeshRecord.SelectionWord + 8), value: box.HalfY);
            WriteSelectionVector(words: ref writer, offset: (SdfMeshRecord.SelectionWord + 12), value: box.HalfZ);
        }

        var enclosed = ((!meshChanged && !basisChanged && Bitwise.Equal(left: in clip, right: in previousClip))
            ? ((selection[3] & 4u) != 0u)
            : draw.EnclosesSolid);
        // A skinned draw's box is a world bound its tables write (WriteSelectionBox), never one in the slot's frame.
        var slotBox = (rides && boxed && draw.Mesh.Members.IsEmpty);

        writer[(SdfMeshRecord.SelectionWord + 3)] = (boxed ? 0u : 1u) | (draw.Glass ? 2u : 0u) | (enclosed ? 4u : 0u) |
            (draw.IsStatic ? 0u : SdfMeshRecord.DynamicFlag) | (slotBox ? SdfMeshRecord.SlotBoxFlag : 0u);

        return boxChanged;
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
                if (m_retained && (m_words[index] == value)) { return; }
                m_words[index] = value;
                ChangedWords |= (1UL << index);
                PackedWords++;
            }
        }
    }

    private static bool SameNormalBasis(in Matrix4x4 left, in Matrix4x4 right) {
        var before = MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in left));
        var after = MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in right));

        return (before[..12].SequenceEqual(other: after[..12]) &&
            before.Slice(length: 12, start: 16).SequenceEqual(other: after.Slice(length: 12, start: 16)) &&
            before.Slice(length: 12, start: 32).SequenceEqual(other: after.Slice(length: 12, start: 32)));
    }
    private static bool SameDrawTranslation(in Matrix4x4 left, in Matrix4x4 right) =>
        MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in left)).Slice(length: 12, start: 48)
            .SequenceEqual(other: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Matrix4x4>(reference: in right)).Slice(length: 12, start: 48));
    // The pose words (SdfMeshRecord.PoseWords): a draw riding its slot's scale and zeros, or any other's affine rows (the
    // basis rows, then the translation; the matrix's fourth column is the affine 0, 0, 0, 1 and is not kept).
    private static void WritePoseWords(in Matrix4x4 matrix, float scale, bool rides, ref DrawRecordWriter record) {
        if (rides) {
            record[SdfMeshRecord.ScaleWord] = BitConverter.SingleToUInt32Bits(value: scale);
            for (var word = (SdfMeshRecord.ScaleWord + 1); (word < SdfMeshRecord.PoseWords); word++) { record[word] = 0u; }

            return;
        }
        record[0] = BitConverter.SingleToUInt32Bits(value: matrix.M11);
        record[1] = BitConverter.SingleToUInt32Bits(value: matrix.M12);
        record[2] = BitConverter.SingleToUInt32Bits(value: matrix.M13);
        record[3] = BitConverter.SingleToUInt32Bits(value: matrix.M21);
        record[4] = BitConverter.SingleToUInt32Bits(value: matrix.M22);
        record[5] = BitConverter.SingleToUInt32Bits(value: matrix.M23);
        record[6] = BitConverter.SingleToUInt32Bits(value: matrix.M31);
        record[7] = BitConverter.SingleToUInt32Bits(value: matrix.M32);
        record[8] = BitConverter.SingleToUInt32Bits(value: matrix.M33);
        record[9] = BitConverter.SingleToUInt32Bits(value: matrix.M41);
        record[10] = BitConverter.SingleToUInt32Bits(value: matrix.M42);
        record[11] = BitConverter.SingleToUInt32Bits(value: matrix.M43);
    }
    private static void WriteSelectionVector(ref DrawRecordWriter words, int offset, Vector3 value) {
        words[(offset + 0)] = BitConverter.SingleToUInt32Bits(value: value.X);
        words[(offset + 1)] = BitConverter.SingleToUInt32Bits(value: value.Y);
        words[(offset + 2)] = BitConverter.SingleToUInt32Bits(value: value.Z);
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
            using var held = mesh.Hold();

            WriteMesh(
                destination: destination,
                held: in held,
                placement: placement
            );
        }
    }
    /// <summary>Writes one mesh's vertices, triangle materials and indices at the words its placement names.</summary>
    /// <param name="held">The mesh, held so its normals and coarser levels' indices are resident while they are read
    /// (<see cref="SdfMesh.Held"/>).</param>
    /// <param name="placement">Where the mesh's sections lie, in words from the start of
    /// <paramref name="destination"/>.</param>
    /// <param name="destination">The words the placement counts from.</param>
    /// <exception cref="ArgumentNullException">The hold names no mesh.</exception>
    public static void WriteMesh(in SdfMesh.Held held, SdfMeshRegionMesh placement, Span<uint> destination) {
        var mesh = held.Mesh;

        ArgumentNullException.ThrowIfNull(argument: mesh, paramName: nameof(held));

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
            var influence = (mesh.Influences.IsEmpty ? SdfMeshInfluence.Rigid : mesh.Influences.Span[vertex]);

            perVertex[(SdfMeshRecord.SkinVertexWords * vertex)] = vertexMembers[vertex];
            perVertex[((SdfMeshRecord.SkinVertexWords * vertex) + 1)] = influence.Blend;
            perVertex[((SdfMeshRecord.SkinVertexWords * vertex) + 2)] = influence.GradientXY;
            perVertex[((SdfMeshRecord.SkinVertexWords * vertex) + 3)] = influence.GradientZ;
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
