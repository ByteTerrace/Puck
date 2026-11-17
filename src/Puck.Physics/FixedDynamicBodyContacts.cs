using System.Numerics;
using Puck.Maths;

namespace Puck.Physics;

/// <summary>Deterministic compound convex-body overlap and depenetration geometry.</summary>
public static class FixedDynamicBodyContacts {
    private static bool IsSupported(FixedBodyColliderKind kind) =>
        (kind is FixedBodyColliderKind.Sphere or FixedBodyColliderKind.Capsule or FixedBodyColliderKind.Box or FixedBodyColliderKind.Ellipsoid);
    private static bool TryVolumeCorrection(
        FixedVector3 leftPosition,
        FixedQuaternion leftOrientation,
        in FixedBodyColliderVolume left,
        FixedVector3 rightPosition,
        FixedQuaternion rightOrientation,
        in FixedBodyColliderVolume right,
        int tieBreaker,
        FixedQ4816 margin,
        out FixedVector3 correction
    ) {
        if (
            !IsSupported(kind: left.Kind) ||
            !IsSupported(kind: right.Kind)
        ) {
            throw new InvalidOperationException(message: $"Unknown body collider pair {left.Kind}/{right.Kind}.");
        }

        if ((left.Kind == FixedBodyColliderKind.Ellipsoid) || (right.Kind == FixedBodyColliderKind.Ellipsoid)) {
            var ellipsoidIsLeft = (left.Kind == FixedBodyColliderKind.Ellipsoid);
            var ellipsoid = (ellipsoidIsLeft ? left : right);
            var shapePosition = (ellipsoidIsLeft ? leftPosition : rightPosition);
            var shapeOrientation = (ellipsoidIsLeft ? leftOrientation : rightOrientation);
            var ellipsoidCollider = FixedStaticCollider.Superellipsoid(
                (shapePosition + shapeOrientation.Rotate(vector: ellipsoid.Center)),
                (shapeOrientation * ellipsoid.Rotation).Normalize(), ellipsoid.HalfExtents,
                FixedQ4816.FromInteger(value: 2));
            var moving = (ellipsoidIsLeft ? right : left);
            var movingPosition = (ellipsoidIsLeft ? rightPosition : leftPosition);
            var movingOrientation = (ellipsoidIsLeft ? rightOrientation : leftOrientation);
            var found = FixedConvexContact.TryPush(collider: in ellipsoidCollider, orientation: in movingOrientation, position: movingPosition,
                push: out var ellipsoidPush, skin: margin, volume: in moving);

            correction = (found ? (ellipsoidPush.Normal * (ellipsoidIsLeft ? -ellipsoidPush.Penetration : ellipsoidPush.Penetration)) : FixedVector3.Zero);
            return found;
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
            var found = TryRoundPush(
                p1: leftStart,
                q1: leftEnd,
                p2: rightStart,
                q2: rightEnd,
                radius: ((left.Radius + right.Radius) + margin),
                tieNormal: (((tieBreaker & 1) == 0) ? FixedVector3.UnitX : -FixedVector3.UnitX),
                push: out var roundPush
            );

            correction = (found ? (roundPush.Normal * roundPush.Penetration) : default);
            return found;
        }

        if (
            (left.Kind == FixedBodyColliderKind.Box) &&
            (right.Kind == FixedBodyColliderKind.Box)
        ) {
            return FixedOrientedBox.TrySeparation(
                correction: out correction,
                left: FixedOrientedBox.FromVolume(
                    orientation: in leftOrientation,
                    position: leftPosition,
                    volume: in left
                ),
                right: FixedOrientedBox.FromVolume(
                    orientation: in rightOrientation,
                    position: rightPosition,
                    volume: in right
                ),
                padding: margin,
                tieBreaker: tieBreaker
            );
        }

        // Exactly one side is a box. Share its closest features with static sharp/rounded boxes;
        // the other side is a sphere or capsule, and reversing the moving side reverses the push.
        var roundIsLeft = (left.Kind != FixedBodyColliderKind.Box);
        var round = (roundIsLeft ? left : right);
        var box = (roundIsLeft ? right : left);
        var roundPosition = (roundIsLeft ? leftPosition : rightPosition);
        var roundOrientation = (roundIsLeft ? leftOrientation : rightOrientation);
        var boxPosition = (roundIsLeft ? rightPosition : leftPosition);
        var boxOrientation = (roundIsLeft ? rightOrientation : leftOrientation);
        var collider = FixedStaticCollider.RoundedBox(
            center: (boxPosition + boxOrientation.Rotate(vector: box.Center)),
            rotation: (boxOrientation * box.Rotation).Normalize(),
            innerHalfExtents: box.HalfExtents, radius: FixedQ4816.Zero);

        if (FixedRoundedBoxContact.TryPush(box: collider, orientation: roundOrientation, position: roundPosition, push: out var push, skin: margin, volume: round)) {
            correction = (push.Normal * (roundIsLeft ? push.Penetration : -push.Penetration));
            return true;
        }
        correction = default;
        return false;
    }

    /// <summary>Returns a rotation-independent sphere enclosing every supplied local volume.</summary>
    /// <param name="volumes">The compound collider's local volumes.</param>
    /// <returns>A conservative broadphase radius.</returns>
    public static FixedQ4816 BroadphaseRadius(ReadOnlySpan<FixedBodyColliderVolume> volumes) {
        var radius = FixedQ4816.Zero;

        foreach (ref readonly var volume in volumes) {
            var extent = volume.Kind switch {
                FixedBodyColliderKind.Sphere => (volume.Center.Length + volume.Radius),
                // The longer endpoint, chosen on the exact squares and rooted once: Length is monotone in the sum.
                FixedBodyColliderKind.Capsule => (((volume.Center.CompareLengthTo(other: volume.Endpoint) >= 0)
                    ? volume.Center
                    : volume.Endpoint).Length + volume.Radius),
                FixedBodyColliderKind.Box => (volume.Center.Length + volume.HalfExtents.Length),
                FixedBodyColliderKind.Ellipsoid => (volume.Center.Length + FixedQ4816.Max(x: volume.HalfExtents.X,
                    y: FixedQ4816.Max(x: volume.HalfExtents.Y, y: volume.HalfExtents.Z))),
                _ => throw new InvalidOperationException(message: $"Unknown body collider kind {volume.Kind}."),
            };

            radius = FixedQ4816.Max(
                x: radius,
                y: extent
            );
        }
        return radius;
    }
    /// <summary>Returns the world-space core segment of a sphere or capsule volume at a pose: a capsule's two
    /// endpoints, or a sphere's center twice.</summary>
    /// <param name="position">The body's world position.</param>
    /// <param name="orientation">The body's world orientation.</param>
    /// <param name="volume">The body-local volume.</param>
    /// <returns>The segment's start (the rotated center) and end (the rotated endpoint for a capsule, the start
    /// otherwise).</returns>
    public static (FixedVector3 Start, FixedVector3 End) Segment(FixedVector3 position, FixedQuaternion orientation, in FixedBodyColliderVolume volume) {
        var start = (position + orientation.Rotate(vector: volume.Center));
        var end = ((volume.Kind == FixedBodyColliderKind.Capsule)
            ? (position + orientation.Rotate(vector: volume.Endpoint))
            : start
        );

        return (start, end);
    }
    /// <summary>Computes the vector from segment <c>[p2, q2]</c>'s closest point to segment <c>[p1, q1]</c>'s — the
    /// one deterministic segment-to-segment witness every capsule/sphere pair test (the contact solver here, the
    /// event feed's overlap sense) measures through, so no consumer carries a second clamp order.</summary>
    /// <param name="p1">The first segment's start.</param>
    /// <param name="q1">The first segment's end (equal to <paramref name="p1"/> for a sphere).</param>
    /// <param name="p2">The second segment's start.</param>
    /// <param name="q2">The second segment's end (equal to <paramref name="p2"/> for a sphere).</param>
    /// <returns>The exact closest-point delta, first segment minus second, rounded once per component to nearest,
    /// ties to even. Parameters and endpoint differences retain full precision.</returns>
    /// <exception cref="OverflowException">A component of the rounded delta cannot fit Q48.16.</exception>
    public static FixedVector3 ClosestDelta(FixedVector3 p1, FixedVector3 q1, FixedVector3 p2, FixedVector3 q2) {
        try {
            return ClosestDelta<Int128>(p1: p1, p2: p2, q1: q1, q2: q2);
        } catch (OverflowException) {
            // The same solve, widened rather than rescaled: extreme endpoints must not lose low bits.
            return ClosestDelta<BigInteger>(p1: p1, p2: p2, q1: q1, q2: q2);
        }
    }

    private static FixedVector3 ClosestDelta<T>(FixedVector3 p1, FixedVector3 q1, FixedVector3 p2, FixedVector3 q2)
        where T : IBinaryInteger<T> {
        checked {
            // Widen BEFORE subtracting, including for a degenerate segment at an extreme coordinate.
            var ux = (T.CreateChecked(value: q1.X.Value) - T.CreateChecked(value: p1.X.Value));
            var uy = (T.CreateChecked(value: q1.Y.Value) - T.CreateChecked(value: p1.Y.Value));
            var uz = (T.CreateChecked(value: q1.Z.Value) - T.CreateChecked(value: p1.Z.Value));
            var vx = (T.CreateChecked(value: q2.X.Value) - T.CreateChecked(value: p2.X.Value));
            var vy = (T.CreateChecked(value: q2.Y.Value) - T.CreateChecked(value: p2.Y.Value));
            var vz = (T.CreateChecked(value: q2.Z.Value) - T.CreateChecked(value: p2.Z.Value));
            var rx = (T.CreateChecked(value: p1.X.Value) - T.CreateChecked(value: p2.X.Value));
            var ry = (T.CreateChecked(value: p1.Y.Value) - T.CreateChecked(value: p2.Y.Value));
            var rz = (T.CreateChecked(value: p1.Z.Value) - T.CreateChecked(value: p2.Z.Value));
            var a = (((ux * ux) + (uy * uy)) + (uz * uz));
            var e = (((vx * vx) + (vy * vy)) + (vz * vz));
            var b = (((ux * vx) + (uy * vy)) + (uz * vz));
            var c = (((ux * rx) + (uy * ry)) + (uz * rz));
            var f = (((vx * rx) + (vy * ry)) + (vz * rz));
            // Both parameters share a positive denominator. Nothing rounds until the final delta.
            var denominator = T.One;
            var s = T.Zero;
            var t = T.Zero;

            if (a == T.Zero) {
                if (e > T.Zero) {
                    denominator = e;
                    t = T.Clamp(f, T.Zero, e);
                }
            } else if (e == T.Zero) {
                denominator = a;
                s = T.Clamp(-c, T.Zero, a);
            } else {
                var determinant = ((a * e) - (b * b));
                var sDenominator = ((determinant > T.Zero) ? determinant : T.One);
                var sNumerator = ((determinant > T.Zero) ? T.Clamp(((b * f) - (c * e)), T.Zero, determinant) : T.Zero);
                // At an endpoint t projects onto the second segment; in the interior use the other
                // determinant numerator directly, avoiding an unnecessary sixth-degree product.
                var tNumerator = ((sNumerator == T.Zero) ? f : ((sNumerator == sDenominator) ? (b + f) : ((a * f) - (b * c))));
                var tDenominator = (((sNumerator == T.Zero) || (sNumerator == sDenominator)) ? e : determinant);

                if (tNumerator < T.Zero) {
                    denominator = a;
                    s = T.Clamp(-c, T.Zero, a);
                } else if (tNumerator > tDenominator) {
                    denominator = a;
                    s = T.Clamp((b - c), T.Zero, a);
                    t = a;
                } else if (sNumerator == T.Zero) {
                    denominator = e;
                    t = f;
                } else if (sNumerator == sDenominator) {
                    denominator = e;
                    s = e;
                    t = (b + f);
                } else {
                    denominator = determinant;
                    s = sNumerator;
                    t = ((a * f) - (b * c));
                }
            }
            // Form the displacement directly, rather than rounding two world-space witnesses separately.
            var x = (((rx * denominator) + (ux * s)) - (vx * t));
            var y = (((ry * denominator) + (uy * s)) - (vy * t));
            var z = (((rz * denominator) + (uz * s)) - (vz * t));

            var (qx, remx) = T.DivRem(left: T.Abs(value: x), right: denominator);
            var (qy, remy) = T.DivRem(left: T.Abs(value: y), right: denominator);
            var (qz, remz) = T.DivRem(left: T.Abs(value: z), right: denominator);
            qx = FixedPointRounding.RoundToNearestTiesToEven(distanceToNext: (denominator - remx), distanceToTruncated: remx, truncated: qx);
            qy = FixedPointRounding.RoundToNearestTiesToEven(distanceToNext: (denominator - remy), distanceToTruncated: remy, truncated: qy);
            qz = FixedPointRounding.RoundToNearestTiesToEven(distanceToNext: (denominator - remz), distanceToTruncated: remz, truncated: qz);
            return new FixedVector3(
                X: FixedQ4816.FromRawBits(value: long.CreateChecked(value: ((x < T.Zero) ? -qx : qx))),
                Y: FixedQ4816.FromRawBits(value: long.CreateChecked(value: ((y < T.Zero) ? -qy : qy))),
                Z: FixedQ4816.FromRawBits(value: long.CreateChecked(value: ((z < T.Zero) ? -qz : qz))));
        }
    }

    /// <summary>Separates two round cores (a capsule's segment, or a sphere's center given as a degenerate segment),
    /// each grown by its radius: the one round-pair contact rule static spheres, dynamic sphere/capsule pairs and the
    /// event feed's overlap sense all answer through.</summary>
    /// <param name="p1">The first core's start.</param>
    /// <param name="q1">The first core's end (equal to <paramref name="p1"/> for a sphere).</param>
    /// <param name="p2">The second core's start.</param>
    /// <param name="q2">The second core's end (equal to <paramref name="p2"/> for a sphere).</param>
    /// <param name="radius">The summed radii, in metres, plus any contact skin or margin.</param>
    /// <param name="tieNormal">The unit direction pushed along when the cores meet exactly, where no witness has a
    /// direction.</param>
    /// <param name="push">The push moving the first core out of the second: the closest-point direction and
    /// <paramref name="radius"/> less the full-width distance.</param>
    /// <returns><see langword="true"/> when the rounded full-width distance is below <paramref name="radius"/>, so a
    /// reported push is always positive and a touching pair reports none; an exactly coincident pair reports
    /// <paramref name="tieNormal"/> at the whole radius.</returns>
    internal static bool TryRoundPush(FixedVector3 p1, FixedVector3 q1, FixedVector3 p2, FixedVector3 q2, FixedQ4816 radius,
        FixedVector3 tieNormal, out FixedContactPush push) {
        var delta = ClosestDelta(p1: p1, p2: p2, q1: q1, q2: q2);

        if (delta == FixedVector3.Zero) {
            push = new FixedContactPush(Normal: tieNormal, Penetration: radius);
            return (radius > FixedQ4816.Zero);
        }
        // Length roots the exact raw square sum once, so a sub-millimetre gap keeps its direction instead of rounding
        // to the coincident case.
        var distance = delta.Length;

        if (distance >= radius) {
            push = default;
            return false;
        }
        push = new FixedContactPush(Normal: delta.Normalize(), Penetration: (radius - distance));
        return true;
    }

    /// <summary>Returns whether any volume pair of two compound colliders overlaps: exactly the pairs
    /// <see cref="TryCorrection"/> depenetrates at zero margin, so overlap sense and contact can never disagree.</summary>
    /// <param name="leftPosition">The left body's world position.</param>
    /// <param name="leftOrientation">The left body's world orientation.</param>
    /// <param name="leftVolumes">The left body's local volumes.</param>
    /// <param name="rightPosition">The right body's world position.</param>
    /// <param name="rightOrientation">The right body's world orientation.</param>
    /// <param name="rightVolumes">The right body's local volumes.</param>
    /// <returns><see langword="true"/> when some pair's narrowphase reports a positive push; a touching pair does not
    /// overlap.</returns>
    public static bool Overlaps(
        FixedVector3 leftPosition,
        FixedQuaternion leftOrientation,
        ReadOnlySpan<FixedBodyColliderVolume> leftVolumes,
        FixedVector3 rightPosition,
        FixedQuaternion rightOrientation,
        ReadOnlySpan<FixedBodyColliderVolume> rightVolumes
    ) {
        foreach (ref readonly var left in leftVolumes) {
            foreach (ref readonly var right in rightVolumes) {
                if (TryVolumeCorrection(correction: out _, left: in left, leftOrientation: leftOrientation,
                    leftPosition: leftPosition, margin: FixedQ4816.Zero, right: in right, rightOrientation: rightOrientation,
                    rightPosition: rightPosition, tieBreaker: 0)) {
                    return true;
                }
            }
        }
        return false;
    }
    /// <summary>Finds the deepest overlap correction from <paramref name="rightVolumes"/> to
    /// <paramref name="leftVolumes"/>, every volume grown by half of <paramref name="margin"/>: a pair whose gap is under
    /// the margin reports a correction whose length is the margin less the gap, so the signed overlap depth is the
    /// correction's length less the margin.</summary>
    /// <param name="leftPosition">The left body's world position.</param>
    /// <param name="leftOrientation">The left body's world orientation.</param>
    /// <param name="leftVolumes">The left body's local volumes.</param>
    /// <param name="rightPosition">The right body's world position.</param>
    /// <param name="rightOrientation">The right body's world orientation.</param>
    /// <param name="rightVolumes">The right body's local volumes.</param>
    /// <param name="tieBreaker">A stable value selecting an axis sign when centers coincide.</param>
    /// <param name="correction">The deepest correction, directed from right to left.</param>
    /// <param name="margin">The non-negative gap, in metres, within which a separated pair still reports a correction;
    /// zero reports only overlapping pairs.</param>
    /// <returns><see langword="true"/> when any volume pair overlaps or lies within the margin.</returns>
    public static bool TryCorrection(
        FixedVector3 leftPosition,
        FixedQuaternion leftOrientation,
        ReadOnlySpan<FixedBodyColliderVolume> leftVolumes,
        FixedVector3 rightPosition,
        FixedQuaternion rightOrientation,
        ReadOnlySpan<FixedBodyColliderVolume> rightVolumes,
        int tieBreaker,
        out FixedVector3 correction,
        FixedQ4816 margin = default
    ) {
        correction = FixedVector3.Zero;

        foreach (ref readonly var left in leftVolumes) {
            foreach (ref readonly var right in rightVolumes) {
                if (!TryVolumeCorrection(
                    correction: out var candidate,
                    left: in left,
                    leftOrientation: leftOrientation,
                    leftPosition: leftPosition,
                    margin: margin,
                    right: in right,
                    rightOrientation: rightOrientation,
                    rightPosition: rightPosition,
                    tieBreaker: tieBreaker
                )) {
                    continue;
                }

                // Keep Q32 through comparison: rounding to Q16 discards shallow, real overlaps.
                if (candidate.CompareLengthTo(other: correction) > 0) {
                    correction = candidate;
                }
            }
        }

        return (correction != FixedVector3.Zero);
    }
}
