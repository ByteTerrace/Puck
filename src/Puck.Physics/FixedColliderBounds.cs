using Puck.Maths;

namespace Puck.Physics;

/// <summary>The world-space axis-aligned bounds of a body-local convex volume — the one envelope every broadphase
/// consumer reads, so dynamic-vs-dynamic and dynamic-vs-static pairs, and the event feed's overlap sense, cannot
/// disagree about the same volume.</summary>
public static class FixedColliderBounds {
    /// <summary>The smallest support radius of a convex volume: its sphere/capsule radius or least box/ellipsoid half-extent.</summary>
    /// <param name="volume">The body's convex volume.</param>
    /// <returns>The least radius, before contact skin.</returns>
    public static FixedQ4816 MinimumSupportRadius(in FixedBodyColliderVolume volume) =>
        ((volume.Kind is FixedBodyColliderKind.Box or FixedBodyColliderKind.Ellipsoid)
            ? FixedQ4816.Min(x: volume.HalfExtents.X, y: FixedQ4816.Min(x: volume.HalfExtents.Y, y: volume.HalfExtents.Z))
            : volume.Radius);
    /// <summary>Returns the body's least projection along a unit direction, including its radius.</summary>
    /// <param name="position">The body position.</param>
    /// <param name="orientation">The body orientation.</param>
    /// <param name="volume">The body-local volume.</param>
    /// <param name="normal">The unit projection direction.</param>
    /// <returns>The minimum support projection.</returns>
    public static FixedQ4816 MinimumProjection(FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedVector3 normal) => volume.Kind switch {
            FixedBodyColliderKind.Sphere => (FixedVector3.Dot(left: (position + orientation.Rotate(vector: volume.Center)), right: normal) - volume.Radius),
            FixedBodyColliderKind.Capsule => (FixedQ4816.Min(
                x: FixedVector3.Dot(left: (position + orientation.Rotate(vector: volume.Center)), right: normal),
                y: FixedVector3.Dot(left: (position + orientation.Rotate(vector: volume.Endpoint)), right: normal)) - volume.Radius),
            FixedBodyColliderKind.Box => FixedOrientedBox.FromVolume(orientation: in orientation, position: position, volume: in volume).MinimumProjection(normal: normal),
            FixedBodyColliderKind.Ellipsoid => FixedVector3.Dot(left: (position + orientation.Rotate(vector:
                FixedRigidWitness.LocalSupportPoint(volume, orientation.RotateInverse(vector: -normal)))), right: normal),
            _ => throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}."),
        };
    /// <summary>Computes the world-space axis-aligned bounds of one body volume at a pose.</summary>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local convex volume.</param>
    /// <returns>The bounds: a sphere's cube of its radius about its center; a capsule's segment spanned and grown by
    /// its radius; a box's center and <see cref="FixedOrientedBox.WorldExtent"/>; an ellipsoid's least and greatest
    /// support along each world axis.</returns>
    /// <exception cref="InvalidOperationException">The volume's kind has no support map.</exception>
    public static FixedAabb WorldBounds(
        FixedVector3 position,
        in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume
    ) {
        if (volume.Kind == FixedBodyColliderKind.Sphere) {
            return FixedAabb.Around(center: (position + orientation.Rotate(vector: volume.Center)), radius: volume.Radius);
        }

        if (volume.Kind == FixedBodyColliderKind.Capsule) {
            var (lower, upper) = FixedDynamicBodyContacts.Segment(
                orientation: orientation,
                position: position,
                volume: in volume
            );
            return new FixedAabb(Maximum: FixedVector3.Max(left: lower, right: upper), Minimum: FixedVector3.Min(left: lower, right: upper)).Grow(margin: volume.Radius);
        }

        if (volume.Kind == FixedBodyColliderKind.Box) {
            var box = FixedOrientedBox.FromVolume(
                orientation: in orientation,
                position: position,
                volume: in volume
            );

            return FixedAabb.FromCenter(center: box.Center, extent: box.WorldExtent);
        }

        if (volume.Kind == FixedBodyColliderKind.Ellipsoid) {
            var center = (position + orientation.Rotate(vector: volume.Center));
            var x = (-MinimumProjection(position, in orientation, in volume, -FixedVector3.UnitX) - center.X);
            var y = (-MinimumProjection(position, in orientation, in volume, -FixedVector3.UnitY) - center.Y);
            var z = (-MinimumProjection(position, in orientation, in volume, -FixedVector3.UnitZ) - center.Z);

            return FixedAabb.FromCenter(center: center, extent: new FixedVector3(X: x, Y: y, Z: z));
        }

        throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}.");
    }
}
