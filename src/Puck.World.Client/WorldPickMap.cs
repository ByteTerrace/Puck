using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>The placement or body a rendered instance stands for. Presentation-only; a pick never grants authority
/// to edit or move this identity.</summary>
/// <param name="Placement">The authored placement id, or null for a body-rooted stamp.</param>
/// <param name="BodyIndex">The stamped body index, or null for a placement.</param>
public sealed record WorldPickTarget(string? Placement, int? BodyIndex);
/// <summary>Builds the host lookup alongside program emission and publishes immutable snapshots for delayed GPU
/// picks. Several SDF ordinals can name one placement, and meshes use their own draw ordinal table.</summary>
public sealed class WorldPickMapBuilder {
    private readonly Dictionary<int, WorldPickTarget> m_instances = [];
    private readonly List<WorldPickTarget?> m_meshes = [];

    private WorldPickMap? m_snapshot;
    private IReadOnlyList<WorldPickTarget?>? m_pool;

    /// <summary>Starts a program rebuild; previously published snapshots remain valid.</summary>
    public void Clear() {
        m_instances.Clear();
        m_meshes.Clear();
        m_snapshot = null;
    }
    /// <summary>Names the half-open SDF instance range emitted for one placement or stamped body.</summary>
    /// <param name="first">The first ordinal.</param>
    /// <param name="end">The exclusive end ordinal.</param>
    /// <param name="target">The host identity.</param>
    public void Instances(int first, int end, WorldPickTarget target) {
        for (var index = first; (index < end); index++) {
            m_instances[index] = target;
        }
        m_snapshot = null;
    }
    /// <summary>Names the half-open static mesh draw range emitted for one placement.</summary>
    /// <param name="first">The first draw ordinal.</param>
    /// <param name="end">The exclusive end ordinal.</param>
    /// <param name="target">The placement identity.</param>
    public void Meshes(int first, int end, WorldPickTarget target) {
        while (m_meshes.Count < end) {
            m_meshes.Add(item: null);
        }
        for (var index = first; (index < end); index++) {
            m_meshes[index] = target;
        }
        m_snapshot = null;
    }
    /// <summary>Returns the immutable table of this program's SDF instances and its static then pooled mesh draws.
    /// A pool retains the list reference while only poses change, so those frames allocate nothing here.</summary>
    /// <param name="pool">The immutable pool mesh identities in draw order.</param>
    /// <returns>The captured lookup.</returns>
    public ISdfPickMap Snapshot(IReadOnlyList<WorldPickTarget?> pool) {
        if ((m_snapshot is null) || !ReferenceEquals(objA: m_pool, objB: pool)) {
            m_pool = pool;
            m_snapshot = new WorldPickMap(instances: new Dictionary<int, WorldPickTarget>(dictionary: m_instances),
                meshes: [.. m_meshes, .. pool]);
        }
        return m_snapshot;
    }

    private sealed class WorldPickMap(Dictionary<int, WorldPickTarget> instances, WorldPickTarget?[] meshes) : ISdfPickMap {
        public object? Resolve(uint identity) {
            var source = identity & 0x3FFFFFFF;

            return (identity >> 30) switch {
                1 => instances.GetValueOrDefault(key: (((int)source) - 1)),
                2 when (source < meshes.Length) => meshes[source],
                _ => null,
            };
        }
    }
}
