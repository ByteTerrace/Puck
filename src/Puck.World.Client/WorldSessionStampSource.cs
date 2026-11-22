using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Physics.Motion;
using Puck.SignedDistance.Queries;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// A session view's <see cref="IWorldStampSource"/>: the destination's <see cref="WorldSessionMirror"/> read as the
/// world a <see cref="WorldCreationInstances"/> roots its stamps on. A body's pose is the mirror's interpolated one, the same
/// blend <see cref="WorldSessionSceneEmitter"/> packs its avatars at (see
/// <see cref="WorldSessionMirror.InterpolationAlpha"/>), so a stamp riding a body stays on it.
/// </summary>
public sealed class WorldSessionStampSource : IWorldStampSource {
    private readonly WorldSessionMirror m_mirror;

    /// <summary>Initializes a new instance of the <see cref="WorldSessionStampSource"/> class.</summary>
    /// <param name="mirror">The destination's mirror.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public WorldSessionStampSource(WorldSessionMirror mirror) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        m_mirror = mirror;
    }

    /// <summary>Gets or sets what answers <see cref="SeatGuess"/>: the presenting client's guess for the world this source
    /// presents (<see cref="WorldClient.SeatGuessAt"/>), set only for the scene a seat may be presented in; a session
    /// screen's source leaves it <see langword="null"/> and offers none.</summary>
    public Func<IWorldSeatGuess?>? Guess { get; set; }
    /// <inheritdoc/>
    public IWorldSeatGuess? SeatGuess => Guess?.Invoke();
    /// <inheritdoc/>
    public WorldDefinition Definition => m_mirror.Definition;
    /// <inheritdoc/>
    public WorldStateMirror StateMirror => m_mirror.FollowState();

    /// <inheritdoc/>
    public float? SolidScale(string placementId) => m_mirror.SolidScale(placementId: placementId);

    /// <inheritdoc/>
    /// <remarks>A session mirrors no static solid field, so an effector finds no ground to plant on and holds its
    /// authored pose.</remarks>
    public SdfFieldEvaluator? StaticField => null;

    /// <inheritdoc/>
    public WorldEntityAddress EntityAddress(int index) => m_mirror.Address(index: index);
    /// <inheritdoc/>
    public BodyFacts Facts(int index) => m_mirror.Facts(index: index);
    /// <inheritdoc/>
    public bool IsActive(int index) => m_mirror.IsActive(index: index);
    /// <inheritdoc/>
    public void CopyActiveBodies(List<int> bodies) => m_mirror.CopyActiveBodies(bodies: bodies);

    // The source's body journal: its frames, its place in the mirror's delivery journal, the runs and moving bodies one
    // frame reads, and the interpolation alpha the last frame closed at.
    private readonly GpuChangedRows m_bodyChanges = new(stableRows: true);
    private readonly GpuRowCursor m_deliveries = new();
    private readonly GpuUploadRuns m_delivered = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly List<int> m_moving = [];
    private float m_alpha = float.NaN;

    /// <inheritdoc/>
    /// <remarks>A journal frame names the bodies the mirror's deliveries changed since the last frame
    /// (<see cref="WorldSessionMirror.CollectBodyChanges"/>), and, when the interpolation alpha moved, every body whose
    /// two endpoints differ (<see cref="WorldSessionMirror.CopyMovingBodies"/>), since its interpolated pose moved with
    /// it.</remarks>
    public GpuRowChanges BodyChanges => m_bodyChanges.Reader;
    /// <inheritdoc/>
    /// <remarks>Reading it closes a journal frame over whatever moved since the last read, so the first consumer of a
    /// frame publishes the changes and the rest read the same serial while nothing moves between them.</remarks>
    public long BodyChangesSerial {
        get {
            var whole = m_mirror.CollectBodyChanges(cursor: m_deliveries, into: m_delivered);
            var alpha = m_mirror.InterpolationAlpha;

            m_deliveries.Commit();
            m_moving.Clear();
            if (BitConverter.SingleToInt32Bits(value: alpha) != BitConverter.SingleToInt32Bits(value: m_alpha)) {
                m_alpha = alpha;
                m_mirror.CopyMovingBodies(bodies: m_moving);
            }
            if (whole || (m_delivered.Count > 0) || (m_moving.Count > 0)) {
                m_bodyChanges.Begin(everything: whole, rows: WorldClient.EntityCapacity);
                if (!whole) {
                    for (var run = 0; (run < m_delivered.Count); run++) {
                        m_bodyChanges.Owe(count: m_delivered.Length(index: run), start: m_delivered.Start(index: run));
                    }
                    foreach (var index in m_moving) {
                        m_bodyChanges.Owe(count: 1, start: index);
                    }
                }
            }

            return m_bodyChanges.Serial;
        }
    }
    /// <inheritdoc/>
    public WorldLook Look(int index) => m_mirror.Look(index: index);
    /// <inheritdoc/>
    public Quaternion Orientation(int index) => WorldPoseInterpolation.Orientation(
        alpha: m_mirror.InterpolationAlpha,
        previous: m_mirror.PreviousOrientation(index: index),
        current: m_mirror.CurrentOrientation(index: index)
    );
    /// <inheritdoc/>
    public string? PlacementId(int index) => m_mirror.PlacementId(index: index);
    /// <inheritdoc/>
    public int PoseEpoch(int index) => m_mirror.PoseEpoch(index: index);

    /// <inheritdoc/>
    public WorldPoseCarries Carries => m_mirror.Carries;

    /// <inheritdoc/>
    public Vector3 Position(int index) => WorldPoseInterpolation.Position(
        alpha: m_mirror.InterpolationAlpha,
        previous: m_mirror.PreviousPosition(index: index),
        current: m_mirror.CurrentPosition(index: index)
    );
    /// <inheritdoc/>
    public bool TryInhabitantBody(string placementId, out int index) => m_mirror.TryInhabitantBody(
        index: out index,
        placementId: placementId
    );
}
