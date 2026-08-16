using System.Numerics;

using Puck.Physics.Tests.Fixtures;
using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// <see cref="FixedRigidSolver"/>'s restitution pass: the threshold gate, an independent oracle over its own
/// arithmetic, the warm-start carrier, and the once-per-<see cref="FixedRigidSolver.Step"/> placement. Every case
/// drives a Restitution=0 twin alongside the run under test — <c>ApplyRestitution</c> reads
/// <see cref="FixedRigidSolverOptions.Restitution"/> nowhere before its own body, so the twin's post-Step state IS
/// the tested run's pre-restitution state, and the twin supplies the measured inputs an independent oracle needs
/// without re-deriving the substep trajectory that produced them.
/// </summary>
public sealed class RestitutionLawTests {
    private static void AssertNear(FixedQ4816 actual, double expected, double tolerance, string subject) {
        var difference = Math.Abs(value: (((double)actual) - expected));

        Assert.True(
            condition: (difference <= tolerance),
            userMessage: $"{subject}: expected {expected}, measured {MeasurementReport.Format(value: actual)}"
        );
    }
    // A sphere already touching the floor (height == its own radius), given an authored downward speed and one
    // Advance() — the minimal shape that puts a live, solved contact under ApplyRestitution on the very first step.
    private static SpikeWorld Drop(FixedQ4816 restitution, double downwardSpeed, int substepCount = 4) {
        var world = SpikeFixtures.HighSpeedApproach(
            options: new() { RateHz = 60, Restitution = restitution, SubstepCount = substepCount, },
            height: 0.1d,
            downwardSpeed: downwardSpeed
        );

        world.Advance();

        return world;
    }
    private static FixedManifoldSlot FindSlot(SpikeWorld world) {
        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref world.Slots[index];

            if (
                slot.Occupied &&
                (slot.SourceId == SpikeFixtures.FloorSourceId)
            ) {
                return slot;
            }
        }

        Assert.Fail(message: "no slot is associated with the floor");

        return default;
    }
    private static void OverwriteNormalImpulse(SpikeWorld world, int sourceId, long normalImpulseRaw) {
        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref world.Slots[index];

            if (
                slot.Occupied &&
                (slot.SourceId == sourceId)
            ) {
                slot.NormalImpulseRaw = normalImpulseRaw;

                return;
            }
        }

        Assert.Fail(message: "no slot is associated with the given surface");
    }

    [InlineData(0d)]
    [InlineData(0.5d)]
    [InlineData(1d)]
    [Theory]
    public void ApplyRestitutionMatchesAnIndependentOracle(double restitution) {
        var restitutionQ = FixedQ4816.FromDouble(value: restitution);
        var zero = Drop(
            restitution: FixedQ4816.Zero,
            downwardSpeed: 6d
        );
        var run = Drop(
            restitution: restitutionQ,
            downwardSpeed: 6d
        );

        Assert.Equal(
            expected: 0,
            actual: zero.Solver.RefusalCount
        );
        Assert.Equal(
            expected: 0,
            actual: run.Solver.RefusalCount
        );

        var zeroSlot = FindSlot(world: zero);

        // Preconditions the fixture must satisfy for the oracle's gate replica to be exercising the real arithmetic
        // rather than passing through one of ApplyRestitution's four early-outs for the wrong reason.
        Assert.True(
            condition: (zeroSlot.NormalMassRaw > 0L),
            userMessage: "the fixture must have formed a real effective mass"
        );
        Assert.True(
            condition: (zeroSlot.TotalNormalImpulseRaw > 0L),
            userMessage: "the fixture must have solved a real contact"
        );
        Assert.True(
            condition: (zeroSlot.RelativeVelocity < -FixedQ4816.FromInteger(value: 1L)),
            userMessage: "the fixture must close faster than the default threshold"
        );

        // zero.Body's post-Step velocity IS the run's pre-restitution velocity: ApplyRestitution reads Restitution
        // nowhere before this point, so the two runs are byte-identical through the end of the substep loop.
        var normalVelocityRaw = FixedVector3.Dot(
            left: (zero.Body.LinearVelocity + FixedVector3.Cross(
                left: zero.Body.AngularVelocity,
                right: zeroSlot.Anchor
            )),
            right: zeroSlot.Normal
        ).Value;
        var expected = RestitutionOracle.ExpectedNormalImpulseRaw(
            effectiveMassFractionBitCount: FixedRigidScales.RoomScale.EffectiveMass,
            normalMassRaw: zeroSlot.NormalMassRaw,
            normalVelocityRaw: normalVelocityRaw,
            priorNormalImpulseRaw: zeroSlot.NormalImpulseRaw,
            relativeVelocityRaw: zeroSlot.RelativeVelocity.Value,
            restitutionRaw: restitutionQ.Value,
            restitutionThresholdRaw: FixedQ4816.FromInteger(value: 1L).Value,
            totalNormalImpulseRawAtGate: zeroSlot.TotalNormalImpulseRaw
        );
        var actual = FindSlot(world: run).NormalImpulseRaw;

        Assert.Equal(
            actual: actual,
            expected: expected
        );
    }
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [Theory]
    public void ClosingVelocityAfterRestitutionMatchesTheTargetAtEverySubstepCount(int substepCount) {
        var restitution = FixedQ4816.FromDouble(value: 0.6d);
        var world = SpikeFixtures.HighSpeedApproach(
            options: new() { RateHz = 60, Restitution = restitution, SubstepCount = substepCount, },
            height: 0.1d,
            downwardSpeed: 6d
        );

        world.Advance();

        Assert.Equal(
            expected: 0,
            actual: world.Solver.RefusalCount
        );

        var slot = FindSlot(world: world);

        Assert.True(
            condition: (slot.TotalNormalImpulseRaw > 0L),
            userMessage: "the fixture must solve a real contact before restitution can be measured"
        );

        // slot.RelativeVelocity is captured once in Prepare, before the substep loop, so it is the same raw at every
        // substep count for this single-step fixture — the trajectory feeding it differs with n, the captured value
        // does not. ApplyRestitution's own delta is built to drive the post-solve normal velocity to exactly
        // -Restitution*RelativeVelocity; this is that claim, not a claim that the trajectory itself is n-invariant.
        var expectedClosing = -(restitution * slot.RelativeVelocity);
        var actualClosing = FixedVector3.Dot(
            left: (world.Body.LinearVelocity + FixedVector3.Cross(
                left: world.Body.AngularVelocity,
                right: slot.Anchor
            )),
            right: slot.Normal
        );

        AssertNear(
            actual: actualClosing,
            expected: ((double)expectedClosing),
            subject: $"post-restitution closing velocity at n={substepCount}",
            tolerance: 0.01d
        );
    }
    [InlineData(0.2d, false)]
    [InlineData(5d, true)]
    [Theory]
    public void TheThresholdGateSkipsBelowAndFiresAbove(double downwardSpeed, bool restitutionShouldFire) {
        var zero = Drop(
            restitution: FixedQ4816.Zero,
            downwardSpeed: downwardSpeed
        );
        var full = Drop(
            restitution: FixedQ4816.One,
            downwardSpeed: downwardSpeed
        );

        Assert.Equal(
            expected: 0,
            actual: zero.Solver.RefusalCount
        );
        Assert.Equal(
            expected: 0,
            actual: full.Solver.RefusalCount
        );

        var zeroSlot = FindSlot(world: zero);
        var fullSlot = FindSlot(world: full);

        // Neither arm may pass through the TotalNormalImpulseRaw==0 gate for the wrong reason: both twins must have
        // actually solved a contact this step, or a below-threshold "skip" would be vacuous.
        Assert.True(
            condition: (zeroSlot.TotalNormalImpulseRaw > 0L),
            userMessage: "the restitution=0 twin must have solved a real contact"
        );
        Assert.True(
            condition: (fullSlot.TotalNormalImpulseRaw > 0L),
            userMessage: "the restitution=1 run must have solved a real contact"
        );

        if (restitutionShouldFire) {
            Assert.True(
                condition: (fullSlot.NormalImpulseRaw > zeroSlot.NormalImpulseRaw),
                userMessage: $"above the threshold, restitution must measurably add impulse: zero={zeroSlot.NormalImpulseRaw}, full={fullSlot.NormalImpulseRaw}"
            );
        } else {
            Assert.Equal(
                actual: fullSlot.NormalImpulseRaw,
                expected: zeroSlot.NormalImpulseRaw
            );
        }
    }
    [Fact]
    public void WarmStartAtTheNextStepConsumesThePostRestitutionImpulseNotTheSolveOnlyValue() {
        var restitution = FixedQ4816.FromDouble(value: 0.15d);
        var real = Drop(
            downwardSpeed: 6d,
            restitution: restitution,
            substepCount: 1
        );
        var sabotaged = Drop(
            downwardSpeed: 6d,
            restitution: restitution,
            substepCount: 1
        );
        var zeroTwin = Drop(
            restitution: FixedQ4816.Zero,
            downwardSpeed: 6d,
            substepCount: 1
        );

        var postRestitutionImpulse = FindSlot(world: real).NormalImpulseRaw;
        var preRestitutionImpulse = FindSlot(world: zeroTwin).NormalImpulseRaw;

        Assert.True(
            condition: (postRestitutionImpulse != preRestitutionImpulse),
            userMessage: "restitution must have moved the accumulated impulse for this fixture to discriminate anything"
        );

        // Simulate a warm start that reads the pre-restitution impulse the ordinary solve alone would have left,
        // rather than the persisted post-restitution one — the exact stale value a deleted ApplyRestitution
        // write-back would leave behind.
        OverwriteNormalImpulse(
            normalImpulseRaw: preRestitutionImpulse,
            sourceId: SpikeFixtures.FloorSourceId,
            world: sabotaged
        );

        real.Advance();
        sabotaged.Advance();

        // Step 2's body is separating (the bounce carries it away within one substep), so its own first biased
        // iteration finds the constraint over-held and drives the accumulated impulse straight to the floor clamp at
        // zero — the SAME correction regardless of where it started from. The MAGNITUDE of that one iteration's own
        // movement is therefore exactly the value warm start applied at the head of the substep: I1 for the real run,
        // I0 for the sabotaged one. A stale (pre-restitution) warm-started value reads back as a different number.
        Assert.Equal(
            expected: postRestitutionImpulse,
            actual: real.Solver.IterationProfile[0]
        );
        Assert.Equal(
            expected: preRestitutionImpulse,
            actual: sabotaged.Solver.IterationProfile[0]
        );
    }
}

/// <summary>
/// Reference arithmetic for <see cref="FixedRigidSolver"/>'s restitution pass, sharing no code with the subject: every
/// value is formed as an exact <see cref="BigInteger"/> rational and rounded ties-to-even exactly where the subject
/// rounds, never through a <c>Puck.Maths</c> or <c>Puck.Physics</c> call.
/// </summary>
internal static class RestitutionOracle {
    /// <summary>Computes the accumulated normal impulse raw <c>ApplyRestitution</c> would leave on one slot after one
    /// restitution iteration, from the measured pre-restitution state.</summary>
    /// <param name="restitutionRaw">The coefficient of restitution raw, at Q48.16.</param>
    /// <param name="restitutionThresholdRaw">The restitution threshold raw, at Q48.16.</param>
    /// <param name="relativeVelocityRaw">The slot's pre-solve closing velocity raw, captured once in <c>Prepare</c>.</param>
    /// <param name="normalVelocityRaw">The current normal velocity raw at the moment restitution would run.</param>
    /// <param name="normalMassRaw">The slot's effective mass raw, at <paramref name="effectiveMassFractionBitCount"/>.</param>
    /// <param name="effectiveMassFractionBitCount">The effective mass raw's fraction bit count.</param>
    /// <param name="totalNormalImpulseRawAtGate">The slot's total normal impulse raw applied so far this step, read at
    /// the same point the subject's own gate reads it.</param>
    /// <param name="priorNormalImpulseRaw">The slot's accumulated normal impulse raw before restitution runs.</param>
    /// <returns>The accumulated normal impulse raw after restitution, replicating every one of the subject's four
    /// early-outs and its floor-clamp exactly.</returns>
    internal static long ExpectedNormalImpulseRaw(
        long restitutionRaw,
        long restitutionThresholdRaw,
        long relativeVelocityRaw,
        long normalVelocityRaw,
        long normalMassRaw,
        int effectiveMassFractionBitCount,
        long totalNormalImpulseRawAtGate,
        long priorNormalImpulseRaw
    ) {
        if (
            (restitutionRaw == 0L) ||
            (normalMassRaw <= 0L) ||
            (totalNormalImpulseRawAtGate == 0L) ||
            (relativeVelocityRaw > -restitutionThresholdRaw)
        ) {
            return priorNormalImpulseRaw;
        }

        var targetTermRaw = RoundTiesToEven(
            shift: FixedQ4816.FractionBitCount,
            value: (((BigInteger)restitutionRaw) * relativeVelocityRaw)
        );
        var drivenRaw = (normalVelocityRaw + targetTermRaw);
        var deltaRaw = -RoundTiesToEven(
            shift: effectiveMassFractionBitCount,
            value: (((BigInteger)drivenRaw) * normalMassRaw)
        );

        // ENVELOPE: the floor clamp below is reproduced for completeness, but no case in this file drives it —
        // ApplyRestitution's own target term is normalVelocity + Restitution*RelativeVelocity, and once the ordinary
        // solve has converged normalVelocity to near zero, RelativeVelocity's own negative sign (required to pass
        // the threshold gate above) keeps the target non-positive and the delta non-negative for every fixture this
        // suite can reach with one dynamic body against a static floor. Confirmed by mutation probe: removing the
        // clamp left every case in this file green.
        return Math.Max(
            val1: (priorNormalImpulseRaw + deltaRaw),
            val2: 0L
        );
    }

    // The exact rational value/2^shift, rounded to the nearest integer, ties to even — the same tie rule every
    // FusedArithmetic rounding face in Puck.Maths documents, reproduced here without calling it.
    private static long RoundTiesToEven(BigInteger value, int shift) {
        if (shift <= 0) {
            return ((long)(value << -shift));
        }

        var divisor = (BigInteger.One << shift);
        var quotient = BigInteger.DivRem(
            dividend: value,
            divisor: divisor,
            remainder: out var remainder
        );

        if (remainder < BigInteger.Zero) {
            quotient -= BigInteger.One;
            remainder += divisor;
        }

        var twiceRemainder = (remainder * 2);

        if (
            (twiceRemainder > divisor) ||
            ((twiceRemainder == divisor) && !quotient.IsEven)
        ) {
            quotient += BigInteger.One;
        }

        return ((long)quotient);
    }
}
