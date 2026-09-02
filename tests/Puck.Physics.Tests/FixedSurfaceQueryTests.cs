using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// The surface-attach query's laws: per-kind exactness, the unit-outward-normal contract, reach, deterministic
/// tie-breaking under array reordering, and the directed variant's angle-before-distance ordering.
/// </summary>
public sealed class FixedSurfaceQueryTests {
    private static FixedVector3 V(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    // An independent (test-only, non-SUT) containment check per collider kind — the oracle the "normal is outward"
    // law measures against, so it cannot pass merely by agreeing with the query's own internal branching.
    private static bool IsStrictlyOutside(FixedStaticCollider collider, FixedVector3 point) => collider.Kind switch {
        FixedStaticColliderKind.Sphere => ((point - collider.Center).Length > collider.Extent.X),
        FixedStaticColliderKind.AxisAlignedBox =>
            ((FixedQ4816.Abs(value: (point.X - collider.Center.X)) > collider.Extent.X) ||
            (FixedQ4816.Abs(value: (point.Y - collider.Center.Y)) > collider.Extent.Y) ||
            (FixedQ4816.Abs(value: (point.Z - collider.Center.Z)) > collider.Extent.Z)),
        FixedStaticColliderKind.HalfSpace => (FixedVector3.Dot(
            left: (point - collider.Center),
            right: collider.Extent
        ) > FixedQ4816.Zero),
        _ => throw new InvalidOperationException(message: $"Unknown collider kind {collider.Kind}."),
    };

    [Fact]
    public void BoxFaceNearestPointAndNormalAreBitExact() {
        var box = FixedStaticCollider.AxisAlignedBox(
            center: FixedVector3.Zero,
            halfExtents: V(x: 1d, y: 1d, z: 1d)
        );
        var probe = V(x: 3d, y: 0.2d, z: 0.3d);

        var found = FixedSurfaceQuery.TryNearest(
            colliders: [box],
            dynamicColliders: [],
            probe: in probe,
            reach: Q(value: 10d),
            candidate: out var candidate
        );

        Assert.True(condition: found);
        Assert.Equal(
            expected: V(x: 1d, y: 0.2d, z: 0.3d),
            actual: candidate.Point
        );
        Assert.Equal(
            expected: V(x: 1d, y: 0d, z: 0d),
            actual: candidate.Normal
        );
        Assert.Equal(
            expected: Q(value: 2d),
            actual: candidate.Distance
        );
        Assert.Equal(
            expected: FixedSurfaceColliderSource.Static,
            actual: candidate.Source
        );
        Assert.Equal(
            expected: 0,
            actual: candidate.ColliderIndex
        );
    }
    [Fact]
    public void BoxInteriorProjectsOutThroughTheNearestFaceIncludingExactlyOnTheBoundary() {
        var box = FixedStaticCollider.AxisAlignedBox(
            center: FixedVector3.Zero,
            halfExtents: V(x: 1d, y: 1d, z: 1d)
        );
        var interior = V(x: 0.9d, y: 0d, z: 0d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [box],
            dynamicColliders: [],
            probe: in interior,
            reach: Q(value: 10d),
            candidate: out var interiorCandidate
        ));
        Assert.Equal(
            expected: V(x: 1d, y: 0d, z: 0d),
            actual: interiorCandidate.Point
        );
        Assert.Equal(
            expected: V(x: 1d, y: 0d, z: 0d),
            actual: interiorCandidate.Normal
        );
        Assert.Equal(
            expected: Q(value: 0.1d),
            actual: interiorCandidate.Distance
        );

        // Exactly on the boundary: no axis needs clamping (the interior branch), and the gap on that axis is
        // already zero, so it wins the face selection and the probe is returned as its own nearest point.
        var onBoundary = V(x: 1d, y: 0d, z: 0d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [box],
            dynamicColliders: [],
            probe: in onBoundary,
            reach: Q(value: 10d),
            candidate: out var boundaryCandidate
        ));
        Assert.Equal(
            expected: onBoundary,
            actual: boundaryCandidate.Point
        );
        Assert.Equal(
            expected: FixedQ4816.Zero,
            actual: boundaryCandidate.Distance
        );
    }
    [Fact]
    public void BoxEdgeAndCornerPointsAreExactAndNormalsMatchTheIndependentUnitDelta() {
        var box = FixedStaticCollider.AxisAlignedBox(
            center: FixedVector3.Zero,
            halfExtents: V(x: 1d, y: 1d, z: 1d)
        );
        var edgeProbe = V(x: 3d, y: 3d, z: 0.3d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [box],
            dynamicColliders: [],
            probe: in edgeProbe,
            reach: Q(value: 10d),
            candidate: out var edgeCandidate
        ));
        Assert.Equal(
            expected: V(x: 1d, y: 1d, z: 0.3d),
            actual: edgeCandidate.Point
        );
        // The bound this collider kind's edge/corner normal carries is Normalize()'s own correctly-rounded root —
        // built here from the case's own known geometry, not read back from the query's internals.
        Assert.Equal(
            expected: V(x: 2d, y: 2d, z: 0d).Normalize(),
            actual: edgeCandidate.Normal
        );

        var cornerProbe = V(x: 3d, y: 3d, z: 3d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [box],
            dynamicColliders: [],
            probe: in cornerProbe,
            reach: Q(value: 10d),
            candidate: out var cornerCandidate
        ));
        Assert.Equal(
            expected: V(x: 1d, y: 1d, z: 1d),
            actual: cornerCandidate.Point
        );
        Assert.Equal(
            expected: V(x: 2d, y: 2d, z: 2d).Normalize(),
            actual: cornerCandidate.Normal
        );
        // A symmetric delta must produce a symmetric unit normal — a sign or axis-swap defect would break this even
        // if it happened to reuse Normalize() correctly elsewhere.
        Assert.Equal(
            expected: cornerCandidate.Normal.X,
            actual: cornerCandidate.Normal.Y
        );
        Assert.Equal(
            expected: cornerCandidate.Normal.Y,
            actual: cornerCandidate.Normal.Z
        );
    }
    [Fact]
    public void SphereAlongAnAxisIsBitExact() {
        var sphere = FixedStaticCollider.Sphere(
            center: FixedVector3.Zero,
            radius: Q(value: 2d)
        );
        var probe = V(x: 10d, y: 0d, z: 0d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [sphere],
            dynamicColliders: [],
            probe: in probe,
            reach: Q(value: 100d),
            candidate: out var candidate
        ));
        Assert.Equal(
            expected: V(x: 2d, y: 0d, z: 0d),
            actual: candidate.Point
        );
        Assert.Equal(
            expected: V(x: 1d, y: 0d, z: 0d),
            actual: candidate.Normal
        );
        Assert.Equal(
            expected: Q(value: 8d),
            actual: candidate.Distance
        );
    }
    [Fact]
    public void SphereProbedAtItsOwnCenterReportsTheCanonicalUpRatherThanAnUndefinedDirection() {
        var sphere = FixedStaticCollider.Sphere(
            center: V(x: 5d, y: 5d, z: 5d),
            radius: Q(value: 1d)
        );
        var probe = V(x: 5d, y: 5d, z: 5d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [sphere],
            dynamicColliders: [],
            probe: in probe,
            reach: Q(value: 10d),
            candidate: out var candidate
        ));
        Assert.Equal(
            expected: V(x: 0d, y: 1d, z: 0d),
            actual: candidate.Normal
        );
        Assert.Equal(
            expected: V(x: 5d, y: 6d, z: 5d),
            actual: candidate.Point
        );
    }
    [Fact]
    public void HalfSpaceProjectionIsExactOnBothSides() {
        var plane = FixedStaticCollider.HalfSpace(
            point: V(x: 0d, y: 5d, z: 0d),
            normal: V(x: 0d, y: 1d, z: 0d)
        );
        var outside = V(x: 3d, y: 8d, z: -1d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [plane],
            dynamicColliders: [],
            probe: in outside,
            reach: Q(value: 10d),
            candidate: out var outsideCandidate
        ));
        Assert.Equal(
            expected: V(x: 3d, y: 5d, z: -1d),
            actual: outsideCandidate.Point
        );
        Assert.Equal(
            expected: Q(value: 3d),
            actual: outsideCandidate.Distance
        );

        // The disallowed side still reports the collider's own normal — a fixed, collider-intrinsic outward
        // direction, never flipped to face the probe.
        var behind = V(x: 0d, y: 1d, z: 0d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [plane],
            dynamicColliders: [],
            probe: in behind,
            reach: Q(value: 10d),
            candidate: out var behindCandidate
        ));
        Assert.Equal(
            expected: V(x: 0d, y: 5d, z: 0d),
            actual: behindCandidate.Point
        );
        Assert.Equal(
            expected: V(x: 0d, y: 1d, z: 0d),
            actual: behindCandidate.Normal
        );
        Assert.Equal(
            expected: Q(value: 4d),
            actual: behindCandidate.Distance
        );
    }
    [Fact]
    public void NormalsAreUnitLengthAndPushOutwardAcrossEveryColliderKind() {
        FixedStaticCollider[] colliders = [
            FixedStaticCollider.AxisAlignedBox(center: FixedVector3.Zero, halfExtents: V(x: 1d, y: 1d, z: 1d)),
            FixedStaticCollider.Sphere(center: V(x: 5d, y: 0d, z: 0d), radius: Q(value: 1d)),
            FixedStaticCollider.HalfSpace(point: V(x: 0d, y: -3d, z: 0d), normal: V(x: 0d, y: 1d, z: 0d)),
        ];
        FixedVector3[] probes = [
            V(x: 3d, y: 0.2d, z: 0.3d), // box face
            V(x: 3d, y: 3d, z: 0.3d), // box edge
            V(x: 3d, y: 3d, z: 3d), // box corner
            V(x: 0.9d, y: 0d, z: 0d), // box interior
            V(x: 8d, y: 1d, z: -1d), // sphere, off-axis
            V(x: 12d, y: 0d, z: 5d), // half-space
        ];
        var step = Q(value: 0.01d);

        foreach (var collider in colliders) {
            foreach (var probe in probes) {
                var found = FixedSurfaceQuery.TryNearest(
                    colliders: [collider],
                    dynamicColliders: [],
                    probe: in probe,
                    reach: Q(value: 1000d),
                    candidate: out var candidate
                );

                Assert.True(condition: found);
                MeasurementAssert.Near(
                    actual: ((double)candidate.Normal.Length),
                    expected: 1d,
                    subject: $"{collider.Kind} normal length for probe {probe}",
                    tolerance: 0.001d
                );

                var steppedOut = (candidate.Point + (candidate.Normal * step));

                Assert.True(
                    condition: IsStrictlyOutside(collider: collider, point: steppedOut),
                    userMessage: $"{collider.Kind}: stepping from the nearest point along its own normal must leave the solid (probe {probe})"
                );
            }
        }
    }
    [Fact]
    public void ReachExcludesBeyondRAndIncludesExactlyAtR() {
        var plane = FixedStaticCollider.HalfSpace(
            point: FixedVector3.Zero,
            normal: V(x: 0d, y: 1d, z: 0d)
        );
        var probe = V(x: 0d, y: 5d, z: 0d);

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [plane],
            dynamicColliders: [],
            probe: in probe,
            reach: Q(value: 5d),
            candidate: out _
        ));
        Assert.False(condition: FixedSurfaceQuery.TryNearest(
            colliders: [plane],
            dynamicColliders: [],
            probe: in probe,
            reach: (Q(value: 5d) - FixedQ4816.Epsilon),
            candidate: out _
        ));
    }
    [Fact]
    public void NoColliderWithinReachReportsNoResult() {
        var sphere = FixedStaticCollider.Sphere(
            center: FixedVector3.Zero,
            radius: Q(value: 1d)
        );
        var probe = V(x: 100d, y: 0d, z: 0d);

        Assert.False(condition: FixedSurfaceQuery.TryNearest(
            colliders: [sphere],
            dynamicColliders: [],
            probe: in probe,
            reach: Q(value: 1d),
            candidate: out var candidate
        ));
        Assert.Equal(
            actual: candidate,
            expected: default
        );
    }
    [Fact]
    public void NegativeReachRefuses() {
        var probe = FixedVector3.Zero;

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => FixedSurfaceQuery.TryNearest(
            colliders: [],
            dynamicColliders: [],
            probe: in probe,
            reach: -FixedQ4816.One,
            candidate: out _
        ));
    }
    // Prove the law once by breaking it: an exact tie must resolve to whichever tied collider currently sits at
    // the lower array index, and the winning geometry itself must not depend on how the rest of the set is
    // ordered around it.
    [Fact]
    public void ResultIsDeterministicAndTieBreaksByAscendingIndexAcrossOneThousandShuffledRebuilds() {
        var probe = FixedVector3.Zero;
        // Nine colliders at distinct distances plus one exact tie pair (indices reserved for "tiedA"/"tiedB" at the
        // same distance from probe, distinct positions so the winner is observably different depending on order).
        FixedStaticCollider[] baseline = [
            FixedStaticCollider.Sphere(center: V(x: 10d, y: 0d, z: 0d), radius: Q(value: 1d)),
            FixedStaticCollider.Sphere(center: V(x: 0d, y: 20d, z: 0d), radius: Q(value: 1d)),
            FixedStaticCollider.Sphere(center: V(x: 0d, y: 0d, z: 30d), radius: Q(value: 1d)),
            FixedStaticCollider.Sphere(center: V(x: -40d, y: 0d, z: 0d), radius: Q(value: 1d)),
            FixedStaticCollider.Sphere(center: V(x: 0d, y: -50d, z: 0d), radius: Q(value: 1d)),
            FixedStaticCollider.Sphere(center: V(x: 0d, y: 0d, z: -60d), radius: Q(value: 1d)),
            FixedStaticCollider.Sphere(center: V(x: 70d, y: 70d, z: 0d), radius: Q(value: 1d)),
            FixedStaticCollider.Sphere(center: V(x: 0d, y: 80d, z: 80d), radius: Q(value: 1d)),
            FixedStaticCollider.HalfSpace(point: V(x: 0d, y: 0d, z: 90d), normal: V(x: 0d, y: 0d, z: 1d)),
            FixedStaticCollider.Sphere(center: V(x: 5d, y: 0d, z: 0d), radius: Q(value: 1d)), // "tiedA": distance 4
            FixedStaticCollider.Sphere(center: V(x: 0d, y: 5d, z: 0d), radius: Q(value: 1d)), // "tiedB": distance 4
        ];
        const int TiedAIndex = 9;
        const int TiedBIndex = 10;
        var expectedWinner = FixedSurfaceQuery.TryNearest(
            colliders: baseline,
            dynamicColliders: [],
            probe: in probe,
            reach: Q(value: 1000d),
            candidate: out var expected
        );

        Assert.True(condition: expectedWinner);

        var random = new Random(Seed: 20260824);
        var permutation = new int[baseline.Length];

        for (var trial = 0; (trial < 1000); trial++) {
            for (var i = 0; (i < permutation.Length); i++) {
                permutation[i] = i;
            }
            for (var i = (permutation.Length - 1); (i > 0); i--) {
                var swapWith = random.Next(maxValue: (i + 1));

                (permutation[i], permutation[swapWith]) = (permutation[swapWith], permutation[i]);
            }

            var shuffled = new FixedStaticCollider[baseline.Length];
            var shuffledTiedAIndex = 0;
            var shuffledTiedBIndex = 0;

            for (var i = 0; (i < permutation.Length); i++) {
                shuffled[i] = baseline[permutation[i]];

                if (permutation[i] == TiedAIndex) {
                    shuffledTiedAIndex = i;
                }
                if (permutation[i] == TiedBIndex) {
                    shuffledTiedBIndex = i;
                }
            }

            var found = FixedSurfaceQuery.TryNearest(
                colliders: shuffled,
                dynamicColliders: [],
                probe: in probe,
                reach: Q(value: 1000d),
                candidate: out var candidate
            );

            Assert.True(condition: found, userMessage: $"trial {trial} lost the result entirely");
            // The winning geometry never depends on array order: the tie is the only pair sharing a distance, so the
            // winner is always whichever tied collider now sits first.
            var expectedIndexThisTrial = ((shuffledTiedAIndex < shuffledTiedBIndex)
                ? shuffledTiedAIndex
                : shuffledTiedBIndex
            );

            Assert.Equal(expected: expectedIndexThisTrial, actual: candidate.ColliderIndex);
            Assert.Equal(expected: FixedSurfaceColliderSource.Static, actual: candidate.Source);
            Assert.Equal(expected: expected.Distance, actual: candidate.Distance);
            Assert.Equal(expected: shuffled[expectedIndexThisTrial], actual: shuffled[candidate.ColliderIndex]);
        }
    }
    [Fact]
    public void DirectedVariantRanksAngularDeviationBeforeDistanceSoTheNearerCandidateCanLoseOnAngle() {
        var origin = FixedVector3.Zero;
        var direction = V(x: 1d, y: 0d, z: 0d);
        // "near": ~1.166 away, ~31 degrees off axis. "far": ~2.001 away, ~1.4 degrees off axis. Both inside a
        // 45-degree assist cone, so both qualify — "far" must still win on angle.
        var near = FixedStaticCollider.Sphere(center: V(x: 1d, y: 0.6d, z: 0d), radius: Q(value: 0.001d));
        var far = FixedStaticCollider.Sphere(center: V(x: 2d, y: 0.05d, z: 0d), radius: Q(value: 0.001d));

        Assert.True(condition: FixedSurfaceQuery.TryNearestDirected(
            colliders: [near, far],
            dynamicColliders: [],
            origin: in origin,
            direction: in direction,
            maxDistance: Q(value: 10d),
            assistHalfAngle: Q(value: (Math.PI / 4d)),
            candidate: out var candidate
        ));
        Assert.Equal(expected: 1, actual: candidate.ColliderIndex);

        // Swapping array order must not change which collider wins — the score, not position, decides.
        Assert.True(condition: FixedSurfaceQuery.TryNearestDirected(
            colliders: [far, near],
            dynamicColliders: [],
            origin: in origin,
            direction: in direction,
            maxDistance: Q(value: 10d),
            assistHalfAngle: Q(value: (Math.PI / 4d)),
            candidate: out var swappedCandidate
        ));
        Assert.Equal(expected: 0, actual: swappedCandidate.ColliderIndex);
        Assert.Equal(expected: candidate.Point, actual: swappedCandidate.Point);
    }
    [Fact]
    public void DirectedVariantExcludesCandidatesOutsideTheAssistCone() {
        var origin = FixedVector3.Zero;
        var direction = V(x: 1d, y: 0d, z: 0d);
        // 90 degrees off axis — outside even a generous cone.
        var perpendicular = FixedStaticCollider.Sphere(center: V(x: 0d, y: 1d, z: 0d), radius: Q(value: 0.001d));

        Assert.False(condition: FixedSurfaceQuery.TryNearestDirected(
            colliders: [perpendicular],
            dynamicColliders: [],
            origin: in origin,
            direction: in direction,
            maxDistance: Q(value: 10d),
            assistHalfAngle: Q(value: (Math.PI / 4d)),
            candidate: out _
        ));
    }
    [Fact]
    public void StaticSpanBeatsDynamicSpanAtEqualDistanceAndIndex() {
        var probe = FixedVector3.Zero;
        var staticSphere = FixedStaticCollider.Sphere(center: V(x: 5d, y: 0d, z: 0d), radius: Q(value: 1d));
        var dynamicSphere = FixedStaticCollider.Sphere(center: V(x: 0d, y: 5d, z: 0d), radius: Q(value: 1d));

        Assert.True(condition: FixedSurfaceQuery.TryNearest(
            colliders: [staticSphere],
            dynamicColliders: [dynamicSphere],
            probe: in probe,
            reach: Q(value: 100d),
            candidate: out var candidate
        ));
        Assert.Equal(expected: FixedSurfaceColliderSource.Static, actual: candidate.Source);
        Assert.Equal(expected: 0, actual: candidate.ColliderIndex);
    }
}
