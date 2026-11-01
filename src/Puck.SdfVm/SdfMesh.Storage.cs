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
    public SdfMesh Acquire(bool cached = false) {
        m_payload?.Acquire(cached: cached);
        return this;
    }
    /// <summary>Releases one mesh claim; the last claimant releases the payload, never a finalizer.</summary>
    /// <param name="cached">Whether the released claim was acquired as cache retention.</param>
    public void Release(bool cached = false) => m_payload?.Release(cached: cached);

    internal SdfMesh NativeCopy(IAllocator? allocator, MemoryAccount account) {
        if (allocator is null) { return this; }
        if (m_payload is not null) { return Acquire(); }
        var payload = new Payload(owner: account.CreateOwner("mesh"));

        try {
            var chunks = new SdfMesh[Chunks.Count];

            for (var index = 0; (index < chunks.Length); index++) {
                chunks[index] = Chunks[index].NativeCopy(account: account, allocator: allocator);
                payload.Chunks.Add(item: chunks[index]);
            }
            var result = new SdfMesh(
                positions: payload.Copy(Positions, allocator), indices: payload.Copy(Indices, allocator),
                normals: payload.Copy(Normals, allocator), triangleMaterials: payload.Copy(TriangleMaterials, allocator),
                members: payload.Copy(Members, allocator), vertexMembers: payload.Copy(VertexMembers, allocator)) {
                BakeCell = BakeCell,
                Chunks = chunks,
                Levels = Levels.Select(selector: level => new SdfMeshLevel(payload.Copy(level.Indices, allocator), payload.Copy(level.TriangleMaterials, allocator), level.Error)).ToArray(),
                m_payload = payload,
            };

            return result;
        } catch { payload.Release(cached: false); throw; }
    }
    // A palette variant shares the original immutable geometry under an explicit source claim. Only its remapped
    // material arrays are new physical allocations; renumbering must not duplicate positions, normals or indices.
    internal SdfMesh RenumberNative(int[] from, int[] to, IAllocator allocator, MemoryAccount account) {
        var payload = new Payload(owner: account.CreateOwner("mesh-materials"));

        payload.Chunks.Add(item: Acquire());
        ReadOnlyMemory<uint> Remap(ReadOnlyMemory<uint> source) {
            if (source.IsEmpty) { return default; }
            var values = new OwnedBuffer<uint>(allocator: allocator, observer: payload.Owner);

            payload.Add(block: values);
            values.Resize(source.Length, clear: false);
            for (var index = 0; (index < source.Length); index++) {
                var place = Array.IndexOf(array: from, value: ((int)source.Span[index]));

                values[index] = (((place >= 0) && (place < to.Length)) ? (uint)to[place] : source.Span[index]);
            }
            return values.Memory;
        }
        try {
            var chunks = new SdfMesh[Chunks.Count];

            for (var index = 0; (index < chunks.Length); index++) {
                chunks[index] = Chunks[index].RenumberNative(account: account, allocator: allocator, from: from, to: to);
                payload.Chunks.Add(item: chunks[index]);
            }
            return new SdfMesh(Positions, Indices, Normals, Remap(source: TriangleMaterials), Members, VertexMembers) {
                BakeCell = BakeCell,
                Chunks = chunks,
                Levels = Levels.Select(selector: level => new SdfMeshLevel(level.Indices, Remap(source: level.TriangleMaterials), level.Error)).ToArray(),
                m_payload = payload,
            };
        } catch { payload.Release(cached: false); throw; }
    }

    // Claims belong to this resource alone, not a universal reference-count or allocation registry. The payload owns
    // every block once, even while a cache, residency, upload and an old GI scene finish independently.
    private sealed class Payload(MemoryAccount.Owner owner) {
        public MemoryAccount.Owner Owner => owner;

        private readonly Lock m_gate = new();
        private readonly List<IDisposable> m_blocks = [];

        public readonly List<SdfMesh> Chunks = [];

        private int m_claims = 1;

        private int m_cached;

        public void Add(IDisposable block) => m_blocks.Add(item: block);
        public ReadOnlyMemory<T> Copy<T>(ReadOnlyMemory<T> source, IAllocator allocator) where T : unmanaged {
            if (source.IsEmpty) { return default; }
            var block = new OwnedBuffer<T>(allocator: allocator, observer: owner);

            m_blocks.Add(item: block);
            block.CopyFrom(source: source.Span);
            return block.Memory;
        }
        public void Acquire(bool cached) {
            lock (m_gate) {
                ObjectDisposedException.ThrowIf(condition: (m_claims == 0), instance: this);
                m_claims++;
                if (cached) { m_cached++; }
                owner.Classify(category: ((m_claims == m_cached) ? MemoryCategory.Cached : MemoryCategory.Live));
                foreach (var chunk in Chunks) { chunk.Acquire(cached: cached); }
            }
        }
        public void Release(bool cached) {
            lock (m_gate) {
                if ((m_claims <= 0) || (cached && (m_cached <= 0)) || (!cached && (m_claims == m_cached))) { throw new InvalidOperationException(message: "A mesh claim was released twice."); }
                m_claims--;
                if (cached) { m_cached--; }
                foreach (var chunk in Chunks) { chunk.Release(cached: cached); }
                if (m_claims == 0) {
                    foreach (var block in m_blocks) { block.Dispose(); }
                    m_blocks.Clear();
                    Chunks.Clear();
                    owner.Dispose();
                } else { owner.Classify(category: ((m_claims == m_cached) ? MemoryCategory.Cached : MemoryCategory.Live)); }
            }
        }
    }
}
