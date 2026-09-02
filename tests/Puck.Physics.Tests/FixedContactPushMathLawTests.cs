using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>The push laws a contact obeys by its normal's alignment with up — the three regimes
/// <see cref="FixedContactPushMath.ComputeOrdinary"/> distinguishes: a walkable slope grounds along its normal, a face
/// steeper than the slope limit is a wall that resolves its penetration ACROSS up (never a lift), and a ceiling
/// resolves along its normal.</summary>
public sealed class FixedContactPushMathLawTests {
    // cos(60 degrees): the default world's walkable-slope limit.
    private static readonly FixedQ4816 GroundedThreshold = FixedQ4816.FromDouble(value: 0.5d);
    private static readonly FixedVector3 Up = Vector(x: 0d, y: 1d, z: 0d);

    private static FixedVector3 Vector(double x, double y, double z) =>
        new(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        );
    // The outward normal of a ramp face rising toward -Z at the given slope: tilted from +Y toward +Z.
    private static FixedVector3 SlopeNormal(double degrees) =>
        Vector(
            x: 0d,
            y: Math.Cos(d: (degrees * (Math.PI / 180d))),
            z: Math.Sin(a: (degrees * (Math.PI / 180d)))
        );
    private static void AssertNear(FixedQ4816 actual, double expected, double tolerance = 0.002d) =>
        Assert.InRange(
            actual: (double)actual,
            high: (expected + tolerance),
            low: (expected - tolerance)
        );

    [Theory]
    [InlineData(65d)]
    [InlineData(75d)]
    [InlineData(89d)]
    public void ASteeperThanWalkableFacePushesAcrossUpAndNeverLifts(double degrees) {
        var normal = SlopeNormal(degrees: degrees);
        var penetration = FixedQ4816.FromDouble(value: 0.12d);
        // Walking into the face (-Z) while falling: the wall may only cancel the -Z approach.
        var velocity = Vector(x: 0d, y: -3d, z: -4d);

        var trial = FixedContactPushMath.ComputeOrdinary(
            groundedThreshold: GroundedThreshold,
            normal: normal,
            penetration: penetration,
            up: Up,
            velocity: in velocity
        );

        Assert.False(condition: trial.Grounded);
        Assert.Equal(
            actual: trial.PositionDelta.Y,
            expected: FixedQ4816.Zero
        );
        Assert.Equal(
            actual: trial.VelocityDelta.Y,
            expected: FixedQ4816.Zero
        );
        // The push still resolves the full penetration measured along the normal.
        AssertNear(
            actual: FixedVector3.Dot(
                left: trial.PositionDelta,
                right: normal
            ),
            expected: 0.12d
        );

        // The approach across the face is cancelled exactly; the fall is untouched.
        var resolved = (velocity + trial.VelocityDelta);

        AssertNear(
            actual: resolved.Z,
            expected: 0d
        );
        AssertNear(
            actual: resolved.Y,
            expected: -3d
        );
    }

    [Fact]
    public void AWalkableSlopeGroundsAlongItsNormal() {
        var normal = SlopeNormal(degrees: 45d);
        var penetration = FixedQ4816.FromDouble(value: 0.1d);
        var velocity = Vector(x: 0d, y: -3d, z: 0d);

        var trial = FixedContactPushMath.ComputeOrdinary(
            groundedThreshold: GroundedThreshold,
            normal: normal,
            penetration: penetration,
            up: Up,
            velocity: in velocity
        );

        Assert.True(condition: trial.Grounded);
        Assert.Equal(
            actual: trial.PositionDelta,
            expected: (normal * penetration)
        );
        Assert.True(condition: (trial.PositionDelta.Y > FixedQ4816.Zero));
    }

    [Fact]
    public void ACeilingPushesDownAlongItsNormalAndClampsTheRise() {
        var normal = Vector(x: 0d, y: -1d, z: 0d);
        var penetration = FixedQ4816.FromDouble(value: 0.05d);
        var velocity = Vector(x: 2d, y: 5d, z: 0d);

        var trial = FixedContactPushMath.ComputeOrdinary(
            groundedThreshold: GroundedThreshold,
            normal: normal,
            penetration: penetration,
            up: Up,
            velocity: in velocity
        );

        Assert.False(condition: trial.Grounded);
        Assert.Equal(
            actual: trial.PositionDelta,
            expected: (normal * penetration)
        );
        Assert.Equal(
            actual: (velocity + trial.VelocityDelta),
            expected: Vector(x: 2d, y: 0d, z: 0d)
        );
    }
}
