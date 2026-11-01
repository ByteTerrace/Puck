using System.Runtime.InteropServices;

namespace Puck.SdfVm;

// One consumer's claims, deduplicated by mesh identity. This is a resource lifecycle, never an allocation registry.
// Every mesh it lists holds exactly one of its claims: a mesh is listed only once its claim was taken.
internal sealed class SdfMeshClaims : IDisposable {
    // Each claimed mesh, with the latest Update that named it.
    private readonly Dictionary<SdfMesh, long> m_meshes = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<SdfMesh> m_leaving = [];
    private readonly bool m_cached;

    private long m_generation;

    public SdfMeshClaims(bool cached = false) { m_cached = cached; }
    public SdfMeshClaims(IEnumerable<SdfMesh> meshes, bool cached = false) : this(cached: cached) {
        try { foreach (var mesh in meshes) { Claim(mesh: mesh, generation: 0); } } catch { Dispose(); throw; }
    }

    public SdfMeshClaims Clone() => new(cached: m_cached, meshes: m_meshes.Keys);
    // Claims the meshes the draws name that it does not hold yet, then releases those the draws no longer name: a mesh
    // both lists name keeps its one claim, so a frame whose draws only move takes no claim and no lock.
    public void Update(IReadOnlyList<SdfMeshDraw> draws) {
        var generation = ++m_generation;

        for (var index = 0; (index < draws.Count); index++) { Claim(mesh: draws[index].Mesh, generation: generation); }
        foreach (var (mesh, named) in m_meshes) {
            if (named != generation) { m_leaving.Add(item: mesh); }
        }
        foreach (var mesh in m_leaving) {
            _ = m_meshes.Remove(key: mesh);
            mesh.Release(cached: m_cached);
        }
        m_leaving.Clear();
    }
    public void Dispose() {
        foreach (var mesh in m_meshes.Keys) { mesh.Release(cached: m_cached); }
        m_meshes.Clear();
    }

    private void Claim(SdfMesh mesh, long generation) {
        ref var named = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary: m_meshes, key: mesh, exists: out var exists);

        named = generation;
        if (exists) { return; }
        try {
            _ = mesh.Acquire(cached: m_cached);
        } catch {
            _ = m_meshes.Remove(key: mesh);
            throw;
        }
    }
}
