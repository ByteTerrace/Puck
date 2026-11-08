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
/// adjacency at all, and the neighbour then resolves from that answer. An overlap is consulted only for a position
/// inside one, and then only when this world's own field found no ground or the position is outward of the
/// boundary plane — where this world's terrain has ended and its field can only be answering with its own rim. A
/// body standing inside its own world therefore pays nothing extra and behaves identically to one in a world with
/// no adjacency at all.</para>
/// <para><b>Untrusted neighbours.</b> A neighbour's bodies and solid geometry move a local body only through
/// <see cref="WorldPopulation.AdmitAdjacencyResponse"/>, which bounds their combined displacement per body per tick by
/// that body's locally owned reach and keeps it, and the body's outward speed, behind every ownership face the body is
/// still owned behind: a crossing comes from the body's own motion. A query naming no local body takes nothing from a
/// neighbour. Body contacts are settled by one responder per authority pair
/// (<see cref="WorldCrossAuthoritySettlement"/>).</para>
/// <para><b>Replay boundary.</b> Neighbour poses presently arrive as presentation-float snapshot fields and are
/// converted to fixed point below at delivery timing. Ground and dynamic-contact correction are therefore not yet
/// replay-deterministic across network schedules. Track 3 replaces this input with tick-addressed taped neighbour
/// records; until that transport exists, this conversion is the named nondeterministic boundary rather than an
/// implied simulation guarantee.</para>
/// </remarks>
public sealed class WorldAdjacencyContactField : IEntityContactField {
    private readonly IContactField m_inner;
    private readonly IWorldAdjacencySource m_source;
    private readonly WorldPopulation m_population;

    /// <summary>Initializes the wrapper.</summary>
    /// <param name="inner">This world's own compiled contact field.</param>
    /// <param name="source">The injected neighbour resolver.</param>
    /// <param name="population">Owns the local envelopes and the correction budget shared across this tick's sweeps.</param>
    public WorldAdjacencyContactField(IContactField inner, IWorldAdjacencySource source, WorldPopulation population) {
        ArgumentNullException.ThrowIfNull(argument: inner);
        ArgumentNullException.ThrowIfNull(argument: source);

        m_inner = inner;
        m_source = source;
        m_population = population;
    }

    private ContactResolution ResolveCore(int entityIndex, in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up, FixedVector3? responseOrigin = null, WorldBody? carrier = null) {
        var entryVelocity = velocity;
        var intended = position;
        var resolution = m_inner.ResolveSweep(
            orientation: in orientation,
            position: ref position,
            previousPosition: previousPosition,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );

        // Only the local field may refuse the body's own move.
        if (resolution.Refusal != ContactRefusal.None) {
            return resolution;
        }
        // A query naming no local body (a probe) takes nothing from a neighbour: every answer that moves a body passes
        // the population's door for that body.
        if (entityIndex < 0) {
            return resolution;
        }
        if ((carrier is not null) && !resolution.Unproved) {
            carrier.ApplyCarryBlock(correction: position - intended,
                ceiling: m_population.EntryBody(index: entityIndex)!.RigidBoundingRadius);
        }
        // A descent selected by untrusted support is not the body's own motion. Unless the local landing grounds,
        // retain the local stationary answer as the baseline and admit the neighbour's entire landing below.
        var neighbourQuery = position;
        if ((responseOrigin is { } origin) && !resolution.Grounded) {
            neighbourQuery = intended;
            position = origin;
            velocity = entryVelocity;
            resolution = m_inner.ResolveSweep(previousPosition: in origin, position: ref position,
                velocity: ref velocity, orientation: in orientation, volumes: volumes, up: in up);
            if (resolution.Refusal != ContactRefusal.None) { return resolution; }
        }

        var localAddress = m_source.LocalEntityAddress(index: entityIndex);
        var localIsSolid = (m_source.LocalBodyContact(index: entityIndex) == WorldBodyContactMode.Solid);
        // The frozen record can predate a handoff committed after it was frozen, within the same tick, so the
        // arrival asks for itself: the entity still shown at the slot it departed from is this body.
        var departedFrom = default(Protocol.WorldEntityAddress);
        var hasDeparted = (localIsSolid && m_source.TryLocalDepartedFrom(
            departedFrom: out departedFrom,
            index: entityIndex
        ));

        foreach (var projection in m_source.Visuals()) {
            if (!TryMapIntoNeighbour(
                mapped: out var neighbourStart,
                position: neighbourQuery,
                projection: projection
            )) {
                continue;
            }

            var neighbour = projection.Neighbour;
            var neighbourPosition = neighbourStart;
            var neighbourPreviousPosition = WorldAdjacencyPath.MapPointIntoNeighbour(
                value: previousPosition,
                path: projection.Path
            );
            var neighbourVelocity = WorldAdjacencyPath.MapVectorIntoNeighbour(
                value: velocity,
                path: projection.Path
            );
            var neighbourOrientation = WorldAdjacencyPath.MapOrientationIntoNeighbour(
                value: orientation,
                path: projection.Path
            );

            var dynamicObstruction = FixedVector3.Zero;
            // One responder per authority pair settles every body contact between them, whichever bodies meet.
            var responds = (localIsSolid && WorldCrossAuthoritySettlement.LocalResponds(
                interaction: "physical-contact",
                localAuthority: localAddress.Authority,
                remoteAuthority: neighbour.Authority
            ));

            for (var entity = 0; (responds && (entity < neighbour.EntityCapacity)); entity++) {
                if (
                    !neighbour.IsEntityActive(index: entity) ||
                    (neighbour.Collider(index: entity) is not { } neighbourCollider) ||
                    (neighbour.BodyContact(index: entity) != WorldBodyContactMode.Solid)
                ) {
                    continue;
                }

                if (hasDeparted && (neighbour.EntityAddress(index: entity) == departedFrom)) {
                    continue;
                }

                var remotePosition = FixedVector3.FromVector3(value: neighbour.CurrentPosition(index: entity));

                if (!FixedDynamicBodyContacts.TryCorrection(
                    leftPosition: neighbourPosition,
                    leftOrientation: neighbourOrientation,
                    leftVolumes: volumes,
                    rightPosition: remotePosition,
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
            // Outward of this world's own boundary plane its terrain has ended by construction, so ground its field
            // still answers there is the rim of that terrain rather than the floor the seam continues — the
            // neighbour's geometry is what decides the vertical (WorldAdjacencyBand.Contains' own outward contract).
            // The local-most stage is the last one TryMapIntoNeighbour walks, so its source frame is the one this
            // position is expressed against.
            var localFrame = projection.Path[(projection.Path.Count - 1)].Source;
            var outward = (FixedVector3.Dot(
                left: (position - localFrame.Origin),
                right: localFrame.Normal
            ) > FixedQ4816.Zero);

            if (
                (!resolution.Grounded || outward) &&
                (neighbour is IWorldAdjacencyNeighbourContact contactNeighbour) &&
                contactNeighbour.TryGetSolidField(
                field: out var neighbourField,
                reason: out _
            ) &&
                (neighbourField is not null)
            ) {
                // The up axis is a DIRECTION in the local frame, so it crosses the seam through the same isometry the
                // orientation does. Handing the neighbour an unmapped axis makes its walkable test measure against the
                // wrong vertical and the body loses ground exactly where the seam hands over.
                var neighbourUp = WorldAdjacencyPath.MapVectorIntoNeighbour(
                    path: projection.Path,
                    value: up
                );

                neighbourResolution = neighbourField.ResolveSweep(
                    orientation: in neighbourOrientation,
                    position: ref neighbourPosition,
                    previousPosition: neighbourPreviousPosition,
                    up: in neighbourUp,
                    velocity: ref neighbourVelocity,
                    volumes: volumes
                );
            }

            if (neighbourResolution.Refusal != ContactRefusal.None) {
                // A refusal is an untrusted proposed rollback, not a local refusal. Its displacement and velocity
                // pass the same door as every other response, and it cannot mark the local step immovable.
                neighbourResolution = neighbourResolution with { Refusal = ContactRefusal.None };
            }

            if (
                (dynamicObstruction == FixedVector3.Zero) &&
                !neighbourResolution.Grounded &&
                (neighbourResolution.ObstructionNormal == FixedVector3.Zero) &&
                (neighbourPosition == neighbourStart) &&
                (neighbourVelocity == WorldAdjacencyPath.MapVectorIntoNeighbour(value: velocity, path: projection.Path))
            ) {
                // Nothing on the neighbour's side either — try the next band (a body straddling a corner post could
                // sit inside two bands' extents at once) rather than committing an inert round trip.
                continue;
            }
            // The neighbour's whole answer, its bodies' pushes and its geometry's resolution, crosses back as one
            // displacement and one velocity, mapped as vectors so a rotated seam's point round trip cannot round extra
            // displacement in, and moves this body only as far as the population's door admits.
            var proposedPosition = neighbourQuery +
                WorldAdjacencyPath.MapDisplacementIntoSource(value: (neighbourPosition - neighbourStart), path: projection.Path);
            var before = position;
            m_population.AdmitAdjacencyResponse(
                displacement: proposedPosition - position,
                entityIndex: entityIndex,
                overlapDepth: projection.OverlapDepth,
                position: ref position,
                proposedVelocity: velocity + WorldAdjacencyPath.MapDisplacementIntoSource(
                    value: neighbourVelocity - WorldAdjacencyPath.MapVectorIntoNeighbour(value: velocity, path: projection.Path), path: projection.Path),
                velocity: ref velocity
            );
            if ((carrier is not null) && !resolution.Unproved && !neighbourResolution.Unproved) {
                m_population.ApplyCarriedAdjacencyResponse(carrier: carrier, overlapDepth: projection.OverlapDepth,
                    displacement: position - before);
            }

            var neighbourObstruction = ((neighbourResolution.ObstructionNormal != FixedVector3.Zero)
                ? neighbourResolution.ObstructionNormal
                : dynamicObstruction
            );

            var accepted = (position == proposedPosition);
            var grounded = (accepted && neighbourResolution.Grounded);
            resolution = new ContactResolution(
                RequiredStepHeight: FixedQ4816.Max(x: resolution.RequiredStepHeight, y: neighbourResolution.RequiredStepHeight),
                // A neighbour's pairs index another authority's colliders, so its uncertainty stays unnamed here.
                Uncertainty: (neighbourResolution.Unproved ? resolution.Uncertainty.Union(other: ContactUncertainty.Unknown) : resolution.Uncertainty),
                Grounded: (resolution.Grounded || grounded),
                GroundNormal: (resolution.Grounded ? resolution.GroundNormal : grounded
                    ? WorldAdjacencyPath.MapVectorIntoSource(value: neighbourResolution.GroundNormal, path: projection.Path).Normalize()
                    : FixedVector3.Zero),
                ObstructionNormal: ((!accepted || (neighbourObstruction == FixedVector3.Zero))
                ? resolution.ObstructionNormal
                : WorldAdjacencyPath.MapVectorIntoSource(
                        value: neighbourObstruction,
                        path: projection.Path
                    ))
            );
            // Every projection resolves from the answer admitted so far; a phantom in the first band cannot hide
            // another band's floor. Support queries retain their original descent until one actually grounds.
            if ((responseOrigin is null) || resolution.Grounded) { neighbourQuery = position; }
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
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) {
        return ResolveCore(
            entityIndex: -1,
            orientation: in orientation,
            position: ref position,
            previousPosition: position,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    }
    /// <inheritdoc/>
    public ContactResolution ResolveEntitySweep(int entityIndex, in FixedVector3 previousPosition, ref FixedVector3 position,
        ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        ResolveCore(
            entityIndex: entityIndex,
            orientation: in orientation,
            position: ref position,
            previousPosition: previousPosition,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    /// <inheritdoc/>
    public ContactResolution ResolveEntitySupportSweep(int entityIndex, in FixedVector3 previousPosition, in FixedVector3 responseOrigin, ref FixedVector3 position,
        ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        ResolveCore(entityIndex: entityIndex, previousPosition: in previousPosition, position: ref position,
            velocity: ref velocity, orientation: in orientation, volumes: volumes, up: in up, responseOrigin: responseOrigin);
    /// <summary>Resolves a carried body's sweep and admits each neighbour's propagated block against its carrier.</summary>
    public ContactResolution ResolveCarriedSweep(int entityIndex, WorldBody carrier, in FixedVector3 previousPosition,
        ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation,
        ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        ResolveCore(entityIndex: entityIndex, previousPosition: in previousPosition, position: ref position,
            velocity: ref velocity, orientation: in orientation, volumes: volumes, up: in up, carrier: carrier);
    /// <inheritdoc/>
    public ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        ResolveCore(
            entityIndex: -1,
            orientation: in orientation,
            position: ref position,
            previousPosition: previousPosition,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    /// <inheritdoc/>
    /// <remarks>The least of this world's own support distance and that of every projected neighbour's solid field the
    /// foot point maps into, each asked through the projection's isometry, which keeps lengths. A neighbour's bodies are
    /// not asked: the landing sweep, which runs through this field, meets them.</remarks>
    public bool TrySupportDistance(in FixedVector3 position, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes,
        in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent) {
        var met = m_inner.TrySupportDistance(descent: out descent, orientation: in orientation, position: in position, reach: reach, up: in up, volumes: volumes);

        foreach (var projection in m_source.Visuals()) {
            if (
                !TryMapIntoNeighbour(mapped: out var neighbourPosition, position: position, projection: projection) ||
                (projection.Neighbour is not IWorldAdjacencyNeighbourContact contactNeighbour) ||
                !contactNeighbour.TryGetSolidField(field: out var neighbourField, reason: out _) ||
                (neighbourField is null) ||
                !neighbourField.TrySupportDistance(
                    descent: out var neighbourDescent,
                    orientation: WorldAdjacencyPath.MapOrientationIntoNeighbour(path: projection.Path, value: orientation),
                    position: in neighbourPosition,
                    reach: (met ? descent : reach),
                    up: WorldAdjacencyPath.MapVectorIntoNeighbour(path: projection.Path, value: up),
                    volumes: volumes
                ) ||
                (met && (neighbourDescent >= descent))
            ) {
                continue;
            }

            descent = neighbourDescent;
            met = true;
        }

        return met;
    }
    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) => m_inner.TryUp(
        position: in position,
        up: out up
    );
}
