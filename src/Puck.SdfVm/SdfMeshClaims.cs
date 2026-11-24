using System.Runtime.InteropServices;

namespace Puck.SdfVm;

// One consumer's claims, deduplicated by mesh identity. This is a resource lifecycle, never an allocation registry.
// Every mesh it lists holds exactly one of its claims: a mesh is listed only once its claim was taken, and for as
// long as a draw names it.
internal sealed class SdfMeshClaims : IDisposable {
    // Each claimed mesh, with how many draws name it.
    private readonly Dictionary<SdfMesh, int> m_meshes = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_leaving = [];

    private readonly bool m_cached;

    public SdfMeshClaims(bool cached = false) { m_cached = cached; }
    public SdfMeshClaims(IEnumerable<SdfMesh> meshes, bool cached = false) : this(cached: cached) {
        try { foreach (var mesh in meshes) { Claim(mesh: mesh); } } catch { Dispose(); throw; }
    }

    public SdfMeshClaims Clone() => new(cached: m_cached, meshes: m_meshes.Keys);
    // The claimed meshes, each once, and how many there are.
    public Dictionary<SdfMesh, int>.KeyCollection Meshes => m_meshes.Keys;
    public int Count => m_meshes.Count;
    // Claims the meshes the draws name that it does not hold yet, then releases those the draws no longer name: a mesh
    // both lists name keeps its one claim, so a frame whose draws only move takes no claim and no lock.
    public void Update(IReadOnlyList<SdfMeshDraw> draws) {
        // A count written through its reference leaves the dictionary's version alone, so the walk stands.
        foreach (var mesh in m_meshes.Keys) { CollectionsMarshal.GetValueRefOrNullRef(dictionary: m_meshes, key: mesh) = 0; }
        for (var index = 0; (index < draws.Count); index++) { Claim(mesh: draws[index].Mesh); }
        foreach (var (mesh, named) in m_meshes) {
            if (named == 0) { m_leaving.Add(item: mesh); }
        }
        foreach (var mesh in m_leaving) {
            _ = m_meshes.Remove(key: mesh);
            mesh.Release(cached: m_cached);
        }
        m_leaving.Clear();
    }
    // Moves one draw's claim from the mesh it named to the mesh it names now: the new mesh is claimed before the
    // old one is let go, and the old one keeps its claim while another draw names it.
    public void Move(SdfMesh from, SdfMesh to) {
        if (ReferenceEquals(objA: from, objB: to)) { return; }
        Claim(mesh: to);

        Release(mesh: from);
    }
    public void Release(SdfMesh mesh) {
        ref var named = ref CollectionsMarshal.GetValueRefOrNullRef(dictionary: m_meshes, key: mesh);

        if (System.Runtime.CompilerServices.Unsafe.IsNullRef(source: ref named) || (--named > 0)) { return; }
        _ = m_meshes.Remove(key: mesh);
        mesh.Release(cached: m_cached);
    }
    public void Dispose() {
        foreach (var mesh in m_meshes.Keys) { mesh.Release(cached: m_cached); }
        m_meshes.Clear();
    }
    public void Claim(SdfMesh mesh) {
        ref var named = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_meshes, exists: out var exists, key: mesh);

        named++;
        if (exists) { return; }
        try {
            _ = mesh.Acquire(cached: m_cached);
        } catch {
            _ = m_meshes.Remove(key: mesh);
            throw;
        }
    }
}
