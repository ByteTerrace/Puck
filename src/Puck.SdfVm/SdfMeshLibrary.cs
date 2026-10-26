using Puck.Abstractions.Counting;
using System.Collections.Concurrent;

namespace Puck.SdfVm;

/// <summary>
/// The baked meshes every residency that holds the library shares, by the geometry each was baked from
/// (<see cref="SdfMeshGeometryBuilder"/>), so a body that crosses into another world takes the mesh its first world baked
/// instead of baking it again. A program numbers its palette as its emitters register materials, so one body's shapes
/// carry other material ids in another world's program; the geometry names each material by its place among the group's
/// own materials, and a mesh taken into a program whose group carries other ids has its triangle materials renumbered by
/// place.
/// <para>A library over a store (<see cref="Disk"/>) keeps what it bakes between runs: every mesh it stores is written
/// there under its bake's identity, and a bake takes a geometry the store holds from disk (<see cref="TryLoad"/>) instead
/// of baking it again.</para>
/// <para>The library retains meshes within <see cref="ByteBudget"/>, evicting the oldest first. Its members are
/// thread-safe: a group's mesh lands from the thread pool.</para>
/// </summary>
/// <param name="store">The store the library keeps its meshes in between runs, or <see langword="null"/> to keep them for
/// the run alone.</param>
/// <param name="byteBudget">The retained byte budget, or null for the machine-derived default.</param>
public sealed class SdfMeshLibrary(SdfMeshStore? store = null, long? byteBudget = null) {
    /// <summary>Gets the machine's default retained mesh budget: one sixteenth of available managed memory, bounded to 16..512 MiB.</summary>
    public static long DefaultByteBudget => Math.Clamp((GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16), (16L << 20), (512L << 20));

    /// <summary>Gets the maximum retained bytes; live residencies retain their own in-use meshes independently.</summary>
    public long ByteBudget { get; } = Math.Max(0, (byteBudget ?? DefaultByteBudget));

    private long m_retainedBytes;

    private readonly LinkedList<SdfMeshGeometry> m_age = new();
    private readonly Dictionary<SdfMeshGeometry, (long Bytes, LinkedListNode<SdfMeshGeometry> Node)> m_usage = [];

    /// <summary>Gets the estimated retained geometry, mesh and material bytes.</summary>
    public long RetainedBytes { get { lock (m_gate) { return m_retainedBytes; } } }

    private readonly Lock m_gate = new();
    private readonly Dictionary<SdfMeshGeometry, List<Variant>> m_meshes = [];
    // The geometry the latest mesh of each dynamic form was held under (TryTakeForm).
    private readonly Dictionary<UInt128, SdfMeshGeometry> m_forms = [];
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
    public void Store(SdfMeshGeometry geometry, SdfMesh mesh, int[] materials, UInt128 identity = default, UInt128? form = null) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        ArgumentNullException.ThrowIfNull(argument: materials);

        if (Hold(form: form, geometry: geometry, materials: materials, mesh: mesh)) {
            _ = Disk?.Write(geometry: geometry, identity: identity, materials: materials, mesh: mesh);
        }
    }
    /// <summary>Takes the latest mesh the library holds of a dynamic group's form, baked at whatever pose its other slots
    /// stood in (<see cref="SdfMeshGeometryBuilder.Form"/>): the same shapes, which the mesh's skin carries through each
    /// member's slot (<see cref="SdfMesh.Members"/>), renumbered for the taking group's materials as
    /// <see cref="TryTake"/> renumbers. A body crossing into another world mid-stride takes the mesh its other pose
    /// baked, and settles later (<see cref="SdfMeshSettle"/>).</summary>
    /// <param name="form">The form.</param>
    /// <param name="materials">The taking group's material ids, by their place in the geometry.</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the library holds a mesh of the form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public bool TryTakeForm(UInt128 form, int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        SdfMeshGeometry geometry;

        lock (m_gate) {
            if (!m_forms.TryGetValue(key: form, value: out geometry)) {
                mesh = null;
                return false;
            }
        }

        return TryTake(geometry: geometry, materials: materials, mesh: out mesh);
    }
    /// <summary>Records that a geometry's bake found no surface, so a later group of it, in another program or residency,
    /// finishes with no mesh rather than baking again. Supplying the bake identity also persists it between runs.</summary>
    /// <param name="geometry">The geometry that meshed nothing.</param>
    /// <param name="identity">The bake identity to persist the empty result under; null keeps it for this run alone.</param>
    public void StoreEmpty(SdfMeshGeometry geometry, UInt128? identity = null) {
        lock (m_gate) {
            if (m_meshes.ContainsKey(key: geometry)) { return; }

            m_meshes.Add(key: geometry, value: []);
            Retain(geometry, 256);
        }
        if (identity is { } value) { _ = Disk?.Write(geometry: geometry, identity: value, materials: [], mesh: null); }
    }
    /// <summary>Returns whether a geometry's bake found no surface (<see cref="StoreEmpty"/>).</summary>
    /// <param name="geometry">The geometry.</param>
    /// <returns><see langword="true"/> when the library holds the geometry as one that meshes nothing.</returns>
    public bool HoldsEmpty(SdfMeshGeometry geometry) {
        lock (m_gate) { return (m_meshes.TryGetValue(key: geometry, value: out var variants) && (variants.Count == 0)); }
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
        if (result.Mesh is null) { StoreEmpty(geometry: geometry); return true; }
        _ = Hold(geometry: geometry, materials: result.Materials, mesh: result.Mesh, form: form);

        if (TryTake(geometry: geometry, materials: materials, mesh: out mesh)) { return true; }
        mesh = (result.Materials.AsSpan().SequenceEqual(materials) ? result.Mesh : Renumber(result.Mesh, result.Materials, materials));
        return true;
    }
    /// <summary>Takes the mesh of a geometry for a group whose shapes carry <paramref name="materials"/>: the stored mesh
    /// when its group carried the same ids, and otherwise the stored mesh with each triangle's material renumbered from the
    /// id its group carried at a place to the id <paramref name="materials"/> holds there, kept for the next group that
    /// carries the same ids.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <param name="materials">The taking group's material ids, by their place in the geometry.</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the library holds the geometry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public bool TryTake(SdfMeshGeometry geometry, int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        lock (m_gate) {
            if (!m_meshes.TryGetValue(key: geometry, value: out var variants) || (variants.Count == 0)) {
                mesh = null;
                return false;
            }
            Touch(geometry);
            foreach (var variant in variants) {
                if (variant.Materials.AsSpan().SequenceEqual(other: materials)) {
                    mesh = variant.Mesh;
                    return true;
                }
            }

            mesh = Renumber(mesh: variants[0].Mesh, from: variants[0].Materials, to: materials);
            variants.Add(item: new Variant(Materials: materials, Mesh: mesh));
            Retain(geometry, (BytesOf(mesh) + (materials.LongLength * sizeof(int))));

            return true;
        }
    }

    // Holds a geometry's first mesh within the byte budget and makes it its form's latest;
    // false when it held one already.
    private bool Hold(SdfMeshGeometry geometry, SdfMesh mesh, int[] materials, UInt128? form) {
        lock (m_gate) {
            if (form is { } shapes) { m_forms[shapes] = geometry; }
            if (m_meshes.ContainsKey(key: geometry)) { return false; }
            m_meshes.Add(key: geometry, value: [new Variant(Materials: materials, Mesh: mesh)]);
            Retain(geometry, ((256 + BytesOf(mesh)) + (materials.LongLength * sizeof(int))));

            return true;
        }
    }
    private static long BytesOf(SdfMesh mesh) => checked((((long)SdfMeshRegion.WordsOf(mesh)) * sizeof(uint)));
    private void Touch(SdfMeshGeometry geometry) {
        if (!m_usage.TryGetValue(geometry, out var usage)) { return; }
        m_age.Remove(usage.Node);
        m_age.AddLast(usage.Node);
    }
    private void Retain(SdfMeshGeometry geometry, long bytes) {
        if (m_usage.TryGetValue(geometry, out var usage)) {
            m_usage[geometry] = ((usage.Bytes + bytes), usage.Node);
            Touch(geometry);
        } else {
            m_usage.Add(geometry, (bytes, m_age.AddLast(geometry)));
        }
        m_retainedBytes += bytes;
        while ((m_retainedBytes > ByteBudget) && (m_age.First is { } oldest)) {
            var victim = oldest.Value;

            m_retainedBytes -= m_usage[victim].Bytes;
            m_usage.Remove(victim);
            m_meshes.Remove(victim);
            foreach (var read in m_reads.Keys) { if (read.Geometry.Equals(victim)) { _ = m_reads.TryRemove(read, out _); } }
            m_age.RemoveFirst();
            foreach (var form in m_forms.Where(entry => entry.Value.Equals(victim)).Select(entry => entry.Key).ToArray()) { m_forms.Remove(form); }
        }
    }
    // The mesh with each triangle material that `from` holds at a place renumbered to what `to` holds there; one neither
    // list reaches keeps its id. The geometry's equality makes the two lists the same length.
    private static SdfMesh Renumber(SdfMesh mesh, int[] from, int[] to) {
        if (mesh.Chunks.Count != 0) { return SdfMeshChunks.Join(chunks: mesh.Chunks.Select(selector: chunk => Renumber(from: from, mesh: chunk, to: to)).ToArray()); }
        var levels = new SdfMeshLevel[mesh.Levels.Count];

        for (var level = 0; (level < levels.Length); level++) {
            levels[level] = (mesh.Levels[level] with { TriangleMaterials = Renumbered(from: from, source: mesh.Levels[level].TriangleMaterials.Span, to: to) });
        }

        return new SdfMesh(indices: mesh.Indices, members: mesh.Members, normals: mesh.Normals, positions: mesh.Positions, triangleMaterials: Renumbered(from: from, source: mesh.TriangleMaterials.Span, to: to), vertexMembers: mesh.VertexMembers) { BakeCell = mesh.BakeCell, Levels = levels };
    }
    private static uint[] Renumbered(ReadOnlySpan<uint> source, int[] from, int[] to) {
        var renumbered = new uint[source.Length];

        for (var triangle = 0; (triangle < source.Length); triangle++) {
            var place = Array.IndexOf(array: from, value: ((int)source[triangle]));

            renumbered[triangle] = (((place >= 0) && (place < to.Length)) ? (uint)to[place] : source[triangle]);
        }

        return renumbered;
    }

    private sealed record Variant(int[] Materials, SdfMesh Mesh);
    private sealed record StoredMesh(bool Found, SdfMesh? Mesh, int[] Materials, string? Refusal);
}
