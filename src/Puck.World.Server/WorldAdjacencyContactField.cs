using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// Wraps this world's own compiled <see cref="IContactField"/> so a body standing in a compiler-derived adjacency
/// overlap gets ground from the neighbour's own solid geometry when this world's own field has none there.
/// Rendering's own composition
/// (<c>Puck.World.Client.WorldAdjacencySceneEmitter</c>) draws the same neighbour geometry through the same
/// isometry, so what a body stands on and what it sees agree.
/// </summary>
/// <remarks>
/// <para><b>The isometry.</b> A query position is mapped into the neighbour's own local frame, and the neighbour's
/// answer is mapped back, through <c>Server.WorldPortalArrivalMath.ComputeArrival</c> — the exact same isometry a
/// crossing traveler's arrival uses, anchored at the two boundaries' own frames rather than one body's swept seam.
/// The overlap serves every point near the boundary. Pure fixed-point
/// throughout; no wall-clock, RNG, or float ever reaches this decision.</para>
/// <para><b>Composition, not replacement.</b> This world's own field resolves first, exactly as it would with no
/// adjacency at all — an overlap is consulted only when the body is not already grounded and its position
/// falls inside one, so a world whose own geometry already reaches the border pays nothing extra and behaves
/// identically to a world with no adjacency at all.</para>
/// </remarks>
internal sealed class WorldAdjacencyContactField : IEntityContactField {
    private static readonly FixedVector3 s_upAxis = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);

    private readonly IContactField m_inner;
    private readonly WorldDefinition m_definition;
    private readonly IReadOnlyList<WorldAdjacencyBand> m_bands;
    private readonly IWorldAdjacencySource m_source;

    /// <summary>Initializes the wrapper.</summary>
    /// <param name="definition">This authority's live definition.</param>
    /// <param name="inner">This world's own compiled contact field.</param>
    /// <param name="bands">Every adjacency band this definition authors.</param>
    /// <param name="source">The injected neighbour resolver.</param>
    public WorldAdjacencyContactField(WorldDefinition definition, IContactField inner, IReadOnlyList<WorldAdjacencyBand> bands, IWorldAdjacencySource source) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: inner);
        ArgumentNullException.ThrowIfNull(argument: bands);
        ArgumentNullException.ThrowIfNull(argument: source);

        m_definition = definition;
        m_inner = inner;
        m_bands = bands;
        m_source = source;
    }

    /// <inheritdoc/>
    public WorldContactCensus Census => m_inner.Census;

    /// <inheritdoc/>
    public bool TryUp(in FixedVector3 position, out FixedVector3 up) => m_inner.TryUp(position: in position, up: out up);

    /// <inheritdoc/>
    public ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        return ResolveCore(entityIndex: -1, position: ref position, velocity: ref velocity, orientation: in orientation, volumes: volumes);
    }

    /// <inheritdoc/>
    public ContactResolution ResolveEntity(int entityIndex, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        return ResolveCore(entityIndex: entityIndex, position: ref position, velocity: ref velocity, orientation: in orientation, volumes: volumes);
    }

    private ContactResolution ResolveCore(int entityIndex, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        var resolution = m_inner.Resolve(position: ref position, velocity: ref velocity, orientation: in orientation, volumes: volumes);

        if (m_bands.Count == 0) {
            return resolution;
        }

        foreach (var band in m_bands) {
            if (!m_source.TryResolve(adjacencyName: band.Name, neighbour: out var neighbour) || (neighbour is null) ||
                !WorldAdjacencyPolicy.TryDeriveOverlap(local: m_definition, neighbour: neighbour.Definition, depth: out var depth, reason: out _) ||
                !band.Contains(position: position, depth: depth)) {
                continue;
            }

            var neighbourFrame = neighbour.CounterpartFrame;
            // deltaYaw alone: feeding a zero traveler yaw makes the returned YawRadians exactly the isometry's own
            // delta, which both maps a full 3D orientation (below) and inverts cleanly by swapping source/destination.
            var toNeighbour = WorldPortalArrivalMath.ComputeArrival(
                travelerPosition: position,
                travelerYawRadians: FixedQ4816.Zero,
                travelerPlanarVelocity: new FixedVector3(X: velocity.X, Y: FixedQ4816.Zero, Z: velocity.Z),
                travelerVerticalVelocity: velocity.Y,
                sourcePosition: band.Frame.Origin,
                sourceYawRadians: band.Frame.PlanarYawRadians,
                destinationPosition: neighbourFrame.Origin,
                destinationYawRadians: neighbourFrame.PlanarYawRadians
            );
            var deltaRotation = FixedQuaternion.FromAxisAngle(axis: s_upAxis, angle: toNeighbour.YawRadians);
            var neighbourPosition = toNeighbour.Position;
            var neighbourVelocity = new FixedVector3(X: toNeighbour.PlanarVelocity.X, Y: toNeighbour.VerticalVelocity, Z: toNeighbour.PlanarVelocity.Z);
            var neighbourOrientation = (deltaRotation * orientation).Normalize();

            var dynamicObstruction = FixedVector3.Zero;
            var localAddress = ((entityIndex >= 0) ? m_source.LocalEntityAddress(index: entityIndex) : default);
            for (var entity = 0; entity < neighbour.EntityCapacity; entity++) {
                if (!neighbour.IsEntityActive(index: entity) || (neighbour.Collider(index: entity) is not { } neighbourCollider) ||
                    (entityIndex < 0) || !WorldCrossAuthoritySettlement.LocalResponds(local: in localAddress, remote: neighbour.EntityAddress(index: entity), interaction: "physical-contact")) {
                    continue;
                }

                if (!WorldDynamicBodyContacts.TryCorrection(
                    leftPosition: neighbourPosition,
                    leftOrientation: neighbourOrientation,
                    leftVolumes: volumes,
                    rightPosition: FixedVector3.FromVector3(value: neighbour.CurrentPosition(index: entity)),
                    rightOrientation: FixedQuaternion.FromQuaternion(value: neighbour.CurrentOrientation(index: entity)).Normalize(),
                    rightCollider: in neighbourCollider,
                    tieBreaker: entity,
                    correction: out var correction)) {
                    continue;
                }

                neighbourPosition += correction;
                var normal = correction.Normalize();
                var inward = FixedVector3.Dot(left: neighbourVelocity, right: normal);
                if (inward < FixedQ4816.Zero) {
                    neighbourVelocity -= (normal * inward);
                }
                dynamicObstruction = normal;
            }

            var neighbourResolution = default(ContactResolution);
            if (!resolution.Grounded && neighbour.TryGetSolidField(field: out var neighbourField, reason: out _) && (neighbourField is not null)) {
                neighbourResolution = neighbourField.Resolve(position: ref neighbourPosition, velocity: ref neighbourVelocity, orientation: in neighbourOrientation, volumes: volumes);
            }

            if ((dynamicObstruction == FixedVector3.Zero) && !neighbourResolution.Grounded && (neighbourResolution.ObstructionNormal == FixedVector3.Zero)) {
                // Nothing on the neighbour's side either — try the next band (a body straddling a corner post could
                // sit inside two bands' extents at once) rather than committing an inert round trip.
                continue;
            }
            // Map the neighbour's depenetrated answer back through the INVERSE isometry (source/destination
            // swapped — the same shape a return crossing's own arrival would compute).
            var back = WorldPortalArrivalMath.ComputeArrival(
                travelerPosition: neighbourPosition,
                travelerYawRadians: FixedQ4816.Zero,
                travelerPlanarVelocity: new FixedVector3(X: neighbourVelocity.X, Y: FixedQ4816.Zero, Z: neighbourVelocity.Z),
                travelerVerticalVelocity: neighbourVelocity.Y,
                sourcePosition: neighbourFrame.Origin,
                sourceYawRadians: neighbourFrame.PlanarYawRadians,
                destinationPosition: band.Frame.Origin,
                destinationYawRadians: band.Frame.PlanarYawRadians
            );
            var backRotation = FixedQuaternion.FromAxisAngle(axis: s_upAxis, angle: back.YawRadians);

            position = back.Position;
            velocity = new FixedVector3(X: back.PlanarVelocity.X, Y: back.VerticalVelocity, Z: back.PlanarVelocity.Z);

            var neighbourObstruction = ((neighbourResolution.ObstructionNormal != FixedVector3.Zero)
                ? neighbourResolution.ObstructionNormal
                : dynamicObstruction);
            return new ContactResolution(
                Grounded: (resolution.Grounded || neighbourResolution.Grounded),
                ObstructionNormal: ((neighbourObstruction == FixedVector3.Zero) ? resolution.ObstructionNormal : backRotation.Rotate(vector: neighbourObstruction))
            );
        }

        return resolution;
    }

}
