using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>The mathematical admission contract for a proposed reprojected march start. The independent fixed
/// evaluator proves the midpoint ball empty; these laws do not install a history sampler or renderer seed.</summary>
public sealed class SdfMarchSeedLawTests {
    // The renderer accepts scaled field values inside max(SurfaceEpsilon, pixelFootprint * traveled).
    // SurfaceEpsilon is 0.001 in Sdf/march/sdf-march-constants.hlsli; the fixed oracle rounds coverage outward.
    private const double SurfaceEpsilon = 0.001;
    private const double Candidate = 8;
    private const double Start = 2;

    [InlineData(2.03125)]
    [InlineData(5)]
    [InlineData(7.96875)]
    [Theory]
    public void NearMidAndFarTinyOccludersPreventSkippingTheCoveredInterval(double center) {
        var program = Sphere(center: center, radius: (1f / 64f));
        var evaluator = new SdfFieldEvaluator(program: program);

        Assert.True(condition: evaluator.TryDistance(position: Point(x: center), distance: out var interior, material: out _));
        Assert.True(condition: (interior < FixedQ4816.Zero));
        var sampled = Probe(candidate: Candidate, footprint: 0, program: program, start: Start);

        Assert.False(condition: sampled.Admitted);
        Assert.False(condition: sampled.FixedClear);
    }
    [InlineData(0, 0)]
    [InlineData(0.00048828125, 0)]
    [InlineData(0.0625, 0.015625)]
    [Theory]
    public void EndpointContactAndAcceptedSurfaceBandsCannotDisappear(double gap, double footprint) {
        var program = Sphere(center: ((Candidate + 0.125) + gap), radius: 0.125f);
        var sampled = Probe(candidate: Candidate, footprint: footprint, program: program, start: Start);
        var evaluator = new SdfFieldEvaluator(program: program);
        var acceptedBand = Math.Max(val1: SurfaceEpsilon, val2: (footprint * Candidate));

        Assert.True(condition: evaluator.TryDistance(position: Point(x: Candidate), distance: out var endpoint, material: out _));
        Assert.InRange(actual: ((double)endpoint), high: acceptedBand, low: 0);
        Assert.False(condition: sampled.Admitted);
        Assert.False(condition: sampled.FixedClear);
        if (gap > 0) {
            // This is the actual discriminator: the center ball without the renderer's acceptance band is empty.
            Assert.True(condition: (sampled.LowerBound > ((Candidate - Start) / 2)));
        }
    }
    [Fact]
    public void AProvenEmptySegmentClearsTheFixedOracleAndEveryAcceptedSurfaceProbe() {
        const double Footprint = (1d / 64);
        var program = Sphere(center: ((Candidate + 0.125) + 0.25), radius: 0.125f);
        var evaluator = new SdfFieldEvaluator(program: program);
        var sampled = Probe(candidate: Candidate, footprint: Footprint, program: program, start: Start);

        Assert.True(condition: sampled.Admitted);
        Assert.True(condition: sampled.FixedClear);
        for (var index = 0; (index <= 256); index++) {
            var depth = (Start + (((Candidate - Start) * index) / 256));
            var threshold = Outward(value: Math.Max(val1: SurfaceEpsilon, val2: (Footprint * depth)));

            Assert.False(condition: evaluator.Overlap(center: Point(x: depth), radius: threshold));
        }
    }
    [Fact]
    public void ChamferOverestimateCannotSubstituteForTheConservativeStepScale() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(normal: -Vector3.UnitX, offset: 7.5f, material: material)
            .ResetPoint().Plane(normal: -Vector3.UnitX, offset: 7.5f, material: material,
                blend: SdfBlendOp.ChamferIntersection, smooth: 0.25f);
        var program = builder.Build();
        var sampled = Probe(candidate: Candidate, footprint: 0, program: program, start: Start);
        var evaluator = new SdfFieldEvaluator(program: program);

        Assert.InRange(actual: program.StepScale, low: 0.7f, high: 0.71f);
        Assert.True(condition: evaluator.TryDistance(position: Point(x: Candidate), distance: out var endpoint, material: out _));
        Assert.True(condition: (endpoint < FixedQ4816.Zero));
        Assert.True(condition: Admit(candidate: Candidate, field: sampled.RawField, footprint: 0, start: Start, stepScale: 1));
        Assert.False(condition: sampled.Admitted);
        Assert.False(condition: sampled.FixedClear);
    }
    [Fact]
    public void ADeepInteriorSampleNeverBecomesAnEmptyBallThroughAbsoluteDistance() {
        var program = Sphere(center: 5, radius: 100);
        var sample = Probe(candidate: Candidate, footprint: 0, program: program, start: Start);

        Assert.True(condition: (Math.Abs(value: sample.LowerBound) > (((Candidate - Start) / 2) + SurfaceEpsilon)));
        Assert.False(condition: sample.Admitted);
        Assert.False(condition: sample.FixedClear);
    }
    [InlineData(null, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(double.NegativeInfinity, 1)]
    [InlineData(-100d, 1)]
    [InlineData(0d, 1)]
    [InlineData(100d, 0)]
    [InlineData(100d, -1)]
    [InlineData(100d, double.NaN)]
    [InlineData(100d, double.PositiveInfinity)]
    [Theory]
    public void MissingNonfiniteOrNonpositiveBoundsNeverAuthorizeASeed(double? field, double scale) {
        Assert.True(condition: Admit(candidate: Candidate, field: 100, footprint: 0, start: Start, stepScale: 1));
        Assert.False(condition: Admit(candidate: Candidate, field: field, footprint: 0, start: Start, stepScale: scale));
    }
    [InlineData(8, 2, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(double.NaN, 8, 0)]
    [InlineData(2, double.PositiveInfinity, 0)]
    [InlineData(2, 8, double.NaN)]
    [InlineData(2, 8, -1)]
    [Theory]
    public void InvalidIntervalsAndFootprintsNeverAuthorizeASeed(double start, double candidate, double footprint) {
        Assert.True(condition: Admit(candidate: Candidate, field: 100, footprint: 0, start: Start, stepScale: 1));
        Assert.False(condition: Admit(candidate: candidate, field: 100, footprint: footprint, start: start, stepScale: 1));
    }
    [Fact]
    public void EqualityWithTheExpandedBallIsNotAnEmptySegmentProof() {
        var boundary = (((Candidate - Start) / 2) + SurfaceEpsilon);

        Assert.False(condition: Admit(candidate: Candidate, field: boundary, footprint: 0, start: Start, stepScale: 1));
        Assert.True(condition: Admit(candidate: Candidate, field: (boundary + 0.001), footprint: 0, start: Start, stepScale: 1));
    }
    [Fact]
    public void AnUnrepresentableMidpointHasNoUsableFieldBound() {
        var evaluator = new SdfFieldEvaluator(program: Sphere(center: 0, radius: 1));
        var outside = new FixedPosition(cellX: long.MaxValue, cellY: 0, cellZ: 0, local: FixedVector3.Zero);

        Assert.False(condition: evaluator.TryDistance(distance: out _, material: out _, position: outside));
        Assert.True(condition: evaluator.Overlap(center: outside, radius: Outward(value: (((Candidate - Start) / 2) + SurfaceEpsilon))));
        Assert.False(condition: Admit(candidate: Candidate, field: null, footprint: 0, start: Start, stepScale: 1));
    }

    // The proposed inequality is stated only in this proof. The separately implemented fixed evaluator supplies
    // the conservative multiply through Overlap, avoiding a second production marcher or lower-bound helper.
    private static bool Admit(double? field, double stepScale, double start, double candidate, double footprint) {
        if ((field is not { } distance) || !double.IsFinite(d: distance) || (distance <= 0) || !double.IsFinite(d: stepScale) || (stepScale <= 0) ||
            !double.IsFinite(d: start) || !double.IsFinite(d: candidate) || (candidate <= start) || !double.IsFinite(d: footprint) || (footprint < 0)) { return false; }
        var lower = (distance * stepScale);
        var radius = ((candidate - start) / 2);
        var margin = Math.Max(val1: SurfaceEpsilon, val2: (footprint * candidate));

        return (double.IsFinite(d: lower) && double.IsFinite(d: radius) && double.IsFinite(d: margin) && (lower > (radius + margin)));
    }
    private static (double RawField, double LowerBound, bool Admitted, bool FixedClear) Probe(SdfProgram program, double start, double candidate, double footprint) {
        var evaluator = new SdfFieldEvaluator(program: program);
        var midpoint = Point(x: (start + ((candidate - start) / 2)));

        Assert.True(condition: evaluator.TryDistance(distance: out var distance, material: out _, position: midpoint));
        var radius = Outward(value: (((candidate - start) / 2) + Math.Max(val1: SurfaceEpsilon, val2: (footprint * candidate))));
        var raw = ((double)distance);

        return (raw, (raw * program.StepScale),
            Admit(field: raw, stepScale: program.StepScale, start: start, candidate: candidate, footprint: footprint),
            !evaluator.Overlap(center: midpoint, radius: radius));
    }
    private static FixedQ4816 Outward(double value) {
        var rounded = FixedQ4816.FromDouble(value: value);

        return ((((double)rounded) < value) ? (rounded + FixedQ4816.Epsilon) : rounded);
    }
    private static FixedPosition Point(double x) => FixedPosition.FromLocal(local: new FixedVector3(
        X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));
    private static SdfProgram Sphere(double center, float radius) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: new Vector3(x: ((float)center), y: 0, z: 0)).Sphere(radius: radius, material: material);
        return builder.Build();
    }
}
