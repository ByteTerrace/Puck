using Puck.Maths;

namespace Puck.Physics;

/// <summary>Contact with an inner box offset by a sphere. All witnesses are on the inner convex features;
/// the offset is applied only after their Euclidean distance is known.</summary>
internal static class FixedRoundedBoxContact {
    internal static (FixedVector3 Point, FixedVector3 Normal, FixedQ4816 Distance) Nearest(FixedVector3 point, FixedVector3 halfExtents) {
        var closest = FixedVector3.Clamp(maximum: halfExtents, minimum: -halfExtents, value: point);
        var delta = (point - closest);

        if (delta == FixedVector3.Zero) {
            var exit = FixedAxisMath.BoxInteriorExit(halfExtents: halfExtents, local: point);

            return (exit.SurfaceLocal, exit.Normal, -exit.Gap);
        }
        var distance = delta.Length;

        return (closest, delta.Normalize(), distance);
    }

    private static void Consider(FixedVector3 delta, ref FixedVector3 best, ref FixedQ4816 distance) {
        var length = delta.Length;

        if (length < distance) {
            best = delta;
            distance = length;
        }
    }
    // Bit-coded corners: changing exactly one bit follows an edge. Each edge is visited once, low endpoint first.
    private static void Corners(in FixedOrientedBox box, Span<FixedVector3> corners) {
        for (var i = 0; (i < 8); i++) {
            corners[i] = box.ToWorld(local: new FixedVector3(
                X: (((i & 1) == 0) ? -box.HalfExtents.X : box.HalfExtents.X),
                Y: (((i & 2) == 0) ? -box.HalfExtents.Y : box.HalfExtents.Y),
                Z: (((i & 4) == 0) ? -box.HalfExtents.Z : box.HalfExtents.Z)));
        }
    }
    private static bool SegmentIntersects(FixedVector3 first, FixedVector3 last, FixedVector3 half) {
        var low = FixedQ4816.Zero;
        var high = FixedQ4816.One;
        var delta = (last - first);

        return (Slab(first.X, delta.X, half.X, ref low, ref high) &&
            Slab(first.Y, delta.Y, half.Y, ref low, ref high) && Slab(first.Z, delta.Z, half.Z, ref low, ref high));
    }
    private static bool Slab(FixedQ4816 start, FixedQ4816 delta, FixedQ4816 half, ref FixedQ4816 low, ref FixedQ4816 high) {
        if (delta == FixedQ4816.Zero) { return (FixedQ4816.Abs(value: start) <= half); }
        var a = ((-half - start) / delta);
        var b = ((half - start) / delta);

        low = FixedQ4816.Max(x: low, y: FixedQ4816.Min(x: a, y: b));
        high = FixedQ4816.Min(x: high, y: FixedQ4816.Max(x: a, y: b));
        return (low <= high);
    }
    // When the segment meets the inner box, its three face axes and three edge cross axes are the complete
    // separating set. Adding the spherical radius to the winning projection gives a valid separating push.
    private static FixedContactPush InteriorSegmentPush(FixedVector3 first, FixedVector3 last, FixedVector3 half, FixedQ4816 radius) {
        Span<FixedVector3> axes = stackalloc FixedVector3[6];

        axes[0] = FixedVector3.UnitX;
        axes[1] = FixedVector3.UnitY;
        axes[2] = FixedVector3.UnitZ;
        var direction = (last - first);

        for (var i = 0; (i < 3); i++) { axes[(3 + i)] = FixedVector3.Cross(left: direction, right: axes[i]).Normalize(); }
        var best = new FixedContactPush(Normal: FixedVector3.UnitX, Penetration: FixedQ4816.MaxValue);

        foreach (var axis in axes) {
            if (axis == FixedVector3.Zero) { continue; }
            var extent = FixedVector3.Dot(left: FixedVector3.Abs(value: axis), right: half);
            var a = FixedVector3.Dot(left: first, right: axis);
            var b = FixedVector3.Dot(left: last, right: axis);
            var positive = (extent - FixedQ4816.Min(x: a, y: b));
            var negative = (extent + FixedQ4816.Max(x: a, y: b));
            var depth = (FixedQ4816.Min(x: positive, y: negative) + radius);

            if (depth < best.Penetration) {
                best = new FixedContactPush(Normal: ((positive <= negative) ? axis : -axis), Penetration: depth);
            }
        }
        return best;
    }
    private static bool CapsulePush(FixedVector3 first, FixedVector3 last, FixedVector3 half, FixedQ4816 radius, out FixedContactPush push) {
        if (SegmentIntersects(first: first, half: half, last: last)) {
            push = InteriorSegmentPush(first: first, half: half, last: last, radius: radius);
            return (push.Penetration > FixedQ4816.Zero);
        }
        // The upright walker is one point query: along local Y the segment's closest height is its clamp of zero.
        if ((first.X == last.X) && (first.Z == last.Z)) {
            var point = new FixedVector3(X: first.X, Y: FixedQ4816.Clamp(value: FixedQ4816.Zero,
                minimum: FixedQ4816.Min(x: first.Y, y: last.Y), maximum: FixedQ4816.Max(x: first.Y, y: last.Y)), Z: first.Z);
            var nearest = Nearest(halfExtents: half, point: point);

            return OutsidePush(delta: (point - nearest.Point), distance: nearest.Distance, push: out push, radius: radius);
        }
        var best = FixedVector3.Zero;
        var distance = FixedQ4816.MaxValue;
        var core = FixedOrientedBox.AxisAligned(center: FixedVector3.Zero, halfExtents: half);
        Span<FixedVector3> corners = stackalloc FixedVector3[8];

        Corners(box: in core, corners: corners);
        Consider(delta: (first - core.ClosestLocal(local: first)), best: ref best, distance: ref distance);
        Consider(delta: (last - core.ClosestLocal(local: last)), best: ref best, distance: ref distance);

        // A closest segment/box pair is endpoint/face or segment/edge (including a box vertex).
        for (var i = 0; (i < 8); i++) {
            for (var bit = 1; (bit <= 4); bit <<= 1) {
                if ((i & bit) != 0) { continue; }
                Consider(delta: FixedDynamicBodyContacts.ClosestDelta(p1: first, q1: last,
                    p2: corners[i], q2: corners[i | bit]), best: ref best, distance: ref distance);
            }
        }
        return OutsidePush(delta: best, distance: distance, push: out push, radius: radius);
    }
    private static bool BoxPush(in FixedOrientedBox body, FixedVector3 half, FixedQ4816 radius, out FixedContactPush push) {
        var core = FixedOrientedBox.AxisAligned(center: FixedVector3.Zero, halfExtents: half);

        if (FixedOrientedBox.Overlaps(left: in body, right: in core)) {
            if (FixedOrientedBox.TrySeparation(correction: out var correction, left: in body, padding: radius,
                right: in core, tieBreaker: 0)) {
                push = new FixedContactPush(Normal: correction.Normalize(), Penetration: correction.Length);
                return true;
            }
            push = default;
            return false;
        }
        var best = FixedVector3.Zero;
        var distance = FixedQ4816.MaxValue;
        Span<FixedVector3> bodyCorners = stackalloc FixedVector3[8];
        Span<FixedVector3> coreCorners = stackalloc FixedVector3[8];

        Corners(box: in body, corners: bodyCorners);
        Corners(box: in core, corners: coreCorners);

        for (var i = 0; (i < 8); i++) {
            Consider(delta: (bodyCorners[i] - core.ClosestLocal(local: bodyCorners[i])), best: ref best, distance: ref distance);
            Consider(delta: (body.ToWorld(local: body.ClosestLocal(local: body.ToLocal(point: coreCorners[i]))) - coreCorners[i]),
                best: ref best, distance: ref distance);
            for (var bit = 1; (bit <= 4); bit <<= 1) {
                if ((i & bit) != 0) { continue; }
                for (var j = 0; (j < 8); j++) {
                    for (var other = 1; (other <= 4); other <<= 1) {
                        if ((j & other) != 0) { continue; }
                        Consider(delta: FixedDynamicBodyContacts.ClosestDelta(p1: bodyCorners[i], q1: bodyCorners[i | bit],
                            p2: coreCorners[j], q2: coreCorners[j | other]), best: ref best, distance: ref distance);
                    }
                }
            }
        }
        return OutsidePush(delta: best, distance: distance, push: out push, radius: radius);
    }

    internal static bool IsEdge(FixedVector3 delta) =>
        (((((delta.X != FixedQ4816.Zero) ? 1 : 0) + ((delta.Y != FixedQ4816.Zero) ? 1 : 0)) +
        ((delta.Z != FixedQ4816.Zero) ? 1 : 0)) > 1);

    private static bool OutsidePush(FixedVector3 delta, FixedQ4816 distance, FixedQ4816 radius, out FixedContactPush push) {
        if ((distance >= radius) || (delta == FixedVector3.Zero)) {
            push = default;
            return false;
        }
        push = new FixedContactPush(Normal: delta.Normalize(), Penetration: (radius - distance), IsEdge: IsEdge(delta: delta));
        return true;
    }

    internal static bool TryPush(in FixedStaticCollider box, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, out FixedContactPush push) {
        if (volume.Kind == FixedBodyColliderKind.Sphere) {
            return FixedSphereBoxContact.TryPush(sphereCenter: (position + orientation.Rotate(vector: volume.Center)),
                radius: ((volume.Radius + box.Radius) + skin), boxCenter: box.Center,
                boxRotation: box.Rotation, halfExtents: box.Extent, push: out push);
        }
        var localPosition = box.Rotation.RotateInverse(vector: (position - box.Center));
        var localOrientation = (box.Rotation.Conjugate() * orientation).Normalize();

        var (center, extent) = FixedColliderBounds.WorldBounds(orientation: in localOrientation, position: localPosition, volume: in volume);
        var limit = ((box.Extent + new FixedVector3(X: (box.Radius + skin), Y: (box.Radius + skin), Z: (box.Radius + skin))) + extent);

        if ((FixedQ4816.Abs(value: center.X) > limit.X) || (FixedQ4816.Abs(value: center.Y) > limit.Y) ||
            (FixedQ4816.Abs(value: center.Z) > limit.Z)) {
            push = default;
            return false;
        }
        bool found;

        if (volume.Kind == FixedBodyColliderKind.Capsule) {
            found = CapsulePush(first: (localPosition + localOrientation.Rotate(vector: volume.Center)),
                last: (localPosition + localOrientation.Rotate(vector: volume.Endpoint)), half: box.Extent,
                radius: ((box.Radius + volume.Radius) + skin), push: out push);
        } else if (volume.Kind == FixedBodyColliderKind.Box) {
            var body = FixedOrientedBox.FromVolume(orientation: in localOrientation, position: localPosition, volume: in volume);

            found = BoxPush(body: in body, half: box.Extent, radius: (box.Radius + skin), push: out push);
        } else {
            throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}.");
        }
        push = push with { Normal = box.Rotation.Rotate(vector: push.Normal) };
        return found;
    }
}
