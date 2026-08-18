using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>
/// Wraps this world's own compiled <see cref="IContactField"/> so a body standing in a direct or compiler-derived
/// corner adjacency overlap gets ground from the projected neighbour's own solid geometry when this world's own
/// field has none there.
/// Rendering's own composition
/// (<c>Puck.World.Client.WorldAdjacencySceneEmitter</c>) draws the same neighbour geometry through the same
/// isometry, so what a body stands on and what it sees agree.
/// </summary>
/// <remarks>
/// <para><b>The isometry.</b> A query position is mapped into the neighbour's own local frame, and the neighbour's
/// answer is mapped back, through <see cref="WorldFrameIsometry"/> — the exact same isometry a crossing traveler's
/// arrival uses, anchored at the two boundaries' own frames. The overlap serves every point near the boundary. Pure
/// fixed-point throughout; no wall-clock, RNG, or float ever reaches this decision.</para>
/// <para><b>Composition, not replacement.</b> This world's own field resolves first, exactly as it would with no
/// adjacency at all — an overlap is consulted only when the body is not already grounded and its position
/// falls inside one, so a world whose own geometry already reaches the border pays nothing extra and behaves
/// identically to a world with no adjacency at all.</para>
/// <para><b>Replay boundary.</b> Neighbour poses presently arrive as presentation-float snapshot fields and are
/// converted to fixed point below at delivery timing. Ground and dynamic-contact correction are therefore not yet
/// replay-deterministic across network schedules. Track 3 replaces this input with tick-addressed taped neighbour
/// records; until that transport exists, this conversion is the named nondeterministic boundary rather than an
/// implied simulation guarantee.</para>
/// </remarks>
internal sealed class WorldAdjacencyContactField : IEntityContactField {
    private readonly IContactField m_inner;
    private readonly IWorldAdjacencySource m_source;

    /// <summary>Initializes the wrapper.</summary>
    /// <param name="inner">This world's own compiled contact field.</param>
    /// <param name="source">The injected neighbour resolver.</param>
    public WorldAdjacencyContactField(IContactField inner, IWorldAdjacencySource source) {
        ArgumentNullException.ThrowIfNull(argument: inner);
        ArgumentNullException.ThrowIfNull(argument: source);

        m_inner = inner;
        m_source = source;
    }

    /// <inheritdoc/>
    public WorldContactCensus Census => m_inner.Census;

    private static FixedVector3 MapIntoNeighbour(FixedVector3 value, IReadOnlyList<WorldAdjacencyFramePair> path) {
        for (var stageIndex = (path.Count - 1); (stageIndex >= 0); stageIndex--) {
            var stage = path[stageIndex];

            value = WorldFrameIsometry.MapPoint(
                point: value,
                source: stage.Source,
                destination: stage.Neighbour
            );
        }
        return value;
    }
    private static FixedVector3 MapIntoSource(FixedVector3 value, IReadOnlyList<WorldAdjacencyFramePair> path) {
        foreach (var stage in path) {
            value = WorldFrameIsometry.MapPoint(
                point: value,
                source: stage.Neighbour,
                destination: stage.Source
            );
        }
        return value;
    }
    private static FixedQuaternion MapOrientationIntoNeighbour(FixedQuaternion value, IReadOnlyList<WorldAdjacencyFramePair> path) {
        for (var stageIndex = (path.Count - 1); (stageIndex >= 0); stageIndex--) {
            var stage = path[stageIndex];

            value = (WorldFrameIsometry.Rotation(
                source: stage.Source,
                destination: stage.Neighbour
            ) * value).Normalize();
        }
        return value;
    }
    private static FixedVector3 MapVectorIntoNeighbour(FixedVector3 value, IReadOnlyList<WorldAdjacencyFramePair> path) {
        for (var stageIndex = (path.Count - 1); (stageIndex >= 0); stageIndex--) {
            var stage = path[stageIndex];

            value = WorldFrameIsometry.MapVector(
                value: value,
                source: stage.Source,
                destination: stage.Neighbour
            );
        }
        return value;
    }
    private static FixedVector3 MapVectorIntoSource(FixedVector3 value, IReadOnlyList<WorldAdjacencyFramePair> path) {
        foreach (var stage in path) {
            value = WorldFrameIsometry.MapVector(
                value: value,
                source: stage.Neighbour,
                destination: stage.Source
            );
        }
        return value;
    }
    private ContactResolution ResolveCore(int entityIndex, in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        var resolution = m_inner.ResolveSweep(
            orientation: in orientation,
            position: ref position,
            previousPosition: previousPosition,
            velocity: ref velocity,
            volumes: volumes
        );

        foreach (var projection in m_source.Visuals()) {
            if (!TryMapIntoNeighbour(
                mapped: out var neighbourPosition,
                position: position,
                projection: projection
            )) {
                continue;
            }

            var neighbour = projection.Neighbour;
            var neighbourPreviousPosition = MapIntoNeighbour(
                value: previousPosition,
                path: projection.Path
            );
            var neighbourVelocity = MapVectorIntoNeighbour(
                value: velocity,
                path: projection.Path
            );
            var neighbourOrientation = MapOrientationIntoNeighbour(
                value: orientation,
                path: projection.Path
            );

            var dynamicObstruction = FixedVector3.Zero;
            var localAddress = ((entityIndex >= 0)
                ? m_source.LocalEntityAddress(index: entityIndex)
                : default
            );
            var localIsSolid = ((entityIndex >= 0) && (m_source.LocalBodyContact(index: entityIndex) == WorldBodyContactMode.Solid));

            for (var entity = 0; (localIsSolid && (entity < neighbour.EntityCapacity)); entity++) {
                if (
                    !neighbour.IsEntityActive(index: entity) ||
                    (neighbour.Collider(index: entity) is not { } neighbourCollider) ||
                    (neighbour.BodyContact(index: entity) != WorldBodyContactMode.Solid) ||
                    (entityIndex < 0) ||
                    !WorldCrossAuthoritySettlement.LocalResponds(
                    local: in localAddress,
                    remote: neighbour.EntityAddress(index: entity),
                    interaction: "physical-contact"
                )
                ) {
                    continue;
                }

                if (!FixedDynamicBodyContacts.TryCorrection(
                    leftPosition: neighbourPosition,
                    leftOrientation: neighbourOrientation,
                    leftVolumes: volumes,
                    rightPosition: FixedVector3.FromVector3(value: neighbour.CurrentPosition(index: entity)),
                    rightOrientation: FixedQuaternion.FromQuaternion(value: neighbour.CurrentOrientation(index: entity)).Normalize(),
                    rightVolumes: neighbourCollider.Volumes,
                    tieBreaker: entity,
                    correction: out var correction
                )) {
                    continue;
                }

                neighbourPosition += correction;
                var normal = correction.Normalize();
                var inward = FixedVector3.Dot(
                    left: neighbourVelocity,
                    right: normal
                );

                if (inward < FixedQ4816.Zero) {
                    neighbourVelocity -= (normal * inward);
                }
                dynamicObstruction = normal;
            }

            var neighbourResolution = default(ContactResolution);

            if (
                !resolution.Grounded &&
                (neighbour is IWorldAdjacencyNeighbourContact contactNeighbour) &&
                contactNeighbour.TryGetSolidField(
                field: out var neighbourField,
                reason: out _
            ) &&
                (neighbourField is not null)
            ) {
                neighbourResolution = neighbourField.ResolveSweep(
                    orientation: in neighbourOrientation,
                    position: ref neighbourPosition,
                    previousPosition: neighbourPreviousPosition,
                    velocity: ref neighbourVelocity,
                    volumes: volumes
                );
            }

            if (
                (dynamicObstruction == FixedVector3.Zero) &&
                !neighbourResolution.Grounded &&
                (neighbourResolution.ObstructionNormal == FixedVector3.Zero)
            ) {
                // Nothing on the neighbour's side either — try the next band (a body straddling a corner post could
                // sit inside two bands' extents at once) rather than committing an inert round trip.
                continue;
            }
            // Map the projected neighbour's depenetrated answer through every forward stage into this authority.
            position = MapIntoSource(
                value: neighbourPosition,
                path: projection.Path
            );
            velocity = MapVectorIntoSource(
                value: neighbourVelocity,
                path: projection.Path
            );

            var neighbourObstruction = ((neighbourResolution.ObstructionNormal != FixedVector3.Zero)
                ? neighbourResolution.ObstructionNormal
                : dynamicObstruction
            );

            return new ContactResolution(
                Grounded: (resolution.Grounded || neighbourResolution.Grounded),
                ObstructionNormal: ((neighbourObstruction == FixedVector3.Zero)
                ? resolution.ObstructionNormal
                : MapVectorIntoSource(
                        value: neighbourObstruction,
                        path: projection.Path
                    ))
            );
        }

        return resolution;
    }
    // Stage 0 is the hop that reads this projection's neighbour geometry; every earlier index is transport toward it.
    private static bool TryMapIntoNeighbour(FixedVector3 position, WorldAdjacencyProjection projection, out FixedVector3 mapped) {
        mapped = position;
        for (var stageIndex = (projection.Path.Count - 1); (stageIndex >= 0); stageIndex--) {
            var stage = projection.Path[stageIndex];
            var band = new WorldAdjacencyBand(
                Name: projection.Name,
                Frame: stage.Source
            );
            var admitted = ((stageIndex == 0)
                ? band.Contains(
                    position: mapped,
                    depth: stage.OverlapDepth,
                    ownershipThreshold: stage.OwnershipThreshold
                )
                : band.Transits(
                    position: mapped,
                    depth: stage.OverlapDepth
                )
            );

            if (!admitted) {
                mapped = default;
                return false;
            }

            mapped = WorldFrameIsometry.MapPoint(
                point: mapped,
                source: stage.Source,
                destination: stage.Neighbour
            );
        }
        return true;
    }

    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        return ResolveCore(
            entityIndex: -1,
            orientation: in orientation,
            position: ref position,
            previousPosition: position,
            velocity: ref velocity,
            volumes: volumes
        );
    }
    /// <inheritdoc/>
    public ContactResolution ResolveEntity(int entityIndex, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        return ResolveCore(
            entityIndex: entityIndex,
            orientation: in orientation,
            position: ref position,
            previousPosition: position,
            velocity: ref velocity,
            volumes: volumes
        );
    }
    /// <inheritdoc/>
    public ContactResolution ResolveEntitySweep(int entityIndex, in FixedVector3 previousPosition, ref FixedVector3 position,
        ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) =>
        ResolveCore(
            entityIndex: entityIndex,
            orientation: in orientation,
            position: ref position,
            previousPosition: previousPosition,
            velocity: ref velocity,
            volumes: volumes
        );
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) =>
        ResolveCore(
            entityIndex: -1,
            orientation: in orientation,
            position: ref position,
            previousPosition: previousPosition,
            velocity: ref velocity,
            volumes: volumes
        );
    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) => m_inner.TryUp(
        position: in position,
        up: out up
    );

}
