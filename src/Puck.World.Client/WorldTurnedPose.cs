using System.Numerics;

namespace Puck.World.Client;

/// <summary>
/// The presented orientation of a placement row that turns (<see cref="WorldPlacement.Turn"/>): the row's own
/// orientation with the turn's yaw about its local up axis applied first. The one place every presentation reader of a
/// turning row (the stamp pool, the rigid copies) composes it.
/// <para>Presentation float over the values the state mirror presents, interpolated between ticks, so a turntable moves
/// smoothly at any frame rate. The authoritative answer is the fixed-point one
/// (<see cref="WorldPlacementTurn.YawRadiansAt"/>), which a solid row's colliders take, composed in the same order.</para>
/// </summary>
public static class WorldTurnedPose {
    /// <summary>Returns a row's orientation turned by its turn facet.</summary>
    /// <param name="turn">The row's turn facet, or <see langword="null"/> for a row that does not turn.</param>
    /// <param name="orientation">The row's orientation before the turn.</param>
    /// <param name="mirror">The state mirror whose presented values the turn's bindings read.</param>
    /// <returns>The turned orientation; <paramref name="orientation"/> itself for a row that does not turn.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> is <see langword="null"/>.</exception>
    public static Quaternion Turned(WorldPlacementTurn? turn, Quaternion orientation, WorldStateMirror mirror) {
        ArgumentNullException.ThrowIfNull(argument: mirror);

        if (turn is null) {
            return orientation;
        }

        var yaw = turn.PresentedYawRadians(read: (scalar, fallback) => Presented(fallback: fallback, mirror: mirror, scalar: scalar));

        return Quaternion.Normalize(value: (orientation * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: ((float)yaw))));
    }

    // A member's presented value at full precision: a binding's presented number, which a long-running clock needs in
    // double to keep its fraction, else the literal.
    private static double Presented(WorldStateMirror mirror, BindableScalar scalar, double fallback) {
        if (scalar.State is { } binding) {
            return (mirror.TryValue(slot: mirror.SlotOf(binding: in binding, conversion: WorldStateConversion.Number), value: out var value) ? value : fallback);
        }

        return mirror.Scalar(fallback: ((float)fallback), scalar: in scalar);
    }
}
