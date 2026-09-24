namespace Puck.Maths.Tests;

internal static partial class Subjects {
    /// <summary>Proves <see cref="FixedVector3.TryIntersectPlane"/> answers exactly what
    /// <see cref="Oracles.RayPlaneDistance"/> answers: the same refusal, and on success the same rounded parameter,
    /// with the refusal leaving zero behind.</summary>
    /// <param name="left">The ray: the origin's three raws, then the direction's.</param>
    /// <param name="right">The plane: the point's three raws, then the normal's.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RayPlaneMatchesOracle(long[] left, long[] right) {
        var hit = FixedVector3.TryIntersectPlane(
            direction: Space(
                x: left[3],
                y: left[4],
                z: left[5]
            ),
            distance: out var distance,
            origin: Space(
                x: left[0],
                y: left[1],
                z: left[2]
            ),
            planeNormal: Space(
                x: right[3],
                y: right[4],
                z: right[5]
            ),
            planePoint: Space(
                x: right[0],
                y: right[1],
                z: right[2]
            )
        );
        var expectedHit = Oracles.RayPlaneDistance(
            direction: left.AsSpan(
                length: 3,
                start: 3
            ),
            distance: out var expected,
            origin: left.AsSpan(
                length: 3,
                start: 0
            ),
            planeNormal: right.AsSpan(
                length: 3,
                start: 3
            ),
            planePoint: right.AsSpan(
                length: 3,
                start: 0
            )
        );

        if (hit != expectedHit) {
            return $"TryIntersectPlane returned {hit}, the oracle {expectedHit} (oracle distance raw {expected})";
        }
        if (distance.Value != expected) {
            return $"TryIntersectPlane distance raw {distance.Value}, expected {expected}";
        }

        return null;
    }

    // Hand-derived hits in whole units: (origin, direction, point, normal, expected parameter raw, or null for a
    // refusal). Each expectation is read off the geometry, not computed.
    private static readonly (long[] Origin, long[] Direction, long[] Point, long[] Normal, long? Distance)[] RayPlaneLadder = [
        // Straight down the axis onto z = 5.
        ([0, 0, 0], [0, 0, 1], [0, 0, 5], [0, 0, 1], (5L << 16)),
        // The normal's orientation does not matter.
        ([0, 0, 0], [0, 0, 1], [0, 0, 5], [0, 0, -1], (5L << 16)),
        // A direction of length two halves the parameter.
        ([0, 0, 0], [0, 0, 2], [0, 0, 5], [0, 0, 1], (5L << 15)),
        // A plane behind the origin refuses.
        ([0, 0, 0], [0, 0, -1], [0, 0, 5], [0, 0, 1], null),
        // A ray parallel to the plane refuses.
        ([0, 0, 0], [1, 0, 0], [0, 0, 5], [0, 0, 1], null),
        // A zero direction refuses.
        ([0, 0, 0], [0, 0, 0], [0, 0, 5], [0, 0, 1], null),
        // An origin on the plane hits at zero.
        ([3, 4, 5], [1, 1, 1], [0, 0, 5], [0, 0, 1], 0L),
        // A diagonal ray onto the tilted plane x + y = 6 from (1, 1, 0) along (1, 1, 0) meets it at t = 2.
        ([1, 1, 0], [1, 1, 0], [6, 0, 0], [1, 1, 0], (2L << 16)),
        // One third, rounded once: from z = 0 along z = 3 onto z = 1 is t = 1/3, whose raw 21845.33 rounds to 21845.
        ([0, 0, 0], [0, 0, 3], [0, 0, 1], [0, 0, 1], 21845L),
    ];

    /// <summary>Proves <see cref="FixedVector3.TryIntersectPlane"/> on hand-derived geometry: axis and tilted planes,
    /// either normal orientation, a scaled direction, a plane behind the origin, a parallel ray, a zero direction, an
    /// origin on the plane, and a parameter that must round.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RayPlaneKnownHits() {
        foreach (var (origin, direction, point, normal, expected) in RayPlaneLadder) {
            var hit = FixedVector3.TryIntersectPlane(
                direction: Whole(lanes: direction),
                distance: out var distance,
                origin: Whole(lanes: origin),
                planeNormal: Whole(lanes: normal),
                planePoint: Whole(lanes: point)
            );

            if (hit != expected.HasValue) {
                return $"ray ({string.Join(separator: ", ", values: origin)}) + t({string.Join(separator: ", ", values: direction)}) against plane at ({string.Join(separator: ", ", values: point)}) normal ({string.Join(separator: ", ", values: normal)}) returned {hit}";
            }
            if (distance.Value != (expected ?? 0L)) {
                return $"ray ({string.Join(separator: ", ", values: origin)}) + t({string.Join(separator: ", ", values: direction)}) distance raw {distance.Value}, expected {(expected ?? 0L)}";
            }
        }

        return null;

        static FixedVector3 Whole(long[] lanes) => Space(
            x: (lanes[0] << 16),
            y: (lanes[1] << 16),
            z: (lanes[2] << 16)
        );
    }
}
