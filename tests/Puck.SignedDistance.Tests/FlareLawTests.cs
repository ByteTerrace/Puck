using System.Numerics;

using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The conservativeness contract of <see cref="SdfOp.AxialProfile"/>: <see cref="SdfProgram.FlareExtrema"/>'s exact
/// scale-profile extrema at the endpoints, the distanceScale/step-clamp mirror against
/// <c>SdfProgram.FlareOperatorNorm</c>, a zero-amount-and-bulge identity, and a finite-difference proof that the
/// warped, corrected field never changes faster than true distance can (the conservative-marching property the step
/// clamp exists for) over a grid of points.
/// </summary>
public sealed class FlareLawTests {
    private static SdfProgram BuildFlaredSphere(float amount, float bulge, float top, float span, float radius = 1.0f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder
            .ResetPoint()
            .AxialProfile(
                amount: amount,
                bulge: bulge,
                span: span,
                top: top
            )
            .Sphere(
                radius: radius,
                material: material
            )
            .Build();
    }
    // Reproduces SDF_OP_AXIAL_PROFILE's mapCore case plus the trailing distanceScale/stepScale multiplies exactly, so this
    // is what the shader's returned distance would be at p for the same authored program.
    private static float FlareFieldDistance(Vector3 p, float amount, float bulge, float top, float span, float radius, float distanceScaleCorrection, float stepScale) {
        var t = Math.Clamp(
            value: ((top - p.Y) / span),
            max: 1f,
            min: 0f
        );
        var s = MathF.Max(
            x: ((1f + (amount * t)) + (bulge * MathF.Sin(x: (MathF.PI * t)))),
            y: SdfProgramBuilder.FlareMinScale
        );
        var warped = new Vector3(
            x: (p.X / s),
            y: p.Y,
            z: (p.Z / s)
        );

        return (((warped.Length() - radius) * distanceScaleCorrection) * stepScale);
    }
    // KEEP IN SYNC with SdfProgram.FlareOperatorNorm.
    private static float MirrorOperatorNorm(float amount, float bulge, float inverseSpan, float distanceScale, float reach) {
        if (
            (amount == 0.0f) &&
            (bulge == 0.0f)
        ) {
            return 1.0f;
        }

        var (minS, maxS) = SdfProgram.FlareExtrema(
            amount: amount,
            bulge: bulge
        );
        var minSClamped = MathF.Max(
            x: minS,
            y: SdfProgramBuilder.FlareMinScale
        );
        var dsdtBound = (MathF.Abs(x: amount) + (MathF.PI * MathF.Abs(x: bulge)));
        var dsdyBound = (dsdtBound * MathF.Abs(x: inverseSpan));
        var diagonalNorm = MathF.Max(
            x: (1.0f / minSClamped),
            y: 1.0f
        );
        // The shear ranges over the FLARED geometry's radial extent, maxS * reach (KEEP IN SYNC).
        var shearNorm = (((reach * maxS) * dsdyBound) / (minSClamped * minSClamped));

        return (distanceScale * (diagonalNorm + shearNorm));
    }

    [Fact]
    public void TheStepClampMatchesTheDerivedOperatorNorm() {
        // A small amount keeps the 1/maxS distanceScale correction near 1 while a tight span drives ds/dy (and so
        // the shear term) well past the diagonal norm — the combination that pushes the operator norm above 1 and
        // exercises the clamp, unlike a large amount alone (whose own distanceScale shrink can bring the product
        // back under 1, in which case AnalyzeLipschitz's final max-with-1 leaves StepScale at the neutral 1.0).
        const float Amount = 0.3f;
        const float Bulge = 0.9f;
        const float Top = 0.0f;
        const float Span = 0.3f;
        const float Radius = 1.0f;

        var program = BuildFlaredSphere(
            amount: Amount,
            bulge: Bulge,
            span: Span,
            top: Top
        );
        var (_, maxS) = SdfProgram.FlareExtrema(
            amount: Amount,
            bulge: Bulge
        );
        // The chain's reach is the sphere's own bounding radius (no Translate on the chain).
        var expected = (1.0f / MathF.Max(
            x: MirrorOperatorNorm(
                amount: Amount,
                bulge: Bulge,
                distanceScale: (1.0f / maxS),
                inverseSpan: (1.0f / Span),
                reach: Radius
            ),
            y: 1.0f
        ));

        Assert.Equal(
            actual: program.StepScale,
            expected: expected
        );
        Assert.True(condition: (program.StepScale < 1.0f));
    }
    [Theory]
    [InlineData(0f, 0f, 1f, 1f)]
    [InlineData(2f, 0f, 1f, 3f)]
    [InlineData(-0.5f, 0f, 0.5f, 1f)]
    public void TheProfileMatchesItsExactEndpointsWhenBulgeIsZero(float amount, float bulge, float expectedMinS, float expectedMaxS) {
        var (minS, maxS) = SdfProgram.FlareExtrema(
            amount: amount,
            bulge: bulge
        );

        Assert.Equal(
            actual: minS,
            expected: expectedMinS,
            precision: 6
        );
        Assert.Equal(
            actual: maxS,
            expected: expectedMaxS,
            precision: 6
        );
    }
    [Theory]
    [InlineData(-0.9f, -1.0f)]
    [InlineData(-0.9f, 1.0f)]
    [InlineData(4.0f, -1.0f)]
    public void MaxSIsAlwaysAtLeastOne(float amount, float bulge) {
        // s(0) == 1 is always a candidate for the maximum, however negative amount/bulge run — the invariant
        // SdfProgramBuilder.AxialProfile's 1/maxS distanceScale correction relies on to stay <= 1.
        var (_, maxS) = SdfProgram.FlareExtrema(
            amount: amount,
            bulge: bulge
        );

        Assert.True(condition: (maxS >= 1.0f));
    }
    [Fact]
    public void ZeroAmountAndBulgeIsAnExactIdentity() {
        var flared = BuildFlaredSphere(
            amount: 0f,
            bulge: 0f,
            span: 3f,
            top: 0.5f
        );

        Assert.Equal(
            actual: flared.StepScale,
            expected: 1.0f
        );

        // Every candidate point returns the exact plain-sphere distance — s(t) == 1 identically for amount == bulge == 0.
        Vector3[] points = [
            new(x: 3f, y: 0f, z: 0f),
            new(x: 0f, y: 5f, z: 0f),
            new(x: -2f, y: -4f, z: 1.5f),
            new(x: 0.2f, y: 0.5f, z: -0.3f),
        ];

        foreach (var point in points) {
            var expected = (point.Length() - 1.0f);
            var actual = FlareFieldDistance(
                p: point,
                amount: 0f,
                bulge: 0f,
                top: 0.5f,
                span: 3f,
                radius: 1.0f,
                distanceScaleCorrection: 1.0f,
                stepScale: flared.StepScale
            );

            Assert.Equal(
                actual: actual,
                expected: expected
            );
        }
    }
    [Fact]
    public void TheWarpedFieldStaysAConservativeLowerBoundOnAGrid() {
        const float Amount = 2.5f;
        const float Bulge = -0.6f;
        const float Top = 1.0f;
        const float Span = 2.0f;
        const float Radius = 1.0f;
        const float Step = 0.2f;
        const float Tolerance = 5.0e-4f;

        var program = BuildFlaredSphere(
            amount: Amount,
            bulge: Bulge,
            span: Span,
            top: Top
        );
        var (_, maxS) = SdfProgram.FlareExtrema(
            amount: Amount,
            bulge: Bulge
        );
        var distanceScaleCorrection = (1.0f / maxS);
        var stepScale = program.StepScale;
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

        for (var x = -3.0f; (x <= 3.0f); x += Step) {
            for (var y = -3.0f; (y <= 5.0f); y += Step) {
                for (var z = -3.0f; (z <= 3.0f); z += Step) {
                    var p = new Vector3(x: x, y: y, z: z);
                    var d0 = FlareFieldDistance(
                        p: p,
                        amount: Amount,
                        bulge: Bulge,
                        top: Top,
                        span: Span,
                        radius: Radius,
                        distanceScaleCorrection: distanceScaleCorrection,
                        stepScale: stepScale
                    );

                    foreach (var axis in axes) {
                        var d1 = FlareFieldDistance(
                            p: (p + (axis * Step)),
                            amount: Amount,
                            bulge: Bulge,
                            top: Top,
                            span: Span,
                            radius: Radius,
                            distanceScaleCorrection: distanceScaleCorrection,
                            stepScale: stepScale
                        );

                        Assert.True(condition: (MathF.Abs(x: (d1 - d0)) <= (Step + Tolerance)));
                    }
                }
            }
        }
    }
    [Fact]
    public void ANonPositiveSpanRefusesByName() {
        var builder = new SdfProgramBuilder();
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.AxialProfile(
            amount: 1.0f,
            bulge: 0.0f,
            span: 0.0f,
            top: 0.0f
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "span"
        );
    }
    [Fact]
    public void ANonFiniteAmountRefusesByName() {
        var builder = new SdfProgramBuilder();
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.AxialProfile(
            amount: float.NaN,
            bulge: 0.0f,
            span: 1.0f,
            top: 0.0f
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "amount"
        );
    }
    [Fact]
    public void TheWarpFreeEvaluatorRefusesTheOpByName() {
        var program = BuildFlaredSphere(
            amount: 1.0f,
            bulge: 0.5f,
            span: 2.0f,
            top: 0.0f
        );
        var exception = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "AxialProfile"
        );
    }
}
