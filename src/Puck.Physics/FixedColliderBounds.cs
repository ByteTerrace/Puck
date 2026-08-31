using Puck.Maths;

namespace Puck.Physics;

/// <summary>The world-space axis-aligned bounds of a body-local convex volume — the one envelope every broadphase
/// consumer reads, so dynamic-vs-dynamic and dynamic-vs-static pairs cannot disagree about the same volume.</summary>
internal static class FixedColliderBounds {
    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2L);

    internal static (FixedVector3 Center, FixedVector3 Extent) WorldBounds(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume
    ) {
        if (volume.Kind == FixedBodyColliderKind.Sphere) {
            return (Center: (position + orientation.Rotate(vector: volume.Center)), Extent: new FixedVector3(
                X: volume.Radius,
                Y: volume.Radius,
                Z: volume.Radius
            ));
        }

        if (volume.Kind == FixedBodyColliderKind.Capsule) {
            var lower = (position + orientation.Rotate(vector: volume.Center));
            var upper = (position + orientation.Rotate(vector: volume.Endpoint));
            var radius = new FixedVector3(
                X: volume.Radius,
                Y: volume.Radius,
                Z: volume.Radius
            );
            var delta = (upper - lower);
            var absoluteDelta = new FixedVector3(
                X: FixedQ4816.Abs(value: delta.X),
                Y: FixedQ4816.Abs(value: delta.Y),
                Z: FixedQ4816.Abs(value: delta.Z)
            );

            return (Center: ((lower + upper) / Two), Extent: ((absoluteDelta / Two) + radius));
        }

        if (volume.Kind == FixedBodyColliderKind.Box) {
            var center = (position + orientation.Rotate(vector: volume.Center));
            var rotation = (orientation * volume.Rotation).Normalize();
            var axisX = rotation.Rotate(vector: FixedAxisMath.UnitX);
            var axisY = rotation.Rotate(vector: FixedAxisMath.UnitY);
            var axisZ = rotation.Rotate(vector: FixedAxisMath.UnitZ);
            var extent = new FixedVector3(
                X: (((FixedQ4816.Abs(value: axisX.X) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: axisY.X) * volume.HalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.X) * volume.HalfExtents.Z)),
                Y: (((FixedQ4816.Abs(value: axisX.Y) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: axisY.Y) * volume.HalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Y) * volume.HalfExtents.Z)),
                Z: (((FixedQ4816.Abs(value: axisX.Z) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: axisY.Z) * volume.HalfExtents.Y)) + (FixedQ4816.Abs(value: axisZ.Z) * volume.HalfExtents.Z))
            );

            return (Center: center, Extent: extent);
        }

        throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}.");
    }
}
