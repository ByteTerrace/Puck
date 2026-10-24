using Puck.Maths;

namespace Puck.Physics;

/// <summary>The world-space axis-aligned bounds of a body-local convex volume — the one envelope every broadphase
/// consumer reads, so dynamic-vs-dynamic and dynamic-vs-static pairs, and the event feed's overlap sense, cannot
/// disagree about the same volume.</summary>
public static class FixedColliderBounds {
    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2L);

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
            _ => throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}."),
        };
    /// <summary>Computes the world-space axis-aligned bounds of one body volume at a pose.</summary>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local convex volume.</param>
    /// <returns>The bounds' center and half-extent: a sphere's center and radius; a capsule's segment midpoint and
    /// half the segment's absolute span plus its radius; a box's center and <see cref="FixedOrientedBox.WorldExtent"/>.</returns>
    /// <exception cref="InvalidOperationException">The volume's kind is not a sphere, capsule or box.</exception>
    public static (FixedVector3 Center, FixedVector3 Extent) WorldBounds(
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
            var (lower, upper) = FixedDynamicBodyContacts.Segment(
                orientation: orientation,
                position: position,
                volume: in volume
            );
            var radius = new FixedVector3(
                X: volume.Radius,
                Y: volume.Radius,
                Z: volume.Radius
            );
            var delta = (upper - lower);

            return (Center: ((lower + upper) / Two), Extent: ((FixedVector3.Abs(value: delta) / Two) + radius));
        }

        if (volume.Kind == FixedBodyColliderKind.Box) {
            var box = FixedOrientedBox.FromVolume(
                orientation: in orientation,
                position: position,
                volume: in volume
            );

            return (Center: box.Center, Extent: box.WorldExtent);
        }

        throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}.");
    }
}
