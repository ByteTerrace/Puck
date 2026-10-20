using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One body's crossing through a portal pair whose two faces stand in the same world: the body and the two
/// face frames it moves between (<see cref="WorldBody.CrossInPlace"/>).</summary>
/// <param name="Slot">The body index, which the crossing keeps.</param>
/// <param name="Source">The frame of the face the body entered.</param>
/// <param name="Destination">The frame of that face's counterpart.</param>
public readonly record struct WorldInPlaceCrossing(int Slot, WorldFaceFrame Source, WorldFaceFrame Destination);
public sealed partial class WorldServer {
    /// <summary>Gets or sets the observer of every in-place crossing this authority applies, heard inside the
    /// authority operation that applied it, so its position among the authority's inputs is the crossing's own. The
    /// replay tape attaches here while it records.</summary>
    public Action<WorldInPlaceCrossing>? CrossingTap { get; set; }

    /// <summary>Moves one body through a portal pair whose two faces both stand in this world
    /// (<see cref="WorldBody.CrossInPlace"/>), keeping its body index, kit, grants, motion and action state and tape,
    /// and accumulates the pair's turn into the body's arrival turn. Reports it to <see cref="CrossingTap"/>. A live
    /// crossing and a replay's re-drive both apply here. The caller holds the authority gate.</summary>
    /// <param name="crossing">The crossing.</param>
    /// <returns><see langword="true"/> when the index held an active body and it moved.</returns>
    public bool ApplyInPlaceCrossing(in WorldInPlaceCrossing crossing) {
        if (
            (((uint)crossing.Slot) >= ((uint)m_population.Capacity)) ||
            !m_population.IsActive(index: crossing.Slot) ||
            (m_population.EntryBody(index: crossing.Slot) is not { } body)
        ) {
            return false;
        }

        var source = crossing.Source;
        var destination = crossing.Destination;

        body.CrossInPlace(
            destination: in destination,
            source: in source
        );
        m_population.SetTravelTurn(
            slot: crossing.Slot,
            travelTurn: FixedQ4816.WrapAngle(radians: (m_population.TravelTurn(index: crossing.Slot) + WorldFrameIsometry.YawDelta(
                destination: in destination,
                source: in source
            )))
        );
        CrossingTap?.Invoke(obj: crossing);

        return true;
    }
}
