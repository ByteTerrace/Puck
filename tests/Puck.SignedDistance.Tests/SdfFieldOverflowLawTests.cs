using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// No step of a field evaluation may leave the carrier unseen. The point evaluator answers only inside its frame, where
/// the bounds interpreter proved that no step can overflow, and refuses outside it, never wrapping; the bounds answer
/// for a box exactly where every point of it is answered. So every point answer lies inside its box's bounds, or both
/// refuse, even for programs whose constants and positions reach the carrier's ends. The certified queries never read
/// an unbounded interval as a clearance.
/// </summary>
public sealed class SdfFieldOverflowLawTests {
    [Fact]
    public void CellDistanceCannotHideAnOverflowedFrequencyProduct() {
        var evaluator = Program(emit: (builder, material) => builder
            .Plane(material: material, normal: Vector3.UnitY, offset: 1f)
            .CellDisplace(amplitude: 0.0000152587890625f, frequency: 1048576f, mode: SdfCellMode.F1, randomness: 0f, seed: 0u));
        // At x = 2^44, x * frequency = 2^64 world units, which wraps to zero. A finite cell-distance
        // envelope must not make that point evaluable. The origin's arithmetic remains expressible.
        var outside = FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromInteger(value: (1L << 44)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        Assert.True(condition: evaluator.TryDistance(position: FixedPosition.Zero, distance: out _, material: out _));
        Assert.True(condition: (evaluator.Frame < FixedQ4816.FromInteger(value: (1L << 44))));
        Assert.False(condition: evaluator.TryDistance(distance: out _, material: out _, position: outside));
        Assert.False(condition: evaluator.TryDistanceBounds(distance: out _, lower: outside, upper: outside));
    }
    [Fact]
    public void RoundConeCannotHideAnOverflowedBranchPredicate() {
        var evaluator = Program(emit: (builder, material) => builder
            .Translate(offset: new Vector3(x: -17592186044416f, y: 0f, z: 0f))
            .RoundCone(lowerRadius: 1048576f, upperRadius: 0f, height: 1f, material: material));
        // At the origin qx = 2^44 and b = 2^20: k = -2^64 wraps to zero, selecting the cone's
        // body (-2^20) instead of its distant lower cap. The predicate overflows even in the smallest cube.
        Assert.True(condition: (evaluator.Frame < FixedQ4816.Zero));
        Assert.False(condition: evaluator.TryDistance(position: FixedPosition.Zero, distance: out _, material: out _));
        Assert.False(condition: evaluator.TryDistanceBounds(lower: FixedPosition.Zero, upper: FixedPosition.Zero, distance: out _));
    }
    [Fact]
    public void VesicaCannotHideAnOverflowedBranchPredicate() {
        var evaluator = Program(emit: (builder, material) => builder.Vesica(radius: 1099511627776f, halfSeparation: 549755813888f, material: material));
        var position = FixedPosition.FromLocal(local: FixedVector3.UnitY);

        // b*d leaves the carrier around the origin. At y = 1 its wrapped comparison selects the positive
        // cap, although the body distance is approximately -2^39. Joining the two arms hides the overflow.
        Assert.True(condition: (evaluator.Frame < FixedQ4816.Zero));
        Assert.False(condition: evaluator.TryDistance(distance: out _, material: out _, position: position));
        Assert.False(condition: evaluator.TryDistanceBounds(distance: out _, lower: position, upper: position));
    }
    [Fact]
    public void AFieldThatOverflowsEverywhereIsRefusedByBothInterpreters() {
        // The review's case: a dilation by the carrier's whole negative range pushes every distance past its top, where
        // the point evaluator once wrapped to a large negative distance and the bounds read the saturated top as finite.
        var evaluator = Program(emit: (builder, material) => builder.Plane(material: material, normal: Vector3.UnitY, offset: 1f).Dilate(radius: -140737488355328f));
        var origin = FixedPosition.FromLocal(local: FixedVector3.Zero);

        Assert.True(condition: (evaluator.Frame < FixedQ4816.Zero), userMessage: $"the frame is {evaluator.Frame}, but no position can be answered");
        Assert.False(condition: evaluator.TryDistance(distance: out var wrapped, material: out _, position: origin), userMessage: $"the point evaluator answered {wrapped.Value} raws");
        Assert.False(condition: evaluator.TryDistanceBounds(distance: out var bounds, lower: origin, upper: origin), userMessage: $"the bounds answered {bounds}");
        Assert.True(condition: evaluator.TryCertifiedLineOfSight(
            boundsQueryBudget: SdfFieldEvaluator.CertifiedLineOfSightMaximumBoundsQueries,
            from: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: -1f, y: 0f, z: 0f))),
            sight: out var sight,
            to: FixedPosition.FromLocal(local: Fixed(value: new Vector3(x: 1f, y: 0f, z: 0f)))
        ));
        Assert.Equal(expected: SdfCertifiedVisibility.Undecided, actual: sight.Visibility);
        Assert.True(condition: evaluator.TryCertifiedSweep(
            boundsQueryBudget: 64,
            displacement: Fixed(value: Vector3.UnitX),
            origin: origin,
            radius: FixedQ4816.Zero,
            sweep: out var sweep
        ));
        Assert.Equal(expected: SdfCertifiedSweepOutcome.Exhausted, actual: sweep.Outcome);
        Assert.Equal(expected: FixedQ4816.Zero, actual: sweep.Fraction);
    }
    public static TheoryData<string> ExtremeCases() {
        var data = new TheoryData<string>();

        foreach (var name in ExtremePrograms.Keys) {
            data.Add(row: name);
        }

        return data;
    }
    [MemberData(memberName: nameof(ExtremeCases))]
    [Theory]
    public void EveryPointAnswerLiesInsideItsBoundsOrBothRefuse(string name) {
        var evaluator = new SdfFieldEvaluator(program: ExtremePrograms[name]());
        var random = new Random(Seed: StableSeed(name: name));
        var answered = 0;
        var refused = 0;

        for (var trial = 0; (trial < 256); trial++) {
            var point = ExtremePoint(frame: evaluator.Frame, random: random);
            var position = FixedPosition.FromLocal(local: point);
            var pointAnswered = evaluator.TryDistance(distance: out var distance, material: out _, position: position);
            var boundsAnswered = evaluator.TryDistanceBounds(distance: out var bounds, lower: position, upper: position);

            Assert.True(condition: (pointAnswered == boundsAnswered), userMessage: $"{name}: at {point} the point evaluator {(pointAnswered ? "answered" : "refused")} but the bounds {(boundsAnswered ? "answered" : "refused")}");

            if (!pointAnswered) {
                ++refused;
                continue;
            }

            ++answered;
            Assert.False(condition: bounds.IsUnbounded, userMessage: $"{name}: inside the frame {evaluator.Frame}, the bounds at {point} are unbounded");
            Assert.True(condition: bounds.Contains(value: distance), userMessage: $"{name}: the point answer {distance.Value} at {point} lies outside its bounds {bounds}");

            // A box about the point answers exactly when its corners do, and then holds every corner's answer.
            var half = FixedQ4816.FromRawBits(value: (1L << random.Next(maxValue: 40, minValue: 0)));
            var lower = new FixedVector3(X: Shift(value: point.X, by: -half), Y: Shift(value: point.Y, by: -half), Z: Shift(value: point.Z, by: -half));
            var upper = new FixedVector3(X: Shift(value: point.X, by: half), Y: Shift(value: point.Y, by: half), Z: Shift(value: point.Z, by: half));

            if (!evaluator.TryDistanceBounds(distance: out var boxBounds, lower: FixedPosition.FromLocal(local: lower), upper: FixedPosition.FromLocal(local: upper))) {
                continue;
            }

            Assert.False(condition: boxBounds.IsUnbounded, userMessage: $"{name}: the box [{lower}, {upper}] inside the frame has unbounded bounds");

            foreach (var corner in ((FixedVector3[])[lower, upper, new(X: lower.X, Y: upper.Y, Z: lower.Z), new(X: upper.X, Y: lower.Y, Z: upper.Z)])) {
                Assert.True(condition: evaluator.TryDistance(distance: out var atCorner, material: out _, position: FixedPosition.FromLocal(local: corner)), userMessage: $"{name}: the bounds answered for a box whose corner {corner} the point evaluator refuses");
                Assert.True(condition: boxBounds.Contains(value: atCorner), userMessage: $"{name}: the corner answer {atCorner.Value} at {corner} lies outside {boxBounds}");
            }
        }

        // The fixture reaches both sides of the frame.
        Assert.True(condition: (answered > 0), userMessage: $"{name}: no position was answered");
        Assert.True(condition: (refused > 0), userMessage: $"{name}: no position was refused");
    }
    [Fact]
    public void CertifiedQueriesNeverCertifyAcrossTheFrame() {
        // A trapezoid's squared distances leave the carrier near 2³⁹ raw, so its frame is a few million units wide; a
        // segment and a sweep reaching past it must not come back certified.
        var evaluator = Program(emit: (builder, material) => builder.Trapezoid(bottomHalfWidth: 0.9f, halfHeight: 0.6f, lift: SdfLift.Extrude, liftAmount: 0.3f, material: material, topHalfWidth: 0.4f));
        var frame = evaluator.Frame;
        var outside = FixedQ4816.FromRawBits(value: (frame.Value + (frame.Value >> 1)));

        Assert.True(condition: (frame > FixedQ4816.FromInteger(value: 1000L)), userMessage: $"the frame {frame} is narrower than the fixture assumes");
        Assert.True(condition: evaluator.TryCertifiedLineOfSight(
            boundsQueryBudget: SdfFieldEvaluator.CertifiedLineOfSightMaximumBoundsQueries,
            from: FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromInteger(value: 10L), Y: FixedQ4816.FromInteger(value: 10L), Z: FixedQ4816.Zero)),
            sight: out var sight,
            to: FixedPosition.FromLocal(local: new FixedVector3(X: outside, Y: FixedQ4816.FromInteger(value: 10L), Z: FixedQ4816.Zero))
        ));
        Assert.NotEqual(expected: SdfCertifiedVisibility.Clear, actual: sight.Visibility);

        var start = new FixedVector3(X: FixedQ4816.FromInteger(value: 10L), Y: FixedQ4816.FromInteger(value: 10L), Z: FixedQ4816.Zero);

        Assert.True(condition: evaluator.TryCertifiedSweep(
            boundsQueryBudget: 4096,
            displacement: new FixedVector3(X: outside, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero),
            origin: FixedPosition.FromLocal(local: start),
            radius: FixedQ4816.One,
            sweep: out var sweep
        ));
        Assert.Equal(expected: SdfCertifiedSweepOutcome.Exhausted, actual: sweep.Outcome);
        Assert.True(condition: sweep.Reached.TryDelta(delta: out var reached, origin: FixedPosition.Zero));
        Assert.True(condition: (reached.X <= frame), userMessage: $"the sweep reached {reached.X}, past the frame {frame}");
        Assert.True(condition: evaluator.TryDistance(distance: out _, material: out _, position: sweep.Reached), userMessage: "the sweep reached a centre the point evaluator refuses");
    }

    // Programs whose constants or reach approach the carrier's ends, so their frames sit at every scale.
    private static readonly IReadOnlyDictionary<string, Func<SdfProgram>> ExtremePrograms = new Dictionary<string, Func<SdfProgram>> {
        ["dilate by 2^40"] = () => Build(emit: (b, m) => b.Sphere(material: m, radius: 1f).Dilate(radius: -1099511627776f)),
        ["dilate by 2^46"] = () => Build(emit: (b, m) => b.Sphere(material: m, radius: 1f).Dilate(radius: 70368744177664f)),
        ["onion of 2^45"] = () => Build(emit: (b, m) => b.Box(halfExtents: Vector3.One, material: m, round: 0f).Onion(thickness: 35184372088832f)),
        ["plane offset 2^46"] = () => Build(emit: (b, m) => b.Plane(material: m, normal: Vector3.Normalize(value: new Vector3(x: 1f, y: 1f, z: 0f)), offset: 70368744177664f)),
        ["translate 2^45"] = () => Build(emit: (b, m) => b.Translate(offset: new Vector3(x: 35184372088832f, y: -35184372088832f, z: 0f)).Sphere(material: m, radius: 2f)),
        ["scale 2^-14"] = () => Build(emit: (b, m) => b.Scale(scale: new Vector3(x: 0.00006103515625f, y: 0.00006103515625f, z: 0.00006103515625f)).Sphere(material: m, radius: 1f)),
        ["scale 2^20"] = () => Build(emit: (b, m) => b.Scale(scale: new Vector3(x: 1048576f, y: 1048576f, z: 1048576f)).Box(halfExtents: Vector3.One, material: m, round: 0.1f)),
        ["trapezoid"] = () => Build(emit: (b, m) => b.Trapezoid(bottomHalfWidth: 0.9f, halfHeight: 0.6f, lift: SdfLift.Revolve, liftAmount: 0.3f, material: m, topHalfWidth: 0.4f)),
        ["capsule and smooth union"] = () => Build(emit: (b, m) => b.Capsule(endpoint: new Vector3(x: 4096f, y: 0f, z: 0f), material: m, radius: 1f).ResetPoint().Sphere(blend: SdfBlendOp.SmoothUnion, material: m, radius: 3f, smooth: 2f)),
    };

    private static SdfFieldEvaluator Program(Func<SdfProgramBuilder, int, SdfProgramBuilder> emit) =>
        new(program: Build(emit: emit));
    private static SdfProgram Build(Func<SdfProgramBuilder, int, SdfProgramBuilder> emit) {
        var builder = new SdfProgramBuilder();

        _ = emit(arg1: builder, arg2: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)));

        return builder.Build();
    }
    // A point at every scale: inside the frame, at its edge, just past it, and out to the carrier's ends.
    private static FixedVector3 ExtremePoint(FixedQ4816 frame, Random random) {
        long Coordinate() {
            var edge = Math.Max(val1: 1L, val2: frame.Value);

            return (random.Next(maxValue: 6, minValue: 0) switch {
                0 => random.NextInt64(maxValue: edge, minValue: -edge),
                1 => ((random.Next(maxValue: 2, minValue: 0) == 0) ? edge : -edge),
                2 => ((random.Next(maxValue: 2, minValue: 0) == 0) ? Math.Min(val1: (long.MaxValue - 1L), val2: (edge + 1L)) : -Math.Min(val1: (long.MaxValue - 1L), val2: (edge + 1L))),
                3 => random.NextInt64(maxValue: long.MaxValue, minValue: long.MinValue),
                4 => ((random.Next(maxValue: 2, minValue: 0) == 0) ? long.MaxValue : long.MinValue),
                _ => random.NextInt64(maxValue: (1L << 24), minValue: -(1L << 24)),
            });
        }

        return new(X: FixedQ4816.FromRawBits(value: Coordinate()), Y: FixedQ4816.FromRawBits(value: Coordinate()), Z: FixedQ4816.FromRawBits(value: Coordinate()));
    }
    private static FixedQ4816 Shift(FixedQ4816 value, FixedQ4816 by) =>
        FixedQ4816.FromRawBits(value: ((long)Int128.Clamp(value: (((Int128)value.Value) + by.Value), min: long.MinValue, max: long.MaxValue)));
    private static FixedVector3 Fixed(Vector3 value) =>
        new(
            X: FixedQ4816.FromDouble(value: value.X),
            Y: FixedQ4816.FromDouble(value: value.Y),
            Z: FixedQ4816.FromDouble(value: value.Z)
        );
    private static int StableSeed(string name) {
        var hash = 23;

        foreach (var character in name) {
            hash = unchecked(((hash * 31) + character));
        }

        return hash;
    }
}
