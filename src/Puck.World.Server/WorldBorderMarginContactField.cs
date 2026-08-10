using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// Wraps this world's own compiled <see cref="IContactField"/> so a body standing within a mapped portal facet's
/// authored margin gets ground from the neighbour's own solid geometry when this world's own field has none there —
/// the collision half of the border-margin strip. Rendering's own composition
/// (<c>Puck.World.Client.WorldBorderMarginSceneEmitter</c>) draws the same neighbour geometry through the same
/// isometry, so what a body stands on and what it sees agree.
/// </summary>
/// <remarks>
/// <para><b>The isometry.</b> A query position is mapped into the neighbour's own local frame, and the neighbour's
/// answer is mapped back, through <c>Server.WorldPortalArrivalMath.ComputeArrival</c> — the exact same isometry a
/// crossing traveler's arrival uses, anchored at the two faces' own frames rather than a crossing's swept seam (a
/// margin strip serves every point near the border, not one traveler's own crossing point). Pure fixed-point
/// throughout; no wall-clock, RNG, or float ever reaches this decision.</para>
/// <para><b>Composition, not replacement.</b> This world's own field resolves first, exactly as it would with no
/// margin strip at all — a margin band is consulted only when the body is not already grounded and its position
/// falls inside one, so a world whose own geometry already reaches the border pays nothing extra and behaves
/// identically to a world with no margin strip at all.</para>
/// </remarks>
internal sealed class WorldBorderMarginContactField : IContactField {
    private static readonly FixedVector3 s_upAxis = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);

    private readonly IContactField m_inner;
    private readonly IReadOnlyList<WorldBorderMarginBand> m_bands;
    private readonly IWorldBorderMarginSource m_source;

    /// <summary>Initializes the wrapper.</summary>
    /// <param name="inner">This world's own compiled contact field.</param>
    /// <param name="bands">Every mapped portal facet's margin band this definition authors.</param>
    /// <param name="source">The injected neighbour resolver.</param>
    public WorldBorderMarginContactField(IContactField inner, IReadOnlyList<WorldBorderMarginBand> bands, IWorldBorderMarginSource source) {
        ArgumentNullException.ThrowIfNull(argument: inner);
        ArgumentNullException.ThrowIfNull(argument: bands);
        ArgumentNullException.ThrowIfNull(argument: source);

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
        var resolution = m_inner.Resolve(position: ref position, velocity: ref velocity, orientation: in orientation, volumes: volumes);

        if (resolution.Grounded || (m_bands.Count == 0)) {
            return resolution;
        }

        foreach (var band in m_bands) {
            if (!band.Contains(position: position)) {
                continue;
            }

            if (!m_source.TryResolve(placementId: band.PlacementId, faceName: band.FaceName, neighbour: out var neighbour) || (neighbour is null) ||
                !neighbour.TryGetSolidField(field: out var neighbourField, reason: out _) || (neighbourField is null)) {
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

            var neighbourResolution = neighbourField.Resolve(position: ref neighbourPosition, velocity: ref neighbourVelocity, orientation: in neighbourOrientation, volumes: volumes);

            if (!neighbourResolution.Grounded && (neighbourResolution.ObstructionNormal == FixedVector3.Zero)) {
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

            return new ContactResolution(
                Grounded: neighbourResolution.Grounded,
                ObstructionNormal: (neighbourResolution.Grounded ? FixedVector3.Zero : backRotation.Rotate(vector: neighbourResolution.ObstructionNormal))
            );
        }

        return resolution;
    }
}
