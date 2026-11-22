using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// A scene's mesh draws (<see cref="SdfFrame.MeshDraws"/>): its static placements' draws, fixed by each static rebuild,
/// then its <see cref="WorldCreationInstances"/>'s (an animated, inhabited or attached stamp's mesh at its root this frame).
/// The local scene and a session view each compose theirs through one. The composition moves only where the static
/// list is a new one or the pool's journal names a draw it rewrote (<see cref="SdfMeshDrawComposer"/>), so a pool whose
/// one draw changed copies and owes that draw alone, and a moving stamp, whose draw rides its root slot
/// (<see cref="SdfMeshDraw.PlacedBySlot"/>), changes nothing here.
/// </summary>
public sealed class WorldSceneMeshDraws {
    private readonly SdfMeshDrawComposer m_composer = new();
    private readonly WorldCreationInstances m_pool;

    /// <summary>Initializes a new instance of the <see cref="WorldSceneMeshDraws"/> class.</summary>
    /// <param name="pool">The scene's stamp pool.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    public WorldSceneMeshDraws(WorldCreationInstances pool) {
        ArgumentNullException.ThrowIfNull(argument: pool);

        m_pool = pool;
    }

    /// <summary>Gets the composed draws: the static draws, then the pool's.</summary>
    public IReadOnlyList<SdfMeshDraw> Draws => Compose().Draws;
    /// <summary>Gets the composition's revision, moved whenever <see cref="Draws"/> changed.</summary>
    public long Revision => Compose().Revision;
    /// <summary>Gets the journal of the composed rows that changed.</summary>
    public GpuRowChanges? Changes => Compose().Changes;
    /// <summary>Gets the journal endpoint captured with <see cref="Draws"/>.</summary>
    public long Serial => Compose().Serial;
    /// <summary>Gets or sets the static placements' draws. A static rebuild replaces the list, never clears it, so a
    /// consumer that keys work on the list sees a new one exactly when the placements it came from moved.</summary>
    public IReadOnlyList<SdfMeshDraw> Static { get; set; } = [];

    private SdfMeshDrawList Compose() {
        m_composer.Begin(parts: 2);
        m_composer.Add(changes: null, draws: Static, revision: 0L, serial: 0L);
        m_composer.Add(changes: m_pool.MeshDrawChanges, draws: m_pool.MeshDraws, revision: m_pool.MeshDrawsRevision, serial: m_pool.MeshDrawChangesSerial);

        return m_composer.End();
    }
}
