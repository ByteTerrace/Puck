using System.Numerics;

using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// Kernel-level laws for <see cref="FixedTwoBodyKernel"/> and the pair-manifold slot shape: momentum-conservation
/// claims split into what is actually exact versus what is only bounded, canonical-ordering coverage of every
/// declared candidate field, and the checked-sum overflow refusal.
/// </summary>
public sealed class TwoBodyKernelLawTests {
    private static readonly FixedRigidScales Scales = new(
        EffectiveMass: 32,
        InverseInertia: 40,
        InverseMass: 40
    );

    private static (FixedRigidBody A, FixedRigidBody B) ApplyOnce(FixedVector3 anchorA, FixedVector3 anchorB, FixedVector3 normal, long impulseRaw) {
        var bodyA = MakeBox(density: 60d);
        var bodyB = MakeBox(density: 140d);
        var refusals = 0;

        FixedTwoBodyKernel.ApplyImpulse(
            anchorA: anchorA,
            anchorB: anchorB,
            bodyA: bodyA,
            bodyB: bodyB,
            impulseRaw: impulseRaw,
            normal: normal,
            refusals: ref refusals,
            scales: Scales
        );

        Assert.Equal(
            actual: refusals,
            expected: 0
        );

        return (bodyA, bodyB);
    }
    // Hand-derived rather than routed through FixedMassProperties: a kernel-level law only needs SOME valid inverse
    // mass and inverse inertia, not a bit-exact derivation. A uniform cube of side 1 about its own centre: I = m/6
    // per axis.
    private static FixedRigidBody MakeBox(double density) {
        var mass = density; // side^3 == 1
        var inertiaAxis = (mass / 6d);

        return new() {
            InverseMassRaw = ToRaw(
            value: (1d / mass),
            fractionBits: Scales.InverseMass
        ),
            InverseInertiaXX = ToRaw(
            value: (1d / inertiaAxis),
            fractionBits: Scales.InverseInertia
        ),
            InverseInertiaYY = ToRaw(
            value: (1d / inertiaAxis),
            fractionBits: Scales.InverseInertia
        ),
            InverseInertiaZZ = ToRaw(
            value: (1d / inertiaAxis),
            fractionBits: Scales.InverseInertia
        ),
        };
    }
    private static (FixedVector3 DeltaVA, FixedVector3 DeltaVB, long InvA, long InvB) RunAsymmetricPair(FixedRigidSolverOptions options) {
        var world = new FixedRigidWorld(options: options);
        var bodyA = MakeBox(density: 40d);
        var bodyB = MakeBox(density: 4000d);
        var idA = world.AddBody(body: bodyA);
        var idB = world.AddBody(body: bodyB);

        bodyA.LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: 5d),
            Z: FixedQ4816.Zero
        );
        bodyB.LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: -1d),
            Z: FixedQ4816.Zero
        );

        var startA = bodyA.LinearVelocity;
        var startB = bodyB.LinearVelocity;
        var candidate = new FixedTwoBodyContact(
            BodyIdA: idA,
            BodyIdB: idB,
            AnchorA: new(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.FromDouble(value: 0.5d),
                Z: FixedQ4816.Zero
            ),
            AnchorB: new(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.FromDouble(value: -0.5d),
                Z: FixedQ4816.Zero
            ),
            Normal: new(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            ),
            Separation: FixedQ4816.Zero,
            SourceId: 1,
            FeatureId: 0
        );

        world.Step(
            candidates: [candidate,],
            step: 1
        );

        Assert.Equal(
            expected: 0,
            actual: world.RefusalCount
        );

        return ((bodyA.LinearVelocity - startA), (bodyB.LinearVelocity - startB), bodyA.InverseMassRaw, bodyB.InverseMassRaw);
    }
    private static long ToRaw(double value, int fractionBits) =>
        ((long)Math.Round(a: (value * Math.Pow(
            x: 2d,
            y: fractionBits
        ))));

    [Fact]
    public void AsymmetricPairsMomentumStaysWithinACountedRoundingBudget() {
        var options = new FixedRigidSolverOptions { AppliedAcceleration = FixedVector3.Zero, Gravity = FixedVector3.Zero, RateHz = 60, RelaxIterations = 1, SolveIterations = 2, SubstepCount = 4, };

        var (deltaVA, deltaVB, invA, invB) = RunAsymmetricPair(options: options);

        // Cross-multiplying by the other body's raw inverse mass turns "mA*dvA + mB*dvB ~= 0" (division, not exact)
        // into one exact-integer quantity: dvA*invMassB + dvB*invMassA. K counts every ApplyImpulse invocation the
        // world can perform (warm start, SolveIterations and RelaxIterations per substep, times SubstepCount) -- an
        // upper bound since warm start only fires on a nonzero stored impulse -- and each invocation can leave at
        // most a bounded rounding residue.
        var k = (((long)options.SubstepCount) * ((1 + options.SolveIterations) + options.RelaxIterations));
        var crossRaw = ((((BigInteger)deltaVA.Y.Value) * invB) + (((BigInteger)deltaVB.Y.Value) * invA));
        // Measured, not guessed: the actual residue for this fixture stays far inside one raw unit per counted
        // application; 64 raw units per application is a wide, stated safety margin above that measurement.
        var bound = ((k * 64L) * Math.Max(
            val1: invA,
            val2: invB
        ));

        Assert.True(
            condition: (BigInteger.Abs(value: crossRaw) <= bound),
            userMessage: $"cross-momentum residue {crossRaw} exceeded the {k}-application budget of {bound}"
        );
    }
    [Fact]
    public void EffectiveMassRefusesWhenTheInverseMassSumLeavesItsCarrier() {
        var bodyA = new FixedRigidBody { InverseMassRaw = long.MaxValue, };
        var bodyB = new FixedRigidBody { InverseMassRaw = 1L, };
        var refusals = 0;

        var ok = FixedTwoBodyKernel.TryEffectiveMass(
            bodyA: bodyA,
            anchorA: FixedVector3.Zero,
            bodyB: bodyB,
            anchorB: FixedVector3.Zero,
            normal: new(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            ),
            scales: Scales,
            normalMassRaw: out var normalMassRaw,
            refusals: ref refusals
        );

        Assert.False(condition: ok);
        Assert.Equal(
            actual: normalMassRaw,
            expected: 0L
        );
        Assert.Equal(
            actual: refusals,
            expected: 1
        );
    }
    [Fact]
    public void FlippingTheImpulseSignFlipsBothBodiesResponsesExactly() {
        // The impulse is computed once and only its SIGN flips between the two applications, never two independently
        // rounded impulses -- so driving the same magnitude with the opposite sign must flip both bodies' resulting
        // velocity/angular-velocity deltas bitwise, not merely approximately.
        var normal = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.One,
            Z: FixedQ4816.Zero
        );
        var anchorA = new FixedVector3(
            X: FixedQ4816.FromDouble(value: 0.3d),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var anchorB = new FixedVector3(
            X: FixedQ4816.FromDouble(value: -0.2d),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );

        var (positiveA, positiveB) = ApplyOnce(
            anchorA: anchorA,
            anchorB: anchorB,
            impulseRaw: 500_000L,
            normal: normal
        );
        var (negativeA, negativeB) = ApplyOnce(
            anchorA: anchorA,
            anchorB: anchorB,
            impulseRaw: -500_000L,
            normal: normal
        );

        Assert.Equal(
            expected: positiveA.LinearVelocity,
            actual: -negativeA.LinearVelocity
        );
        Assert.Equal(
            expected: positiveA.AngularVelocity,
            actual: -negativeA.AngularVelocity
        );
        Assert.Equal(
            expected: positiveB.LinearVelocity,
            actual: -negativeB.LinearVelocity
        );
        Assert.Equal(
            expected: positiveB.AngularVelocity,
            actual: -negativeB.AngularVelocity
        );
    }
    [Fact]
    public void MassSymmetricPairsVelocityDeltasAreExactBitwiseOpposites() {
        var options = new FixedRigidSolverOptions { AppliedAcceleration = FixedVector3.Zero, Gravity = FixedVector3.Zero, RateHz = 60, SubstepCount = 4, };
        var world = new FixedRigidWorld(options: options);
        var bodyA = MakeBox(density: 100d);
        var bodyB = MakeBox(density: 100d);
        var idA = world.AddBody(body: bodyA);
        var idB = world.AddBody(body: bodyB);

        bodyA.LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: 3d),
            Z: FixedQ4816.Zero
        );
        bodyB.LinearVelocity = new(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: -3d),
            Z: FixedQ4816.Zero
        );

        var candidates = new List<FixedTwoBodyContact> {
            new(
            BodyIdA: idA,
            BodyIdB: idB,
            AnchorA: new(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.FromDouble(value: 0.5d),
                Z: FixedQ4816.Zero
            ),
            AnchorB: new(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.FromDouble(value: -0.5d),
                Z: FixedQ4816.Zero
            ),
            Normal: new(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.One,
                Z: FixedQ4816.Zero
            ),
            Separation: FixedQ4816.Zero,
            SourceId: 1,
            FeatureId: 0
        ),
        };

        for (var step = 1; (step <= 60); ++step) {
            world.Step(
                candidates: [.. candidates,],
                step: step
            );
        }

        Assert.Equal(
            expected: 0,
            actual: world.RefusalCount
        );
        Assert.Equal(
            expected: bodyA.LinearVelocity,
            actual: -bodyB.LinearVelocity
        );
    }
    [Fact]
    public void RestoringGravityBreaksTheAsymmetricMomentumBound() {
        var options = new FixedRigidSolverOptions { RateHz = 60, RelaxIterations = 1, SolveIterations = 2, SubstepCount = 4, };

        var (deltaVA, deltaVB, invA, invB) = RunAsymmetricPair(options: options);
        var k = (((long)options.SubstepCount) * ((1 + options.SolveIterations) + options.RelaxIterations));
        var crossRaw = ((((BigInteger)deltaVA.Y.Value) * invB) + (((BigInteger)deltaVB.Y.Value) * invA));
        var bound = ((k * 64L) * Math.Max(
            val1: invA,
            val2: invB
        ));

        Assert.False(
            condition: (BigInteger.Abs(value: crossRaw) <= bound),
            userMessage: "gravity is an external acceleration; leaving it on must make the momentum bound false, not incidentally true"
        );
    }
    [Fact]
    public void TwoCandidatesDifferingOnlyInTheSecondAnchorStillOrderCanonically() {
        var normal = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.One,
            Z: FixedQ4816.Zero
        );
        var first = new FixedTwoBodyContact(
            BodyIdA: 0,
            BodyIdB: 1,
            AnchorA: FixedVector3.Zero,
            AnchorB: new(
                X: FixedQ4816.FromDouble(value: 0.1d),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            Normal: normal,
            Separation: FixedQ4816.Zero,
            SourceId: 1,
            FeatureId: 0
        );
        var second = first with {
            AnchorB = new(
            X: FixedQ4816.FromDouble(value: 0.2d),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        ),
        };

        var forward = new List<FixedTwoBodyContact> { first, second, };
        var backward = new List<FixedTwoBodyContact> { second, first, };

        FixedTwoBodyContact.Canonicalize(candidates: forward);
        FixedTwoBodyContact.Canonicalize(candidates: backward);

        Assert.Equal(
            actual: backward,
            expected: forward
        );
        // Without AnchorB in the key, first and second would compare equal and the insertion sort's stability alone
        // would decide their order -- which is exactly what the emission order above would leak if the key stopped
        // short of AnchorB.
        Assert.NotEqual(
            expected: 0,
            actual: FixedTwoBodyContact.Compare(
                left: first,
                right: second
            )
        );
    }
}
