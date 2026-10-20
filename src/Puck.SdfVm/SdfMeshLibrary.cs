using Puck.Abstractions.Counting;

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
/// <para>The library holds at most <see cref="Capacity"/> geometries and empties whole past that. Its members are
/// thread-safe: a group's mesh lands from the thread pool.</para>
/// </summary>
/// <param name="store">The store the library keeps its meshes in between runs, or <see langword="null"/> to keep them for
/// the run alone.</param>
public sealed class SdfMeshLibrary(SdfMeshStore? store = null) {
    /// <summary>The most geometries the library holds before it empties.</summary>
    public const int Capacity = 4096;

    private readonly Lock m_gate = new();
    private readonly Dictionary<SdfMeshGeometry, List<Variant>> m_meshes = [];

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
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    public void Store(SdfMeshGeometry geometry, SdfMesh mesh, int[] materials, UInt128 identity = default) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        ArgumentNullException.ThrowIfNull(argument: materials);

        if (Hold(geometry: geometry, materials: materials, mesh: mesh)) {
            _ = Disk?.Write(geometry: geometry, identity: identity, materials: materials, mesh: mesh);
        }
    }
    /// <summary>Returns whether <see cref="Disk"/> holds a geometry's mesh under a bake's identity, without reading it.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The geometry.</param>
    /// <returns><see langword="true"/> when the store holds an entry for it.</returns>
    public bool Holds(UInt128 identity, SdfMeshGeometry geometry) => (Disk?.Contains(geometry: geometry, identity: identity) ?? false);
    /// <summary>Takes a geometry's mesh from <see cref="Disk"/> for a group whose shapes carry <paramref name="materials"/>,
    /// holding it for the run as <see cref="Store"/> would, then taking it as <see cref="TryTake"/> does.</summary>
    /// <param name="identity">The bake's identity.</param>
    /// <param name="geometry">The geometry.</param>
    /// <param name="materials">The taking group's material ids, by their place in the geometry.</param>
    /// <param name="mesh">The mesh, when this returns <see langword="true"/>.</param>
    /// <param name="refusal">Why the store refused the entry it held, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the store held a sound entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materials"/> is <see langword="null"/>.</exception>
    public bool TryLoad(UInt128 identity, SdfMeshGeometry geometry, int[] materials, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out SdfMesh? mesh, out string? refusal) {
        ArgumentNullException.ThrowIfNull(argument: materials);

        mesh = null;
        refusal = null;
        if ((Disk is null) || !Disk.TryRead(geometry: geometry, identity: identity, materials: out var stored, mesh: out var read, refusal: out refusal)) { return false; }
        _ = Hold(geometry: geometry, materials: stored, mesh: read);

        return TryTake(geometry: geometry, materials: materials, mesh: out mesh);
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
            if (!m_meshes.TryGetValue(key: geometry, value: out var variants)) {
                mesh = null;
                return false;
            }
            foreach (var variant in variants) {
                if (variant.Materials.AsSpan().SequenceEqual(other: materials)) {
                    mesh = variant.Mesh;
                    return true;
                }
            }

            mesh = Renumber(mesh: variants[0].Mesh, from: variants[0].Materials, to: materials);
            variants.Add(item: new Variant(Materials: materials, Mesh: mesh));

            return true;
        }
    }

    // Holds a geometry's first mesh for the run, emptying the library past its capacity; false when it held one already.
    private bool Hold(SdfMeshGeometry geometry, SdfMesh mesh, int[] materials) {
        lock (m_gate) {
            if (m_meshes.ContainsKey(key: geometry)) { return false; }
            if (m_meshes.Count >= Capacity) { m_meshes.Clear(); }
            m_meshes.Add(key: geometry, value: [new Variant(Materials: materials, Mesh: mesh)]);

            return true;
        }
    }
    // The mesh with each triangle material that `from` holds at a place renumbered to what `to` holds there; one neither
    // list reaches keeps its id. The geometry's equality makes the two lists the same length.
    private static SdfMesh Renumber(SdfMesh mesh, int[] from, int[] to) {
        var source = mesh.TriangleMaterials.Span;
        var renumbered = new uint[source.Length];

        for (var triangle = 0; (triangle < source.Length); triangle++) {
            var place = Array.IndexOf(array: from, value: (int)source[triangle]);

            renumbered[triangle] = (((place >= 0) && (place < to.Length)) ? (uint)to[place] : source[triangle]);
        }

        return new SdfMesh(indices: mesh.Indices, members: mesh.Members, normals: mesh.Normals, positions: mesh.Positions, triangleMaterials: renumbered, vertexMembers: mesh.VertexMembers);
    }

    private sealed record Variant(int[] Materials, SdfMesh Mesh);
}
