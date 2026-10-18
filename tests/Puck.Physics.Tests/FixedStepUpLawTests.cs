using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>The step-up law over the analytic solver, whose box contact reads a riser's face as a wall: a blocked step
/// lands on a riser no taller than the step height at its full planar reach and pace, and every other blocked step
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
            ).Resolve(
                colliders: m_colliders,
                dynamicColliders: [],
                orientation: FixedQuaternion.Identity,
                position: ref position,
                up: FixedVector3.UnitY,
                velocity: ref velocity,
                volumes: Capsule
            );
    }

    // A box riser spanning x 1..2 and the given height; a body at x 0.65 stands against its face.
    private static FixedStaticCollider Riser(double height) => FixedStaticCollider.AxisAlignedBox(
        center: Vector(x: 1.5d, y: (height / 2d), z: 0d),
        halfExtents: Vector(x: 0.5d, y: (height / 2d), z: 2d)
    );
    private static FixedVector3 Vector(double x, double y, double z) => new(
        X: FixedQ4816.FromDouble(value: x),
        Y: FixedQ4816.FromDouble(value: y),
        Z: FixedQ4816.FromDouble(value: z)
    );
    // Runs the ordinary step from the face 5 cm into the riser, then the step-up. A level step is asserted blocked
    // first; a rising one may clear the riser's top, and the step-up must still decline it.
    private static bool Step(FixedStaticCollider[] colliders, double rise, FixedQ4816 stepHeight, out FixedStepUpResult result) {
        var start = Vector(x: 0.65d, y: 0d, z: 0d);
        var intended = Vector(x: 0.7d, y: rise, z: 0d);
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
    public void ABlockedStepLandsOnALowRiserAtItsFullReachAndPace() {
        Assert.True(condition: Step(colliders: [Ground, Riser(height: 0.1d)], rise: 0d, stepHeight: StepHeight, result: out var result));
        Assert.True(condition: result.Resolution.Grounded);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 0.7d), actual: result.Position.X);
        Assert.InRange(
            actual: result.Position.Y,
            high: FixedQ4816.FromDouble(value: 0.102d),
            low: FixedQ4816.FromDouble(value: 0.099d)
        );
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 1.5d), actual: result.Velocity.X);
    }
    [Fact]
    public void ABlockedStepStandsAgainstATallRiserARisingStepASteepFaceAndAZeroStepHeight() {
        // Taller than the step height: still a wall at the raised height.
        Assert.False(condition: Step(colliders: [Ground, Riser(height: 0.5d)], rise: 0d, stepHeight: StepHeight, result: out _));
        // No step height: never steps.
        Assert.False(condition: Step(colliders: [Ground, Riser(height: 0.1d)], rise: 0d, stepHeight: FixedQ4816.Zero, result: out _));
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
}
