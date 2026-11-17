using System.Numerics;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

public sealed partial record SdfMesh {
    private Payload? m_payload;

    // A native mesh is immutable after publication. Record clones are useful while constructing managed contours,
    // but must never silently copy the ownership record of a published native mesh.
    private SdfMesh(SdfMesh source) {
        if (source.m_payload is not null) { throw new InvalidOperationException(message: "A published native mesh cannot be record-cloned."); }
        BakeCell = source.BakeCell;
        Chunks = source.Chunks;
        Encloses = source.Encloses;
        Positions = source.Positions;
        PositionLow = source.PositionLow;
        PositionHigh = source.PositionHigh;
        Normals = source.Normals;
        m_indices = source.m_indices;
        m_triangleCount = source.m_triangleCount;
        TriangleMaterials = source.TriangleMaterials;
        Members = source.Members;
        VertexMembers = source.VertexMembers;
        Influences = source.Influences;
        m_levels = source.m_levels;
        m_levelledTriangleCount = source.m_levelledTriangleCount;
    }
    // A published chunked mesh: the joined vertex and triangle attributes its chunks view, and no joined indices, which
    // nothing reads once the chunks are the drawn, traced and stored triangles.
    private SdfMesh(SdfMesh[] chunks, ReadOnlyMemory<Vector3> positions, ReadOnlyMemory<Vector3> normals, ReadOnlyMemory<uint> materials, bool encloses, Payload payload) {
        var low = chunks[0].PositionLow;
        var high = chunks[0].PositionHigh;
        var triangles = 0L;

        foreach (var chunk in chunks) {
            low = Vector3.Min(value1: low, value2: chunk.PositionLow);
            high = Vector3.Max(value1: high, value2: chunk.PositionHigh);
            triangles += chunk.TriangleCount;
        }
        if (triangles > MaxTriangles) {
            throw new ArgumentException(message: $"A mesh holds at most {MaxTriangles} triangles, each numbered by a value the mesh target's float holds exactly; this one has {triangles}.", paramName: nameof(chunks));
        }
        BakeCell = chunks[0].BakeCell;
        Chunks = chunks;
        Encloses = encloses;
        Positions = positions;
        PositionLow = low;
        PositionHigh = high;
        Normals = normals;
        TriangleMaterials = materials;
        m_triangleCount = ((int)triangles);
        m_joinedIndicesOmitted = true;
        m_payload = payload;
    }

    /// <summary>Acquires a resource-specific mesh claim. The caller releases it after every borrowed view ends.</summary>
    /// <param name="cached">Whether the claim is reclaimable cache retention rather than an active consumer.</param>
    /// <returns>The mesh under the new claim.</returns>
    /// <exception cref="ObjectDisposedException">The mesh's storage was already released.</exception>
    public SdfMesh Acquire(bool cached = false) {
        m_payload?.Acquire(cached: cached);
        return this;
    }
    /// <summary>Releases one mesh claim; the last claimant releases the payload, never a finalizer.</summary>
    /// <param name="cached">Whether the released claim was acquired as cache retention.</param>
    public void Release(bool cached = false) => m_payload?.Release(cached: cached);

    /// <summary>Gets the bytes the mesh's own storage holds: for a native mesh, what its storage owners are charged,
    /// excluding storage it shares with the mesh it was renumbered from; for a managed one, its arrays' bytes, counting
    /// a chunked mesh's vertex and triangle attributes once, since its chunks are views of them
    /// (<see cref="SdfMeshChunks.Join"/>).</summary>
    public long StorageBytes => (m_payload?.Charged ?? ManagedBytes());

    private long ManagedBytes() {
        var bytes = OwnArrayBytes(attributes: true, mesh: this);

        foreach (var chunk in Chunks) { bytes += OwnArrayBytes(attributes: false, mesh: chunk); }
        return bytes;
    }
    // A mesh's array bytes: its indices and levels, and with `attributes` its vertex and triangle attributes too.
    private static long OwnArrayBytes(SdfMesh mesh, bool attributes) {
        var bytes = (((long)mesh.Indices.Length) * sizeof(uint));

        foreach (var level in mesh.Levels) { bytes += ((((long)level.Indices.Length) + level.TriangleMaterials.Length) * sizeof(uint)); }
        if (attributes) {
            bytes += ((((long)mesh.Positions.Length) + mesh.Normals.Length) * (3 * sizeof(float)));
            bytes += (((long)mesh.TriangleMaterials.Length) * sizeof(uint));
            bytes += (((((long)mesh.Members.Length) * System.Runtime.CompilerServices.Unsafe.SizeOf<SdfMeshMember>()) + mesh.VertexMembers.Length) + (12L * mesh.Influences.Length));
        }
        return bytes;
    }
    // Whether the mesh's own vertices and triangles are its chunks', in order (SdfMeshChunks.Join), so the chunks can
    // be views of its storage.
    private bool JoinsChunks() {
        if (Chunks.Count == 0) { return false; }
        var vertices = 0L;
        var triangles = 0L;

        foreach (var chunk in Chunks) {
            if ((chunk.Normals.IsEmpty != Normals.IsEmpty) || (chunk.TriangleMaterials.IsEmpty != TriangleMaterials.IsEmpty) || !chunk.VertexMembers.IsEmpty) { return false; }
            vertices += chunk.Positions.Length;
            triangles += chunk.TriangleCount;
        }
        return ((vertices == Positions.Length) && (triangles == TriangleCount) && VertexMembers.IsEmpty);
    }

    /// <summary>Copies a managed mesh into native storage under the process account, the copy holding its first claim; a
    /// published mesh is claimed again instead.</summary>
    /// <param name="allocator">The host allocator, or <see langword="null"/> to keep the mesh managed.</param>
    /// <param name="account">The account the storage is charged to.</param>
    /// <param name="restore">The library and store key the mesh was read from, whose store can restore its normals and
    /// coarser levels' indices once no region needs them (<see cref="Need"/>), or <see langword="null"/> for a mesh that
    /// keeps them for its lifetime.</param>
    /// <returns>The native mesh, or this mesh when <paramref name="allocator"/> is <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">The mesh is chunked, and its chunks are not views of its attributes.</exception>
    internal SdfMesh NativeCopy(IAllocator? allocator, MemoryAccount account, (SdfMeshLibrary Library, UInt128 Identity, SdfMeshGeometry Geometry)? restore = null) {
        if (allocator is null) { return this; }
        if (m_payload is not null) { return Acquire(); }
        if ((Chunks.Count != 0) && !JoinsChunks()) { throw new InvalidOperationException(message: "A chunked mesh's chunks view its attributes."); }

        var storage = Native(allocator: allocator, account: account, restore: restore);

        try {
            var shape = SdfMeshShape.Of(mesh: this);

            _ = storage.Begin(shape: shape);
            for (var piece = 0; (piece < shape.Pieces.Length); piece++) {
                var source = Piece(index: piece);

                if (storage.Positions(piece: piece, destination: out var positions)) { source.Positions.Span.CopyTo(destination: positions); }
                if (storage.Normals(piece: piece, destination: out var normals)) { source.Normals.Span.CopyTo(destination: normals); }
                if (storage.Indices(piece: piece, destination: out var indices)) { source.Indices.Span.CopyTo(destination: indices); }
                if (storage.Materials(piece: piece, destination: out var materials)) { source.TriangleMaterials.Span.CopyTo(destination: materials); }
                if (!shape.Chunked) {
                    if (storage.Members(piece: piece, destination: out var members)) { source.Members.Span.CopyTo(destination: members); }
                    if (storage.VertexMembers(piece: piece, destination: out var vertexMembers)) { source.VertexMembers.Span.CopyTo(destination: vertexMembers); }
                    if (storage.Influences(piece: piece, destination: out var influences)) { source.Influences.Span.CopyTo(destination: influences); }
                }
                for (var level = 0; (level < source.Levels.Count); level++) {
                    if (storage.LevelIndices(piece: piece, level: level, destination: out var levelIndices)) { source.Levels[level].Indices.Span.CopyTo(destination: levelIndices); }
                    if (storage.LevelMaterials(piece: piece, level: level, destination: out var levelMaterials)) { source.Levels[level].TriangleMaterials.Span.CopyTo(destination: levelMaterials); }
                }
            }

            return storage.Finish()!;
        } catch { storage.Abandon(); throw; }
    }

    /// <summary>Returns the storage a decode of a store entry, or a copy of a managed mesh, writes the mesh's planes into:
    /// blocks of one payload under the process account, held by the mesh the storage finishes.</summary>
    /// <param name="allocator">The host allocator.</param>
    /// <param name="account">The account the blocks are charged to.</param>
    /// <param name="restore">The library and store key the mesh is read from, whose store can restore its normals and
    /// coarser levels' indices once no region needs them (<see cref="Need"/>), or <see langword="null"/> for a mesh that
    /// keeps them for its lifetime.</param>
    /// <returns>The storage.</returns>
    internal static SdfMeshStorage Native(IAllocator allocator, MemoryAccount account, (SdfMeshLibrary Library, UInt128 Identity, SdfMeshGeometry Geometry)? restore = null) => new NativeStorage(allocator: allocator, account: account, restore: restore);

    // The planes of a mesh in the payload's blocks. A single mesh holds every plane in one payload. A chunked mesh holds the
    // vertex and triangle attributes once, in a part its chunks borrow, and each chunk its own indices and levels in a part
    // of its own: the sharing SdfMeshChunks.Join gives a managed mesh. Each coarser level's indices and the normals are
    // registered with the storage that may let them go (Restorable).
    private sealed class NativeStorage(IAllocator allocator, MemoryAccount account, (SdfMeshLibrary Library, UInt128 Identity, SdfMeshGeometry Geometry)? restore) : SdfMeshStorage {
        private SdfMeshShape? m_shape;
        private Payload? m_payload;
        private Restorable? m_restorable;
        private Memory<Vector3> m_positions;
        private Memory<Vector3> m_normals;
        private Memory<uint> m_materials;
        private Part[] m_parts = [];

        private sealed class Part {
            public Payload Owner = null!;
            public Memory<Vector3> Positions;
            public Memory<Vector3> Normals;
            public Memory<uint> Indices;
            public Memory<uint> Materials;
            public Memory<SdfMeshMember> Members;
            public Memory<byte> VertexMembers;
            public Memory<SdfMeshInfluence> Influences;
            public Memory<uint>[] LevelIndices = [];
            public Memory<uint>[] LevelMaterials = [];
        }

        public override bool Begin(SdfMeshShape shape) {
            Abandon();
            m_shape = shape;
            m_payload = new Payload(owner: account.CreateOwner(memoryClass: Puck.Abstractions.Memory.MemoryClass.Elastic, name: "mesh"));
            try {
                var restorable = ((restore is { } from) ? new Restorable(geometry: from.Geometry, identity: from.Identity, library: from.Library) : null);

                m_restorable = restorable;
                m_parts = new Part[shape.Pieces.Length];
                if (shape.Chunked) {
                    var shared = m_payload.Part(owner: account.CreateOwner(memoryClass: Puck.Abstractions.Memory.MemoryClass.Elastic, name: "mesh"));

                    m_positions = Take<Vector3>(block: out _, length: shape.Pieces.Sum(selector: static piece => piece.Vertices), owner: shared);
                    m_normals = Take<Vector3>(block: out var normals, length: shape.Pieces.Sum(selector: static piece => piece.Normals), owner: shared);
                    restorable?.AddNormals(block: normals, owner: shared, piece: -1);
                    m_materials = Take<uint>(block: out _, length: shape.Pieces.Sum(selector: static piece => piece.Materials), owner: shared);
                    var vertex = 0;
                    var normal = 0;
                    var material = 0;

                    for (var index = 0; (index < m_parts.Length); index++) {
                        var piece = shape.Pieces[index];
                        var owner = m_payload.Part(owner: account.CreateOwner(memoryClass: Puck.Abstractions.Memory.MemoryClass.Elastic, name: "mesh"));

                        owner.Borrow(source: shared);
                        m_parts[index] = new Part {
                            Indices = Take<uint>(block: out _, length: piece.Indices, owner: owner),
                            Materials = ((piece.Materials == 0) ? default : m_materials.Slice(length: piece.Materials, start: material)),
                            Normals = ((piece.Normals == 0) ? default : m_normals.Slice(length: piece.Normals, start: normal)),
                            Owner = owner,
                            Positions = m_positions.Slice(length: piece.Vertices, start: vertex),
                        };
                        TakeLevels(part: m_parts[index], piece: index, shape: piece);
                        vertex += piece.Vertices;
                        normal += piece.Normals;
                        material += piece.Materials;
                    }
                } else if (m_parts.Length != 0) {
                    var piece = shape.Pieces[0];
                    var part = new Part {
                        Indices = Take<uint>(block: out _, length: piece.Indices, owner: m_payload),
                        Influences = Take<SdfMeshInfluence>(block: out _, length: piece.Influences, owner: m_payload),
                        Members = Take<SdfMeshMember>(block: out _, length: piece.Members, owner: m_payload),
                        Owner = m_payload,
                        Positions = Take<Vector3>(block: out _, length: piece.Vertices, owner: m_payload),
                        VertexMembers = Take<byte>(block: out _, length: piece.VertexMembers, owner: m_payload),
                    };

                    part.Normals = Take<Vector3>(block: out var normals, length: piece.Normals, owner: m_payload);
                    restorable?.AddNormals(block: normals, owner: m_payload, piece: 0);
                    part.Materials = Take<uint>(block: out _, length: piece.Materials, owner: m_payload);
                    TakeLevels(part: part, piece: 0, shape: piece);
                    m_parts[0] = part;
                }
            } catch { Abandon(); throw; }

            return true;
        }
        // A block of a payload, or nothing for a plane the mesh does not have.
        private Memory<T> Take<T>(Payload owner, int length, out OwnedBuffer<T>? block) where T : unmanaged {
            block = ((length == 0) ? null : owner.Block<T>(allocator: allocator, length: length));

            return (block?.Memory ?? default);
        }
        private void TakeLevels(Part part, int piece, SdfMeshPieceShape shape) {
            part.LevelIndices = new Memory<uint>[shape.Levels.Length];
            part.LevelMaterials = new Memory<uint>[shape.Levels.Length];
            for (var level = 0; (level < shape.Levels.Length); level++) {
                part.LevelIndices[level] = Take<uint>(block: out var block, length: shape.Levels[level].Indices, owner: part.Owner);
                m_restorable?.AddLevel(block: block, level: level, owner: part.Owner, piece: piece);
                part.LevelMaterials[level] = Take<uint>(block: out _, length: shape.Levels[level].Materials, owner: part.Owner);
            }
        }

        public override bool Positions(int piece, out Span<Vector3> destination) { destination = m_parts[piece].Positions.Span; return true; }
        public override bool Normals(int piece, out Span<Vector3> destination) { destination = m_parts[piece].Normals.Span; return true; }
        public override bool Indices(int piece, out Span<uint> destination) { destination = m_parts[piece].Indices.Span; return true; }
        public override bool Materials(int piece, out Span<uint> destination) { destination = m_parts[piece].Materials.Span; return true; }
        public override bool Members(int piece, out Span<SdfMeshMember> destination) { destination = m_parts[piece].Members.Span; return true; }
        public override bool VertexMembers(int piece, out Span<byte> destination) { destination = m_parts[piece].VertexMembers.Span; return true; }
        public override bool Influences(int piece, out Span<SdfMeshInfluence> destination) { destination = m_parts[piece].Influences.Span; return true; }
        public override bool LevelIndices(int piece, int level, out Span<uint> destination) { destination = m_parts[piece].LevelIndices[level].Span; return true; }
        public override bool LevelMaterials(int piece, int level, out Span<uint> destination) { destination = m_parts[piece].LevelMaterials[level].Span; return true; }

        public override SdfMesh? Finish() {
            var payload = m_payload;
            var shape = m_shape;

            if ((payload is null) || (shape is null)) { throw new InvalidOperationException(message: "Native storage finishes a mesh after it has begun one."); }
            if (m_parts.Length == 0) {
                Abandon();

                return null;
            }
            try {
                SdfMesh mesh;

                if (shape.Chunked) {
                    var chunks = new SdfMesh[m_parts.Length];

                    for (var index = 0; (index < chunks.Length); index++) {
                        var part = m_parts[index];

                        chunks[index] = new SdfMesh(
                            positions: part.Positions,
                            indices: part.Indices,
                            normals: part.Normals,
                            triangleMaterials: part.Materials) {
                            BakeCell = shape.Pieces[index].BakeCell,
                            Levels = LevelsOf(part: part, shape: shape.Pieces[index]),
                            m_payload = part.Owner.Seal(),
                            m_restorable = m_restorable,
                        };
                    }
                    mesh = new SdfMesh(chunks: chunks, positions: m_positions, normals: m_normals, materials: m_materials, encloses: shape.Encloses, payload: payload.Seal());
                } else {
                    var part = m_parts[0];

                    mesh = new SdfMesh(
                        positions: part.Positions, indices: part.Indices,
                        normals: part.Normals, triangleMaterials: part.Materials,
                        members: part.Members, vertexMembers: part.VertexMembers, influences: part.Influences) {
                        BakeCell = shape.Pieces[0].BakeCell,
                        Encloses = shape.Encloses,
                        Levels = LevelsOf(part: part, shape: shape.Pieces[0]),
                        m_payload = payload.Seal(),
                    };
                }
                mesh = Restorable.Attach(restorable: m_restorable, mesh: mesh);
                Forget();

                return mesh;
            } catch { Abandon(); throw; }
        }
        private static SdfMeshLevel[] LevelsOf(Part part, SdfMeshPieceShape shape) {
            var levels = new SdfMeshLevel[shape.Levels.Length];

            for (var level = 0; (level < levels.Length); level++) {
                levels[level] = new SdfMeshLevel(part.LevelIndices[level], part.LevelMaterials[level], shape.Levels[level].Error);
            }

            return levels;
        }
        public override void Abandon() {
            var payload = m_payload;

            Forget();
            payload?.Release(cached: false);
        }
        // Forgets the blocks without releasing them: a finished mesh owns them.
        private void Forget() {
            m_payload = null;
            m_restorable = null;
            m_shape = null;
            m_positions = default;
            m_normals = default;
            m_materials = default;
            m_parts = [];
        }
    }

    // A renumbered view of a published mesh: its geometry, its storage's restorable parts and its counts, with other
    // triangle materials and levels over the same indices. Nothing is validated or read, since the geometry was validated
    // when published and its normals and levels may be let go (SdfMesh.Restore.cs).
    private SdfMesh(SdfMesh source, ReadOnlyMemory<uint> materials, SdfMeshLevel[] levels, SdfMesh[] chunks, Payload payload) {
        BakeCell = source.BakeCell;
        Chunks = chunks;
        Encloses = source.Encloses;
        Positions = source.Positions;
        PositionLow = source.PositionLow;
        PositionHigh = source.PositionHigh;
        Normals = source.Normals;
        m_indices = source.m_indices;
        m_triangleCount = source.m_triangleCount;
        m_joinedIndicesOmitted = source.m_joinedIndicesOmitted;
        TriangleMaterials = materials;
        Members = source.Members;
        VertexMembers = source.VertexMembers;
        Influences = source.Influences;
        m_levels = levels;
        m_levelledTriangleCount = source.m_levelledTriangleCount;
        m_payload = payload;
        m_restorable = source.m_restorable;
    }

    // A palette variant shares the original immutable geometry under an explicit source claim. Only its remapped
    // material arrays are new physical allocations; renumbering must not duplicate positions, normals or indices, and a
    // chunked mesh's chunks view one renumbered copy of its triangle materials. A native chunked mesh's chunks always
    // view its attributes (NativeCopy refuses any other, and NativeStorage builds no other).
    internal SdfMesh RenumberNative(int[] from, int[] to, IAllocator allocator, MemoryAccount account) {
        var payload = new Payload(owner: account.CreateOwner(memoryClass: Puck.Abstractions.Memory.MemoryClass.Elastic, name: "mesh-materials"));

        ReadOnlyMemory<uint> Remap(Payload owner, ReadOnlyMemory<uint> source) {
            if (source.IsEmpty) { return default; }
            var values = owner.Block<uint>(allocator: allocator, length: source.Length);
            var input = source.Span;

            for (var index = 0; (index < input.Length); index++) {
                values[index] = SdfMeshMaterials.Renumber(material: input[index], from: from, to: to);
            }
            return values.Memory;
        }
        try {
            payload.Borrow(source: m_payload);

            var chunks = new SdfMesh[Chunks.Count];
            var shared = ((chunks.Length != 0) ? payload.Part(owner: account.CreateOwner(memoryClass: Puck.Abstractions.Memory.MemoryClass.Elastic, name: "mesh-materials")) : payload);
            var materials = Remap(owner: shared, source: TriangleMaterials);
            var triangle = 0;

            for (var index = 0; (index < chunks.Length); index++) {
                var chunk = Chunks[index];
                var part = payload.Part(owner: account.CreateOwner(memoryClass: Puck.Abstractions.Memory.MemoryClass.Elastic, name: "mesh-materials"));

                part.Borrow(source: chunk.m_payload);
                part.Borrow(source: shared);
                chunks[index] = new SdfMesh(
                    chunks: [],
                    levels: chunk.Levels.Select(selector: level => new SdfMeshLevel(level.Indices, Remap(owner: part, source: level.TriangleMaterials), level.Error)).ToArray(),
                    materials: (materials.IsEmpty ? default : materials.Slice(start: triangle, length: chunk.TriangleCount)),
                    payload: part.Seal(),
                    source: chunk);
                triangle += chunk.TriangleCount;
            }
            return new SdfMesh(
                chunks: chunks,
                levels: Levels.Select(selector: level => new SdfMeshLevel(level.Indices, Remap(owner: payload, source: level.TriangleMaterials), level.Error)).ToArray(),
                materials: materials,
                payload: payload.Seal(),
                source: this);
        } catch { payload.Release(cached: false); throw; }
    }

    // Claims belong to this resource alone, not a universal reference-count or allocation registry. The payload owns
    // every block once, even while a cache, residency, upload and an old GI scene finish independently. A payload holds
    // one claim on each source for each claim held on it: the parts it was built with (a chunked mesh's shared
    // attributes and chunks) and the storage it borrows (a renumbered variant's original).
    private sealed class Payload(MemoryAccount.Owner owner) {
        public MemoryAccount.Owner Owner => owner;

        private readonly Lock m_gate = new();
        private readonly List<IDisposable> m_blocks = [];
        private readonly List<Payload> m_sources = [];
        private readonly List<Payload> m_parts = [];
        private int m_claims = 1;

        private int m_cached;

        private MemoryCategory m_category = MemoryCategory.Live;

        // The bytes this payload and its parts are charged, fixed once the mesh is published (Seal).
        public long Charged { get; private set; }

        public OwnedBuffer<T> Block<T>(IAllocator allocator, int length) where T : unmanaged {
            var block = new OwnedBuffer<T>(allocator: allocator, observer: owner);

            m_blocks.Add(item: block);
            block.Resize(length, clear: false, preserve: false);
            return block;
        }
        public ReadOnlyMemory<T> Copy<T>(ReadOnlyMemory<T> source, IAllocator allocator) where T : unmanaged => Copy(allocator: allocator, block: out _, source: source);
        public ReadOnlyMemory<T> Copy<T>(ReadOnlyMemory<T> source, IAllocator allocator, out OwnedBuffer<T>? block) where T : unmanaged {
            block = null;
            if (source.IsEmpty) { return default; }
            block = Block<T>(allocator: allocator, length: source.Length);
            source.Span.CopyTo(destination: block.GetSpan());
            return block.Memory;
        }

        // The storage that may let go of some of this payload's blocks; detached before they are disposed.
        public Restorable? Restorable { get; set; }

        // A new payload built with this one: this one's first claim on it is the part's initial claim.
        public Payload Part(MemoryAccount.Owner owner) {
            var part = new Payload(owner: owner);

            m_sources.Add(item: part);
            m_parts.Add(item: part);
            return part;
        }
        // Claims storage this payload views but did not build.
        public void Borrow(Payload? source) {
            if (source is null) { return; }
            source.Acquire(cached: false);
            m_sources.Add(item: source);
        }
        public Payload Seal() {
            if (m_sealed) { return this; }
            var charged = owner.Bytes;

            foreach (var part in m_parts) { charged += part.Seal().Charged; }
            Charged = charged;
            m_sealed = true;
            return this;
        }

        private bool m_sealed;

        public void Acquire(bool cached) {
            lock (m_gate) {
                ObjectDisposedException.ThrowIf(condition: (m_claims == 0), instance: this);
                var acquired = 0;

                try {
                    for (; (acquired < m_sources.Count); acquired++) { m_sources[acquired].Acquire(cached: cached); }
                } catch {
                    for (var index = 0; (index < acquired); index++) { m_sources[index].Release(cached: cached); }
                    throw;
                }
                m_claims++;
                if (cached) { m_cached++; }
                Classify();
            }
        }
        public void Release(bool cached) {
            lock (m_gate) {
                if ((m_claims <= 0) || (cached && (m_cached <= 0)) || (!cached && (m_claims == m_cached))) { throw new InvalidOperationException(message: "A mesh claim was released twice."); }
                m_claims--;
                if (cached) { m_cached--; }
                if (m_claims == 0) { Restorable?.Detach(owner: this); }
                foreach (var source in m_sources) { source.Release(cached: cached); }
                if (m_claims == 0) {
                    foreach (var block in m_blocks) { block.Dispose(); }
                    m_blocks.Clear();
                    m_sources.Clear();
                    m_parts.Clear();
                    owner.Dispose();
                } else { Classify(); }
            }
        }

        // Moves the charge between live and cached only when the kind of claim holding it changes, so a claim that
        // keeps its category never takes the account's lock.
        private void Classify() {
            var category = ((m_claims == m_cached) ? MemoryCategory.Cached : MemoryCategory.Live);

            if (category == m_category) { return; }
            m_category = category;
            owner.Classify(category: category);
        }
    }
}
