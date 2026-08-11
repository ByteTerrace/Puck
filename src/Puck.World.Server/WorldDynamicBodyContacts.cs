using Puck.Maths;

namespace Puck.World.Server;

/// <summary>Deterministic convex-body depenetration shared by local pairs and delivered neighbour ghosts.</summary>
internal static class WorldDynamicBodyContacts {
    private static readonly FixedQ4816 s_two = FixedQ4816.FromInteger(value: 2L);

    /// <summary>A rotation-independent sphere enclosing every volume in a compiled collider. The local solver uses
    /// it only to reject impossible pairs before narrow phase; conservative excess work is safe, a false negative is
    /// not.</summary>
    public static FixedQ4816 BroadphaseRadius(in FixedWorldCollider collider) {
        var radius = FixedQ4816.Zero;
        foreach (ref readonly var volume in collider.Volumes.AsSpan()) {
            var extent = volume.Kind switch {
                FixedBodyColliderKind.Sphere => (volume.Center.Length + volume.Radius),
                FixedBodyColliderKind.Capsule => (FixedQ4816.Max(x: volume.Center.Length, y: volume.Endpoint.Length) + volume.Radius),
                _ => (volume.Center.Length + volume.HalfExtents.Length),
            };
            radius = FixedQ4816.Max(x: radius, y: extent);
        }
        return radius;
    }

    public static bool TryCorrection(
        FixedVector3 leftPosition,
        FixedQuaternion leftOrientation,
        in FixedWorldCollider leftCollider,
        FixedVector3 rightPosition,
        FixedQuaternion rightOrientation,
        in FixedWorldCollider rightCollider,
        int tieBreaker,
        out FixedVector3 correction
    ) {
        return TryCorrection(
            leftPosition: leftPosition,
            leftOrientation: leftOrientation,
            leftVolumes: leftCollider.Volumes,
            rightPosition: rightPosition,
            rightOrientation: rightOrientation,
            rightCollider: in rightCollider,
            tieBreaker: tieBreaker,
            correction: out correction);
    }

    public static bool TryCorrection(
        FixedVector3 leftPosition,
        FixedQuaternion leftOrientation,
        ReadOnlySpan<FixedBodyColliderVolume> leftVolumes,
        FixedVector3 rightPosition,
        FixedQuaternion rightOrientation,
        in FixedWorldCollider rightCollider,
        int tieBreaker,
        out FixedVector3 correction
    ) {
        correction = FixedVector3.Zero;
        var deepestSquared = FixedQ4816.Zero;

        foreach (ref readonly var left in leftVolumes) {
            foreach (var right in rightCollider.Volumes) {
                if (!TryVolumeCorrection(leftPosition: leftPosition, leftOrientation: leftOrientation, left: in left,
                    rightPosition: rightPosition, rightOrientation: rightOrientation, right: in right,
                    tieBreaker: tieBreaker, correction: out var candidate)) {
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
        if ((left.Kind != FixedBodyColliderKind.Box) && (right.Kind != FixedBodyColliderKind.Box)) {
            var (leftStart, leftEnd) = Segment(position: leftPosition, orientation: leftOrientation, volume: in left);
            var (rightStart, rightEnd) = Segment(position: rightPosition, orientation: rightOrientation, volume: in right);
            var delta = ClosestDelta(p1: leftStart, q1: leftEnd, p2: rightStart, q2: rightEnd);
            var distanceSquared = delta.LengthSquared;
            var radius = (left.Radius + right.Radius);
            if (distanceSquared >= (radius * radius)) {
                correction = default;
                return false;
            }

            if (distanceSquared <= FixedQ4816.Zero) {
                var sign = ((tieBreaker & 1) == 0 ? FixedQ4816.One : -FixedQ4816.One);
                correction = new FixedVector3(X: (radius * sign), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
                return true;
            }

            var distance = FixedQ4816.Sqrt(value: distanceSquared);
            correction = (delta * ((radius - distance) / distance));
            return true;
        }

        var (leftCenter, leftExtent) = Bounds(position: leftPosition, orientation: leftOrientation, volume: in left);
        var (rightCenter, rightExtent) = Bounds(position: rightPosition, orientation: rightOrientation, volume: in right);
        var deltaCenter = (leftCenter - rightCenter);
        var overlapX = ((leftExtent.X + rightExtent.X) - FixedQ4816.Abs(value: deltaCenter.X));
        var overlapY = ((leftExtent.Y + rightExtent.Y) - FixedQ4816.Abs(value: deltaCenter.Y));
        var overlapZ = ((leftExtent.Z + rightExtent.Z) - FixedQ4816.Abs(value: deltaCenter.Z));
        if ((overlapX <= FixedQ4816.Zero) || (overlapY <= FixedQ4816.Zero) || (overlapZ <= FixedQ4816.Zero)) {
            correction = default;
            return false;
        }

        if ((overlapX <= overlapY) && (overlapX <= overlapZ)) {
            var sign = ((deltaCenter.X == FixedQ4816.Zero) ? (((tieBreaker & 1) == 0) ? FixedQ4816.One : -FixedQ4816.One) : (deltaCenter.X > FixedQ4816.Zero ? FixedQ4816.One : -FixedQ4816.One));
            correction = new FixedVector3(X: (overlapX * sign), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        } else if (overlapY <= overlapZ) {
            var sign = ((deltaCenter.Y >= FixedQ4816.Zero) ? FixedQ4816.One : -FixedQ4816.One);
            correction = new FixedVector3(X: FixedQ4816.Zero, Y: (overlapY * sign), Z: FixedQ4816.Zero);
        } else {
            var sign = ((deltaCenter.Z == FixedQ4816.Zero) ? (((tieBreaker & 1) == 0) ? FixedQ4816.One : -FixedQ4816.One) : (deltaCenter.Z > FixedQ4816.Zero ? FixedQ4816.One : -FixedQ4816.One));
            correction = new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: (overlapZ * sign));
        }
        return true;
    }

    private static (FixedVector3 Start, FixedVector3 End) Segment(FixedVector3 position, FixedQuaternion orientation, in FixedBodyColliderVolume volume) {
        var start = (position + orientation.Rotate(vector: volume.Center));
        var end = (volume.Kind == FixedBodyColliderKind.Capsule) ? (position + orientation.Rotate(vector: volume.Endpoint)) : start;
        return (start, end);
    }

    private static FixedVector3 ClosestDelta(FixedVector3 p1, FixedVector3 q1, FixedVector3 p2, FixedVector3 q2) {
        var d1 = (q1 - p1);
        var d2 = (q2 - p2);
        var r = (p1 - p2);
        var a = FixedVector3.Dot(left: d1, right: d1);
        var e = FixedVector3.Dot(left: d2, right: d2);
        var f = FixedVector3.Dot(left: d2, right: r);
        FixedQ4816 s;
        FixedQ4816 t;

        if ((a <= FixedQ4816.Zero) && (e <= FixedQ4816.Zero)) {
            return r;
        }
        if (a <= FixedQ4816.Zero) {
            s = FixedQ4816.Zero;
            t = FixedQ4816.Clamp(value: (f / e), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
        } else {
            var c = FixedVector3.Dot(left: d1, right: r);
            if (e <= FixedQ4816.Zero) {
                t = FixedQ4816.Zero;
                s = FixedQ4816.Clamp(value: (-c / a), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
            } else {
                var b = FixedVector3.Dot(left: d1, right: d2);
                var denominator = ((a * e) - (b * b));
                s = (denominator > FixedQ4816.Zero)
                    ? FixedQ4816.Clamp(value: (((b * f) - (c * e)) / denominator), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One)
                    : FixedQ4816.Zero;
                t = (((b * s) + f) / e);
                if (t < FixedQ4816.Zero) {
                    t = FixedQ4816.Zero;
                    s = FixedQ4816.Clamp(value: (-c / a), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
                } else if (t > FixedQ4816.One) {
                    t = FixedQ4816.One;
                    s = FixedQ4816.Clamp(value: ((b - c) / a), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
                }
            }
        }
        return ((p1 + (d1 * s)) - (p2 + (d2 * t)));
    }

    private static (FixedVector3 Center, FixedVector3 Extent) Bounds(FixedVector3 position, FixedQuaternion orientation, in FixedBodyColliderVolume volume) {
        if (volume.Kind == FixedBodyColliderKind.Sphere) {
            var extent = new FixedVector3(X: volume.Radius, Y: volume.Radius, Z: volume.Radius);
            return ((position + orientation.Rotate(vector: volume.Center)), extent);
        }
        if (volume.Kind == FixedBodyColliderKind.Capsule) {
            var (start, end) = Segment(position: position, orientation: orientation, volume: in volume);
            var delta = (end - start);
            var radius = new FixedVector3(X: volume.Radius, Y: volume.Radius, Z: volume.Radius);
            return ((start + end) / s_two, new FixedVector3(
                X: (FixedQ4816.Abs(value: delta.X) / s_two),
                Y: (FixedQ4816.Abs(value: delta.Y) / s_two),
                Z: (FixedQ4816.Abs(value: delta.Z) / s_two)) + radius);
        }

        var center = (position + orientation.Rotate(vector: volume.Center));
        var rotation = (orientation * volume.Rotation).Normalize();
        var x = rotation.Rotate(vector: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));
        var y = rotation.Rotate(vector: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero));
        var z = rotation.Rotate(vector: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One));
        return (center, new FixedVector3(
            X: ((FixedQ4816.Abs(value: x.X) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: y.X) * volume.HalfExtents.Y) + (FixedQ4816.Abs(value: z.X) * volume.HalfExtents.Z)),
            Y: ((FixedQ4816.Abs(value: x.Y) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: y.Y) * volume.HalfExtents.Y) + (FixedQ4816.Abs(value: z.Y) * volume.HalfExtents.Z)),
            Z: ((FixedQ4816.Abs(value: x.Z) * volume.HalfExtents.X) + (FixedQ4816.Abs(value: y.Z) * volume.HalfExtents.Y) + (FixedQ4816.Abs(value: z.Z) * volume.HalfExtents.Z))));
    }
}
