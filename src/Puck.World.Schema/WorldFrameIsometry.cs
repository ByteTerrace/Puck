using Puck.Maths;

namespace Puck.World;

/// <summary>One mapped arrival: a traveler's pose and velocity at the source, mapped into the destination's frame.</summary>
/// <param name="Position">The mapped world position.</param>
/// <param name="YawRadians">The mapped world yaw, fixed-point radians. Unwrapped: the traveler's own accumulator
/// plus one boundary delta, so a crossing never discards accumulated turns.</param>
/// <param name="PlanarVelocity">The mapped planar velocity.</param>
/// <param name="VerticalVelocity">The mapped vertical velocity.</param>
public readonly record struct WorldFrameArrival(FixedVector3 Position, FixedQ4816 YawRadians, FixedVector3 PlanarVelocity, FixedQ4816 VerticalVelocity);
/// <summary>The fixed-point frame isometry shared by handoff, adjacency rendering/contact, and compiler topology
/// proofs. A boundary mapping reverses the local right and normal axes while preserving local up; keeping it here prevents the
/// validator and runtime from growing merely similar transform math.</summary>
/// <remarks>
/// <para>A reciprocal pair's composed rotation preserves world up — the validator refuses a pair that does not — so
/// the map is always a yaw rotation about world up composed with the two frames' translation, whatever the
/// boundaries' own pitch. Walking out of the source face along its outward normal walks IN through the destination
/// face, which is the half turn <see cref="MapVector"/> folds in.</para>
/// <para>The turn that half turn leaves is <c>destinationYaw - sourceYaw - 180</c> about world up, where each yaw is
/// the face's own authored outward heading: two faces pointing at each other are 180 degrees apart and map as the
/// identity, and every other authored pair turns the traveler by the remainder. A boundary's right axis is its yaw's
/// horizontal right whatever its pitch, so the formula holds unchanged for a face lying flat, where the heading no
/// longer participates in the outward direction and becomes the rectangle's roll about the vertical: an untwisted
/// floor seam authors its two yaws 180 degrees apart exactly as a wall pair does, and a flat pair authored at one
/// yaw hands the traveler a half turn.</para>
/// <para>Arrival anchors on the frames' own origins, not on the traveler's swept seam point. The two are the same
/// map: a source seam at in-plane <c>(u, v)</c> maps to the counterpart's <c>(-u, v)</c> by
/// <see cref="MapVector"/> applied to <see cref="WorldFaceFrame.Right"/> and <see cref="WorldFaceFrame.Up"/>, so an
/// off-center crossing lands at its counterpart point BY the isometry and needs no seam carried beside it.</para>
/// </remarks>
public static class WorldFrameIsometry {
    private static readonly FixedVector3 Forward = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.One
    );

    /// <summary>Maps a traveler's whole kinematic state through a reciprocal boundary pair — the one arrival
    /// function portal furniture and invisible adjacency borders both use.</summary>
    /// <param name="travelerPosition">The traveler's captured world position at the source, fixed point.</param>
    /// <param name="travelerYawRadians">The traveler's captured world yaw at the source, fixed-point radians.</param>
    /// <param name="travelerPlanarVelocity">The traveler's captured planar velocity at the source.</param>
    /// <param name="travelerVerticalVelocity">The traveler's captured vertical velocity at the source.</param>
    /// <param name="source">The source boundary frame.</param>
    /// <param name="destination">The destination counterpart's boundary frame.</param>
    /// <returns>The mapped arrival.</returns>
    public static WorldFrameArrival MapArrival(
        FixedVector3 travelerPosition,
        FixedQ4816 travelerYawRadians,
        FixedVector3 travelerPlanarVelocity,
        FixedQ4816 travelerVerticalVelocity,
        in WorldFaceFrame source,
        in WorldFaceFrame destination
    ) {
        var velocity = new FixedVector3(
            X: travelerPlanarVelocity.X,
            Y: travelerVerticalVelocity,
            Z: travelerPlanarVelocity.Z
        );
        var mappedVelocity = MapVector(
            destination: destination,
            source: source,
            value: velocity
        );

        return new WorldFrameArrival(
            Position: MapPoint(
                destination: destination,
                point: travelerPosition,
                source: source
            ),
            YawRadians: (travelerYawRadians + YawDelta(
                destination: destination,
                source: source
            )),
            PlanarVelocity: new FixedVector3(
                X: mappedVelocity.X,
                Y: FixedQ4816.Zero,
                Z: mappedVelocity.Z
            ),
            VerticalVelocity: mappedVelocity.Y
        );
    }
    public static FixedVector3 MapPoint(FixedVector3 point, in WorldFaceFrame source, in WorldFaceFrame destination) {
        return (destination.Origin + MapVector(
            value: (point - source.Origin),
            source: source,
            destination: destination
        ));
    }
    /// <summary>Maps a direction through a reciprocal boundary. The seam's local up is continuous while local right
    /// and outward normal reverse, yielding a proper rotation for arbitrarily oriented boundary planes.</summary>
    public static FixedVector3 MapVector(FixedVector3 value, in WorldFaceFrame source, in WorldFaceFrame destination) {
        var u = FixedVector3.Dot(
            left: value,
            right: source.Right
        );
        var v = FixedVector3.Dot(
            left: value,
            right: source.Up
        );
        var n = FixedVector3.Dot(
            left: value,
            right: source.Normal
        );

        return (((destination.Right * -u) + (destination.Up * v)) + (destination.Normal * -n));
    }
    /// <summary>Returns whether a boundary pair maps world up exactly onto itself, the condition every crossing needs:
    /// a traveler's yaw, planar velocity and vertical velocity cross it without loss only when it does. A wall pair
    /// does, and so does a floor face whose counterpart is a ceiling face (outward normals world up and world down),
    /// which maps a fall into the floor to a fall out of the ceiling.</summary>
    /// <param name="source">The source boundary frame.</param>
    /// <param name="destination">The destination counterpart's boundary frame.</param>
    /// <returns><see langword="true"/> when <see cref="MapVector"/> carries world <c>+Y</c> to exactly world
    /// <c>+Y</c>.</returns>
    public static bool PreservesWorldUp(in WorldFaceFrame source, in WorldFaceFrame destination) => (MapVector(
        destination: destination,
        source: source,
        value: FixedVector3.UnitY
    ) == FixedVector3.UnitY);
    /// <summary>Returns the unit rotation represented by <see cref="MapVector"/> for a pair that maps world up onto
    /// itself (<see cref="PreservesWorldUp"/>, which every crossing and adjacency pair is held to): its turn about up,
    /// <see cref="YawDelta"/>. Built from that one angle rather than by chaining shortest arcs, whose axis is
    /// arbitrary for the half turns a floor and ceiling pair or a turned wall pair makes.</summary>
    /// <param name="source">The source boundary frame.</param>
    /// <param name="destination">The destination counterpart's boundary frame.</param>
    /// <returns>The rotation.</returns>
    public static FixedQuaternion Rotation(in WorldFaceFrame source, in WorldFaceFrame destination) => FixedQuaternion.FromAxisAngle(
        angle: YawDelta(
            destination: in destination,
            source: in source
        ),
        axis: FixedVector3.UnitY
    );
    /// <summary>Returns the boundary rotation's yaw about world up, in radians — the one heading delta every mapped
    /// arrival adds to the traveler's own yaw. The representative is reduced to <c>(-pi, pi]</c>; the traveler's own
    /// yaw is an unbounded accumulator, so adding it keeps the arrival heading continuous across a crossing.</summary>
    /// <param name="source">The source boundary frame.</param>
    /// <param name="destination">The destination counterpart's boundary frame.</param>
    /// <returns>The composed yaw delta, fixed-point radians.</returns>
    public static FixedQ4816 YawDelta(in WorldFaceFrame source, in WorldFaceFrame destination) {
        var mappedForward = MapVector(
            destination: destination,
            source: source,
            value: Forward
        );

        return FixedQ4816.Atan2(
            y: mappedForward.X,
            x: mappedForward.Z
        );
    }
    /// <summary>Returns the turn about world up that carries a traveler's heading from <paramref name="before"/> to
    /// <paramref name="after"/>, reduced onto one turn by <see cref="FixedQ4816.WrapAngle"/>, in <c>[-pi, pi]</c>: the
    /// one turn rule a crossing reports. A mapped arrival's turn is the turn between its departure and arrival yaws
    /// (<see cref="YawDelta"/>); the turn a route change carries is the turn between the traveler's accumulated arrival
    /// turns (<see cref="AccumulateTurn"/>) before and after it.</summary>
    /// <param name="before">The earlier heading or accumulated turn, fixed-point radians.</param>
    /// <param name="after">The later one.</param>
    /// <returns>The turn.</returns>
    public static FixedQ4816 TurnBetween(FixedQ4816 before, FixedQ4816 after) => FixedQ4816.WrapAngle(radians: (after - before));
    /// <summary>Returns a traveler's accumulated arrival turn after one more mapped arrival: the turns of every mapped
    /// arrival it has made, reduced by <see cref="FixedQ4816.WrapAngle"/> to <c>[-pi, pi]</c>. An observer that held the
    /// traveler at one accumulated turn turns its view by <see cref="TurnBetween"/> that and the next, however many
    /// arrivals lie between.</summary>
    /// <param name="travelTurn">The traveler's accumulated arrival turn before this arrival.</param>
    /// <param name="departureYaw">The traveler's yaw at departure, fixed-point radians.</param>
    /// <param name="arrivalYaw">The traveler's mapped yaw at arrival.</param>
    /// <returns>The accumulated arrival turn after this arrival.</returns>
    public static FixedQ4816 AccumulateTurn(FixedQ4816 travelTurn, FixedQ4816 departureYaw, FixedQ4816 arrivalYaw) => FixedQ4816.WrapAngle(radians: (travelTurn + TurnBetween(
        after: arrivalYaw,
        before: departureYaw
    )));
    /// <summary>Returns whether <paramref name="turn"/> is a reduced turn, in <c>[-pi, pi]</c>: what a wire or
    /// checkpoint reader accepts for an accumulated arrival turn.</summary>
    /// <param name="turn">The turn to check.</param>
    /// <returns><see langword="true"/> when the turn is reduced.</returns>
    public static bool IsTurn(FixedQ4816 turn) => ((turn >= -FixedQ4816.Pi) && (turn <= FixedQ4816.Pi));
}
