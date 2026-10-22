using Puck.Maths;

namespace Puck.World;

/// <summary>One mapped arrival: a traveler's pose and velocity at the source, mapped into the destination's frame.</summary>
/// <param name="Position">The mapped world position.</param>
/// <param name="YawRadians">The mapped world yaw, fixed-point radians. Unwrapped: the traveler's own accumulator
/// plus one boundary delta, so a crossing never discards accumulated turns.</param>
/// <param name="PlanarVelocity">The mapped planar velocity.</param>
/// <param name="VerticalVelocity">The mapped vertical velocity.</param>
public readonly record struct WorldFrameArrival(FixedVector3 Position, FixedQ4816 YawRadians, FixedVector3 PlanarVelocity, FixedQ4816 VerticalVelocity);
/// <summary>The fixed-point frame map shared by handoff, adjacency rendering/contact, and compiler topology
/// proofs. A boundary mapping reverses the local right and normal axes while preserving local up; keeping it here prevents the
/// validator and runtime from growing merely similar transform math.</summary>
/// <remarks>
/// <para>The map is a similarity: a rotation, a uniform scale and a translation. The scale is the counterpart face's
/// size over the source face's (<see cref="Scale"/>), so a door paired with one a quarter its size shrinks what crosses
/// it to a quarter, and the pair back grows it again. Two faces of one size, which every adjacency pair is held to,
/// scale by exactly one and map as the rigid isometry. Points scale (<see cref="MapPoint"/>), rates scale
/// (<see cref="MapArrival"/>), and directions only turn (<see cref="MapVector"/>).</para>
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
        var mappedVelocity = MapRate(
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
    /// <summary>Maps a point through the pair: its offset from the source face turns with <see cref="MapVector"/>,
    /// scales by <see cref="Scale"/>, and lands at that offset from the counterpart face.</summary>
    /// <param name="point">The point, in the source world.</param>
    /// <param name="source">The source boundary frame.</param>
    /// <param name="destination">The destination counterpart's boundary frame.</param>
    /// <returns>The mapped point.</returns>
    public static FixedVector3 MapPoint(FixedVector3 point, in WorldFaceFrame source, in WorldFaceFrame destination) {
        return (destination.Origin + MapRate(
            value: (point - source.Origin),
            source: source,
            destination: destination
        ));
    }
    /// <summary>Maps a displacement or a rate (a velocity, an acceleration) through the pair: it turns with
    /// <see cref="MapVector"/> and scales by <see cref="Scale"/>, so a body a quarter its size moves a quarter as
    /// fast.</summary>
    /// <param name="value">The displacement or rate.</param>
    /// <param name="source">The source boundary frame.</param>
    /// <param name="destination">The destination counterpart's boundary frame.</param>
    /// <returns>The mapped displacement or rate.</returns>
    public static FixedVector3 MapRate(FixedVector3 value, in WorldFaceFrame source, in WorldFaceFrame destination) {
        var turned = MapVector(
            destination: destination,
            source: source,
            value: value
        );
        var scale = Scale(
            destination: destination,
            source: source
        );

        return ((scale == FixedQ4816.One)
            ? turned
            : (turned * scale)
        );
    }
    /// <summary>Returns the pair's uniform scale: the counterpart face's width over the source face's. Two faces of
    /// one width scale by exactly one.</summary>
    /// <param name="source">The source boundary frame.</param>
    /// <param name="destination">The destination counterpart's boundary frame.</param>
    /// <returns>The scale.</returns>
    public static FixedQ4816 Scale(in WorldFaceFrame source, in WorldFaceFrame destination) => ((destination.HalfWidth == source.HalfWidth)
        ? FixedQ4816.One
        : (destination.HalfWidth / source.HalfWidth)
    );
    /// <summary>Returns whether a traveller can cross the pair at its scale (<see cref="Scale"/>): the two faces are
    /// the same rectangle at two sizes, and the scale and its inverse are exact in fixed point, so a body that crosses
    /// and crosses back returns at exactly the size it left with. A ratio of a power of two (a quarter, a half, two,
    /// four) between two sizes fixed point holds exactly passes; a third does not.</summary>
    /// <param name="source">The source boundary frame.</param>
    /// <param name="destination">The destination counterpart's boundary frame.</param>
    /// <param name="scale">The pair's scale, on success.</param>
    /// <param name="reason">Why the pair cannot be crossed, on failure.</param>
    /// <returns><see langword="true"/> when the pair can be crossed.</returns>
    public static bool TryScale(in WorldFaceFrame source, in WorldFaceFrame destination, out FixedQ4816 scale, out string reason) {
        scale = Scale(
            destination: destination,
            source: source
        );

        if (scale == FixedQ4816.One) {
            if (destination.HalfHeight != source.HalfHeight) {
                reason = $"the two faces are one width and two heights ({Size(frame: in source)} and {Size(frame: in destination)}), so no one scale maps one onto the other";

                return false;
            }

            reason = string.Empty;

            return true;
        }
        if (
            (source.HalfWidth <= FixedQ4816.Zero) ||
            (destination.HalfWidth <= FixedQ4816.Zero) ||
            (((Int128)destination.HalfWidth.Value * source.HalfHeight.Value) != ((Int128)destination.HalfHeight.Value * source.HalfWidth.Value))
        ) {
            reason = $"the two faces are not one rectangle at two sizes ({Size(frame: in source)} and {Size(frame: in destination)}), so no one scale maps one onto the other";

            return false;
        }

        var inverse = (source.HalfWidth / destination.HalfWidth);

        if (
            ((source.HalfWidth * scale) != destination.HalfWidth) ||
            ((destination.HalfWidth * inverse) != source.HalfWidth) ||
            ((scale * inverse) != FixedQ4816.One)
        ) {
            reason = $"the two faces' sizes ({Size(frame: in source)} and {Size(frame: in destination)}) are in a ratio fixed point does not hold exactly both ways, so a round trip would not return a traveller to its size; author sizes a power of two apart";

            return false;
        }

        reason = string.Empty;

        return true;

        static string Size(in WorldFaceFrame frame) => $"{(((double)frame.HalfWidth) * 2):0.#####}x{(((double)frame.HalfHeight) * 2):0.#####}";
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
