namespace Puck.SdfVm;

/// <summary>
/// The baked meshes every residency that holds the library shares, by the geometry each was baked from
/// (<see cref="SdfMeshGeometryBuilder"/>), so a body that crosses into another world takes the mesh its first world baked
/// instead of baking it again. A program numbers its palette as its emitters register materials, so one body's shapes
/// carry other material ids in another world's program; the geometry names each material by its place among the group's
/// own materials, and a mesh taken into a program whose group carries other ids has its triangle materials renumbered by
/// place.
/// <para>The library holds at most <see cref="Capacity"/> geometries and empties whole past that. Its members are
/// thread-safe: a group's mesh lands from the thread pool.</para>
/// </summary>
public sealed class SdfMeshLibrary {
    /// <summary>The most geometries the library holds before it empties.</summary>
    public const int Capacity = 4096;

    private readonly Lock m_gate = new();
    private readonly Dictionary<SdfMeshGeometry, List<Variant>> m_meshes = [];

    /// <summary>Gets how many geometries the library holds.</summary>
    public int Count {
        get {
            lock (m_gate) { return m_meshes.Count; }
        }
    }

    /// <summary>Stores a group's mesh, baked from its geometry, with the material ids its group's shapes carry in the order
    /// the geometry numbers them; a geometry already held keeps its mesh.</summary>
    /// <param name="geometry">The geometry the mesh was baked from.</param>
    /// <param name="mesh">The mesh, whose triangle materials are ids of <paramref name="materials"/> or ids no shape of the
    /// group carries.</param>
    /// <param name="materials">The group's material ids, by their place in the geometry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> or <paramref name="materials"/> is
    /// <see langword="null"/>.</exception>
    public void Store(SdfMeshGeometry geometry, SdfMesh mesh, int[] materials) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        ArgumentNullException.ThrowIfNull(argument: materials);

        lock (m_gate) {
            if (m_meshes.ContainsKey(key: geometry)) { return; }
            if (m_meshes.Count >= Capacity) { m_meshes.Clear(); }
            m_meshes.Add(key: geometry, value: [new Variant(Materials: materials, Mesh: mesh)]);
        }
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

    // The mesh with each triangle material that `from` holds at a place renumbered to what `to` holds there; one neither
    // list reaches keeps its id. The geometry's equality makes the two lists the same length.
    private static SdfMesh Renumber(SdfMesh mesh, int[] from, int[] to) {
        var source = mesh.TriangleMaterials.Span;
        var renumbered = new uint[source.Length];

        for (var triangle = 0; (triangle < source.Length); triangle++) {
            var place = Array.IndexOf(array: from, value: (int)source[triangle]);

            renumbered[triangle] = (((place >= 0) && (place < to.Length)) ? (uint)to[place] : source[triangle]);
        }

        return new SdfMesh(indices: mesh.Indices, normals: mesh.Normals, positions: mesh.Positions, triangleMaterials: renumbered);
    }

    private sealed record Variant(int[] Materials, SdfMesh Mesh);
}
