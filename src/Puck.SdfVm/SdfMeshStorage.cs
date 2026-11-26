using System.Numerics;

namespace Puck.SdfVm;

// The counts of one level of detail's planes.
internal readonly record struct SdfMeshLevelShape(int Indices, int Materials, float Error);

// The counts of one independently drawn mesh's planes: what a storage sizes its parts by before any plane exists.
internal sealed record SdfMeshPieceShape(Vector3 BakeCell, int Vertices, int Normals, int Indices, int Materials, int Members, int VertexMembers, int Influences, SdfMeshLevelShape[] Levels) {
    // Triangles by the index plane; the mesh refuses an index count that is no multiple of three when it is built.
    public int Triangles => (Indices / 3);
}

// The counts of every plane of a mesh, which a decode measures from an entry's plane headers and a copy reads from a
// mesh. A chunked mesh's pieces are its chunks, and its vertex and triangle attributes are shared by them.
internal sealed record SdfMeshShape(bool Chunked, bool Encloses, SdfMeshPieceShape[] Pieces) {
    // The shape of a mesh's planes; a chunked mesh's pieces carry no skin, as a chunk is a view of the joined attributes.
    public static SdfMeshShape Of(SdfMesh mesh) {
        var chunked = (mesh.Chunks.Count != 0);
        var pieces = new SdfMeshPieceShape[mesh.PieceCount];

        for (var index = 0; (index < pieces.Length); index++) {
            var piece = mesh.Piece(index: index);
            var levels = new SdfMeshLevelShape[piece.Levels.Count];

            for (var level = 0; (level < levels.Length); level++) {
                levels[level] = new SdfMeshLevelShape(Indices: piece.Levels[level].IndexCount, Materials: piece.Levels[level].MaterialCount, Error: piece.Levels[level].Error);
            }
            pieces[index] = new SdfMeshPieceShape(
                BakeCell: piece.BakeCell,
                Indices: piece.IndexCount,
                Influences: (chunked ? 0 : piece.Influences.Length),
                Levels: levels,
                Materials: (piece.HasTriangleMaterials ? piece.TriangleCount : 0),
                Members: (chunked ? 0 : piece.Members.Length),
                Normals: (piece.HasNormals ? piece.VertexCount : 0),
                VertexMembers: (chunked ? 0 : piece.VertexMembers.Length),
                Vertices: piece.VertexCount
            );
        }

        return new SdfMeshShape(Chunked: chunked, Encloses: mesh.Encloses, Pieces: pieces);
    }
    // The first normal of a piece among the pieces' together, where a chunked mesh's shared normals put it.
    public int FirstNormal(int piece) {
        var first = 0;

        for (var index = 0; (index < piece); index++) { first += Pieces[index].Normals; }

        return first;
    }
}

/// <summary>
/// Where a decode of a store entry, or a copy of a mesh, puts each plane: the one decoder (<see cref="SdfMeshStoreCodec"/>)
/// writes every plane into the span a storage hands out, so the mesh a host keeps in native blocks and the mesh an offline
/// tool reads into arrays come from the same plane readers. A storage is told the shape of the whole mesh first
/// (<see cref="Begin"/>), takes the planes it keeps, and answers <see langword="false"/> for a plane it does not take, which
/// the decoder skips by its stated length: a restore takes only the normals and the coarser levels' indices.
/// </summary>
internal abstract class SdfMeshStorage {
    /// <summary>Sizes the storage for a mesh. A storage that holds a published mesh's parts answers <see langword="false"/>
    /// when the shape is not the one its parts were sized for, before it has written any.</summary>
    /// <param name="shape">The counts of every plane, in the entry's order.</param>
    /// <returns><see langword="true"/> when the storage takes the shape.</returns>
    public abstract bool Begin(SdfMeshShape shape);
    /// <summary>Tells the storage the material ids, by place, the triangle materials are written in, before it is sized
    /// (<see cref="Begin"/>): a storage whose mesh may let its planes go and restore them keeps them, so a restore writes
    /// the planes in the ids the mesh carries rather than the entry's canonical ones.</summary>
    /// <param name="materials">The ids by material place.</param>
    public virtual void WritesMaterialsIn(int[] materials) { }
    /// <summary>Hands out the span a piece's positions are written to.</summary>
    public abstract bool Positions(int piece, out Span<Vector3> destination);
    /// <summary>Hands out the span a piece's normals are written to.</summary>
    public abstract bool Normals(int piece, out Span<Vector3> destination);
    /// <summary>Hands out the span a piece's indices are written to.</summary>
    public abstract bool Indices(int piece, out Span<uint> destination);
    /// <summary>Hands out the span a piece's triangle materials are written to.</summary>
    public abstract bool Materials(int piece, out Span<uint> destination);
    /// <summary>Hands out the span a piece's members are written to.</summary>
    public abstract bool Members(int piece, out Span<SdfMeshMember> destination);
    /// <summary>Hands out the span a piece's vertex members are written to.</summary>
    public abstract bool VertexMembers(int piece, out Span<byte> destination);
    /// <summary>Hands out the span a piece's influences are written to.</summary>
    public abstract bool Influences(int piece, out Span<SdfMeshInfluence> destination);
    /// <summary>Hands out the span a coarser level's indices are written to.</summary>
    public abstract bool LevelIndices(int piece, int level, out Span<uint> destination);
    /// <summary>Hands out the span a coarser level's triangle materials are written to.</summary>
    public abstract bool LevelMaterials(int piece, int level, out Span<uint> destination);
    /// <summary>Builds the mesh over the written planes and gives up the storage's ownership of them.</summary>
    /// <returns>The mesh, or <see langword="null"/> for an entry whose bake found no surface or a storage that takes
    /// parts of a mesh that exists already.</returns>
    /// <exception cref="ArgumentException">The planes are no valid mesh.</exception>
    public abstract SdfMesh? Finish();
    /// <summary>Releases what <see cref="Begin"/> sized and nothing <see cref="Finish"/> handed on; a later
    /// <see cref="Begin"/> starts afresh.</summary>
    public abstract void Abandon();
}

// Arrays an offline caller keeps: each plane its own array, a chunked mesh joined as SdfMeshChunks.Join joins it.
internal sealed class SdfMeshManagedStorage : SdfMeshStorage {
    private SdfMeshShape? m_shape;
    private Piece[] m_pieces = [];

    private sealed class Piece {
        public Vector3[] Positions = [];
        public Vector3[] Normals = [];
        public uint[] Indices = [];
        public uint[] Materials = [];
        public SdfMeshMember[] Members = [];
        public byte[] VertexMembers = [];
        public SdfMeshInfluence[] Influences = [];
        public uint[][] LevelIndices = [];
        public uint[][] LevelMaterials = [];
    }

    public override bool Begin(SdfMeshShape shape) {
        m_shape = shape;
        m_pieces = new Piece[shape.Pieces.Length];
        for (var index = 0; (index < m_pieces.Length); index++) {
            var piece = shape.Pieces[index];

            m_pieces[index] = new Piece {
                Indices = Of<uint>(count: piece.Indices),
                Influences = Of<SdfMeshInfluence>(count: piece.Influences),
                LevelIndices = [.. piece.Levels.Select(selector: static level => Of<uint>(count: level.Indices))],
                LevelMaterials = [.. piece.Levels.Select(selector: static level => Of<uint>(count: level.Materials))],
                Materials = Of<uint>(count: piece.Materials),
                Members = Of<SdfMeshMember>(count: piece.Members),
                Normals = Of<Vector3>(count: piece.Normals),
                Positions = Of<Vector3>(count: piece.Vertices),
                VertexMembers = Of<byte>(count: piece.VertexMembers),
            };
        }

        return true;
    }
    private static T[] Of<T>(int count) => ((count == 0) ? [] : GC.AllocateUninitializedArray<T>(length: count));

    public override bool Positions(int piece, out Span<Vector3> destination) { destination = m_pieces[piece].Positions; return true; }
    public override bool Normals(int piece, out Span<Vector3> destination) { destination = m_pieces[piece].Normals; return true; }
    public override bool Indices(int piece, out Span<uint> destination) { destination = m_pieces[piece].Indices; return true; }
    public override bool Materials(int piece, out Span<uint> destination) { destination = m_pieces[piece].Materials; return true; }
    public override bool Members(int piece, out Span<SdfMeshMember> destination) { destination = m_pieces[piece].Members; return true; }
    public override bool VertexMembers(int piece, out Span<byte> destination) { destination = m_pieces[piece].VertexMembers; return true; }
    public override bool Influences(int piece, out Span<SdfMeshInfluence> destination) { destination = m_pieces[piece].Influences; return true; }
    public override bool LevelIndices(int piece, int level, out Span<uint> destination) { destination = m_pieces[piece].LevelIndices[level]; return true; }
    public override bool LevelMaterials(int piece, int level, out Span<uint> destination) { destination = m_pieces[piece].LevelMaterials[level]; return true; }

    public override SdfMesh? Finish() {
        var shape = m_shape!;
        var meshes = new SdfMesh[m_pieces.Length];

        for (var index = 0; (index < meshes.Length); index++) {
            var piece = m_pieces[index];
            var levels = new SdfMeshLevel[piece.LevelIndices.Length];

            for (var level = 0; (level < levels.Length); level++) {
                levels[level] = new SdfMeshLevel(piece.LevelIndices[level], piece.LevelMaterials[level], shape.Pieces[index].Levels[level].Error);
            }
            meshes[index] = new SdfMesh(indices: piece.Indices, influences: piece.Influences, members: piece.Members, normals: piece.Normals, positions: piece.Positions, triangleMaterials: piece.Materials, vertexMembers: piece.VertexMembers) {
                BakeCell = shape.Pieces[index].BakeCell,
                Levels = levels,
            };
        }
        Abandon();

        return ((meshes.Length == 0) ? null : (shape.Chunked ? SdfMeshChunks.Join(chunks: meshes) : meshes[0]));
    }
    public override void Abandon() {
        m_shape = null;
        m_pieces = [];
    }
}
