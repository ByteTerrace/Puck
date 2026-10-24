using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>The step-up law over the analytic solver, whose box contact reads a riser's face as a wall: a blocked step
/// lands on a riser no taller than the step height at its full planar reach, and every other blocked step
/// stands.</summary>
public sealed class FixedStepUpLawTests {
    // cos(60 degrees), the shipped walkable slope.
    private static readonly FixedQ4816 GroundedThreshold = FixedQ4816.FromDouble(value: 0.5d);
    private static readonly FixedQ4816 StepHeight = FixedQ4816.FromDouble(value: 0.3d);
    private static readonly FixedBodyColliderVolume[] Capsule = [new(
        Kind: FixedBodyColliderKind.Capsule,
        Center: Vector(x: 0d, y: 0.35d, z: 0d),
        Endpoint: Vector(x: 0d, y: 1.71d, z: 0d),
        HalfExtents: FixedVector3.Zero,
        Rotation: FixedQuaternion.Identity,
        Radius: FixedQ4816.FromDouble(value: 0.35d)
    )];
    private static readonly FixedStaticCollider Ground = FixedStaticCollider.HalfSpace(
        normal: FixedVector3.UnitY,
        point: FixedVector3.Zero
    );

    private readonly ref struct StaticStep(FixedStaticCollider[] colliders) : IFixedContactStep {
        private readonly FixedStaticCollider[] m_colliders = colliders;

        public ContactResolution Sweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity) =>
            new FixedStaticContactSolver(
                ContactSkin: FixedQ4816.Zero,
                GroundedThreshold: GroundedThreshold,
                MaxIterations: 4
            ).ResolveSweep(
                colliders: m_colliders,
                dynamicColliders: [],
                orientation: FixedQuaternion.Identity,
                position: ref position,
                previousPosition: in previousPosition,
                up: FixedVector3.UnitY,
                velocity: ref velocity,
                volumes: Capsule
            );
    }

    // A box riser spanning x 1..2 and the given height; a capsule at x 0.75 is just clear of its lower edge.
    private static FixedStaticCollider Riser(double height) => FixedStaticCollider.AxisAlignedBox(
        center: Vector(x: 1.5d, y: (height / 2d), z: 0d),
        halfExtents: Vector(x: 0.5d, y: (height / 2d), z: 2d)
    );
    private static FixedVector3 Vector(double x, double y, double z) => new(
        X: FixedQ4816.FromDouble(value: x),
        Y: FixedQ4816.FromDouble(value: y),
        Z: FixedQ4816.FromDouble(value: z)
    );
    // Runs the ordinary step 5 cm toward the riser, then the step-up. A level step is asserted blocked
    // first; a rising one may clear the riser's top, and the step-up must still decline it.
    private static bool Step(FixedStaticCollider[] colliders, double rise, FixedQ4816 stepHeight, out FixedStepUpResult result) {
        var start = Vector(x: 0.75d, y: 0d, z: 0d);
        var intended = Vector(x: 0.8d, y: rise, z: 0d);
        var velocity = Vector(x: 1.5d, y: 0d, z: 0d);
        var step = new StaticStep(colliders: colliders);
        var blocked = intended;
        var blockedVelocity = velocity;
        var resolution = step.Sweep(position: ref blocked, previousPosition: in start, velocity: ref blockedVelocity);

        if (rise == 0d) {
            Assert.NotEqual(
                expected: FixedVector3.Zero,
                actual: resolution.ObstructionNormal
            );
        }

        return FixedStepUp.TryStep(
            requiredStepHeight: resolution.RequiredStepHeight,
            blocked: in blocked,
            intended: in intended,
            result: out result,
            start: in start,
            step: ref step,
            stepHeight: stepHeight,
            up: FixedVector3.UnitY,
            velocity: in velocity
        );
    }

    [Fact]
    public void ABlockedStepLandsOnALowRisersSurfaceAtItsFullReach() {
        Assert.True(condition: Step(colliders: [Ground, Riser(height: 0.1d)], rise: 0d, stepHeight: StepHeight, result: out var result));
        Assert.True(condition: result.Resolution.Grounded);
        // A shallow edge landing depenetrates along its normal by at most one bisection interval.
        Assert.InRange(actual: ((double)result.Position.X), low: 0.799d, high: 0.8001d);
        // The foot is still outside the riser: its capsule lands on the edge, not on an inflated box top.
        var delta = ((result.Position + Vector(x: 0d, y: 0.35d, z: 0d)) - Vector(x: 1d, y: 0.1d, z: 0d));

        Assert.InRange(actual: ((double)delta.Length), low: 0.3498d, high: 0.3502d);
        Assert.InRange(actual: ((double)result.Position.Y), low: 0.02d, high: 0.04d);
        Assert.True(condition: (result.Velocity.X > FixedQ4816.Zero));
    }
    [Fact]
    public void ABlockedStepStandsAgainstATallRiserARisingStepASteepFaceAndAZeroStepHeight() {
        // Taller than the step height: still a wall at the raised height.
        Assert.False(condition: Step(colliders: [Ground, Riser(height: 0.5d)], rise: 0d, stepHeight: StepHeight, result: out _));
        // No step height: never steps.
        Assert.False(condition: Step(colliders: [Ground, Riser(height: 0.1d)], rise: 0d, stepHeight: FixedQ4816.Zero, result: out _));
        Assert.False(condition: Step(colliders: [Ground, Riser(height: 0.1d)], rise: 0d, stepHeight: FixedQ4816.FromDouble(value: 0.05d), result: out _));
        // A rising step (a jump) lowers only to where it would have ended, above the riser's top.
        Assert.False(condition: Step(colliders: [Ground, Riser(height: 0.1d)], rise: 0.25d, stepHeight: StepHeight, result: out _));
        Assert.False(condition: Step(colliders: [Ground, Riser(height: 0.1d)], rise: 0.4d, stepHeight: StepHeight, result: out _));
        // A face 70 degrees from level, whose foot the body stands against: the landing meets the face, not ground.
        var steep = FixedStaticCollider.HalfSpace(
            normal: Vector(x: -0.93969262d, y: 0.34202014d, z: 0d),
            point: Vector(x: 0.895d, y: 0d, z: 0d)
        );

        Assert.False(condition: Step(colliders: [Ground, steep], result: out _, rise: 0d, stepHeight: StepHeight));
    }
    [InlineData(0, false)]
    [InlineData(0.05, false)]
    [InlineData(0.3, true)]
    [Theory]
    public void TheExactChamferStillNeedsEnoughStepHeight(double height, bool expected) {
        var fillet = FixedQ4816.FromDouble(value: 0.002);
        var chamfer = (FixedQ4816.FromDouble(value: 0.02) - (fillet *
            (FixedQ4816.FromInteger(value: 2) - FixedQ4816.Sqrt(value: FixedQ4816.FromInteger(value: 2)))));
        var riser = FixedStaticCollider.ChamferedBox(center: Vector(x: 1.5, y: 0.05, z: 0),
            rotation: FixedQuaternion.Identity, innerHalfExtents: Vector(x: 0.498, y: 0.048, z: 1.998),
            chamfer: chamfer, radius: fillet);

        var actual = Step(colliders: [Ground, riser], rise: 0, stepHeight: FixedQ4816.FromDouble(value: height), result: out var result);

        Assert.Equal(actual: actual, expected: expected);
        if (actual) {
            Assert.True(condition: result.Resolution.Grounded);
            Assert.InRange(((double)result.Position.X), 0.798, 0.801);
            Assert.InRange(((double)result.Position.Y), 0.01, 0.1);
        }
    }
    [InlineData(15)]
    [InlineData(30)]
    [Theory]
    public void ASideApproachStepsOntoTheLowEndOfATiltedRamp(double degrees) {
        var angle = FixedQ4816.FromDegrees(degrees: degrees);
        var rotation = FixedQuaternion.FromAxisAngle(axis: FixedVector3.UnitX, angle: -angle);
        var center = Vector(x: 0, y: 0, z: 0) with {
            Y = (((FixedQ4816.Sin(angle: angle) * FixedQ4816.FromInteger(value: 2)) -
                (FixedQ4816.Cos(angle: angle) * FixedQ4816.FromDouble(value: 0.15))) + FixedQ4816.FromDouble(value: 0.01)),
        };
        var ramp = FixedStaticCollider.RoundedBox(center: center, rotation: rotation,
            innerHalfExtents: Vector(x: 1.48, y: 0.13, z: 1.98), radius: FixedQ4816.FromDouble(value: 0.02));
        var edge = (center + rotation.Rotate(vector: Vector(x: 1.5, y: 0.15, z: -1.7)));
        var start = Vector(x: 1.75, y: 0, z: 0) with { Z = edge.Z };
        var intended = start with { X = FixedQ4816.FromDouble(value: 1.7) };
        var velocity = -FixedVector3.UnitX;
        var blocked = intended;
        var blockedVelocity = velocity;
        var step = new StaticStep(colliders: [Ground, ramp]);
        var resolution = step.Sweep(position: ref blocked, previousPosition: in start, velocity: ref blockedVelocity);

        Assert.NotEqual(FixedVector3.Zero, resolution.ObstructionNormal);
        Assert.InRange(((double)resolution.RequiredStepHeight), 0.03, 0.3);
        Assert.True(condition: FixedStepUp.TryStep(ref step, in start, in intended, in velocity, FixedVector3.UnitY,
            StepHeight, in blocked, out var result, resolution.RequiredStepHeight));
        Assert.True(condition: result.Resolution.Grounded);
        Assert.InRange(((double)result.Position.Y), 0.001, 0.3);
        Assert.True(condition: (result.Position.X < blocked.X));
    }
}
