using Puck.Abstractions.Counting;
using System.Collections.Concurrent;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

/// <summary>
/// The baked meshes every residency that holds the library shares, by the geometry each was baked from
/// (<see cref="SdfMeshGeometryBuilder"/>) and the identity of the bake that meshed it
/// (<see cref="SdfMeshStore.IdentityOf"/>), so a body that crosses into another world takes the mesh its first world baked
/// instead of baking it again, and a bake whose identity moved (an edit to its code or kernels) never takes a mesh an
/// earlier identity baked. A program numbers its palette as its emitters register materials, so one body's shapes
/// carry other material ids in another world's program; the geometry names each material by its place among the group's
/// own materials, and a mesh taken into a program whose group carries other ids has its triangle materials renumbered by
/// place.
/// <para>A library over a store (<see cref="Disk"/>) keeps what it bakes between runs: every mesh it stores is written
/// there under its bake's identity, and a bake takes a geometry the store holds from disk (<see cref="TryLoad"/>) instead
/// of baking it again.</para>
/// <para>The library retains meshes within <see cref="ByteBudget"/>, measured as the storage each retained mesh is
/// charged to the process account (<see cref="SdfMesh.StorageBytes"/>), evicting the oldest first. Its members are
/// thread-safe: a group's mesh lands from the thread pool.</para>
/// </summary>
/// <param name="store">The store the library keeps its meshes in between runs, or <see langword="null"/> to keep them for
/// the run alone.</param>
/// <param name="byteBudget">The retained byte budget, or null for the machine-derived default.</param>
/// <param name="allocator">The host allocator for published payloads; null keeps an offline library managed.</param>
public sealed class SdfMeshLibrary(SdfMeshStore? store = null, long? byteBudget = null, IAllocator? allocator = null) : IDisposable {
    /// <summary>Gets the process account's machine-derived retained cache allowance, bounded to 16..512 MiB.</summary>
    public static long DefaultByteBudget => MemoryAccount.Shared.CacheBudget;
    /// <summary>Gets the maximum retained bytes; live residencies retain their own in-use meshes independently.</summary>
    public long ByteBudget => Math.Max(val1: 0, val2: Math.Min(val1: (byteBudget ?? Account.CacheBudget), val2: (Account.Read().Budget / 16)));

    /// <summary>Gets the allocator supplied by the host, or null for a managed-only offline library.</summary>
    public IAllocator? Allocator { get; } = allocator;
    /// <summary>Gets the single process byte-accounting authority.</summary>
    public MemoryAccount Account => MemoryAccount.Shared;

    private long m_retainedBytes;
    private bool m_listening;

    private readonly LinkedList<(UInt128 Identity, SdfMeshGeometry Geometry)> m_age = new();
    private readonly Dictionary<(UInt128 Identity, SdfMeshGeometry Geometry), (long Bytes, LinkedListNode<(UInt128 Identity, SdfMeshGeometry Geometry)> Node)> m_usage = [];

    /// <summary>Gets the storage bytes of the meshes the library retains.</summary>
    public long RetainedBytes { get { lock (m_gate) { return m_retainedBytes; } } }

    private readonly Lock m_gate = new();
    private readonly Dictionary<(UInt128 Identity, SdfMeshGeometry Geometry), List<Variant>> m_meshes = [];
    // The geometry the latest mesh of each dynamic form was held under, by bake identity (TryTakeForm).
    private readonly Dictionary<(UInt128 Identity, UInt128 Form), SdfMeshGeometry> m_forms = [];
    // Groups sharing geometry also share its disk decode; the variants below hold only material renumberings.
    private readonly ConcurrentDictionary<(UInt128 Identity, SdfMeshGeometry Geometry), Lazy<StoredMesh>> m_reads = new();

    /// <summary>Gets the store the library keeps its meshes in between runs, or <see langword="null"/>.</summary>
    public SdfMeshStore? Disk { get; } = store;
    /// <summary>Gets the settle rebakes' counts (<see cref="SdfMeshSettle"/>) every bake holding the library counts
    /// into, so one source reads a presentation's residencies together.</summary>
    public WorkCounterSet Settles { get; } = SdfMeshSettle.Counters();

    /// <summary>Gets how many geometries the library holds.</summary>
    public int Count {
        get {
            lock (m_gate) { return m_meshes.Count; }
        }
    }

    /// <summary>Stores a group's mesh, baked from its geometry, with the material ids its group's shapes carry in the order
    /// the geometry numbers them, and writes it to <see cref="Disk"/> under the bake's identity; a geometry already held
    /// keeps its mesh.</summary>
    /// <param name="geometry">The geometry the mesh was baked from.</param>
    /// <param name="mesh">The mesh, whose triangle materials are ids of <paramref name="materials"/> or ids no shape of the
    /// group carries.</param>
    /// <param name="materials">The group's material ids, by their place in the geometry.</param>
    /// <param name="identity">The identity of the bake that meshed it (<see cref="SdfMeshStore.IdentityOf"/>).</param>
    /// <param name="form">The form of the dynamic group the mesh was baked for (<see cref="SdfMeshGeometryBuilder.Form"/>),
    /// which <see cref="TryTakeForm"/> then answers with it, or <see langword="null"/> for a group in world space.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    /// <returns>An owned mesh claim. The caller releases it after its last borrowed view ends.</returns>
    /// <remarks>A library with a native allocator and a store publishes the mesh the store reads back, so a mesh baked
    /// in this run draws what a later run loads, and its normals and coarser levels can be let go and restored from the
    /// entry (<see cref="SdfMesh.DescribeRestores"/>); a write the store refuses publishes the baked mesh, which keeps
    /// them.</remarks>
    public SdfMesh Store(SdfMeshGeometry geometry, SdfMesh mesh, int[] materials, UInt128 identity = default, UInt128? form = null) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        ArgumentNullException.ThrowIfNull(argument: materials);

        if ((Allocator is not null) && (Disk is { } disk) && !HoldsAny(geometry: geometry, identity: identity) &&
            disk.Write(geometry: geometry, identity: identity, materials: materials, mesh: mesh) &&
            TryLoad(form: form, geometry: geometry, identity: identity, materials: materials, mesh: out var stored, refusal: out _) &&
            (stored is not null)) {
            return stored;
        }

        var owned = mesh.NativeCopy(Allocator, Account);

        try {
            if (Hold(form: form, geometry: geometry, identity: identity, materials: materials, mesh: owned)) {
                _ = Disk?.Write(geometry: geometry, identity: identity, materials: materials, mesh: owned);
            }
            return owned;
        } catch { owned.Release(); throw; }
    }
    private bool HoldsAny(SdfMeshGeometry geometry, UInt128 identity) {
        lock (m_gate) { return m_meshes.ContainsKey(key: (identity, geometry)); }
    }
    /// <summary>Takes the latest mesh the library holds of a dynamic group's form, baked at whatever pose its other slots
    /// stood in (<see cref="SdfMeshGeometryBuilder.Form"/>): the same shapes, which the mesh's skin carries through each
    /// member's slot (<see cref="SdfMesh.Members"/>), renumbered for the taking group's materials as
    /// <see cref="TryTake"/> renumbers. A body crossing into another world mid-stride takes the mesh its other pose
    /// baked, and settles later (<see cref="SdfMeshSettle"/>).</summary>
    /// <param name="form">The form.</param>
    /// <param name="materials">The taking group's material ids, by their place in the geometry.</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <param name="identity">The taking bake's identity; only a mesh baked under it is taken.</param>
    /// <returns><see langword="true"/> when the library holds a mesh of the form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public bool TryTakeForm(UInt128 form, int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, UInt128 identity = default) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        SdfMeshGeometry geometry;

        lock (m_gate) {
            if (!m_forms.TryGetValue(key: (identity, form), value: out geometry)) {
                mesh = null;
                return false;
            }
        }

        return TryTake(geometry: geometry, identity: identity, materials: materials, mesh: out mesh);
    }
    /// <summary>Records that a geometry's bake found no surface, so a later group of it under the same bake identity, in
    /// another program or residency, finishes with no mesh rather than baking again.</summary>
    /// <param name="geometry">The geometry that meshed nothing.</param>
    /// <param name="identity">The identity of the bake that found no surface.</param>
    /// <param name="persist">Whether the empty result is also written to <see cref="Disk"/> under
    /// <paramref name="identity"/>, to be kept between runs.</param>
    public void StoreEmpty(SdfMeshGeometry geometry, UInt128 identity = default, bool persist = false) {
        lock (m_gate) {
            if (m_meshes.ContainsKey(key: (identity, geometry))) { return; }

            m_meshes.Add(key: (identity, geometry), value: []);
            Retain(bytes: 0, key: (identity, geometry));
        }
        if (persist) { _ = Disk?.Write(geometry: geometry, identity: identity, materials: [], mesh: null); }
    }
    /// <summary>Returns whether a geometry's bake under an identity found no surface (<see cref="StoreEmpty"/>).</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="identity">The bake's identity.</param>
    /// <returns><see langword="true"/> when the library holds the geometry as one that meshes nothing.</returns>
    public bool HoldsEmpty(SdfMeshGeometry geometry, UInt128 identity = default) {
        lock (m_gate) { return (m_meshes.TryGetValue(key: (identity, geometry), value: out var variants) && (variants.Count == 0)); }
    }
    /// <summary>Returns whether <see cref="Disk"/> holds a geometry's mesh under a bake's identity, without reading it.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The geometry.</param>
    /// <returns><see langword="true"/> when the store holds an entry for it.</returns>
    public bool Holds(UInt128 identity, SdfMeshGeometry geometry) => (Disk?.Contains(geometry: geometry, identity: identity) ?? false);
    /// <summary>Takes a geometry's mesh from <see cref="Disk"/> for a group whose shapes carry <paramref name="materials"/>,
    /// holding it for the run as <see cref="Store"/> would, then taking it as <see cref="TryTake"/> does. Groups share
    /// one decode per identity and geometry until the library empties; a failed read is retried next time.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The geometry.</param>
    /// <param name="materials">The taking group's material ids, by their place in the geometry.</param>
    /// <param name="mesh">The mesh, or null for a sound entry whose bake found no surface.</param>
    /// <param name="refusal">Why the store refused the entry it held, or <see langword="null"/>.</param>
    /// <param name="form">The form of the dynamic group loading it, as <see cref="Store"/> takes it, or
    /// <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the store held a sound entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public bool TryLoad(UInt128 identity, SdfMeshGeometry geometry, int[] materials, out SdfMesh? mesh, out string? refusal, UInt128? form = null) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        mesh = null;
        refusal = null;
        if (Disk is null) { return false; }
        // A geometry the library already holds under this identity is served from memory: the decode is shared until
        // the library evicts it, and the store is read again only for a geometry it does not hold.
        if (HoldsEmpty(geometry: geometry, identity: identity)) { return true; }
        if (TryTake(geometry: geometry, identity: identity, materials: materials, mesh: out var held)) {
            if (form is { } heldForm) { lock (m_gate) { m_forms[(identity, heldForm)] = geometry; } }
            mesh = held;
            return true;
        }
        var key = (Identity: identity, Geometry: geometry);
        var pending = m_reads.GetOrAdd(key, static (key, disk) => new Lazy<StoredMesh>(valueFactory: () => {
            var loaded = disk.TryRead(geometry: key.Geometry, identity: key.Identity, materials: out var stored, mesh: out var read, refusal: out var why);

            return new StoredMesh(Found: loaded, Materials: stored, Mesh: read, Refusal: why);
        }), Disk);
        var result = pending.Value;

        refusal = result.Refusal;
        if (!result.Found) {
            _ = m_reads.TryRemove(item: new KeyValuePair<(UInt128, SdfMeshGeometry), Lazy<StoredMesh>>(key: key, value: pending));
            return false;
        }
        try {
            if (result.Mesh is null) { StoreEmpty(geometry: geometry, identity: identity); return true; }
            if (TryTake(geometry: geometry, identity: identity, materials: materials, mesh: out mesh)) { return true; }
            // The decode takes the group's ids before the native copy, so the published storage holds one set of triangle
            // materials rather than the store's canonical set beside a renumbered variant. The held mesh is what later
            // groups renumber from, so it keeps the canonical ids when the group's would make that ambiguous: an id no
            // shape owns equal to one of the group's, or two places with one id (SdfMeshMaterials.Reversible). The copy
            // runs outside the gate the frame thread takes; the first copy held wins, and a later one is released.
            var pieces = ((result.Mesh.Chunks.Count == 0) ? (IReadOnlyList<SdfMesh>)[result.Mesh] : result.Mesh.Chunks);
            var heldIds = ((result.Materials.AsSpan().SequenceEqual(other: materials) || !SdfMeshMaterials.Reversible(from: result.Materials, pieces: pieces, to: materials)) ? result.Materials : materials);
            var source = (ReferenceEquals(objA: heldIds, objB: result.Materials) ? result.Mesh : RenumberManaged(from: result.Materials, mesh: result.Mesh, to: materials));
            // Its parts are let go and restored from this set, so the set stays while this process runs.
            if (Allocator is not null) { _ = Disk.Hold(identity: identity); }
            var copy = source.NativeCopy(Allocator, Account, restore: (this, identity, geometry));

            try {
                _ = Hold(geometry, identity, copy, heldIds, form);
                if (TryTake(geometry: geometry, identity: identity, materials: materials, mesh: out mesh)) { return true; }
                // The library let go of the geometry between the hold and the take (a trim): the copy is the mesh.
                mesh = (ReferenceEquals(objA: heldIds, objB: materials) ? copy.Acquire() : Renumber(from: heldIds, mesh: copy, to: materials));
                return true;
            } finally { copy.Release(); }
        } finally { _ = m_reads.TryRemove(item: new KeyValuePair<(UInt128, SdfMeshGeometry), Lazy<StoredMesh>>(key: key, value: pending)); }
    }
    /// <summary>Takes the mesh of a geometry for a group whose shapes carry <paramref name="materials"/>: the stored mesh
    /// when its group carried the same ids, and otherwise the stored mesh with each triangle's material renumbered from the
    /// id its group carried at a place to the id <paramref name="materials"/> holds there, kept for the next group that
    /// carries the same ids.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="materials">The taking group's material ids, by their place in the geometry.</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <param name="identity">The taking bake's identity; only a mesh baked under it is taken.</param>
    /// <returns><see langword="true"/> when the library holds the geometry under <paramref name="identity"/>.</returns>
    /// <remarks>A successful take returns an owned mesh claim; the consumer releases it after its last borrowed view ends.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public bool TryTake(SdfMeshGeometry geometry, int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, UInt128 identity = default) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        var key = (identity, geometry);
        Variant source;

        lock (m_gate) {
            if (!m_meshes.TryGetValue(key: key, value: out var variants) || (variants.Count == 0)) {
                mesh = null;
                return false;
            }
            Touch(key: key);
            foreach (var variant in variants) {
                if (variant.Materials.AsSpan().SequenceEqual(other: materials)) {
                    mesh = variant.Mesh.Acquire();
                    return true;
                }
            }
            source = variants[0];
            _ = source.Mesh.Acquire();
        }

        // The renumbered copy is made outside the gate the frame thread takes; the first variant held for these ids wins.
        SdfMesh renumbered;

        try {
            renumbered = Renumber(mesh: source.Mesh, from: source.Materials, to: materials);
        } finally {
            source.Mesh.Release();
        }
        lock (m_gate) {
            if (m_meshes.TryGetValue(key: key, value: out var variants) && (variants.Count > 0)) {
                foreach (var variant in variants) {
                    if (variant.Materials.AsSpan().SequenceEqual(other: materials)) {
                        mesh = variant.Mesh.Acquire();
                        renumbered.Release();
                        return true;
                    }
                }
                variants.Add(item: new Variant(Materials: materials, Mesh: renumbered.Acquire(cached: true)));
                Retain(key, renumbered.StorageBytes);
            }
        }
        mesh = renumbered;
        return true;
    }

    // Holds a geometry's first mesh under a bake identity within the byte budget and makes it its form's latest under
    // that identity; false when it held one already.
    private bool Hold(SdfMeshGeometry geometry, UInt128 identity, SdfMesh mesh, int[] materials, UInt128? form) {
        var key = (identity, geometry);

        lock (m_gate) {
            if (form is { } shapes) { m_forms[(identity, shapes)] = geometry; }
            if (m_meshes.ContainsKey(key: key)) { return false; }
            m_meshes.Add(key: key, value: [new Variant(Materials: materials, Mesh: mesh.Acquire(cached: true))]);
            Retain(key, mesh.StorageBytes);

            return true;
        }
    }
    private void Touch((UInt128 Identity, SdfMeshGeometry Geometry) key) {
        if (!m_usage.TryGetValue(key: key, value: out var usage)) { return; }
        m_age.Remove(node: usage.Node);
        m_age.AddLast(node: usage.Node);
    }
    private void Retain((UInt128 Identity, SdfMeshGeometry Geometry) key, long bytes) {
        if (!m_listening) { Account.TrimRequested += Trim; m_listening = true; }
        if (m_usage.TryGetValue(key: key, value: out var usage)) {
            m_usage[key] = ((usage.Bytes + bytes), usage.Node);
            Touch(key: key);
        } else {
            m_usage.Add(key: key, value: (bytes, m_age.AddLast(value: key)));
        }
        m_retainedBytes += bytes;
        while ((m_retainedBytes > ByteBudget) && (m_age.First is { } oldest)) { Evict(key: oldest.Value); }
    }
    // Drops a geometry's cache claims, its pending read and the forms it answers, under the gate.
    private void Evict((UInt128 Identity, SdfMeshGeometry Geometry) key) {
        if (m_usage.Remove(key: key, value: out var usage)) {
            m_retainedBytes -= usage.Bytes;
            m_age.Remove(node: usage.Node);
        }
        if (m_meshes.Remove(key: key, value: out var variants)) {
            foreach (var variant in variants) { variant.Mesh.Release(cached: true); }
        }
        _ = m_reads.TryRemove(key: key, value: out _);
        foreach (var form in m_forms.Where(predicate: entry => ((entry.Key.Identity == key.Identity) && entry.Value.Equals(other: key.Geometry))).Select(selector: entry => entry.Key).ToArray()) { m_forms.Remove(key: form); }
    }

    /// <summary>Gets a count that moves each time a published mesh's store entry could not restore its normals and
    /// levels (<see cref="Lose"/>): a bake holding the library restarts when it moves, baking again each group whose
    /// mesh is lost.</summary>
    public long LostRevision => Interlocked.Read(location: ref m_lostRevision);
    private long m_lostRevision;

    /// <summary>Forgets a geometry whose store entry could not restore a published mesh's normals and levels, so the
    /// next group of it misses the library and the store and bakes, and moves <see cref="LostRevision"/>.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="identity">The bake identity it was held under.</param>
    /// <param name="reason">Why the restore failed, for the log.</param>
    internal void Lose(SdfMeshGeometry geometry, UInt128 identity, string reason) {
        lock (m_gate) { Evict(key: (identity, geometry)); }
        var lost = Interlocked.Increment(location: ref m_lostRevision);

        // An evicted set loses every geometry it held at once: the first and each doubling are named.
        if ((lost & (lost - 1)) == 0) {
            Console.Error.WriteLine(value: $"[sdf.mesh-bake] lost identity={identity:x32} geometry={geometry.Hash:x32} lost={lost}: the store could not restore its normals and levels ({reason}); its groups bake again");
        }
    }

    /// <summary>Evicts cache claims; live residency and scene claims keep their payloads.</summary>
    public void Dispose() {
        lock (m_gate) {
            if (m_listening) { Account.TrimRequested -= Trim; m_listening = false; }
            Trim();
        }
    }

    // Releases every cache claim the library holds, as the account's trim requests; live residency and scene claims keep
    // their payloads, and the store keeps its entries.
    private void Trim() {
        lock (m_gate) {
            foreach (var variants in m_meshes.Values) { foreach (var variant in variants) { variant.Mesh.Release(cached: true); } }
            m_meshes.Clear();
            m_reads.Clear();
            m_forms.Clear();
            m_usage.Clear();
            m_age.Clear();
            m_retainedBytes = 0;
        }
    }
    // The mesh with each triangle material that `from` holds at a place renumbered to what `to` holds there; one neither
    // list reaches keeps its id. The geometry's equality makes the two lists the same length.
    private SdfMesh Renumber(SdfMesh mesh, int[] from, int[] to) => ((Allocator is { } native) ? mesh.RenumberNative(from, to, native, Account) : RenumberManaged(from: from, mesh: mesh, to: to));
    // A managed mesh renumbered into new managed arrays: an offline library's variants, and a decode before its native copy.
    private static SdfMesh RenumberManaged(SdfMesh mesh, int[] from, int[] to) {
        if (mesh.Chunks.Count != 0) { return SdfMeshChunks.Join(chunks: mesh.Chunks.Select(selector: chunk => RenumberManaged(from: from, mesh: chunk, to: to)).ToArray()); }
        var levels = new SdfMeshLevel[mesh.Levels.Count];

        for (var level = 0; (level < levels.Length); level++) {
            levels[level] = (mesh.Levels[level] with { TriangleMaterials = Renumbered(from: from, source: mesh.Levels[level].TriangleMaterials.Span, to: to) });
        }

        return new SdfMesh(indices: mesh.Indices, members: mesh.Members, normals: mesh.Normals, positions: mesh.Positions, triangleMaterials: Renumbered(from: from, source: mesh.TriangleMaterials.Span, to: to), vertexMembers: mesh.VertexMembers, influences: mesh.Influences) { BakeCell = mesh.BakeCell, Levels = levels };
    }
    private static uint[] Renumbered(ReadOnlySpan<uint> source, int[] from, int[] to) {
        var renumbered = new uint[source.Length];

        for (var triangle = 0; (triangle < source.Length); triangle++) {
            renumbered[triangle] = SdfMeshMaterials.Renumber(material: source[triangle], from: from, to: to);
        }

        return renumbered;
    }

    private sealed record Variant(int[] Materials, SdfMesh Mesh);
    private sealed record StoredMesh(bool Found, SdfMesh? Mesh, int[] Materials, string? Refusal);
}
