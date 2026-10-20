using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One body's crossing through a portal pair whose two faces stand in the same world: where the body lands
/// and how it moves on, already mapped through the pair (<see cref="WorldFrameIsometry.MapArrival"/>).</summary>
/// <param name="Slot">The body index, which the crossing keeps.</param>
/// <param name="Position">The mapped position, fixed point.</param>
/// <param name="YawRadians">The mapped yaw, fixed-point radians, unwrapped.</param>
/// <param name="PlanarVelocity">The mapped planar velocity.</param>
/// <param name="VerticalVelocity">The mapped vertical velocity.</param>
/// <param name="TravelTurn">The body's accumulated arrival turn after the crossing
/// (<see cref="WorldFrameIsometry.AccumulateTurn"/>).</param>
public readonly record struct WorldInPlaceCrossing(int Slot, FixedVector3 Position, FixedQ4816 YawRadians, FixedVector3 PlanarVelocity, FixedQ4816 VerticalVelocity, FixedQ4816 TravelTurn);
public sealed partial class WorldServer {
    /// <summary>Gets or sets the observer of every in-place crossing this authority applies, heard inside the
    /// authority operation that applied it, so its position among the authority's inputs is the crossing's own. The
    /// replay tape attaches here while it records.</summary>
    public Action<WorldInPlaceCrossing>? CrossingTap { get; set; }

    /// <summary>Moves one body through a portal pair whose two faces both stand in this world: maps its pose and
    /// velocity from <paramref name="source"/> through <paramref name="destination"/> and applies the result in place,
    /// keeping its body index, kit, grants, motion program, action state and tape. The caller holds the authority
    /// gate. Every computation is fixed point, so the same state crosses to the same place on every machine.</summary>
    /// <param name="slot">The body index.</param>
    /// <param name="source">The frame of the face the body entered.</param>
    /// <param name="destination">The frame of that face's counterpart.</param>
    /// <returns>The crossing applied, or <see langword="null"/> when the index holds no active body.</returns>
    public WorldInPlaceCrossing? CrossInPlace(int slot, in WorldFaceFrame source, in WorldFaceFrame destination) {
        if (
            (((uint)slot) >= ((uint)m_population.Capacity)) ||
            !m_population.IsActive(index: slot) ||
            (m_population.EntryBody(index: slot) is not { } body)
        ) {
            return null;
        }

        var state = body.CaptureTransferState();
        var yaw = body.FixedYaw;
        var mapped = WorldFrameIsometry.MapArrival(
            destination: in destination,
            source: in source,
            travelerPlanarVelocity: state.PlanarVelocity,
            travelerPosition: body.FixedPosition,
            travelerVerticalVelocity: state.VerticalVelocity,
            travelerYawRadians: yaw
        );
        var crossing = new WorldInPlaceCrossing(
            PlanarVelocity: mapped.PlanarVelocity,
            Position: mapped.Position,
            Slot: slot,
            TravelTurn: WorldFrameIsometry.AccumulateTurn(
                arrivalYaw: mapped.YawRadians,
                departureYaw: yaw,
                travelTurn: m_population.TravelTurn(index: slot)
            ),
            VerticalVelocity: mapped.VerticalVelocity,
            YawRadians: mapped.YawRadians
        );

        return (ApplyInPlaceCrossing(crossing: in crossing)
            ? crossing
            : null);
    }
    /// <summary>Applies one in-place crossing (<see cref="CrossInPlace"/>) and reports it to <see cref="CrossingTap"/>.
    /// A live crossing and a replay's re-drive both apply here. The caller holds the authority gate.</summary>
    /// <param name="crossing">The crossing.</param>
    /// <returns><see langword="true"/> when the body was active and moved; <see langword="false"/>, changing nothing,
    /// when the index holds no active body or the arrival turn is not reduced to <c>[-pi, pi]</c>.</returns>
    public bool ApplyInPlaceCrossing(in WorldInPlaceCrossing crossing) {
        if (
            !WorldFrameIsometry.IsTurn(turn: crossing.TravelTurn) ||
            (((uint)crossing.Slot) >= ((uint)m_population.Capacity)) ||
            (m_population.EntryBody(index: crossing.Slot) is not { } body) ||
            !m_population.ApplyMappedArrival(
            destinationCompletedEngineTick: CompletedEngineTicks,
            motionProgramName: body.BodyMotionProgram,
            planarVelocity: crossing.PlanarVelocity,
            position: crossing.Position,
            slot: crossing.Slot,
            verticalVelocity: crossing.VerticalVelocity,
            yawRadians: crossing.YawRadians
        )
        ) {
            return false;
        }

        m_population.SetTravelTurn(
            slot: crossing.Slot,
            travelTurn: crossing.TravelTurn
        );
        CrossingTap?.Invoke(obj: crossing);

        return true;
    }
}
