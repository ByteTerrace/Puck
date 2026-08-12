using Puck.Maths;

namespace Puck.World;

/// <summary>The fixed-point frame isometry shared by handoff, adjacency rendering/contact, and compiler topology
/// proofs. A boundary mapping reverses the local right and normal axes while preserving local up; keeping it here prevents the
/// validator and runtime from growing merely similar transform math.</summary>
public static class WorldFrameIsometry {
    private static readonly FixedVector3 s_up = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedQ4816 s_halfTurn = FixedQ4816.FromDouble(value: Math.PI);
    private static readonly FixedQ4816 s_fullTurn = (s_halfTurn + s_halfTurn);

    /// <summary>Returns the composed boundary-facing rotation. The unwrapped representative is intentional because
    /// mapped-arrival yaw is authoritative state; <see cref="Rotate"/>'s full-turn periodicity is pinned by law.</summary>
    public static FixedQ4816 RotationDelta(FixedQ4816 sourceYaw, FixedQ4816 destinationYaw) =>
        ((destinationYaw - sourceYaw) + s_halfTurn);

    public static FixedVector3 Rotate(FixedVector3 value, FixedQ4816 deltaYaw) {
        var identity = (Math.Abs(deltaYaw.Value) <= 1L) ||
            (Math.Abs(deltaYaw.Value - s_fullTurn.Value) <= 1L) ||
            (Math.Abs(deltaYaw.Value + s_fullTurn.Value) <= 1L);
        return identity ? value : FixedQuaternion.FromAxisAngle(axis: s_up, angle: deltaYaw).Rotate(vector: value);
    }

    public static FixedVector3 MapPoint(FixedVector3 point, in WorldFaceFrame source, in WorldFaceFrame destination) {
        return (destination.Origin + MapVector(value: (point - source.Origin), source: source, destination: destination));
    }

    /// <summary>Maps a direction through a reciprocal boundary. The seam's local up is continuous while local right
    /// and outward normal reverse, yielding a proper rotation for arbitrarily oriented boundary planes.</summary>
    public static FixedVector3 MapVector(FixedVector3 value, in WorldFaceFrame source, in WorldFaceFrame destination) {
        var u = FixedVector3.Dot(left: value, right: source.Right);
        var v = FixedVector3.Dot(left: value, right: source.Up);
        var n = FixedVector3.Dot(left: value, right: source.Normal);
        return ((destination.Right * -u) + (destination.Up * v) + (destination.Normal * -n));
    }

    /// <summary>Returns the unit rotation represented by <see cref="MapVector"/>.</summary>
    public static FixedQuaternion Rotation(in WorldFaceFrame source, in WorldFaceFrame destination) {
        var first = FixedQuaternion.FromTo(from: source.Right, to: -destination.Right);
        var second = FixedQuaternion.FromTo(from: first.Rotate(vector: source.Up), to: destination.Up);
        return (second * first).Normalize();
    }
}
