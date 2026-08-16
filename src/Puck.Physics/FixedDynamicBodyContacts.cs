using Puck.Maths;

namespace Puck.Physics;

/// <summary>Deterministic compound convex-body overlap and depenetration geometry.</summary>
public static class FixedDynamicBodyContacts {
    private static FixedVector3 ClosestDelta(FixedVector3 p1, FixedVector3 q1, FixedVector3 p2, FixedVector3 q2) {
        var d1 = (q1 - p1);
        var d2 = (q2 - p2);
        var r = (p1 - p2);
        var a = FixedVector3.Dot(
            left: d1,
            right: d1
        );
        var e = FixedVector3.Dot(
            left: d2,
            right: d2
        );
        var f = FixedVector3.Dot(
            left: d2,
            right: r
        );
        FixedQ4816 s;
        FixedQ4816 t;

        if (
            (a <= FixedQ4816.Zero) &&
            (e <= FixedQ4816.Zero)
        ) {
            return r;
        }
        if (a <= FixedQ4816.Zero) {
            s = FixedQ4816.Zero;
            t = FixedQ4816.Clamp(
                value: (f / e),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );
        } else {
            var c = FixedVector3.Dot(
                left: d1,
                right: r
            );

            if (e <= FixedQ4816.Zero) {
                t = FixedQ4816.Zero;
                s = FixedQ4816.Clamp(
                    value: (-c / a),
                    minimum: FixedQ4816.Zero,
                    maximum: FixedQ4816.One
                );
            } else {
                var b = FixedVector3.Dot(
                    left: d1,
                    right: d2
                );
                var denominator = ((a * e) - (b * b));

                s = ((denominator > FixedQ4816.Zero)
                    ? FixedQ4816.Clamp(
                        value: (((b * f) - (c * e)) / denominator),
                        minimum: FixedQ4816.Zero,
                        maximum: FixedQ4816.One
                    )
                    : FixedQ4816.Zero
                );
                t = (((b * s) + f) / e);
                if (t < FixedQ4816.Zero) {
                    t = FixedQ4816.Zero;
                    s = FixedQ4816.Clamp(
                        value: (-c / a),
                        minimum: FixedQ4816.Zero,
                        maximum: FixedQ4816.One
                    );
                } else if (t > FixedQ4816.One) {
                    t = FixedQ4816.One;
                    s = FixedQ4816.Clamp(
                        value: ((b - c) / a),
                        minimum: FixedQ4816.Zero,
                        maximum: FixedQ4816.One
                    );
                }
            }
        }
        return ((p1 + (d1 * s)) - (p2 + (d2 * t)));
    }
    private static bool IsSupported(FixedBodyColliderKind kind) =>
        (kind is FixedBodyColliderKind.Sphere or FixedBodyColliderKind.Capsule or FixedBodyColliderKind.Box);
    private static (FixedVector3 Start, FixedVector3 End) Segment(FixedVector3 position, FixedQuaternion orientation, in FixedBodyColliderVolume volume) {
        var start = (position + orientation.Rotate(vector: volume.Center));
        var end = ((volume.Kind == FixedBodyColliderKind.Capsule)
            ? (position + orientation.Rotate(vector: volume.Endpoint))
            : start
        );

        return (start, end);
    }
    private static bool TryVolumeCorrection(
        FixedVector3 leftPosition,
        FixedQuaternion leftOrientation,
        in FixedBodyColliderVolume left,
        FixedVector3 rightPosition,
        FixedQuaternion rightOrientation,
        in FixedBodyColliderVolume right,
        int tieBreaker,
        out FixedVector3 correction
    ) {
        if (
            !IsSupported(kind: left.Kind) ||
            !IsSupported(kind: right.Kind)
        ) {
            throw new InvalidOperationException(message: $"Unknown body collider pair {left.Kind}/{right.Kind}.");
        }

        if (
            (left.Kind != FixedBodyColliderKind.Box) &&
            (right.Kind != FixedBodyColliderKind.Box)
        ) {
            var (leftStart, leftEnd) = Segment(
                orientation: leftOrientation,
                position: leftPosition,
                volume: in left
            );
            var (rightStart, rightEnd) = Segment(
                orientation: rightOrientation,
                position: rightPosition,
                volume: in right
            );
            var delta = ClosestDelta(
                p1: leftStart,
                p2: rightStart,
                q1: leftEnd,
                q2: rightEnd
            );
            var distanceSquared = delta.LengthSquared;
            var radius = (left.Radius + right.Radius);

            if (distanceSquared >= (radius * radius)) {
                correction = default;
                return false;
            }

            if (distanceSquared <= FixedQ4816.Zero) {
                var sign = (((tieBreaker & 1) == 0)
                    ? FixedQ4816.One
                    : -FixedQ4816.One
                );

                correction = new FixedVector3(
                    X: (radius * sign),
                    Y: FixedQ4816.Zero,
                    Z: FixedQ4816.Zero
                );
                return true;
            }

            var distance = FixedQ4816.Sqrt(value: distanceSquared);

            correction = (delta * ((radius - distance) / distance));
            return true;
        }

        var (leftCenter, leftExtent) = FixedColliderBounds.WorldBounds(
            orientation: leftOrientation,
            position: leftPosition,
            volume: in left
        );
        var (rightCenter, rightExtent) = FixedColliderBounds.WorldBounds(
            orientation: rightOrientation,
            position: rightPosition,
            volume: in right
        );
        var deltaCenter = (leftCenter - rightCenter);
        var overlapX = ((leftExtent.X + rightExtent.X) - FixedQ4816.Abs(value: deltaCenter.X));
        var overlapY = ((leftExtent.Y + rightExtent.Y) - FixedQ4816.Abs(value: deltaCenter.Y));
        var overlapZ = ((leftExtent.Z + rightExtent.Z) - FixedQ4816.Abs(value: deltaCenter.Z));

        if (
            (overlapX <= FixedQ4816.Zero) ||
            (overlapY <= FixedQ4816.Zero) ||
            (overlapZ <= FixedQ4816.Zero)
        ) {
            correction = default;
            return false;
        }

        if (
            (overlapX <= overlapY) &&
            (overlapX <= overlapZ)
        ) {
            var sign = ((deltaCenter.X == FixedQ4816.Zero)
                ? (((tieBreaker & 1) == 0)
                    ? FixedQ4816.One
                    : -FixedQ4816.One)
                : ((deltaCenter.X > FixedQ4816.Zero)
                    ? FixedQ4816.One
                    : -FixedQ4816.One
            ));

            correction = new FixedVector3(
                X: (overlapX * sign),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            );
        } else if (overlapY <= overlapZ) {
            var sign = ((deltaCenter.Y >= FixedQ4816.Zero)
                ? FixedQ4816.One
                : -FixedQ4816.One
            );

            correction = new FixedVector3(
                X: FixedQ4816.Zero,
                Y: (overlapY * sign),
                Z: FixedQ4816.Zero
            );
        } else {
            var sign = ((deltaCenter.Z == FixedQ4816.Zero)
                ? (((tieBreaker & 1) == 0)
                    ? FixedQ4816.One
                    : -FixedQ4816.One)
                : ((deltaCenter.Z > FixedQ4816.Zero)
                    ? FixedQ4816.One
                    : -FixedQ4816.One
            ));

            correction = new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.Zero,
                Z: (overlapZ * sign)
            );
        }
        return true;
    }

    /// <summary>Returns a rotation-independent sphere enclosing every supplied local volume.</summary>
    /// <param name="volumes">The compound collider's local volumes.</param>
    /// <returns>A conservative broadphase radius.</returns>
    public static FixedQ4816 BroadphaseRadius(ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        var radius = FixedQ4816.Zero;

        foreach (ref readonly var volume in volumes) {
            var extent = volume.Kind switch {
                FixedBodyColliderKind.Sphere => (volume.Center.Length + volume.Radius),
                FixedBodyColliderKind.Capsule => (FixedQ4816.Max(
                x: volume.Center.Length,
                y: volume.Endpoint.Length
            ) + volume.Radius),
                FixedBodyColliderKind.Box => (volume.Center.Length + volume.HalfExtents.Length),
                _ => throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}."),
            };

            radius = FixedQ4816.Max(
                x: radius,
                y: extent
            );
        }
        return radius;
    }
    /// <summary>Finds the deepest overlap correction from <paramref name="rightVolumes"/> to
    /// <paramref name="leftVolumes"/>.</summary>
    /// <param name="leftPosition">The left body's world position.</param>
    /// <param name="leftOrientation">The left body's world orientation.</param>
    /// <param name="leftVolumes">The left body's local volumes.</param>
    /// <param name="rightPosition">The right body's world position.</param>
    /// <param name="rightOrientation">The right body's world orientation.</param>
    /// <param name="rightVolumes">The right body's local volumes.</param>
    /// <param name="tieBreaker">A stable value selecting an axis sign when centers coincide.</param>
    /// <param name="correction">The deepest correction, directed from right to left.</param>
    /// <returns><see langword="true"/> when any volume pair overlaps.</returns>
    public static bool TryCorrection(
        FixedVector3 leftPosition,
        FixedQuaternion leftOrientation,
        ReadOnlySpan<FixedBodyColliderVolume> leftVolumes,
        FixedVector3 rightPosition,
        FixedQuaternion rightOrientation,
        ReadOnlySpan<FixedBodyColliderVolume> rightVolumes,
        int tieBreaker,
        out FixedVector3 correction
    ) {
        correction = FixedVector3.Zero;
        var deepestSquared = FixedQ4816.Zero;

        foreach (ref readonly var left in leftVolumes) {
            foreach (ref readonly var right in rightVolumes) {
                if (!TryVolumeCorrection(
                    correction: out var candidate,
                    left: in left,
                    leftOrientation: leftOrientation,
                    leftPosition: leftPosition,
                    right: in right,
                    rightOrientation: rightOrientation,
                    rightPosition: rightPosition,
                    tieBreaker: tieBreaker
                )) {
                    continue;
                }

                var squared = candidate.LengthSquared;

                if (squared > deepestSquared) {
                    deepestSquared = squared;
                    correction = candidate;
                }
            }
        }

        return (deepestSquared > FixedQ4816.Zero);
    }
}
