using System.Globalization;
using Puck.Abstractions.Gpu;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

/// <summary>
/// A presentation's mesh region: the one buffer every residency on a device draws its meshes' words from, so a mesh any
/// number of residencies name is placed, landed and held once. A residency keeps its own draw records (its tables' draw
/// region) and names the meshes its rows draw (<see cref="Name"/>) and those its bake is about to draw
/// (<see cref="ReplaceAhead"/>); the store places each named mesh in its arena (<see cref="SdfMeshRegionArena"/>), lands
/// its words through whichever residency's background GPU work takes the job, and answers every residency alike where a
/// mesh lies and whether its words stand (<see cref="TryPlacement"/>, <see cref="Stands"/>).
/// <para>The region is created with the store, one granule long, and grows to the need without waiting for the device.
/// A mapped region (<see cref="GpuRegion.Mapped"/>) backs and gives back a chunk at a time wherever its placements lie
/// (<see cref="Cover"/>); any other grows by a replacement the next recorded copy carries. It never grows past its limit,
/// which the selector's one region grant reserves (<see cref="SdfResidencyGrant.ReserveGrowth"/>): a mesh the arena finds
/// no room for under it is refused, its draws stay absent like an owed mesh's, and the bakes coarsen what the views can
/// spare until the names fit.</para>
/// <para>Invariants, and the one place each is enforced:</para>
/// <para>A mesh stands (<see cref="Stands"/>) only once every word of it at its current placement has been written by a
/// completed submission (<c>CompleteLanding</c>, on the fence the landing job's service observed) or by a move the
/// region owes, which every upload records before its residency's passes (<see cref="RecordOwed"/>). The owed set's one
/// door is <c>OwedMeshes</c>.</para>
/// <para>The words a moved mesh left stay allocated and backed until the move's copy is recorded and a residency has
/// uploaded twice since (<c>ReleaseMoveSources</c>): a draw record a residency staged before the move reads them until
/// that residency stages again, and every residency whose rows name a mesh whose address or standing changed is told
/// to stage those rows again before it next draws (<see cref="SdfWorldTables.NoteStoreChange"/>). The guarantee stops
/// at a residency that draws a held frame without staging: the tables' two staging doors read the note, and nothing
/// else draws.</para>
/// <para>Thread-safe: every member takes the store's gate, which a residency's staging holds across its names and its
/// plan, so two residencies staged side by side never interleave a plan with a read of it.</para>
/// </summary>
public sealed partial class SdfMeshRegionStore : IDisposable {
    /// <summary>The most bytes the region can hold on any device, a whole number of words
    /// (<see cref="GpuRegion.MaxByteCount"/>): the ceiling of the limit its grant reserves for it.</summary>
    public const ulong MostBytes = ((ulong)GpuRegion.MaxByteCount);
    /// <summary>The unit a region that grows by replacement is rounded up to: 16 MiB. A mapped region rounds to its chunk
    /// instead.</summary>
    public const ulong GranuleBytes = (16UL << 20);

    // The region's slots: the first for the landing jobs in flight, the rest for the copies an upload records.
    private const int LandingSlots = 4;
    private const int UploadSlots = 4;

    private readonly Lock m_gate = new();
    private readonly IGpuDeviceContext m_device;
    private readonly IGpuComputePipeline m_copyPipeline;
    private readonly SdfResidencyGrant m_grant;
    private readonly MemoryAccount.Owner m_hostCopies = MemoryAccount.Shared.CreateOwner(name: "sdf.mesh-region.host-copies");
    private readonly SdfMeshRegionArena m_arena;
    private readonly GpuRegion m_region;
    private readonly GpuRetirementQueue<IDisposable> m_retired = new();
    private readonly List<SdfWorldTables> m_clients = [];
    // Each named mesh's residencies, with how many rows of each name it.
    private readonly Dictionary<SdfMesh, Use> m_uses = new(comparer: ReferenceEqualityComparer.Instance);
    // Each placed mesh's words in the region, as a draw record names them: the arena's placements, kept beside them.
    private readonly Dictionary<SdfMesh, SdfMeshRegionMesh> m_placements = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SdfMesh, GpuBufferPages.Lease> m_pages = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly Stack<int> m_freeLandingSlots = new();
    private readonly Stack<int> m_freeUploadSlots = new();
    private readonly HashSet<SdfMesh> m_standing = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_scratch = [];

    private IGpuBuffer? m_pageDestination;
    private ulong m_pageRevision;
    private long m_version;
    private bool m_disposed;
    private bool m_trimListening;
    // One while an account trim request waits: it is raised on whichever thread asked, and served under the gate at the
    // next residency's service.
    private int m_trimRequested;
    // Whether a plan forgot or moved meshes since the region was last fitted to the arena.
    private bool m_fitOwed;
    // The copies an upload recorded that its residency has not submitted yet, and those submitted and not yet complete.
    private int m_unsubmitted;
    private int m_inFlight;
    // The residencies that uploaded since the latest moves were recorded: a second upload by any of them releases the
    // words the moves left.
    private readonly HashSet<SdfWorldTables> m_moveWitnesses = new(comparer: ReferenceEqualityComparer.Instance);
    private long m_bytes;
    private long m_moved;
    private long m_gpuMoves;
    private long m_gpuMovedWords;
    private long m_refusals;
    private long m_forgotten;
    private long m_moveSlotMisses;
    private long m_wordsWritten;

    // One named mesh's residencies, each with how many of its rows name the mesh and the landing priority it last
    // measured for it from its own rows (SdfWorldTables.MeasureLanding): negative infinity before its first measure.
    // A residency's rows and draws are its own, packed on whichever thread packs it, so only that residency's staging
    // measures them, and the store ranks a mesh by what each residency last told it. A mesh few residencies name is the
    // rule, so the lists stay arrays walked in order.
    private sealed class Use {
        public SdfWorldTables[] Clients = new SdfWorldTables[2];
        public int[] Rows = new int[2];
        public float[] Priority = new float[2];
        public int[] First = new int[2];
        public int Count;

        public int IndexOf(SdfWorldTables client) {
            for (var index = 0; (index < Count); index++) { if (ReferenceEquals(objA: Clients[index], objB: client)) { return index; } }
            return -1;
        }
    }

    internal SdfMeshRegionStore(IGpuDeviceContext device, IGpuComputePipeline copyPipeline, SdfResidencySelector selector) {
        m_device = device;
        m_copyPipeline = copyPipeline;
        // The arena places nothing past the region's first granule until the grant reserves room to grow (PrepareLimit).
        m_arena = new SdfMeshRegionArena(limitBytes: GranuleBytes);
        m_owed = new OwedMeshes(arena: m_arena, touched: m_landingTouched);

        var services = device.Services;
        var first = ((services.BufferFactory.MappedChunkBytes is var chunk and > 0UL) ? ((long)chunk) : ((long)GranuleBytes));

        // Reserved before the region is created, so the ledger admits its first buffer against the grant.
        m_grant = selector.RegisterRegion();
        m_grant.Created(capacityBytes: first);
        try {
            m_region = new GpuRegion(
                bindings: services.Bindings,
                buffers: services.BufferFactory,
                byteCount: first,
                copyPipeline: copyPipeline,
                memory: GpuHostVisibleMemory.Host,
                name: new GpuObjectName(owner: SdfResidencySelector.RegionOwner, part: "mesh-region"),
                policy: GpuResidencyPolicy.Staged,
                recorder: services.Recorder,
                slotCount: (LandingSlots + UploadSlots),
                hostCopies: m_hostCopies,
                staging: new GpuObjectName(owner: SdfResidencySelector.RegionStagingOwner, part: "mesh-region"),
                // The probe scene's bottom levels read their triangles from the region in place (SdfMeshProbeGpuWork.Rays).
                destinationUsage: ((device.Capabilities?.Has(required: GpuOptionalCapabilities.RayQuery) ?? false) ? GpuBufferUsage.AccelerationStructureInput : GpuBufferUsage.None),
                // Grows and shrinks in place where the device can back a buffer a chunk at a time, so a growth never holds
                // two regions at once (SdfResidencySelector leaves no replacement's room for it).
                mapped: true,
                // Every word lands through a gather or a move, so the region keeps no copy of them on the host.
                hostCopyBytes: 0L
            );
        } catch {
            m_grant.Dispose();
            m_hostCopies.Dispose();
            throw;
        }
        m_region.Shadow(shadowBytes: 0L, source: new NoWords());
        m_grant.Mapped = m_region.Mapped;
        m_grant.Created(capacityBytes: ((long)m_region.DestinationBytes));
        // The arena compacts by moving standing meshes on the GPU (SdfMeshRegionArena.Moved, GpuRegion.TryOweMove). A
        // mapped region's free words cost nothing past the chunks they share, so only an unmapped one compacts to shrink.
        m_region.ReserveRunCopies();
        m_arena.FreeWordsCost = !m_region.Mapped;
        m_bytes = m_region.ByteCount;
        for (var slot = (LandingSlots - 1); (slot >= 0); slot--) { m_freeLandingSlots.Push(item: slot); }
        for (var slot = ((LandingSlots + UploadSlots) - 1); (slot >= LandingSlots); slot--) { m_freeUploadSlots.Push(item: slot); }
    }

    // The region stages no word from the host: a flush that asks for one finds zeros, which no draw reads.
    private sealed class NoWords : IGpuRegionSource {
        private static readonly uint[] Zeros = new uint[(1 << 12)];

        public void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) {
            while (count > 0) {
                var piece = Math.Min(val1: count, val2: Zeros.Length);

                buffer.Write<uint>(data: Zeros.AsSpan(length: piece, start: 0), destinationOffsetBytes: destinationOffsetBytes);
                destinationOffsetBytes += (((ulong)piece) * sizeof(uint));
                count -= piece;
            }
        }
    }

    /// <summary>Gets the gate every member takes, which a residency's staging holds across its names, its plan and the
    /// records it packs from the plan's placements.</summary>
    internal Lock Gate => m_gate;
    /// <summary>Gets the region-copy pipeline the region records with.</summary>
    internal IGpuComputePipeline CopyPipeline => m_copyPipeline;
    /// <summary>Gets the region's destination buffer, which every residency's sets bind at the mesh region's member. A
    /// region that grows by replacement answers another buffer once it has grown (<see cref="BufferGeneration"/>).</summary>
    internal IGpuBuffer Buffer { get { lock (m_gate) { return m_region.Buffer(slot: 0); } } }
    /// <summary>Gets a count that moves whenever <see cref="Buffer"/> answers another buffer, so a residency writes its
    /// sets again.</summary>
    internal long BufferGeneration { get; private set; }
    /// <summary>Gets a count that moves whenever a placement is given, moved or taken back.</summary>
    internal long Version => Volatile.Read(location: ref m_version);
    /// <summary>Gets the region's grant from the selector: the reservation the region grows under.</summary>
    public SdfResidencyGrant Grant => m_grant;
    /// <summary>Gets the bytes the region holds. Safe to read from any thread.</summary>
    public long Bytes => Volatile.Read(location: ref m_bytes);
    /// <summary>Gets the bytes of the region the placed meshes use: up to the last placed mesh.</summary>
    public long UsedBytes { get { lock (m_gate) { return (m_arena.Words * sizeof(uint)); } } }
    /// <summary>Gets the most bytes the region places, as its grant last reserved them before a plan.</summary>
    public long LimitBytes { get { lock (m_gate) { return (m_arena.LimitWords * sizeof(uint)); } } }
    /// <summary>Gets whether the store was disposed: its last residency left.</summary>
    internal bool IsDisposed => m_disposed;
    /// <summary>Gets how many residencies are attached.</summary>
    public int Clients { get { lock (m_gate) { return m_clients.Count; } } }

    // Adds a residency; false for a store its last residency already left, which is disposed.
    internal bool TryAttach(SdfWorldTables client) {
        lock (m_gate) {
            if (m_disposed) { return false; }
            m_clients.Add(item: client);
            if (!m_trimListening) { MemoryAccount.Shared.TrimRequested += RequestTrim; m_trimListening = true; }

            return true;
        }
    }
    // Takes a residency out: its rows' names and its names ahead go, and the last residency to leave disposes the store.
    // The caller disposes its tables only once no submission reads them, the region among them.
    internal void Detach(SdfWorldTables client, List<(SdfMesh Mesh, bool Product)> ahead) {
        lock (m_gate) {
            if (m_disposed || !m_clients.Remove(item: client)) { return; }
            m_scratch.Clear();
            foreach (var (mesh, use) in m_uses) { if (use.IndexOf(client: client) >= 0) { m_scratch.Add(item: mesh); } }
            // Each mesh's own count goes, one name at a time: a mesh a renumbered one draws its geometry from is listed
            // itself, with the names those meshes' rows gave it, so nothing here follows a mesh to its source.
            foreach (var mesh in m_scratch) {
                var use = m_uses[mesh];

                for (var rows = use.Rows[use.IndexOf(client: client)]; (rows > 0); rows--) { UnnameOne(client: client, mesh: mesh); }
            }
            m_scratch.Clear();
            foreach (var (mesh, product) in ahead) { m_arena.UnnameAhead(mesh: mesh, product: product); }
            ahead.Clear();
            _ = m_moveWitnesses.Remove(item: client);
            m_landingAheadStale = true;
            m_owed.Sync();
            if (m_clients.Count == 0) { Dispose(); }
        }
    }

    private void RequestTrim() {
        if (Interlocked.Exchange(location1: ref m_trimRequested, value: 1) == 0) { MemoryAccount.Shared.DeferTrim(); }
    }

    /// <summary>Names a mesh for one more draw row of a residency.</summary>
    /// <param name="client">The residency's tables.</param>
    /// <param name="mesh">The mesh, a chunk of a chunked mesh being a mesh of its own.</param>
    internal void Name(SdfWorldTables client, SdfMesh mesh) {
        lock (m_gate) {
            NameOne(client: client, mesh: mesh);
            // A mesh renumbered from another draws that mesh's geometry, so a row naming it names both.
            if (mesh.GeometrySource is { } source) { NameOne(client: client, mesh: source); }
        }
    }
    // The meshes renumbered from each mesh that a row names (SdfMesh.GeometrySource): what a change to the mesh's place
    // or standing reaches beside the mesh itself, since their draws read its words.
    private readonly Dictionary<SdfMesh, List<SdfMesh>> m_variants = new(comparer: ReferenceEqualityComparer.Instance);

    private void NameOne(SdfWorldTables client, SdfMesh mesh) {
        {
            if (!m_uses.TryGetValue(key: mesh, value: out var use)) {
                m_uses.Add(key: mesh, value: use = new Use());
                if (mesh.GeometrySource is { } source) {
                    if (!m_variants.TryGetValue(key: source, value: out var variants)) { m_variants.Add(key: source, value: variants = []); }
                    variants.Add(item: mesh);
                }
                Recompose(mesh: mesh);
            }

            var index = use.IndexOf(client: client);

            if (index < 0) {
                var words = SdfMeshRegion.PlacedWordsOf(mesh: mesh);

                // The mesh's words are shared out among the residencies that name it, so the residencies' named bytes
                // together are the bytes the names hold.
                for (var other = 0; (other < use.Count); other++) { use.Clients[other].AttributeNamedWords(delta: ((((double)words) / (use.Count + 1)) - (((double)words) / use.Count))); }
                if (use.Count == use.Clients.Length) {
                    Array.Resize(array: ref use.Clients, newSize: (2 * use.Count)); Array.Resize(array: ref use.Rows, newSize: (2 * use.Count));
                    Array.Resize(array: ref use.Priority, newSize: (2 * use.Count)); Array.Resize(array: ref use.First, newSize: (2 * use.Count));
                }
                index = use.Count++;
                use.Clients[index] = client;
                use.Rows[index] = 0;
                use.Priority[index] = float.NegativeInfinity;
                use.First[index] = -1;
                client.AttributeNamedWords(delta: (((double)words) / use.Count));
                if (m_landingRanks.ContainsKey(key: mesh)) { client.AddOwedWords(delta: words); }
            }
            use.Rows[index]++;
            m_arena.Name(mesh: mesh);
            m_landingTouched.Add(item: mesh);
        }
    }
    /// <summary>Lets go of one draw row's name of a mesh.</summary>
    /// <param name="client">The residency's tables.</param>
    /// <param name="mesh">The mesh.</param>
    internal void Unname(SdfWorldTables client, SdfMesh mesh) {
        lock (m_gate) {
            UnnameOne(client: client, mesh: mesh);
            if (mesh.GeometrySource is { } source) { UnnameOne(client: client, mesh: source); }
        }
    }
    private void UnnameOne(SdfWorldTables client, SdfMesh mesh) {
        {
            var use = m_uses[mesh];
            var index = use.IndexOf(client: client);

            m_arena.Unname(mesh: mesh);
            m_landingTouched.Add(item: mesh);
            if (--use.Rows[index] > 0) { return; }

            var words = SdfMeshRegion.PlacedWordsOf(mesh: mesh);

            client.AttributeNamedWords(delta: -(((double)words) / use.Count));
            if (m_landingRanks.ContainsKey(key: mesh)) { client.AddOwedWords(delta: -words); }
            use.Count--;
            use.Clients[index] = use.Clients[use.Count];
            use.Rows[index] = use.Rows[use.Count];
            use.Priority[index] = use.Priority[use.Count];
            use.First[index] = use.First[use.Count];
            use.Clients[use.Count] = null!;
            if (use.Count == 0) {
                _ = m_uses.Remove(key: mesh);
                if ((mesh.GeometrySource is { } source) && m_variants.TryGetValue(key: source, value: out var variants) && variants.Remove(item: mesh) && (variants.Count == 0)) { _ = m_variants.Remove(key: source); }
                return;
            }
            for (var other = 0; (other < use.Count); other++) { use.Clients[other].AttributeNamedWords(delta: ((((double)words) / use.Count) - (((double)words) / (use.Count + 1)))); }
        }
    }
    /// <summary>Names the meshes a residency's draws are about to name, in place of those it named ahead before: each is
    /// placed beside the meshes the draws read and lands after the meshes a draw waits on, and the store keeps it until a
    /// draw names it or it leaves every residency's set. No draw record changes.</summary>
    /// <param name="client">The residency's tables.</param>
    /// <param name="held">The names the residency holds ahead, which this call replaces with the new ones.</param>
    /// <param name="meshes">The meshes; one listed twice is named once.</param>
    /// <param name="products">How many of <paramref name="meshes"/>, from the first, replace a drawn mesh and wait
    /// beside it: the ones the region gives back first when its share of the pool falls. The rest keep their words.</param>
    internal void ReplaceAhead(SdfWorldTables client, List<(SdfMesh Mesh, bool Product)> held, IReadOnlyList<SdfMesh> meshes, int products) {
        lock (m_gate) {
            if (m_disposed) { return; }
            m_standing.Clear();
            m_scratch.Clear();
            // A mesh listed as a floor too is named as a floor: it keeps its words whatever else names it.
            // A mesh renumbered from another is named with the mesh whose geometry it draws.
            for (var index = products; (index < meshes.Count); index++) {
                if (m_standing.Add(item: meshes[index])) { m_scratch.Add(item: meshes[index]); }
                if ((meshes[index].GeometrySource is { } source) && m_standing.Add(item: source)) { m_scratch.Add(item: source); }
            }

            var floors = m_scratch.Count;

            for (var index = 0; (index < products); index++) {
                if (m_standing.Add(item: meshes[index])) { m_scratch.Add(item: meshes[index]); }
                if ((meshes[index].GeometrySource is { } source) && m_standing.Add(item: source)) { m_scratch.Add(item: source); }
            }
            // The new names are taken before the old ones go, so a mesh named both before and after keeps its name.
            for (var index = 0; (index < m_scratch.Count); index++) { m_arena.NameAhead(mesh: m_scratch[index], product: (index >= floors)); }
            foreach (var (mesh, product) in held) { m_arena.UnnameAhead(mesh: mesh, product: product); }
            held.Clear();
            for (var index = 0; (index < m_scratch.Count); index++) { held.Add(item: (m_scratch[index], (index >= floors))); }
            m_standing.Clear();
            m_scratch.Clear();
            Plan(planner: client);
            // A mesh that left the set unnamed lets its need go, and one named again takes it back and its place in line.
            m_owed.Sync();
            m_landingAheadStale = true;
        }
    }

    /// <summary>Places what the names ask and brings the region and every residency to the result: the region grows,
    /// backs and gives back what the placements cover, a newly placed mesh is owed, a moved mesh is copied on the GPU or
    /// owed again, and every residency whose rows name a moved mesh is told to stage those rows again. A residency
    /// other than the planner is also told when a mesh loses its place.</summary>
    /// <param name="planner">The residency whose staging asked for the plan.</param>
    internal void Plan(SdfWorldTables? planner) {
        lock (m_gate) {
            if (m_disposed) { return; }
            PrepareLimit();
            m_arena.Plan();
            Apply(planner: planner);
            m_grant.RegionNamed(bytes: (m_arena.NamedWords * sizeof(uint)));
        }
    }
    // Brings the store's own state to the arena's latest plan, clip or trim.
    private void Apply(SdfWorldTables? planner) {
        if (m_arena.Changed) { m_version++; }
        ForgetArenaMeshes(planner: planner);
        if (m_arena.Words > 0L) {
            // A fit waits for the copies of moved meshes, which read words past the arena's end until they run.
            m_fitOwed |= ((m_arena.Forgotten.Count > 0) || (m_arena.Moved.Count > 0));
            if (m_fitOwed && !m_arena.HoldsMoveSources) {
                m_fitOwed = false;
                Fit(bytes: (((ulong)m_arena.Words) * sizeof(uint)));
            }
            EnsureCapacity(bytes: (((ulong)m_arena.Words) * sizeof(uint)), planner: planner);
            Cover();
        } else {
            m_grant.Kept();
        }
        foreach (var mesh in m_arena.Placed) {
            if (!m_arena.Placements.TryGetValue(key: mesh, value: out var word)) { continue; }
            // A mesh placed again within one plan held another place until now: nothing of it stands at this one.
            ForgetLanding(mesh: mesh);
            _ = m_pages.Remove(key: mesh);
            Recompose(mesh: mesh);
            m_owed.Add(mesh: mesh);
        }
        // Whether each moved mesh stood at the words it left is read before it is settled.
        m_standing.Clear();
        foreach (var mesh in m_arena.Moved) {
            if (!m_owed.Contains(mesh: mesh) && m_pages.TryGetValue(key: mesh, value: out var stood) && stood.IsCurrent) { _ = m_standing.Add(item: mesh); }
        }
        foreach (var mesh in m_arena.Moved) {
            MoveMesh(mesh: mesh, stood: m_standing.Contains(item: mesh));
            Notify(mesh: mesh, moved: true);
        }
        if (m_arena.Moved.Count > 0) { m_moveWitnesses.Clear(); }
        m_standing.Clear();
        RefreshPages();
        if (m_arena.Changed) {
            m_refusals += m_arena.Refused.Count;
            // An owed mesh the names let go of lets its need go, and one named again takes it back.
            m_owed.Sync();
        }
    }
    // A placement of a mesh's words from a first word: its vertices, triangle materials, indices and skin, in that order.
    private static SdfMeshRegionMesh PlacementAt(SdfMesh mesh, int vertexWord) {
        var materialWord = (vertexWord + (mesh.Positions.Length * SdfMeshRecord.VertexWords));
        var indexWord = (materialWord + SdfMeshRegion.MaterialWordsOf(mesh: mesh));
        var skinWord = (mesh.VertexMembers.IsEmpty ? 0 : (indexWord + SdfMeshRegion.IndexWordsOf(mesh: mesh)));

        return new SdfMeshRegionMesh(IndexCount: mesh.Indices.Length, IndexWord: indexWord, MaterialWord: materialWord, VertexWord: vertexWord, SkinWord: skinWord);
    }
    // Tells residencies whose rows name a mesh, or a mesh renumbered from it, that its address or standing changed.
    private void Notify(SdfMesh mesh, SdfWorldTables? except = null, bool moved = false) {
        NotifyOne(except: except, mesh: mesh, moved: moved);
        if (!m_variants.TryGetValue(key: mesh, value: out var variants)) { return; }
        foreach (var variant in variants) { NotifyOne(except: except, mesh: variant, moved: moved); }
    }
    private void NotifyOne(SdfMesh mesh, SdfWorldTables? except, bool moved) {
        if (!m_uses.TryGetValue(key: mesh, value: out var use)) { return; }
        for (var index = 0; (index < use.Count); index++) {
            if (!ReferenceEquals(objA: use.Clients[index], objB: except)) { use.Clients[index].NoteStoreChange(mesh: mesh, moved: moved); }
        }
    }
    // Brings a mesh's placement, and those of the meshes renumbered from it, to the arena's: a mesh that owns its
    // geometry lies whole from its first word, and one renumbered from another holds its triangle materials at its own
    // word and draws its vertices, indices and members from the other's.
    private void Recompose(SdfMesh mesh) {
        RecomposeOne(mesh: mesh);
        if (!m_variants.TryGetValue(key: mesh, value: out var variants)) { return; }
        foreach (var variant in variants) { RecomposeOne(mesh: variant); }
    }
    private void RecomposeOne(SdfMesh mesh) {
        if (!m_arena.Placements.TryGetValue(key: mesh, value: out var word)) { _ = m_placements.Remove(key: mesh); return; }
        if (mesh.GeometrySource is not { } source) { m_placements[mesh] = PlacementAt(mesh: mesh, vertexWord: word); return; }
        if (!m_arena.Placements.TryGetValue(key: source, value: out var sourceWord)) { _ = m_placements.Remove(key: mesh); return; }

        var body = PlacementAt(mesh: source, vertexWord: sourceWord);

        m_placements[mesh] = new SdfMeshRegionMesh(IndexCount: mesh.Indices.Length, IndexWord: body.IndexWord, MaterialWord: word, VertexWord: body.VertexWord, SkinWord: body.SkinWord);
    }
    // Whether a mesh's own words stand at its place.
    private bool StandsOne(SdfMesh mesh) => (m_arena.Placements.ContainsKey(key: mesh) && !m_owed.Contains(mesh: mesh) && m_pages.TryGetValue(key: mesh, value: out var pages) && pages.IsCurrent);
    // The arena took these meshes' placements back: their words are free for the next mesh they hold, so they owe none,
    // their landing starts again should a plan place them later, and a residency whose rows read one stages them absent.
    private void ForgetArenaMeshes(SdfWorldTables? planner) {
        foreach (var mesh in m_arena.Forgotten) {
            if (m_arena.Placements.ContainsKey(key: mesh)) { continue; }
            if (!m_pages.ContainsKey(key: mesh) && !m_owed.Contains(mesh: mesh) && !m_placements.ContainsKey(key: mesh)) { continue; }
            m_forgotten++;
            ForgetLanding(mesh: mesh);
            var stood = (m_pages.Remove(key: mesh) && !m_owed.Contains(mesh: mesh));

            _ = m_owed.Remove(mesh: mesh);
            Recompose(mesh: mesh);
            if (stood) { Notify(mesh: mesh, except: planner); }
        }
    }
    // Mapping changes invalidate publication even when no name changed. Re-owe only the affected meshes, which land
    // again through the ordinary door, and tell the residencies whose rows read them.
    private void RefreshPages() {
        var destination = m_region.Buffer(slot: 0);
        var revision = m_region.PageRevision;

        if (ReferenceEquals(objA: destination, objB: m_pageDestination) && (revision == m_pageRevision)) { return; }
        if (!ReferenceEquals(objA: destination, objB: m_pageDestination) && (m_pageDestination is not null)) {
            BufferGeneration++;
            foreach (var client in m_clients) { client.NoteStoreBuffer(); }
        }
        m_pageDestination = destination;
        m_pageRevision = revision;
        m_scratch.Clear();
        foreach (var (mesh, lease) in m_pages) { if (!lease.IsCurrent) { m_scratch.Add(item: mesh); } }
        foreach (var mesh in m_scratch) {
            _ = m_pages.Remove(key: mesh);
            m_owed.Add(mesh: mesh);
            ForgetLanding(mesh: mesh);
            Notify(mesh: mesh);
        }
        m_scratch.Clear();
    }
    // Settles a mesh the arena moved (SdfMeshRegionArena.Moved). One that stood at the words it left is copied from them
    // on the GPU (GpuRegion.TryOweMove), in the next recorded copy and before any word landed after it, so it stays
    // standing at its new words and no word of it passes through the host. Any other is owed and lands through the
    // background work, its rows absent until it has.
    private void MoveMesh(SdfMesh mesh, bool stood) {
        m_moved++;
        ForgetLanding(mesh: mesh);
        _ = m_pages.Remove(key: mesh);
        Recompose(mesh: mesh);
        if (!m_arena.Placements.TryGetValue(key: mesh, value: out var word)) {
            _ = m_owed.Remove(mesh: mesh);
            return;
        }

        var words = SdfMeshRegion.PlacedWordsOf(mesh: mesh);

        if (stood && (m_region.TryLease((((long)word) * sizeof(uint)), (((long)words) * sizeof(uint))) is { } lease) &&
            m_arena.MovedFrom(mesh: mesh, word: out var from) && m_region.TryOweMove(
                destinationOffset: (((long)word) * sizeof(uint)),
                length: (((long)words) * sizeof(uint)),
                sourceOffset: (((long)from) * sizeof(uint)))) {
            m_pages[mesh] = lease;
            _ = m_owed.Remove(mesh: mesh);
            m_gpuMoves++;
            m_gpuMovedWords += words;
            return;
        }
        m_owed.Add(mesh: mesh);
    }

    /// <summary>Returns where a placed mesh's words lie in the region.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="placement">The words, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> for a mesh the arena does not hold.</returns>
    internal bool TryPlacement(SdfMesh mesh, out SdfMeshRegionMesh placement) {
        lock (m_gate) { return m_placements.TryGetValue(key: mesh, value: out placement); }
    }
    /// <summary>Returns whether a mesh's words stand in the region where a draw naming it would read them: the arena
    /// holds it, the region owes none of its words, and its destination page generation still stands.</summary>
    /// <param name="mesh">The mesh, a chunk of a chunked mesh being a mesh of its own.</param>
    /// <returns><see langword="true"/> when a draw may name the mesh without waiting out of the frame.</returns>
    internal bool Stands(SdfMesh mesh) {
        lock (m_gate) { return (StandsOne(mesh: mesh) && ((mesh.GeometrySource is not { } source) || StandsOne(mesh: source))); }
    }
    /// <summary>Returns whether the region still owes a named mesh's words: it is placed and has not landed, or a draw
    /// names it and the arena refuses it under its limit.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns><see langword="true"/> while the region owes it.</returns>
    internal bool Owes(SdfMesh mesh) {
        lock (m_gate) { return (OwesOne(mesh: mesh) || ((mesh.GeometrySource is { } source) && OwesOne(mesh: source))); }
    }
    private bool OwesOne(SdfMesh mesh) => (m_arena.Refused.Contains(item: mesh) || (m_arena.Placements.ContainsKey(key: mesh) && m_owed.Contains(mesh: mesh)));
    /// <summary>Returns whether a draw names a mesh the arena refuses under its limit, so its draws wait out of the frame
    /// until a later plan fits it.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns><see langword="true"/> while the arena refuses it.</returns>
    internal bool Refuses(SdfMesh mesh) {
        lock (m_gate) { return (m_arena.Refused.Contains(item: mesh) || ((mesh.GeometrySource is { } source) && m_arena.Refused.Contains(item: source))); }
    }
    /// <summary>Returns whether the region found no room for a mesh named ahead beside the meshes the draws read: the
    /// arena placed it nowhere, or the device refused to back the words it was placed in (a mapped region's chunk).
    /// Either way it can land only in the memory the mesh it replaces gives up, so it takes the draws first.</summary>
    /// <param name="mesh">The mesh.</param>
    /// <returns><see langword="true"/> when the mesh is named ahead and refused.</returns>
    internal bool RefusesAhead(SdfMesh mesh) {
        lock (m_gate) { return (RefusesAheadOne(mesh: mesh) || ((mesh.GeometrySource is { } source) && RefusesAheadOne(mesh: source))); }
    }
    private bool RefusesAheadOne(SdfMesh mesh) {
        return (m_arena.AheadRefused.Contains(item: mesh) || (m_backingRefused && m_arena.Placements.TryGetValue(key: mesh, value: out var word) &&
            !m_region.IsBacked(bytes: (((long)SdfMeshRegion.PlacedWordsOf(mesh: mesh)) * sizeof(uint)), offset: (((long)word) * sizeof(uint)))));
    }
    /// <summary>Gets how many meshes draws name that the arena refuses under its limit.</summary>
    internal int RefusedCount { get { lock (m_gate) { return m_arena.Refused.Count; } } }
    /// <summary>Gets whether every copy the region owed has been recorded, submitted and completed: no move or carry
    /// waits for an upload, and none an upload recorded is still in flight.</summary>
    internal bool CopiesComplete { get { lock (m_gate) { return (!m_region.OwesMoves && !m_region.OwesCarry && (m_unsubmitted == 0) && (m_inFlight == 0)); } } }

    // Called only during a background admission on the frame thread. An owed geometry is never bound.
    internal bool TryProbeScene(SdfMeshIrradianceScene scene, out IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, out IGpuBuffer meshes, out long version) {
        lock (m_gate) {
            placements = m_placements;
            meshes = m_region.Buffer(slot: 0);
            version = m_version;
            return (!m_region.OwesMoves && !m_region.OwesCarry && (m_unsubmitted == 0) && scene.HasGpuPlacements(placements, Stands));
        }
    }

    /// <summary>Records the copies the region owes that no landing job carries (a grown region's carry and the moves of
    /// a compaction) into a residency's upload, ahead of its passes, so no pass reads a moved mesh at its new words, or
    /// a replaced buffer, before the copy that fills them. Records nothing when nothing is owed.</summary>
    /// <param name="client">The residency whose upload records them.</param>
    /// <param name="recorder">The recorder the upload records through.</param>
    /// <param name="commandBuffer">The upload's command buffer.</param>
    /// <param name="readers">The shader stages that read the region.</param>
    /// <returns>Whether a copy was recorded, so the residency reports the upload's submission
    /// (<see cref="SubmittedOwed"/>).</returns>
    internal bool RecordOwed(SdfWorldTables client, IGpuRecorder recorder, nint commandBuffer, GpuStage readers) {
        lock (m_gate) {
            if (m_disposed) { return false; }
            // A second upload by one residency since the moves were recorded has staged every row it draws again, and
            // every other residency stages again before it draws (NoteStoreChange): the words the moves left are free.
            if (m_arena.HoldsMoveSources && !m_region.OwesMoves && (m_unsubmitted == 0) && !m_moveWitnesses.Add(item: client)) {
                m_arena.ReleaseMoveSources();
                m_moveWitnesses.Clear();
                m_version++;
                m_fitOwed = true;
            }
            if (!m_region.OwesMoves && !m_region.OwesCarry) { return false; }
            if (!m_freeUploadSlots.TryPop(result: out var slot)) {
                m_moveSlotMisses++;
                return false;
            }
            m_region.Flush(slot: slot);

            var copies = new GpuRegionCopyRecording(recorder: recorder, readers: readers, begin: () => commandBuffer);

            copies.Record(flushed: true, handsToReaders: true, region: m_region, slot: slot);
            _ = copies.Finish();
            m_unsubmitted++;
            m_retired.Hold(item: new UploadSlot(store: this, slot: slot), pendingUse: () => (m_unsubmitted > 0));
            m_inFlight++;
            return true;
        }
    }
    /// <summary>Reports that the upload a residency recorded the region's owed copies into has been submitted.</summary>
    internal void SubmittedOwed() {
        lock (m_gate) { if (m_unsubmitted > 0) { m_unsubmitted--; } }
    }
    // An upload slot coming back once the upload that recorded into it has completed.
    private sealed class UploadSlot(SdfMeshRegionStore store, int slot) : IDisposable {
        public void Dispose() {
            if (store.m_disposed) { return; }
            store.m_region.Retire(slot: slot);
            store.m_freeUploadSlots.Push(item: slot);
            store.m_inFlight--;
        }
    }

    /// <summary>Serves what the store owes outside a plan, on the frame thread once a prepared frame for each residency:
    /// releases what the device has finished reading, answers an account trim, and fits the mapped region again while a
    /// chunk the device refused waits.</summary>
    internal void Service() {
        lock (m_gate) {
            if (m_disposed) { return; }
            if (m_retired.Count > 0) { m_retired.Poll(timeline: m_device.Services.QueueSubmitter.Timeline, release: static replaced => replaced.Dispose()); }
            if (Interlocked.Exchange(location1: ref m_trimRequested, value: 0) != 0) {
                try {
                    SdfRowChecks.ProcessWork.Count(kind: SdfRowChecks.Trims);
                    m_refusedGrowthBytes = -1L;
                    _ = ForgetUnused();
                    // A mapped region gives the forgotten meshes' chunks back now, under the pressure that asked.
                    m_version++;
                    Cover();
                } finally {
                    MemoryAccount.Shared.CompleteDeferredTrim();
                }
            }
            if (m_backingRefused) { Cover(); }
        }
    }
    // Forgets the placed meshes no name holds and what the store holds for them. A mapped region's chunks follow at its
    // next fit. True when a mesh was forgotten.
    private bool ForgetUnused() {
        if (!m_arena.TrimUnused()) { return false; }
        m_version++;
        ForgetArenaMeshes(planner: null);
        return true;
    }

    /// <summary>Sets the diagnostic admission cap (<see cref="SdfMeshRegionArena.ForceLimit"/>) and plans again under
    /// it.</summary>
    /// <param name="bytes">The cap in bytes, or null to lift it.</param>
    public void ForceLimit(ulong? bytes) {
        lock (m_gate) {
            if (m_disposed) { return; }
            m_arena.ForceLimit(bytes: bytes);
            m_version++;
            ForgetArenaMeshes(planner: null);
            Plan(planner: null);
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            if (m_trimListening) { MemoryAccount.Shared.TrimRequested -= RequestTrim; m_trimListening = false; }
            if (Interlocked.Exchange(location1: ref m_trimRequested, value: 0) != 0) { MemoryAccount.Shared.CompleteDeferredTrim(); }
            DisposeLanding();
            m_owed.Clear();
            m_arena.Dispose();
            m_retired.Release(completed: ulong.MaxValue, release: static replaced => replaced.Dispose());
            m_region.Dispose();
            m_grant.Dispose();
            m_hostCopies.Dispose();
            m_placements.Clear();
            m_pages.Clear();
            m_uses.Clear();
        }
    }

    /// <summary>Describes the store: the residencies attached, the bytes the region holds and its names use, what it
    /// owes, the arena's moves and compactions, its limits, the meshes it refuses now and those it refused and forgot in
    /// all, and the landing jobs.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string Describe() {
        lock (m_gate) {
            var shared = 0;
            var sharedWords = 0L;
            var variants = 0;
            var variantWords = 0L;

            foreach (var (mesh, use) in m_uses) {
                if (use.Count > 1) { shared++; sharedWords += (((long)SdfMeshRegion.PlacedWordsOf(mesh: mesh)) * (use.Count - 1)); }
                // A mesh renumbered from another is placed as its materials alone: the rest is the other's words.
                if (mesh.GeometrySource is not null) { variants++; variantWords += (SdfMeshRegion.WordsOf(mesh: mesh) - SdfMeshRegion.PlacedWordsOf(mesh: mesh)); }
            }

            return string.Create(provider: CultureInfo.InvariantCulture, handler: $"mesh-store residencies={m_clients.Count} region-held={Bytes}B region-used={(m_arena.Words * sizeof(uint))}B meshes={m_placements.Count} named={m_uses.Count} region-named={(m_arena.NamedWords * sizeof(uint))}B shared={shared} shared-saved={(sharedWords * sizeof(uint))}B variants={variants} variants-saved={(variantWords * sizeof(uint))}B written={(m_wordsWritten * sizeof(uint))}B owed-meshes={m_owed.Count} moved={m_moved} compactions={m_arena.Compactions} compacted={m_arena.CompactionMoves}/{(m_arena.CompactionWords * sizeof(uint))}B gpu-moved={m_gpuMoves}/{(m_gpuMovedWords * sizeof(uint))}B move-sources={(m_arena.HoldsMoveSources ? "held" : "none")} move-slot-misses={m_moveSlotMisses} region-limit={(m_arena.LimitWords * sizeof(uint))}B region-held-limit={((m_arena.HeldLimitWords == long.MaxValue) ? "none" : $"{(m_arena.HeldLimitWords * sizeof(uint))}B")} arena-held={(m_arena.HeldWords * sizeof(uint))}B arena-placed={(m_arena.PlacedWords * sizeof(uint))}B shed={m_arena.Shed} staging-refused={m_region.StagingRefusals} refused={m_arena.Refused.Count} ahead={m_arena.AheadCount} ahead-refused={m_arena.AheadRefused.Count} refusals={m_refusals} forgotten={m_forgotten} {DescribeLandingJobs()}");
        }
    }
    /// <summary>Gets the bytes of the region's buffers, by the memory they live in.</summary>
    public GpuMemoryBytes OwnedBytes { get { lock (m_gate) { return (m_disposed ? default : m_region.OwnedBytes); } } }
}
