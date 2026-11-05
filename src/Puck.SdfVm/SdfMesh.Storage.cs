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
        Positions = source.Positions;
        Normals = source.Normals;
        Indices = source.Indices;
        TriangleMaterials = source.TriangleMaterials;
        Members = source.Members;
        VertexMembers = source.VertexMembers;
        m_levels = source.m_levels;
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
        var bytes = OwnArrayBytes(mesh: this, attributes: true);

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
            bytes += ((((long)mesh.Members.Length) * System.Runtime.CompilerServices.Unsafe.SizeOf<SdfMeshMember>()) + mesh.VertexMembers.Length);
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

    internal SdfMesh NativeCopy(IAllocator? allocator, MemoryAccount account) {
        if (allocator is null) { return this; }
        if (m_payload is not null) { return Acquire(); }
        var payload = new Payload(owner: account.CreateOwner("mesh"));

        try {
            var chunks = new SdfMesh[Chunks.Count];
            ReadOnlyMemory<Vector3> positions;
            ReadOnlyMemory<Vector3> normals;
            ReadOnlyMemory<uint> materials;

            if (JoinsChunks()) {
                // One copy of the vertex and triangle attributes, which the parent and every chunk view; each chunk keeps
                // its own indices and levels.
                var shared = payload.Part(owner: account.CreateOwner("mesh"));

                positions = shared.Copy(Positions, allocator);
                normals = shared.Copy(Normals, allocator);
                materials = shared.Copy(TriangleMaterials, allocator);
                var vertex = 0;
                var triangle = 0;

                for (var index = 0; (index < chunks.Length); index++) {
                    var chunk = Chunks[index];
                    var part = payload.Part(owner: account.CreateOwner("mesh"));

                    part.Borrow(source: shared);
                    chunks[index] = new SdfMesh(
                        positions: positions.Slice(start: vertex, length: chunk.Positions.Length),
                        indices: part.Copy(chunk.Indices, allocator),
                        normals: (normals.IsEmpty ? default : normals.Slice(start: vertex, length: chunk.Positions.Length)),
                        triangleMaterials: (materials.IsEmpty ? default : materials.Slice(start: triangle, length: chunk.TriangleCount))) {
                        BakeCell = chunk.BakeCell,
                        Levels = chunk.Levels.Select(selector: level => new SdfMeshLevel(part.Copy(level.Indices, allocator), part.Copy(level.TriangleMaterials, allocator), level.Error)).ToArray(),
                        m_payload = part.Seal(),
                    };
                    vertex += chunk.Positions.Length;
                    triangle += chunk.TriangleCount;
                }
            } else {
                for (var index = 0; (index < chunks.Length); index++) {
                    chunks[index] = Chunks[index].NativeCopy(account: account, allocator: allocator);
                    payload.Adopt(source: chunks[index].m_payload);
                }
                positions = payload.Copy(Positions, allocator);
                normals = payload.Copy(Normals, allocator);
                materials = payload.Copy(TriangleMaterials, allocator);
            }

            return new SdfMesh(
                positions: positions, indices: payload.Copy(Indices, allocator),
                normals: normals, triangleMaterials: materials,
                members: payload.Copy(Members, allocator), vertexMembers: payload.Copy(VertexMembers, allocator)) {
                BakeCell = BakeCell,
                Chunks = chunks,
                Levels = Levels.Select(selector: level => new SdfMeshLevel(payload.Copy(level.Indices, allocator), payload.Copy(level.TriangleMaterials, allocator), level.Error)).ToArray(),
                m_payload = payload.Seal(),
            };
        } catch { payload.Release(cached: false); throw; }
    }
    // A palette variant shares the original immutable geometry under an explicit source claim. Only its remapped
    // material arrays are new physical allocations; renumbering must not duplicate positions, normals or indices, and a
    // chunked mesh's chunks view one renumbered copy of its triangle materials.
    internal SdfMesh RenumberNative(int[] from, int[] to, IAllocator allocator, MemoryAccount account) {
        var payload = new Payload(owner: account.CreateOwner("mesh-materials"));

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
            var joined = JoinsChunks();
            var shared = (joined ? payload.Part(owner: account.CreateOwner("mesh-materials")) : payload);
            var materials = Remap(owner: shared, source: TriangleMaterials);
            var triangle = 0;

            for (var index = 0; (index < chunks.Length); index++) {
                var chunk = Chunks[index];

                if (!joined) {
                    chunks[index] = chunk.RenumberNative(account: account, allocator: allocator, from: from, to: to);
                    payload.Adopt(source: chunks[index].m_payload);
                    continue;
                }
                var part = payload.Part(owner: account.CreateOwner("mesh-materials"));

                part.Borrow(source: chunk.m_payload);
                part.Borrow(source: shared);
                chunks[index] = new SdfMesh(chunk.Positions, chunk.Indices, chunk.Normals, (materials.IsEmpty ? default : materials.Slice(start: triangle, length: chunk.TriangleCount)), chunk.Members, chunk.VertexMembers) {
                    BakeCell = chunk.BakeCell,
                    Levels = chunk.Levels.Select(selector: level => new SdfMeshLevel(level.Indices, Remap(owner: part, source: level.TriangleMaterials), level.Error)).ToArray(),
                    m_payload = part.Seal(),
                };
                triangle += chunk.TriangleCount;
            }
            return new SdfMesh(Positions, Indices, Normals, materials, Members, VertexMembers) {
                BakeCell = BakeCell,
                Chunks = chunks,
                Levels = Levels.Select(selector: level => new SdfMeshLevel(level.Indices, Remap(owner: payload, source: level.TriangleMaterials), level.Error)).ToArray(),
                m_payload = payload.Seal(),
            };
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
        public ReadOnlyMemory<T> Copy<T>(ReadOnlyMemory<T> source, IAllocator allocator) where T : unmanaged {
            if (source.IsEmpty) { return default; }
            var block = Block<T>(allocator: allocator, length: source.Length);

            source.Span.CopyTo(destination: block.GetSpan());
            return block.Memory;
        }
        // A new payload built with this one: this one's first claim on it is the part's initial claim.
        public Payload Part(MemoryAccount.Owner owner) {
            var part = new Payload(owner: owner);

            m_sources.Add(item: part);
            m_parts.Add(item: part);
            return part;
        }
        // Takes the initial claim of a payload built for this one.
        public void Adopt(Payload? source) {
            if (source is null) { return; }
            m_sources.Add(item: source);
            m_parts.Add(item: source);
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
