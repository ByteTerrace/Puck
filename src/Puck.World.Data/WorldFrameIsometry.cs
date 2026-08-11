using Puck.Maths;

namespace Puck.World;

/// <summary>The fixed-point planar isometry shared by handoff, adjacency rendering/contact, and compiler topology
/// proofs. A boundary mapping is <c>destination * half-turn * inverse(source)</c>; keeping it here prevents the
/// validator and runtime from growing merely similar transform math.</summary>
public static class WorldFrameIsometry {
    private static readonly FixedVector3 s_up = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedQ4816 s_halfTurn = FixedQ4816.FromDouble(value: Math.PI);
    private static readonly FixedQ4816 s_fullTurn = (s_halfTurn + s_halfTurn);

    public static FixedQ4816 RotationDelta(FixedQ4816 sourceYaw, FixedQ4816 destinationYaw) =>
        ((destinationYaw - sourceYaw) + s_halfTurn);

    public static FixedVector3 Rotate(FixedVector3 value, FixedQ4816 deltaYaw) {
        var identity = (Math.Abs(deltaYaw.Value) <= 1L) ||
            (Math.Abs(deltaYaw.Value - s_fullTurn.Value) <= 1L) ||
            (Math.Abs(deltaYaw.Value + s_fullTurn.Value) <= 1L);
        return identity ? value : FixedQuaternion.FromAxisAngle(axis: s_up, angle: deltaYaw).Rotate(vector: value);
    }

    public static FixedVector3 MapPoint(FixedVector3 point, in WorldFaceFrame source, in WorldFaceFrame destination) {
        var delta = RotationDelta(sourceYaw: source.PlanarYawRadians, destinationYaw: destination.PlanarYawRadians);
        return (destination.Origin + Rotate(value: (point - source.Origin), deltaYaw: delta));
    }
}
