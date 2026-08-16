using System.Numerics;

using Puck.Physics.Tests.Fixtures;
using Puck.Physics.Tests.Geometry;
using Puck.Maths;

namespace Puck.Physics.Tests;

/// <summary>
/// <see cref="FixedRigidSolver"/>'s friction pass: the coupled 2x2 tangent solve against an independent oracle, the
/// unsaturated (static) branch, byte-identity at the default zero coefficient, and permutation invariance. Every
/// case that needs pre-friction state drives a Friction=0 twin, mirroring <see cref="RestitutionOracle"/>'s own
/// technique — friction's own delta computation runs unconditionally (only the cone bound differs with the
/// coefficient), so a zero-friction twin's measured Prepare outputs and pre-friction body state are exactly what
/// the tested run saw too.
/// </summary>
public sealed class FrictionLawTests {
    private static void AssertNear(FixedQ4816 actual, double expected, double tolerance, string subject) {
        var difference = Math.Abs(value: (((double)actual) - expected));

        Assert.True(
            condition: (difference <= tolerance),
            userMessage: $"{subject}: expected {expected}, measured {MeasurementReport.Format(value: actual)}"
        );
    }
    private static void AssertWithinRelativeTolerance(long expected, long actual, double relativeTolerance, string subject) {
        var scale = Math.Max(
            val1: Math.Abs(value: expected),
            val2: 1L
        );
        var difference = Math.Abs(value: (actual - expected));

        Assert.True(
            condition: (difference <= (scale * relativeTolerance)),
            userMessage: $"{subject}: expected {expected}, measured {actual} (tolerance {(scale * relativeTolerance)})"
        );
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
    private static FixedRigidSolverOptions FrictionOptions() =>
        new() {
            RateHz = 60,
            SubstepCount = 4,
            AppliedAcceleration = SpikeFixtures.Vector(
            x: -3d,
            y: 0d,
            z: 0d
        ),
            Friction = FixedQ4816.FromDouble(value: 0.5d),
        };
    // A deterministic pseudo-random reordering keyed by an integer — mirrors OrderingLawTests' own permutation
    // hook so the same fixed key set exercises the same reachable orderings run to run.
    private static List<FixedContactCandidate> Shuffle(int key, List<FixedContactCandidate> source) {
        var pool = new List<FixedContactCandidate>(collection: source);
        var result = new List<FixedContactCandidate>(capacity: pool.Count);
        var state = (((ulong)key) + 0x9E3779B97F4A7C15UL);

        while (pool.Count > 0) {
            state = unchecked((state ^ (state << 13)) ^ (state >> 7) ^ (state << 17));

            var pick = ((int)(state % ((ulong)pool.Count)));

            result.Add(item: pool[index: pick]);
            pool.RemoveAt(index: pick);
        }

        return result;
    }
    // A body settled to a steady resting contact FIRST — so NormalImpulseRaw (the persisted, warm-started
    // weight-bearing impulse) and TotalNormalImpulseRaw (one step's own small residual on top of it) are genuinely
    // different — then given an authored tangential slide speed for exactly one more Advance(). SubstepCount=1
    // keeps that last step's own arithmetic tractable for the oracle: position integrates once, before friction
    // (relax-only) ever runs.
    private static SpikeWorld Slide(FixedQ4816 friction, double slideSpeed) {
        var radius = FixedQ4816.FromDouble(value: 0.5d);
        var body = SpikeBodies.Sphere(
            radius: radius,
            density: FixedQ4816.FromInteger(value: 20L),
            scales: FixedRigidScales.RoomScale
        );
        var world = new SpikeWorld(
            options: new() { Friction = friction, RateHz = 60, SubstepCount = 1, },
            body: body,
            pose: new() {
                Center = new FixedVector3(
                X: FixedQ4816.Zero,
                Y: radius,
                Z: FixedQ4816.Zero
            ),
            },
            shape: SpikeShape.Sphere(radius: radius),
            reach: radius,
            new HalfSpaceSurface(
                sourceId: SpikeFixtures.FloorSourceId,
                normal: AxisY,
                offset: FixedQ4816.Zero
            )
        );

        world.Advance(count: 30);

        body.LinearVelocity += new FixedVector3(
            X: FixedQ4816.FromDouble(value: slideSpeed),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );

        world.Advance();

        return world;
    }
    // A box dropped with spin onto a plane — SpikeFixtures.RotatingBox's own shape, at Friction and SubstepCount=1
    // so exactly one friction call happens: the minimal shape whose corner anchor is not parallel to Normal.
    private static SpikeWorld SpinningBox(FixedQ4816 friction) {
        var halfExtents = SpikeFixtures.Vector(
            x: 0.4d,
            y: 0.25d,
            z: 0.3d
        );
        var body = SpikeBodies.Box(
            density: FixedQ4816.FromInteger(value: 50L),
            halfExtents: halfExtents,
            scales: FixedRigidScales.RoomScale
        );

        body.LinearVelocity = SpikeFixtures.Vector(
            x: 0d,
            y: -1d,
            z: 0d
        );
        body.AngularVelocity = SpikeFixtures.Vector(
            x: 0d,
            y: 0d,
            z: 0.9d
        );

        // Tilted about X so the leading two corners of one edge touch first — a box dropped flat presents its whole
        // face (four-plus simultaneous candidates), and this fixture keeps it to exactly two: the oracle below
        // measures body velocity after the WHOLE step, which folds in the SECOND slot's own normal and friction
        // impulses too (that slot's own SolveOnce block runs after the first's, in the same relax pass) — an
        // ENVELOPE the tolerance below accounts for, not a claim of bit-exactness the way the single-contact
        // sphere case above achieves.
        var tilt = FixedQuaternion.FromAxisAngle(
            axis: new FixedVector3(
                X: FixedQ4816.One,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            angle: FixedQ4816.FromDouble(value: 0.35d)
        );
        var world = new SpikeWorld(
            options: new() { Friction = friction, RateHz = 60, SubstepCount = 1, },
            body: body,
            pose: new() {
                Center = SpikeFixtures.Vector(
                x: 0d,
                y: 0.3d,
                z: 0d
            ),
                Orientation = tilt,
            },
            shape: SpikeShape.Box(halfExtents: halfExtents),
            reach: FixedQ4816.FromDouble(value: 0.5545d),
            new HalfSpaceSurface(
                sourceId: SpikeFixtures.FloorSourceId,
                normal: AxisY,
                offset: FixedQ4816.Zero
            )
        );

        world.Advance();

        return world;
    }

    [InlineData(0.1d)]
    [InlineData(0.6d)]
    [Theory]
    public void CoupledTangentMassMatchesAnIndependentOracleOnASpinningBox(double friction) {
        // A box, not a sphere: a sphere's anchor is exactly -radius*Normal, which is perpendicular to BOTH tangents
        // by construction, so its off-diagonal tangent-mass term is exactly zero on geometric grounds alone — no
        // fixture choice could make it otherwise. A box's corner anchor has no such exact perpendicularity, and the
        // authored spin here is what makes the lever arms toward Tangent1 and Tangent2 genuinely unaligned.
        var frictionQ = FixedQ4816.FromDouble(value: friction);
        var zero = SpinningBox(friction: FixedQ4816.Zero);
        var run = SpinningBox(friction: frictionQ);

        Assert.Equal(
            expected: 0,
            actual: zero.Solver.RefusalCount
        );
        Assert.Equal(
            expected: 0,
            actual: run.Solver.RefusalCount
        );

        var zeroSlot = FindSlot(world: zero);

        Assert.True(
            condition: (zeroSlot.NormalImpulseRaw > 0L),
            userMessage: "the fixture must have solved a real contact before friction can be measured"
        );
        Assert.True(
            condition: (zeroSlot.TangentMassXXRaw > 0L),
            userMessage: "the fixture must have formed a real coupled tangent mass"
        );
        Assert.True(
            condition: (zeroSlot.TangentMassXYRaw != 0L),
            userMessage: "the fixture must exercise a genuinely coupled tangent mass, not a degenerate diagonal one"
        );

        var tangentVelocityXRaw = FixedVector3.Dot(
            left: (zero.Body.LinearVelocity + FixedVector3.Cross(
                left: zero.Body.AngularVelocity,
                right: zeroSlot.Anchor
            )),
            right: zeroSlot.Tangent1
        ).Value;
        var tangentVelocityYRaw = FixedVector3.Dot(
            left: (zero.Body.LinearVelocity + FixedVector3.Cross(
                left: zero.Body.AngularVelocity,
                right: zeroSlot.Anchor
            )),
            right: zeroSlot.Tangent2
        ).Value;

        FrictionOracle.ExpectedTangentImpulseRaw(
            effectiveMassFractionBitCount: FixedRigidScales.RoomScale.EffectiveMass,
            frictionRaw: frictionQ.Value,
            normalImpulseRaw: zeroSlot.NormalImpulseRaw,
            tangentMassXXRaw: zeroSlot.TangentMassXXRaw,
            tangentMassXYRaw: zeroSlot.TangentMassXYRaw,
            tangentMassYYRaw: zeroSlot.TangentMassYYRaw,
            tangentVelocityXRaw: tangentVelocityXRaw,
            tangentVelocityYRaw: tangentVelocityYRaw,
            expectedXRaw: out var expectedX,
            expectedYRaw: out var expectedY
        );

        var runSlot = FindSlot(world: run);

        // ENVELOPE: a second Constraint slot exists on this fixture (see above), so the measured tangential
        // velocity is not exactly what THIS slot's own SolveFriction call saw; ten percent of the oracle's own
        // prediction is generous room for that contamination while remaining far tighter than what decoupling the
        // tangent mass (this law's own mutation target) actually moves the result by.
        AssertWithinRelativeTolerance(
            actual: runSlot.TangentImpulseXRaw,
            expected: expectedX,
            relativeTolerance: 0.1d,
            subject: "coupled tangent impulse X"
        );
        AssertWithinRelativeTolerance(
            actual: runSlot.TangentImpulseYRaw,
            expected: expectedY,
            relativeTolerance: 0.1d,
            subject: "coupled tangent impulse Y"
        );
    }
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(5_000)]
    [Theory]
    public void FrictionDoesNotReintroduceOrderDependence(int permutationKey) {
        var canonical = SpikeFixtures.BoxInCorner(options: FrictionOptions());
        var permuted = SpikeFixtures.BoxInCorner(options: FrictionOptions());

        permuted.Permutation = (candidates) => Shuffle(
            key: permutationKey,
            source: candidates
        );

        canonical.Advance(count: 120);
        permuted.Advance(count: 120);

        Assert.Equal(
            expected: 0,
            actual: canonical.Solver.RefusalCount
        );
        Assert.Equal(
            expected: 0,
            actual: permuted.Solver.RefusalCount
        );
        Assert.Equal(
            expected: canonical.Digest,
            actual: permuted.Digest
        );

        // The recompose-and-store pass at the end of Step is what makes FrictionImpulse survive into the next
        // step's warm start at all; without it, every slot would read back exactly Zero here regardless of how
        // much sliding resistance this fixture's own nonzero Friction actually produced.
        var anyNonZeroFriction = false;

        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref canonical.Slots[index];

            if (
                slot.Occupied &&
                (slot.FrictionImpulse != FixedVector3.Zero)
            ) {
                anyNonZeroFriction = true;

                break;
            }
        }

        Assert.True(
            condition: anyNonZeroFriction,
            userMessage: "no occupied slot carried a persisted friction impulse after 120 steps of a nonzero-friction fixture"
        );
    }
    [InlineData(0.1d)]
    [InlineData(0.6d)]
    [Theory]
    public void KineticFrictionMatchesAnIndependentOracle(double friction) {
        const double SlideSpeed = 3d;

        var frictionQ = FixedQ4816.FromDouble(value: friction);
        var zero = Slide(
            friction: FixedQ4816.Zero,
            slideSpeed: SlideSpeed
        );
        var run = Slide(
            friction: frictionQ,
            slideSpeed: SlideSpeed
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

        Assert.True(
            condition: (zeroSlot.NormalImpulseRaw > 0L),
            userMessage: "the fixture must have solved a real contact before friction can be measured"
        );
        Assert.True(
            condition: (zeroSlot.TangentMassXXRaw > 0L),
            userMessage: "the fixture must have formed a real coupled tangent mass"
        );

        // zero.Body's post-Step state IS the moment the real run's own friction call sees, up to and including the
        // normal block that same relax iteration: friction's delta computation does not depend on the coefficient,
        // only the cone bound (friction·NormalImpulseRaw) does, and the zero twin's own cone forces its accumulated
        // impulse back to exactly zero every time, leaving its state as if friction had never applied anything.
        var tangentVelocityXRaw = FixedVector3.Dot(
            left: (zero.Body.LinearVelocity + FixedVector3.Cross(
                left: zero.Body.AngularVelocity,
                right: zeroSlot.Anchor
            )),
            right: zeroSlot.Tangent1
        ).Value;
        var tangentVelocityYRaw = FixedVector3.Dot(
            left: (zero.Body.LinearVelocity + FixedVector3.Cross(
                left: zero.Body.AngularVelocity,
                right: zeroSlot.Anchor
            )),
            right: zeroSlot.Tangent2
        ).Value;

        FrictionOracle.ExpectedTangentImpulseRaw(
            effectiveMassFractionBitCount: FixedRigidScales.RoomScale.EffectiveMass,
            frictionRaw: frictionQ.Value,
            normalImpulseRaw: zeroSlot.NormalImpulseRaw,
            tangentMassXXRaw: zeroSlot.TangentMassXXRaw,
            tangentMassXYRaw: zeroSlot.TangentMassXYRaw,
            tangentMassYYRaw: zeroSlot.TangentMassYYRaw,
            tangentVelocityXRaw: tangentVelocityXRaw,
            tangentVelocityYRaw: tangentVelocityYRaw,
            expectedXRaw: out var expectedX,
            expectedYRaw: out var expectedY
        );

        var runSlot = FindSlot(world: run);

        Assert.Equal(
            actual: runSlot.TangentImpulseXRaw,
            expected: expectedX
        );
        Assert.Equal(
            actual: runSlot.TangentImpulseYRaw,
            expected: expectedY
        );

        // Position integrates from the BIASED pass's own velocity, before friction — which is relax-only — ever
        // touches it, so at SubstepCount=1 the horizontal slide distance is exactly slideSpeed/RateHz regardless of
        // the coefficient: friction cannot have removed any of it yet. A friction call that fires during the biased
        // pass would arrest some of that speed early and fall short of this closed form.
        AssertNear(
            actual: run.Pose.Center.X,
            expected: (SlideSpeed / 60d),
            tolerance: 0.0002d,
            subject: "horizontal slide distance before friction can have acted"
        );
    }
    [Fact]
    public void StaticFrictionHoldsBelowTheConeThreshold() {
        // Settled and warm-started FIRST, so NormalImpulseRaw (the persisted, warm-started weight-bearing impulse)
        // and TotalNormalImpulseRaw (this one step's OWN small correction on top of it) are now genuinely
        // different — the exact condition that distinguishes the correct cone bound from the review's rejected one.
        var radius = FixedQ4816.FromDouble(value: 0.5d);
        var body = SpikeBodies.Sphere(
            radius: radius,
            density: FixedQ4816.FromInteger(value: 20L),
            scales: FixedRigidScales.RoomScale
        );
        var world = new SpikeWorld(
            options: new() { Friction = FixedQ4816.One, RateHz = 60, SubstepCount = 1, },
            body: body,
            pose: new() {
                Center = new FixedVector3(
                X: FixedQ4816.Zero,
                Y: radius,
                Z: FixedQ4816.Zero
            ),
            },
            shape: SpikeShape.Sphere(radius: radius),
            reach: radius,
            new HalfSpaceSurface(
                sourceId: SpikeFixtures.FloorSourceId,
                normal: AxisY,
                offset: FixedQ4816.Zero
            )
        );

        world.Advance(count: 60);

        // A gentle nudge whose required arresting force is comfortably inside a strong coefficient's cone: the
        // coupled solve's own unsaturated delta, never the maxImpulse bound, is what should zero the slide.
        body.LinearVelocity += new FixedVector3(
            X: FixedQ4816.FromDouble(value: 0.05d),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );

        world.Advance();

        Assert.Equal(
            expected: 0,
            actual: world.Solver.RefusalCount
        );

        var slot = FindSlot(world: world);
        var residualTangentVelocity = FixedVector3.Dot(
            left: (world.Body.LinearVelocity + FixedVector3.Cross(
                left: world.Body.AngularVelocity,
                right: slot.Anchor
            )),
            right: slot.Tangent1
        );

        Assert.True(
            condition: (FixedQ4816.Abs(value: residualTangentVelocity) < FixedQ4816.FromDouble(value: 0.01d)),
            userMessage: $"a gentle slide under a strong coefficient must be arrested, not merely slowed; residual tangential speed {MeasurementReport.Format(value: residualTangentVelocity)}"
        );

        // The cone did not saturate: the accumulated tangential impulse stayed inside friction*NormalImpulseRaw with
        // room to spare, proving the unsaturated branch (the coupled TryApplySymmetric2 delta alone) did the work.
        var impulseMagnitudeSquared = ((((Int128)slot.TangentImpulseXRaw) * slot.TangentImpulseXRaw) + (((Int128)slot.TangentImpulseYRaw) * slot.TangentImpulseYRaw));
        var coneRaw = slot.NormalImpulseRaw;
        var coneSquared = (((Int128)coneRaw) * coneRaw);

        Assert.True(
            condition: (impulseMagnitudeSquared < coneSquared),
            userMessage: "the accumulated tangential impulse reached the friction cone; the fixture must be gentler"
        );
    }
    [Fact]
    public void ZeroFrictionReproducesEveryExistingFixtureByteForByte() {
        (SpikeWorld World, int Steps)[] fixtures = [
            (SpikeFixtures.Corner(options: SpikeFixtures.CornerOptions(
                rateHz: 60,
                substepCount: 4
            )), 240),
            (SpikeFixtures.RotatingBox(options: new() { RateHz = 60, SubstepCount = 4, }), 180),
            (SpikeFixtures.HighSpeedApproach(
                options: new() { RateHz = 60, SubstepCount = 1, },
                height: 1d,
                downwardSpeed: 400d
            ), 1),
            (SpikeFixtures.DeepOverlap(options: new() { RateHz = 60, SubstepCount = 4, }), 60),
            (SpikeFixtures.BoxInCorner(options: SpikeFixtures.BoxInCornerOptions(
                rateHz: 60,
                substepCount: 4
            )), 180),
        ];

        foreach (var (world, steps) in fixtures) {
            world.Advance(count: steps);

            Assert.Equal(
                expected: 0,
                actual: world.Solver.RefusalCount
            );

            for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
                ref readonly var slot = ref world.Slots[index];

                if (slot.Occupied) {
                    Assert.Equal(
                        expected: FixedVector3.Zero,
                        actual: slot.FrictionImpulse
                    );
                }
            }
        }
    }

    private static FixedVector3 AxisY => new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
}

/// <summary>
/// Reference arithmetic for <see cref="FixedRigidSolver"/>'s friction pass, sharing no code with the subject: every
/// value is formed as an exact <see cref="BigInteger"/> rational and rounded ties-to-even exactly where the subject
/// rounds, never through a <c>Puck.Maths</c> or <c>Puck.Physics</c> call. The cone rescale is the one place both the
/// subject and this oracle need a square root, and both take the SAME integer route: an exact squared-length
/// compare, then an integer square root only on the branch that needs one.
/// </summary>
internal static class FrictionOracle {
    /// <summary>Computes the accumulated tangential impulse raws <c>SolveFriction</c> would leave on one slot after
    /// one coupled relax iteration, from the measured pre-friction state, with a zero prior impulse (the only case
    /// this suite's fixtures need — a fresh first-step association warm-starts nothing).</summary>
    internal static void ExpectedTangentImpulseRaw(
        long tangentMassXXRaw,
        long tangentMassXYRaw,
        long tangentMassYYRaw,
        long tangentVelocityXRaw,
        long tangentVelocityYRaw,
        int effectiveMassFractionBitCount,
        long frictionRaw,
        long normalImpulseRaw,
        out long expectedXRaw,
        out long expectedYRaw
    ) {
        var deltaXRaw = RoundTiesToEven(
            shift: effectiveMassFractionBitCount,
            value: ((((BigInteger)tangentMassXXRaw) * -tangentVelocityXRaw) + (((BigInteger)tangentMassXYRaw) * -tangentVelocityYRaw))
        );
        var deltaYRaw = RoundTiesToEven(
            shift: effectiveMassFractionBitCount,
            value: ((((BigInteger)tangentMassXYRaw) * -tangentVelocityXRaw) + (((BigInteger)tangentMassYYRaw) * -tangentVelocityYRaw))
        );
        var maxImpulseRaw = RoundTiesToEven(
            shift: FixedQ4816.FractionBitCount,
            value: (((BigInteger)frictionRaw) * normalImpulseRaw)
        );
        var lengthSquared = ((((BigInteger)deltaXRaw) * deltaXRaw) + (((BigInteger)deltaYRaw) * deltaYRaw));
        var maxSquared = (((BigInteger)maxImpulseRaw) * maxImpulseRaw);

        if (lengthSquared <= maxSquared) {
            expectedXRaw = deltaXRaw;
            expectedYRaw = deltaYRaw;

            return;
        }

        var lengthRaw = IntegerSquareRoot(value: lengthSquared);

        if (lengthRaw <= BigInteger.Zero) {
            expectedXRaw = 0L;
            expectedYRaw = 0L;

            return;
        }

        expectedXRaw = ((long)((((BigInteger)deltaXRaw) * maxImpulseRaw) / lengthRaw));
        expectedYRaw = ((long)((((BigInteger)deltaYRaw) * maxImpulseRaw) / lengthRaw));
    }

    // A bracketed integer search whose predicate is one exact squaring — never the subject's own SquareRoot face.
    private static BigInteger IntegerSquareRoot(BigInteger value) {
        if (value <= BigInteger.Zero) {
            return BigInteger.Zero;
        }

        var low = BigInteger.Zero;
        var high = value;

        while (low < high) {
            var mid = (((low + high) + BigInteger.One) / 2);

            if ((mid * mid) <= value) {
                low = mid;
            } else {
                high = (mid - BigInteger.One);
            }
        }

        return low;
    }
    // The exact rational value/2^shift, rounded to the nearest integer, ties to even.
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
