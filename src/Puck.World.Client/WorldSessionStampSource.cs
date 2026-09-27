using System.Numerics;
using Puck.Physics.Motion;
using Puck.SignedDistance.Queries;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// A session view's <see cref="IWorldStampSource"/>: the destination's <see cref="WorldSessionMirror"/> read as the
/// world a <see cref="WorldStampPool"/> roots its stamps on. A body's pose is the mirror's interpolated one, the same
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

    /// <inheritdoc/>
    public WorldDefinition Definition => m_mirror.Definition;
    /// <inheritdoc/>
    public WorldStateMirror StateMirror => m_mirror.FollowState();
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
    public WorldLook Look(int index) => m_mirror.Look(index: index);
    /// <inheritdoc/>
    public Quaternion Orientation(int index) => Quaternion.Lerp(
        amount: m_mirror.InterpolationAlpha,
        quaternion1: m_mirror.PreviousOrientation(index: index),
        quaternion2: m_mirror.CurrentOrientation(index: index)
    );
    /// <inheritdoc/>
    public string? PlacementId(int index) => m_mirror.PlacementId(index: index);
    /// <inheritdoc/>
    public int PoseEpoch(int index) => m_mirror.PoseEpoch(index: index);
    /// <inheritdoc/>
    public Vector3 Position(int index) => Vector3.Lerp(
        amount: m_mirror.InterpolationAlpha,
        value1: m_mirror.PreviousPosition(index: index),
        value2: m_mirror.CurrentPosition(index: index)
    );
    /// <inheritdoc/>
    public bool TryInhabitantBody(string placementId, out int index) {
        for (var candidate = 0; (candidate < WorldBodiesLimits.CapacityCeiling); candidate++) {
            if (
                m_mirror.IsActive(index: candidate) &&
                string.Equals(
                a: m_mirror.PlacementId(index: candidate),
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                index = candidate;

                return true;
            }
        }

        index = -1;

        return false;
    }
}
