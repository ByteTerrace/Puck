using Puck.Maths;

namespace Puck.Physics;

/// <summary>Closest-point geometry for a capped circular cylinder in its local Y frame.</summary>
internal static class FixedCylinderContact {
    internal static (FixedVector3 Point, FixedVector3 Normal, FixedQ4816 Distance) Nearest(
        FixedVector3 point, FixedQ4816 radius, FixedQ4816 halfHeight) {
        var radial = new FixedVector3(X: point.X, Y: FixedQ4816.Zero, Z: point.Z);
        var length = radial.Length;
        var side = (length - radius);
        var cap = (FixedQ4816.Abs(value: point.Y) - halfHeight);
        var radialNormal = ((length > FixedQ4816.Zero) ? (radial / length) : FixedVector3.UnitX);
        var capNormal = (FixedVector3.UnitY * FixedQ4816.CopySign(value: FixedQ4816.One, sign: point.Y));

        if ((side <= FixedQ4816.Zero) && (cap <= FixedQ4816.Zero)) {
            var normal = ((cap >= side) ? capNormal : radialNormal);
            var distance = FixedQ4816.Max(x: side, y: cap);

            return ((point - (normal * distance)), normal, distance);
        }
        var closestRadial = ((side > FixedQ4816.Zero) ? (radialNormal * radius) : radial);
        var closest = new FixedVector3(X: closestRadial.X,
            Y: FixedQ4816.Clamp(value: point.Y, minimum: -halfHeight, maximum: halfHeight), Z: closestRadial.Z);
        var delta = (point - closest);
        var gap = delta.Length;

        return (closest, (delta / gap), gap);
    }
    internal static bool TryPush(in FixedStaticCollider cylinder, FixedVector3 position,
        in FixedQuaternion orientation, in FixedBodyColliderVolume volume, FixedQ4816 skin,
        out FixedContactPush push) {
        var rotation = cylinder.Rotation;
        var first = rotation.RotateInverse(vector: ((position + orientation.Rotate(vector: volume.Center)) - cylinder.Center));
        var last = ((volume.Kind == FixedBodyColliderKind.Capsule)
            ? rotation.RotateInverse(vector: ((position + orientation.Rotate(vector: volume.Endpoint)) - cylinder.Center))
            : first);
        var radius = (volume.Radius + skin);
        var cylinderRadius = cylinder.Extent.X;
        var halfHeight = cylinder.Extent.Y;

        if (volume.Kind == FixedBodyColliderKind.Box) {
            // The existing capsule/box path is conservative at box edges. Here retain the body's bounds in
            // the cylinder frame, but test its rectangle against the circle rather than against a square.
            var (_, extent) = FixedColliderBounds.WorldBounds(position: FixedVector3.Zero,
                orientation: (rotation.Conjugate() * orientation).Normalize(), volume: in volume);
            var radial = new FixedVector3(
                X: (first.X - FixedQ4816.Clamp(value: first.X, minimum: -extent.X, maximum: extent.X)),
                Y: FixedQ4816.Zero,
                Z: (first.Z - FixedQ4816.Clamp(value: first.Z, minimum: -extent.Z, maximum: extent.Z)));
            var radialLength = radial.Length;
            var side = ((cylinderRadius + skin) - radialLength);
            var cap = (((halfHeight + extent.Y) + skin) - FixedQ4816.Abs(value: first.Y));
            var sideNormal = ((radialLength > FixedQ4816.Zero) ? (radial / radialLength) : FixedVector3.UnitX);

            if (radialLength == FixedQ4816.Zero) {
                var xGap = (extent.X - FixedQ4816.Abs(value: first.X));
                var zGap = (extent.Z - FixedQ4816.Abs(value: first.Z));

                side += FixedQ4816.Min(x: xGap, y: zGap);
                sideNormal = ((xGap <= zGap)
                    ? (FixedVector3.UnitX * FixedQ4816.CopySign(value: FixedQ4816.One, sign: first.X))
                    : (FixedVector3.UnitZ * FixedQ4816.CopySign(value: FixedQ4816.One, sign: first.Z)));
            }
            if ((side <= FixedQ4816.Zero) || (cap <= FixedQ4816.Zero)) {
                push = default;
                return false;
            }
            var normal = ((cap <= side) ? (FixedVector3.UnitY * FixedQ4816.CopySign(value: FixedQ4816.One, sign: first.Y))
                : sideNormal);

            push = new FixedContactPush(Normal: rotation.Rotate(vector: normal), Penetration: FixedQ4816.Min(x: side, y: cap));
            return true;
        }

        var nearest = Nearest(halfHeight: halfHeight, point: first, radius: cylinderRadius);

        if ((last != first) && (first.X == last.X) && (first.Z == last.Z)) {
            var middle = new FixedVector3(X: first.X,
                Y: FixedQ4816.Clamp(value: FixedQ4816.Zero,
                    minimum: FixedQ4816.Min(x: first.Y, y: last.Y),
                    maximum: FixedQ4816.Max(x: first.Y, y: last.Y)), Z: first.Z);

            nearest = Nearest(halfHeight: halfHeight, point: middle, radius: cylinderRadius);
        } else if (last != first) {
            var endpoint = Nearest(halfHeight: halfHeight, point: last, radius: cylinderRadius);

            if (endpoint.Distance < nearest.Distance) { nearest = endpoint; }
            // Distance to a convex cylinder along a segment is convex. Narrow the interval on the fixed
            // parameter grid, retaining endpoints and deterministic left ties. No floating arithmetic.
            var low = FixedQ4816.Zero;
            var high = FixedQ4816.One;
            var three = FixedQ4816.FromInteger(value: 3);

            for (var iteration = 0; ((iteration < 32) && ((high - low) > FixedQ4816.Epsilon)); iteration++) {
                var third = ((high - low) / three);
                var left = (low + third);
                var right = (high - third);
                var a = Nearest(point: FixedVector3.Lerp(amount: left, from: first, to: last), radius: cylinderRadius, halfHeight: halfHeight);
                var b = Nearest(point: FixedVector3.Lerp(amount: right, from: first, to: last), radius: cylinderRadius, halfHeight: halfHeight);

                if (a.Distance < nearest.Distance) { nearest = a; }
                if (b.Distance < nearest.Distance) { nearest = b; }
                if (a.Distance <= b.Distance) { high = right; } else { low = left; }
            }
        }
        if (nearest.Distance >= radius) {
            push = default;
            return false;
        }
        push = new FixedContactPush(Normal: rotation.Rotate(vector: nearest.Normal), Penetration: (radius - nearest.Distance),
            IsEdge: ((nearest.Normal.Y != FixedQ4816.Zero) && ((nearest.Normal.X != FixedQ4816.Zero) || (nearest.Normal.Z != FixedQ4816.Zero))));
        return true;
    }
}
