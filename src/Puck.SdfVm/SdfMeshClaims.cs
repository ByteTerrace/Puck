namespace Puck.SdfVm;

// One consumer's claims, deduplicated by mesh identity. This is a resource lifecycle, never an allocation registry.
internal sealed class SdfMeshClaims : IDisposable {
    private readonly HashSet<SdfMesh> m_meshes = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly bool m_cached;

    public SdfMeshClaims(IEnumerable<SdfMesh> meshes, bool cached = false) {
        m_cached = cached;
        try { foreach (var mesh in meshes) { if (m_meshes.Add(item: mesh)) { mesh.Acquire(cached: cached); } } } catch { Dispose(); throw; }
    }

    public SdfMeshClaims Clone() => new(cached: m_cached, meshes: m_meshes);
    public void Dispose() { foreach (var mesh in m_meshes) { mesh.Release(cached: m_cached); } m_meshes.Clear(); }
}
