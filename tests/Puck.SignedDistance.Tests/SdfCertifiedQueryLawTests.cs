using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The certified queries prove what they answer. A certified sweep never carries a sphere through a surface, however
/// thin the surface and however long the step, where a fixed-step stepper that samples the field only at each step's ends
/// passes straight through the same wall (the red leg, which keeps the fixture honest). Every sampled point of the swept
/// sphere keeps the point field above zero, and a certified line of sight's Clear and Blocked agree with the point field
/// along the segment.
/// </summary>
public sealed class SdfCertifiedQueryLawTests {
    private const float Radius = 0.1f;
    private const int SweepBudget = 4096;
    private const float WallHalfThickness = 0.005f;

    [Fact]
    public void ALineOfSightDoesNotWrapTheDifferenceBetweenRepresentableEndpoints() {
        var builder = new SdfProgramBuilder();

        _ = builder.Translate(offset: new Vector3(x: ((float)(-(7L << 44))), y: 0f, z: 0f))
            .Sphere(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), radius: 1f);

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var from = FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromRawBits(value: -(3L << 61)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));
        var to = FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromRawBits(value: (3L << 61)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        // The sphere lies left of both endpoints. A wrapped displacement instead visits its centre at t = 1/4.
        Assert.True(condition: evaluator.TryCertifiedLineOfSight(boundsQueryBudget: 4, from: from, sight: out var sight, to: to));
        Assert.NotEqual(expected: SdfCertifiedVisibility.Blocked, actual: sight.Visibility);
    }
    [Fact]
    public void ASphereSweepProvesItsVolumeWhenTheFieldGradientExceedsOne() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: Vector3.UnitY, offset: 0f)
            .Plane(blend: SdfBlendOp.ChamferIntersection, material: material, normal: Vector3.UnitY, offset: 0f, smooth: 0f);

        var evaluator = new SdfFieldEvaluator(program: builder.Build());

        Assert.True(condition: evaluator.TryCertifiedSweep(
            boundsQueryBudget: SweepBudget,
            displacement: Fixed(value: new Vector3(x: 0f, y: -1.25f, z: 0f)),
            origin: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: 0f, y: 2f, z: 0f))),
            radius: FixedQ4816.One,
            sweep: out var sweep
        ));
        Assert.Equal(expected: CertifiedSweepOutcome.Contact, actual: sweep.Outcome);
        Assert.True(condition: sweep.Reached.TryDelta(delta: out var reached, origin: FixedPosition.Zero));
        // Both planes have the same zero set, y = 0, despite the bevel's larger field gradient.
        Assert.True(condition: (reached.Y > FixedQ4816.One));
        Assert.True(condition: (reached.Y < FixedQ4816.FromDouble(value: 1.01)));
    }
    [Fact]
    public void ASweepCannotCertifyTravelPastTheEvaluatorFrame() {
        var builder = new SdfProgramBuilder();

        _ = builder.Plane(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), normal: Vector3.UnitY, offset: 0f);

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var start = new FixedVector3(X: FixedQ4816.FromRawBits(value: (long.MaxValue - 100L)), Y: FixedQ4816.One, Z: FixedQ4816.Zero);

        Assert.True(condition: evaluator.TryCertifiedSweep(
            boundsQueryBudget: 256,
            displacement: new FixedVector3(X: FixedQ4816.FromRawBits(value: 200L), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            origin: FixedPosition.FromLocal(local: start),
            radius: FixedQ4816.Zero,
            sweep: out var sweep
        ));
        Assert.NotEqual(expected: CertifiedSweepOutcome.Clear, actual: sweep.Outcome);
        Assert.True(condition: (sweep.Fraction < UnitInterval32.One));
        Assert.True(condition: sweep.Reached.TryDelta(delta: out var reached, origin: FixedPosition.Zero));
        Assert.InRange(actual: reached.X.Value, low: start.X.Value, high: (long.MaxValue - 1L));
    }
    [InlineData(1f)]
    [InlineData(10f)]
    [InlineData(1000f)]
    [InlineData(100000f)]
    [Theory]
    public void ACertifiedSweepNeverTunnelsThroughAThinWall(float speed) {
        var evaluator = ThinWall();

        for (var lane = -4; (lane <= 4); lane++) {
            var startX = -0.6f;
            var start = Fixed(value: new Vector3(x: startX, y: (0.37f * lane), z: (-0.21f * lane)));
            var displacement = Fixed(value: new Vector3(x: speed, y: (0.01f * lane), z: 0f));

            Assert.True(condition: evaluator.TryCertifiedSweep(
                displacement: displacement,
                origin: FixedPosition.FromLocal(local: start),
                boundsQueryBudget: SweepBudget,
                radius: FixedQ4816.FromDouble(value: Radius),
                sweep: out var sweep
            ));

            Assert.True(condition: sweep.Reached.TryDelta(delta: out var reachedPoint, origin: FixedPosition.Zero));

            var reached = reachedPoint.X;
            var face = FixedQ4816.FromDouble(value: (-WallHalfThickness - Radius));

            Assert.Equal(expected: CertifiedSweepOutcome.Contact, actual: sweep.Outcome);
            Assert.True(condition: (reached <= face), userMessage: $"speed {speed}, lane {lane}: the sphere's centre reached {reached}, past the wall's face at {face}");
            // Progress: the sweep stops close to contact, not at its start.
            Assert.True(
                condition: ((face - reached) < FixedQ4816.FromDouble(value: 0.01)),
                userMessage: $"speed {speed}, lane {lane}: the sweep stopped {(face - reached)} short of the wall after {sweep.BoundsQueries} bounds queries"
            );
        }
    }
    [InlineData(10f)]
    [InlineData(1000f)]
    [Theory]
    public void AFixedStepStepperTunnelsThroughTheSameWall(float speed) {
        var evaluator = ThinWall();
        var start = Fixed(value: new Vector3(x: -2.5f, y: 0f, z: 0f));
        var end = Fixed(value: new Vector3(x: (-2.5f + speed), y: 0f, z: 0f));
        var radius = FixedQ4816.FromDouble(value: Radius);

        Assert.True(condition: evaluator.TryDistance(distance: out var atStart, material: out _, position: FixedPosition.FromLocal(local: start)));
        Assert.True(condition: evaluator.TryDistance(distance: out var atEnd, material: out _, position: FixedPosition.FromLocal(local: end)));
        // Both ends are clear, so a stepper that tests only the ends accepts the move, and lands past the wall.
        Assert.True(condition: (atStart > radius));
        Assert.True(condition: (atEnd > radius));
        Assert.True(condition: (end.X > FixedQ4816.FromDouble(value: WallHalfThickness)));
    }
    [Fact]
    public void EveryPointOfACertifiedSweepStaysClear() {
        var evaluator = Scene();
        var random = new Random(Seed: 1234);
        var radius = FixedQ4816.FromDouble(value: Radius);
        FixedVector3[] offsets = [
            FixedVector3.Zero,
            new(X: radius, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            new(X: -radius, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            new(X: FixedQ4816.Zero, Y: radius, Z: FixedQ4816.Zero),
            new(X: FixedQ4816.Zero, Y: -radius, Z: FixedQ4816.Zero),
            new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: radius),
            new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: -radius),
        ];

        for (var trial = 0; (trial < 64); trial++) {
            var start = Fixed(value: new Vector3(x: Next(random: random, scale: 4f), y: (1.5f + Next(random: random, scale: 1f)), z: Next(random: random, scale: 4f)));
            var displacement = Fixed(value: new Vector3(x: Next(random: random, scale: 6f), y: Next(random: random, scale: 3f), z: Next(random: random, scale: 6f)));

            Assert.True(condition: evaluator.TryCertifiedSweep(boundsQueryBudget: SweepBudget, displacement: displacement, origin: FixedPosition.FromLocal(local: start), radius: radius, sweep: out var sweep));
            if ((sweep.Outcome == CertifiedSweepOutcome.Contact) && (sweep.Reached == FixedPosition.FromLocal(local: start))) {
                Assert.Equal(expected: UnitInterval32.Zero, actual: sweep.Fraction);
                continue;
            }

            for (var sample = 0; (sample <= 64); sample++) {
                // Each sample's parameter is floored from the sweep's 2⁻³² fraction to the Q16 grid, so it never passes it.
                var t = FixedQ4816.FromRawBits(value: ((long)(((sweep.Fraction.Value * ((ulong)sample)) / 64UL) >> (UnitInterval32.FractionBitCount - FixedQ4816.FractionBitCount))));
                var point = (start + (displacement * t));

                foreach (var offset in offsets) {
                    Assert.True(condition: evaluator.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: (point + offset))));
                    Assert.True(condition: (distance > FixedQ4816.Zero), userMessage: $"trial {trial}: the certified sweep reached {sweep.Fraction}, but at {t} the sphere's offset {offset} reads {distance}");
                }
            }

            foreach (var offset in offsets) {
                Assert.True(condition: evaluator.TryDistance(distance: out var atReached, material: out _, position: (sweep.Reached + offset)));
                Assert.True(condition: (atReached > FixedQ4816.Zero), userMessage: $"trial {trial}: the reached sphere's offset {offset} reads {atReached}");
            }
        }
    }
    [Fact]
    public void ACertifiedLineOfSightAgreesWithThePointField() {
        var evaluator = Scene();
        var random = new Random(Seed: 99);
        var verdicts = new Dictionary<SdfCertifiedVisibility, int>();

        for (var trial = 0; (trial < 96); trial++) {
            var from = Fixed(value: new Vector3(x: Next(random: random, scale: 5f), y: (1f + Next(random: random, scale: 2f)), z: Next(random: random, scale: 5f)));
            var to = Fixed(value: new Vector3(x: Next(random: random, scale: 5f), y: (1f + Next(random: random, scale: 2f)), z: Next(random: random, scale: 5f)));

            Assert.True(condition: evaluator.TryCertifiedLineOfSight(boundsQueryBudget: SdfFieldEvaluator.CertifiedLineOfSightMaximumBoundsQueries, from: FixedPosition.FromLocal(local: from), sight: out var sight, to: FixedPosition.FromLocal(local: to)));
            var visibility = sight.Visibility;

            verdicts[visibility] = (verdicts.GetValueOrDefault(key: visibility) + 1);

            var least = FixedQ4816.MaxValue;

            for (var sample = 0; (sample <= 256); sample++) {
                var point = (from + ((to - from) * FixedQ4816.FromRawBits(value: ((FixedQ4816.One.Value * sample) / 256L))));

                Assert.True(condition: evaluator.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: point)));
                least = FixedQ4816.Min(x: least, y: distance);
            }

            if (visibility == SdfCertifiedVisibility.Clear) {
                Assert.True(condition: (least > FixedQ4816.Zero), userMessage: $"trial {trial}: certified clear, but a sample reads {least}");
            }
        }

        // The fixture reaches both proved verdicts.
        Assert.True(condition: (verdicts.GetValueOrDefault(key: SdfCertifiedVisibility.Clear) > 0));
        Assert.True(condition: (verdicts.GetValueOrDefault(key: SdfCertifiedVisibility.Blocked) > 0));
    }
    [Fact]
    public void ACertifiedLineOfSightSeesAThinWall() {
        var evaluator = ThinWall();

        Assert.True(condition: evaluator.TryCertifiedLineOfSight(
            from: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -3f, y: 0.2f, z: 0.1f))),
            to: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: 3f, y: -0.1f, z: 0.3f))),
            sight: out var through,
            boundsQueryBudget: SdfFieldEvaluator.CertifiedLineOfSightMaximumBoundsQueries
        ));
        Assert.Equal(actual: through.Visibility, expected: SdfCertifiedVisibility.Blocked);
        Assert.True(condition: evaluator.TryCertifiedLineOfSight(
            from: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -3f, y: 0.2f, z: 0.1f))),
            to: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -0.5f, y: -0.1f, z: 0.3f))),
            sight: out var before,
            boundsQueryBudget: SdfFieldEvaluator.CertifiedLineOfSightMaximumBoundsQueries
        ));
        Assert.Equal(actual: before.Visibility, expected: SdfCertifiedVisibility.Clear);
    }
    [Fact]
    public void ACertifiedLineOfSightSpendsNoMoreThanItsBudgetOrItsCeiling() {
        var evaluator = Scene();
        var random = new Random(Seed: 4242);
        var deepest = 0;

        for (var trial = 0; (trial < 96); trial++) {
            var from = FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: Next(random: random, scale: 5f), y: (1f + Next(random: random, scale: 2f)), z: Next(random: random, scale: 5f))));
            var to = FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: Next(random: random, scale: 5f), y: (1f + Next(random: random, scale: 2f)), z: Next(random: random, scale: 5f))));

            Assert.True(condition: evaluator.TryCertifiedLineOfSight(boundsQueryBudget: int.MaxValue, from: from, sight: out var unlimited, to: to));
            Assert.InRange(actual: unlimited.BoundsQueries, high: SdfFieldEvaluator.CertifiedLineOfSightMaximumBoundsQueries, low: 1);
            deepest = Math.Max(val1: deepest, val2: unlimited.BoundsQueries);

            // A smaller budget runs a prefix of the larger one: within budget it proves the same verdict at the same
            // cost, and past it it stops at exactly its budget, undecided.
            foreach (var budget in ((int[])[1, 2, 3, 7, 16, 32])) {
                Assert.True(condition: evaluator.TryCertifiedLineOfSight(boundsQueryBudget: budget, from: from, sight: out var limited, to: to));

                if (unlimited.BoundsQueries <= budget) {
                    Assert.Equal(actual: limited, expected: unlimited);
                } else {
                    Assert.Equal(expected: new SdfCertifiedSight(BoundsQueries: budget, Visibility: SdfCertifiedVisibility.Undecided), actual: limited);
                }
            }
        }

        // The fixture reaches past the largest budget, so every prefix leg cuts at least one trial.
        Assert.True(condition: (deepest > 32), userMessage: $"the deepest line of sight spent only {deepest} queries");
    }
    [Fact]
    public void AGrazingLineOfSightStaysUnderTheCeiling() {
        // A segment skimming a sphere at a raw's height splits deepest; whatever it answers, it answers within the
        // proved ceiling.
        var builder = new SdfProgramBuilder();

        _ = builder.Sphere(material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), radius: 1f);

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var skim = (1f + (1f / 65536f));

        Assert.True(condition: evaluator.TryCertifiedLineOfSight(
            boundsQueryBudget: int.MaxValue,
            from: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -4f, y: skim, z: 0f))),
            sight: out var sight,
            to: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: 4f, y: skim, z: 0f)))
        ));
        Assert.InRange(actual: sight.BoundsQueries, high: SdfFieldEvaluator.CertifiedLineOfSightMaximumBoundsQueries, low: 1);
    }
    [Fact]
    public void ACertifiedSweepSpendsNoMoreThanItsBudget() {
        var evaluator = ThinWall();
        var origin = FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -0.6f, y: 0.1f, z: 0f)));
        var displacement = Fixed(value: new Vector3(x: 1000f, y: 0f, z: 0f));
        var radius = FixedQ4816.FromDouble(value: Radius);

        Assert.True(condition: evaluator.TryCertifiedSweep(boundsQueryBudget: int.MaxValue, displacement: displacement, origin: origin, radius: radius, sweep: out var full));
        Assert.Equal(expected: CertifiedSweepOutcome.Contact, actual: full.Outcome);

        var previous = UnitInterval32.Zero;

        for (var budget = 1; (budget <= (full.BoundsQueries + 2)); budget++) {
            Assert.True(condition: evaluator.TryCertifiedSweep(boundsQueryBudget: budget, displacement: displacement, origin: origin, radius: radius, sweep: out var limited));
            Assert.True(condition: (limited.BoundsQueries <= budget), userMessage: $"budget {budget}: the sweep spent {limited.BoundsQueries}");

            if (budget >= full.BoundsQueries) {
                Assert.Equal(actual: limited, expected: full);
            } else {
                // A cut-short sweep reports the budget spent and keeps only ground it proved, never more than a longer
                // budget proves.
                Assert.Equal(expected: CertifiedSweepOutcome.Exhausted, actual: limited.Outcome);
                Assert.Equal(expected: budget, actual: limited.BoundsQueries);
                Assert.True(condition: (limited.Fraction >= previous));
                Assert.True(condition: (limited.Fraction <= full.Fraction));
                previous = limited.Fraction;
            }
        }
    }
    [Fact]
    public void ASweepWithAContactToleranceStopsOnceItsProvedClearanceIsThatSmall() {
        var evaluator = ThinWall();
        var origin = FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -0.6f, y: 0.1f, z: 0f)));
        var displacement = Fixed(value: new Vector3(x: 1000f, y: 0f, z: 0f));
        var radius = FixedQ4816.FromDouble(value: Radius);
        var tolerance = FixedQ4816.FromDouble(value: 0.01);

        Assert.True(condition: evaluator.TryCertifiedSweep(boundsQueryBudget: int.MaxValue, contactTolerance: FixedQ4816.Zero, displacement: displacement, origin: origin, radius: radius, sweep: out var exact));
        Assert.True(condition: evaluator.TryCertifiedSweep(boundsQueryBudget: SweepBudget, contactTolerance: tolerance, displacement: displacement, origin: origin, radius: radius, sweep: out var tolerant));
        Assert.Equal(expected: CertifiedSweepOutcome.Contact, actual: tolerant.Outcome);
        Assert.True(condition: (tolerant.Fraction <= exact.Fraction));

        // Conservative advancement nears a face ever more slowly; the tolerance ends the approach in a few queries
        // instead of the many the last sliver costs.
        Assert.True(condition: ((tolerant.BoundsQueries * 2) < exact.BoundsQueries), userMessage: $"the tolerant sweep spent {tolerant.BoundsQueries} queries, the exact one {exact.BoundsQueries}");
        Assert.True(condition: evaluator.TryDistance(distance: out var distance, material: out _, position: tolerant.Reached));

        var clearance = (distance - radius);

        Assert.True(condition: ((clearance > FixedQ4816.Zero) && (clearance <= (tolerance + FixedQ4816.FromDouble(value: 0.001)))), userMessage: $"the sweep stopped {clearance} clear of the face");
    }
    [Fact]
    public void ALargeClearanceTimesALargeStepScaleProposesTheWholeStep() {
        // A field 2⁴⁰ units from everything with a step scale of 2⁴⁰ (a Lipschitz bound of 2⁻⁴⁰) proves a one-unit step
        // in one stride. Their product, the reach, is 2⁹⁶ raws; shifted onto the 2⁻³² fraction grid it passed Int128,
        // wrapped to a proposal of nothing, and the sweep crept one 2⁻³² step a query until its budget ran out.
        var far = FixedQ4816.FromInteger(value: (1L << 40));
        var field = new ConstantBounds(Distance: far, StepScale: far);

        Assert.True(condition: CertifiedFieldSweep.TrySweep(
            boundsQueryBudget: 64,
            contactTolerance: FixedQ4816.Zero,
            displacement: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            field: field,
            origin: FixedPosition.Zero,
            radius: FixedQ4816.One,
            sweep: out var sweep
        ));
        Assert.Equal(expected: CertifiedSweepOutcome.Clear, actual: sweep.Outcome);
        Assert.Equal(expected: UnitInterval32.One, actual: sweep.Fraction);
        Assert.Equal(expected: 2, actual: sweep.BoundsQueries);
    }

    private static SdfFieldEvaluator ThinWall() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Box(halfExtents: new Vector3(x: WallHalfThickness, y: 4f, z: 4f), material: material, round: 0f);

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static SdfFieldEvaluator Scene() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder
            .Plane(material: material, normal: Vector3.UnitY, offset: 0f)
            .ResetPoint().Translate(offset: new Vector3(x: 1f, y: 1.5f, z: -1f)).Sphere(material: material, radius: 0.8f)
            .ResetPoint().Translate(offset: new Vector3(x: -2f, y: 1f, z: 1.5f)).Rotate(rotation: Quaternion.Normalize(value: new Quaternion(w: 0.9f, x: 0.1f, y: 0.4f, z: 0f))).Box(halfExtents: new Vector3(x: 0.7f, y: 1f, z: 0.3f), material: material, round: 0.05f)
            .ResetPoint().Translate(offset: new Vector3(x: 2.5f, y: 1f, z: 2f)).Torus(blend: SdfBlendOp.SmoothUnion, majorRadius: 0.9f, material: material, minorRadius: 0.25f, smooth: 0.3f);

        return new SdfFieldEvaluator(program: builder.Build());
    }
    private static FixedVector3 Fixed(Vector3 value) =>
        new(
            X: FixedQ4816.FromDouble(value: value.X),
            Y: FixedQ4816.FromDouble(value: value.Y),
            Z: FixedQ4816.FromDouble(value: value.Z)
        );

    // A field at one distance everywhere, with whatever step scale the law names.
    private sealed record ConstantBounds(FixedQ4816 Distance, FixedQ4816 StepScale) : IFieldBounds {
        public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
            distance = FixedInterval.FromPoint(value: Distance);

            return true;
        }
    }

    private static float Next(Random random, float scale) =>
        (((((float)random.NextDouble()) * 2f) - 1f) * scale);
}
