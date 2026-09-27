using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// A scene's mesh draws (<see cref="SdfFrame.MeshDraws"/>): its static placements' draws, fixed by each static rebuild,
/// then its <see cref="WorldStampPool"/>'s (an animated, inhabited or attached stamp's mesh at its root this frame).
/// The local scene and a session view each compose theirs through one. The composition is recomposed only when the
/// static list is a new one or the pool rewrote its own; either passes through alone, and both are copied into one list
/// rewritten in place, so a moving stamp allocates nothing here.
/// </summary>
public sealed class WorldSceneMeshDraws {
    private readonly List<SdfMeshDraw> m_composed = [];
    private readonly WorldStampPool m_pool;

    private IReadOnlyList<SdfMeshDraw>? m_composedStatic;
    private long m_composedPoolRevision = -1L;
    private IReadOnlyList<SdfMeshDraw> m_draws = [];
    private long m_revision;

    /// <summary>Initializes a new instance of the <see cref="WorldSceneMeshDraws"/> class.</summary>
    /// <param name="pool">The scene's stamp pool.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    public WorldSceneMeshDraws(WorldStampPool pool) {
        ArgumentNullException.ThrowIfNull(argument: pool);

        m_pool = pool;
    }

    /// <summary>Gets the composed draws: the static draws, then the pool's.</summary>
    public IReadOnlyList<SdfMeshDraw> Draws {
        get {
            Compose();

            return m_draws;
        }
    }
    /// <summary>Gets the composition's revision, moved whenever <see cref="Draws"/> changed.</summary>
    public long Revision {
        get {
            Compose();

            return m_revision;
        }
    }
    /// <summary>Gets or sets the static placements' draws. A static rebuild replaces the list, never clears it, so a
    /// consumer that keys work on the list sees a new one exactly when the placements it came from moved.</summary>
    public IReadOnlyList<SdfMeshDraw> Static { get; set; } = [];

    private void Compose() {
        var pooled = m_pool.MeshDraws;
        var pooledRevision = m_pool.MeshDrawsRevision;
        var statics = Static;

        if (
            ReferenceEquals(objA: statics, objB: m_composedStatic) &&
            (pooledRevision == m_composedPoolRevision)
        ) {
            return;
        }

        m_composedStatic = statics;
        m_composedPoolRevision = pooledRevision;
        m_revision++;

        if (pooled.Count == 0) {
            m_draws = statics;

            return;
        }
        if (statics.Count == 0) {
            m_draws = pooled;

            return;
        }

        m_composed.Clear();

        for (var index = 0; (index < statics.Count); index++) {
            m_composed.Add(item: statics[index]);
        }
        for (var index = 0; (index < pooled.Count); index++) {
            m_composed.Add(item: pooled[index]);
        }

        m_draws = m_composed;
    }
}
