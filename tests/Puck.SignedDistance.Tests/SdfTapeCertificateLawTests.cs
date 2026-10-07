using System.Numerics;
using Puck.Maths;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfTapeCertificateLawTests {
    [Fact]
    public void DynamicCandidatesUseTheirOwnFrameAndRequireThePoseBall() {
        var builder = Builder(material: out var material);

        _ = builder.ResetPoint().TransformDynamic(slot: 0).Translate(offset: Vector3.UnitX).Sphere(radius: 1f, material: material);
        var program = builder.Build();
        var certificate = program.TapeCertificate(instruction: (program.InstructionCount - 1));

        Assert.Equal(expected: SdfTapeCertificate.Certified | SdfTapeCertificate.DynamicFrame, actual: certificate.Flags);
        var poseBall = SdfTapeCertificate.TransformBall(centreMagnitude: 3f, radius: 2f, position: new Vector3(x: 4f, y: 2f, z: 1f),
            orientation: new Quaternion(w: 0f, x: 0f, y: 2f, z: 0f));

        Assert.True(condition: poseBall.IsCertified);
        Assert.True(condition: (poseBall.Radius >= 14f));
        Assert.True(condition: (poseBall.Magnitude >= 49f));
        Assert.False(condition: SdfTapeCertificate.TransformBall(centreMagnitude: 3f, radius: 2f, position: new Vector3(value: float.MaxValue),
            orientation: Quaternion.Identity).IsCertified);
    }
    [Fact]
    public void ADynamicTransformAfterAPointPrefixHasNoFrameCertificate() {
        var builder = Builder(material: out var material);

        _ = builder.ResetPoint().Translate(offset: Vector3.One).TransformDynamic(slot: 0).Sphere(radius: 1f, material: material);
        var program = builder.Build();

        Assert.Equal(expected: 0u, actual: program.TapeCertificate(instruction: (program.InstructionCount - 1)).Flags);
    }
    [Fact]
    public void DynamicBallIncludesFloatPoseRoundingAtNearbySamples() {
        var centre = new Vector3(x: 12345.13f, y: -6789.5f, z: 32.2f);
        var position = new Vector3(x: -1.25f, y: 2f, z: 3f);
        var orientation = Quaternion.Normalize(value: new Quaternion(w: 0.79f, x: 0.23f, y: 0.48f, z: -0.33f));
        var transformedCentre = Pose(orientation: orientation, point: centre, position: position);
        var needsRoundoff = false;

        for (var x = -2; (x <= 2); x++) {
            for (var y = -2; (y <= 2); y++) {
                for (var z = -2; (z <= 2); z++) {
                    var sample = (centre + new Vector3(x: (x * 0.003f), y: (y * 0.003f), z: (z * 0.003f)));
                    var exactRadius = Distance(first: sample, second: centre);
                    var radius = ((float)exactRadius);

                    if (((double)radius) < exactRadius) { radius = float.BitIncrement(x: radius); }
                    var ball = SdfTapeCertificate.TransformBall(centreMagnitude: 12345.13f, orientation: orientation,
                        position: position, radius: radius);

                    Assert.True(condition: ball.IsCertified);
                    var transformedDistance = Distance(first: Pose(orientation: orientation, point: sample, position: position), second: transformedCentre);

                    needsRoundoff |= (transformedDistance > (exactRadius * 1.001));
                    Assert.True(condition: (transformedDistance <= ball.Radius),
                        userMessage: $"float pose moved {transformedDistance} beyond certified radius {ball.Radius}");
                }
            }
        }
        Assert.True(condition: needsRoundoff, userMessage: "The sample set must distinguish pose rounding from a unit-radius-only bound.");
    }
    [Fact]
    public void NonuniformScaleBoundsThePointMapRatherThanDistanceScaleAlone() {
        var builder = Builder(material: out var material);

        _ = builder.ResetPoint().Scale(scale: new Vector3(x: 4f, y: 0.125f, z: 2f)).Sphere(radius: 1f, material: material);
        var program = builder.Build();
        var certificate = program.TapeCertificate(instruction: (program.InstructionCount - 1));

        Assert.Equal(expected: 3u, actual: certificate.Flags);
        Assert.InRange(actual: certificate.Lipschitz, low: 1f, high: 1.001f);
        var ball = certificate.Enclose(centreMagnitude: 0f, centreValue: -0.125f, radius: 4f);

        for (var sample = -256; (sample <= 256); sample++) {
            var y = (sample / 64d);
            var exact = ((Math.Abs(value: (y / 0.125d)) - 1d) * 0.125d);

            Contains(interval: ball, value: exact);
        }
    }
    [Fact]
    public void PackedQuaternionPolynomialCarriesItsActualOperatorNorm() {
        var instructions = new[] {
            new SdfInstruction(Op: SdfOp.ResetPoint, Shape: 0u, Blend: 0u, Material: 0u, Data0: default, Data1: default),
            new SdfInstruction(Op: SdfOp.Rotate, Shape: 0u, Blend: 0u, Material: 0u, Data0: new Vector4(w: 0f, x: 0f, y: 2f, z: 0f), Data1: default),
            new SdfInstruction(Op: SdfOp.ShapeBlend, Shape: ((uint)SdfShapeType.Sphere), Blend: 0u, Material: 0u, Data0: new Vector4(w: 0f, x: 1f, y: 0f, z: 0f), Data1: default),
        };
        var program = new SdfProgram(instructions: instructions, materials: [new SdfMaterial(Albedo: Vector3.One)]);
        var certificate = program.TapeCertificate(instruction: 2);

        Assert.InRange(actual: certificate.Lipschitz, low: 7f, high: 7.001f);
        Contains(interval: certificate.Enclose(centreMagnitude: 0f, centreValue: -1f, radius: 2f), value: 13d);
    }
    [Fact]
    public void FloatCentreErrorIsEnclosedAtZeroRadius() {
        var builder = Builder(material: out var material);

        _ = builder.ResetPoint().Sphere(radius: 0.3f, material: material);
        var program = builder.Build();
        var certificate = program.TapeCertificate(instruction: 1);
        var needsFloatMargin = false;

        for (var sample = 1; (sample <= 128); sample++) {
            var point = new Vector3(x: (sample * 127.25f), y: ((sample * 231.75f) + 0.1f), z: (sample * 321.125f));
            var center = (point.Length() - 0.3f);
            var exact = (Math.Sqrt(d: (((((double)point.X) * point.X) + (((double)point.Y) * point.Y)) + (((double)point.Z) * point.Z))) - ((double)0.3f));

            needsFloatMargin |= (Math.Abs(value: (exact - center)) > (1d / 65536d));
            Contains(interval: certificate.Enclose(centreValue: center,
                centreMagnitude: MathF.Max(x: MathF.Abs(x: point.X), y: MathF.Max(x: MathF.Abs(x: point.Y), y: MathF.Abs(x: point.Z))), radius: 0f), value: exact);
        }
        Assert.True(condition: needsFloatMargin);
    }
    [InlineData(0f, 0.25f, 1.75f, 2.25f)]
    [InlineData(0f, 0.25f, 2.25f, 1.75f)]
    [InlineData(0.125f, 0f, 1.5f, 2.625f)]
    [InlineData(0.125f, 0f, 2.5f, 1.375f)]
    [InlineData(0.0625f, 0.125f, 1.625f, 2.4375f)]
    [InlineData(0.0625f, 0.125f, 2.375f, 1.5625f)]
    [Theory]
    public void EncloseIncludesOppositeCentreAndRayEvaluationErrors(float errorSlope, float errorOffset, float centreValue, float rayValue) {
        // The exact field is the constant two at centre x=4 and ray sample x=5. These independent dyadic samples
        // reach opposite ends of their own error budgets; a zero Lipschitz bound leaves no geometric allowance.
        const float ExactField = 2f;
        var centreError = (centreValue - ExactField);
        var rayError = (rayValue - ExactField);
        var rayErrorBudget = ((errorSlope * 5f) + errorOffset);

        Assert.Equal(expected: ((errorSlope * 4f) + errorOffset), actual: MathF.Abs(x: centreError));
        Assert.Equal(expected: rayErrorBudget, actual: MathF.Abs(x: rayError));
        Assert.True(condition: ((centreError * rayError) < 0f));
        Assert.True(condition: (MathF.Abs(x: (rayValue - centreValue)) > rayErrorBudget),
            userMessage: "The independent samples must lie farther apart than one error allowance.");
        var certificate = new SdfTapeCertificate(ErrorOffset: errorOffset, ErrorSlope: errorSlope,
            Flags: SdfTapeCertificate.Certified, Lipschitz: 0f);
        var interval = certificate.Enclose(centreMagnitude: 4f, centreValue: centreValue, radius: 1f);

        Contains(interval: interval, value: rayValue);
    }
    [Fact]
    public void SubnormalFloatCentreConvertsOutwardRatherThanToNearestFixed() {
        var certificate = new SdfTapeCertificate(ErrorOffset: 0f, ErrorSlope: 0f, Flags: SdfTapeCertificate.Certified, Lipschitz: 0f);
        var positive = certificate.Enclose(centreMagnitude: 0f, centreValue: float.Epsilon, radius: 0f);
        var negative = certificate.Enclose(centreMagnitude: 0f, centreValue: -float.Epsilon, radius: 0f);

        Assert.Equal(expected: 1L, actual: positive.Upper.Value);
        Assert.Equal(expected: -1L, actual: negative.Lower.Value);
    }
    [Fact]
    public void UnmodelledWarpsAndShapesRetainTheCandidate() {
        var builder = Builder(material: out var material);

        _ = builder.ResetPoint().TwistY(rate: 0.5f).Sphere(radius: 1f, material: material);
        _ = builder.ResetPoint().Superellipsoid(radii: Vector3.One, exponent: 4f, material: material);
        var program = builder.Build();

        foreach (var (instruction, index) in program.Instructions.Select(selector: (instruction, index) => (instruction, index))) {
            if (instruction.Op == SdfOp.ShapeBlend) {
                Assert.Equal(expected: 0u, actual: program.TapeCertificate(instruction: index).Flags);
            }
        }
        var unknown = default(SdfTapeCertificate).Enclose(centreMagnitude: 0f, centreValue: 100f, radius: 1f);

        Assert.True(condition: unknown.IsUnbounded);
        Assert.False(condition: SdfTapeCertificate.CandidateLoses(current: Point(value: 0), candidate: unknown,
            blend: SdfBlendOp.Union, radius: FixedQ4816.Zero));
    }
    [Fact]
    public void BallsLeavingTheAdmittedDomainStayLive() {
        var certificate = new SdfTapeCertificate(ErrorOffset: 1f, ErrorSlope: 1f, Flags: SdfTapeCertificate.Certified, Lipschitz: 1f);

        Assert.True(condition: certificate.Enclose(centreMagnitude: SdfTapeCertificate.CoordinateLimit, centreValue: 1f, radius: 1f).IsUnbounded);
        Assert.True(condition: certificate.Enclose(centreMagnitude: 0f, centreValue: float.PositiveInfinity, radius: 1f).IsUnbounded);
    }
    [Fact]
    public void FloatBlendRoundoffKeepsAnAlmostSeparatedCandidateLive() {
        var current = FixedInterval.FromPoint(value: FixedQ4816.FromInteger(value: 1000));
        var candidate = FixedInterval.FromPoint(value: new FixedQ4816(Value: (current.Upper.Value + 1)));

        Assert.False(condition: SdfTapeCertificate.CandidateLoses(current: current, candidate: candidate,
            blend: SdfBlendOp.Union, radius: FixedQ4816.Zero));
    }
    [Fact]
    public void TrapezoidPrismsCarryACertifiedModel() {
        var builder = Builder(material: out var material);

        _ = builder.ResetPoint().Trapezoid(bottomHalfWidth: 0.1f, topHalfWidth: 0f, halfHeight: 0.2f,
            lift: SdfLift.Extrude, liftAmount: 0.02f, material: material);
        var program = builder.Build();
        var certificate = program.TapeCertificate(instruction: (program.InstructionCount - 1));

        Assert.Equal(expected: 3u, actual: certificate.Flags);
        Assert.InRange(actual: certificate.Lipschitz, low: 1f, high: 1.001f);
        Assert.True(condition: (certificate.ErrorSlope > 0f));
    }
    [Fact]
    public void TrapezoidProjectionNeedsAFiniteCertifiedDenominator() {
        var shape = Shape(type: SdfShapeType.Trapezoid, data0: new Vector4(w: 0.02f, x: 0f, y: 100000000f, z: 1e-35f),
            data1: new Vector4(w: 0f, x: 0f, y: 1f, z: 0f));
        var program = RawProgram(instructions: [shape]);

        Assert.Equal(expected: 0u, actual: program.TapeCertificate(instruction: 0).Flags);
        var finiteSlant = RawProgram(instructions: [shape with { Data0 = new Vector4(w: 0.02f, x: 0f, y: 1f, z: 1e-35f) }]);

        Assert.Equal(expected: 3u, actual: finiteSlant.TapeCertificate(instruction: 0).Flags);
    }
    [Fact]
    public void RawTransformAndGaugeParametersMustMeetTheCertificateModel() {
        var sphere = Shape(type: SdfShapeType.Sphere, data0: new Vector4(w: 0f, x: 1f, y: 0f, z: 0f));

        foreach (var transform in new[] {
            new SdfInstruction(Op: SdfOp.Scale, Shape: 0u, Blend: 0u, Material: 0u,
                Data0: new Vector4(w: -2f, x: -2f, y: -3f, z: -4f), Data1: default),
            new SdfInstruction(Op: SdfOp.Elongate, Shape: 0u, Blend: 0u, Material: 0u,
                Data0: new Vector4(w: 0f, x: -1000f, y: 0f, z: 0f), Data1: default),
        }) {
            var program = RawProgram(instructions: [transform, sphere]);

            Assert.Equal(expected: 0u, actual: program.TapeCertificate(instruction: 1).Flags);
        }
        var gauge = RawProgram(instructions: [Shape(type: SdfShapeType.Superellipsoid,
            data0: new Vector4(w: 2f, x: 1f, y: 1f, z: 1f), data1: new Vector4(w: 1f, x: 0f, y: -100f, z: 1f))]);

        Assert.Equal(expected: 0u, actual: gauge.TapeCertificate(instruction: 0).Flags);
    }
    [Fact]
    public void ConditionalCandidateSegmentsCannotBeDeletedAsUnits() {
        var builder = Builder(material: out var material);

        _ = builder.ResetPoint().Sphere(radius: 1f, material: material, detail: true);
        var program = builder.Build();

        Assert.Equal(expected: SdfTapeCertificate.Certified, actual: program.TapeCertificate(instruction: 1).Flags);
    }
    [InlineData(SdfBlendOp.Union, 8, true)]
    [InlineData(SdfBlendOp.Union, 2, false)]
    [InlineData(SdfBlendOp.SmoothUnion, 4, false)]
    [InlineData(SdfBlendOp.SmoothUnion, 5, true)]
    [InlineData(SdfBlendOp.Intersection, -5, true)]
    [InlineData(SdfBlendOp.SmoothIntersection, 0, false)]
    [InlineData(SdfBlendOp.SmoothIntersection, -1, true)]
    [InlineData(SdfBlendOp.Subtraction, 5, true)]
    [InlineData(SdfBlendOp.SmoothSubtraction, 0, false)]
    [InlineData(SdfBlendOp.SmoothSubtraction, 1, true)]
    [InlineData(SdfBlendOp.ChamferUnion, 100, false)]
    [Theory]
    public void BlendDeletionRequiresStrictCertifiedSeparation(SdfBlendOp blend, int candidate, bool expected) {
        Assert.Equal(expected: expected, actual: SdfTapeCertificate.CandidateLoses(current: Point(value: 2), candidate: Point(value: candidate),
            blend: blend, radius: FixedQ4816.FromInteger(value: 2)));
    }
    [Fact]
    public void DenseProgramsKeepResetChainsAsIndependentDeletionUnits() {
        Assert.Equal(expected: 1, actual: CoincidentSpheres(count: 14).SkipSegmentCount);
        var program = CoincidentSpheres(count: 16);

        Assert.Equal(expected: 16, actual: program.SkipSegmentCount);
        for (var index = 1; (index < program.InstructionCount); index += 2) {
            Assert.Equal(expected: 3u, actual: program.TapeCertificate(instruction: index).Flags);
        }
    }
    [Fact]
    public void FieldBoundariesStayLiveWhileTheirIndependentInnerChainsCanPrune() {
        var builder = Builder(material: out var material);

        _ = builder.PushField().ResetPoint().Sphere(radius: 1f, material: material)
            .ResetPoint().Sphere(radius: 2f, material: material).PopField();
        var program = builder.Build();
        var shapes = program.Instructions.Select(selector: (instruction, index) => (instruction, index))
            .Where(predicate: pair => (pair.instruction.Op == SdfOp.ShapeBlend)).Select(selector: pair => pair.index).ToArray();

        Assert.Equal(expected: 3u, actual: program.TapeCertificate(instruction: shapes[0]).Flags);
        Assert.Equal(expected: 1u, actual: program.TapeCertificate(instruction: shapes[1]).Flags);
    }

    private static SdfProgram CoincidentSpheres(int count) {
        var builder = Builder(material: out var material);

        for (var index = 0; (index < count); index++) { _ = builder.ResetPoint().Sphere(radius: 1f, material: material); }
        return builder.Build();
    }
    private static SdfProgramBuilder Builder(out int material) {
        var builder = new SdfProgramBuilder();

        material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        return builder;
    }
    private static FixedInterval Point(int value) => FixedInterval.FromPoint(value: FixedQ4816.FromInteger(value: value));
    private static SdfInstruction Shape(SdfShapeType type, Vector4 data0, Vector4 data1 = default) => new(
        Op: SdfOp.ShapeBlend, Shape: ((uint)type), Blend: ((uint)SdfBlendOp.Union), Material: 0u, Data0: data0, Data1: data1);
    private static SdfProgram RawProgram(SdfInstruction[] instructions) => new(
        instructions: instructions, materials: [new SdfMaterial(Albedo: Vector3.One)]);
    private static Vector3 Pose(Vector3 point, Vector3 position, Quaternion orientation) {
        var p = (point - position);
        var u = -new Vector3(x: orientation.X, y: orientation.Y, z: orientation.Z);

        return (p + (2f * Vector3.Cross(vector1: u, vector2: ((orientation.W * p) + Vector3.Cross(vector1: u, vector2: p)))));
    }
    private static double Distance(Vector3 first, Vector3 second) {
        var x = (((double)first.X) - second.X);
        var y = (((double)first.Y) - second.Y);
        var z = (((double)first.Z) - second.Z);

        return Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));
    }
    private static void Contains(FixedInterval interval, double value) {
        Assert.False(condition: interval.IsUnbounded);
        Assert.InRange(actual: value, low: (((double)interval.Lower.Value) / 65536d), high: (((double)interval.Upper.Value) / 65536d));
    }
}
