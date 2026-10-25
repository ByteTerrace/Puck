using Puck.Maths;

namespace Puck.Physics;

/// <summary>Deterministic compound convex-body overlap and depenetration geometry.</summary>
public static class FixedDynamicBodyContacts {
    private static bool IsSupported(FixedBodyColliderKind kind) =>
        (kind is FixedBodyColliderKind.Sphere or FixedBodyColliderKind.Capsule or FixedBodyColliderKind.Box);
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

        if (FixedRoundedBoxContact.TryPush(collider, roundPosition, roundOrientation, round, FixedQ4816.Zero, out var push)) {
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
    /// <returns>The closest-point delta, first segment minus second.</returns>
    public static FixedVector3 ClosestDelta(FixedVector3 p1, FixedVector3 q1, FixedVector3 p2, FixedVector3 q2) {
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
        var deepestSquared = UInt128.Zero;

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

                // Keep Q32 through comparison: rounding to Q16 discards shallow, real overlaps.
                var squared = ((Square(value: candidate.X.Value) + Square(value: candidate.Y.Value)) + Square(value: candidate.Z.Value));

                if (squared > deepestSquared) {
                    deepestSquared = squared;
                    correction = candidate;
                }
            }
        }

        return (deepestSquared > UInt128.Zero);
    }

    private static UInt128 Square(long value) => ((UInt128)(((Int128)value) * value));
}
