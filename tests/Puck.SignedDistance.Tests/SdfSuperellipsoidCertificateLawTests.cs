using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfSuperellipsoidCertificateLawTests {
    [Fact]
    public void RationalNormFactorHasAProvedLogarithmSlope() {
        var factor = SdfTapeCertificate.SuperellipsoidNormFactor(exponent: 3f);

        Contains(interval: factor, value: (18d / 25d));
        var slope = (Point(value: 1) - factor);
        var exponent = (Point(value: 4) * slope);
        var term = Point(value: 1);
        var sum = term;
        // Positive Taylor terms give exp(4*slope) >= this partial sum > 3, hence slope > ln(3)/4.
        for (var index = 1; (index <= 5); index++) {
            term = ((term * exponent) / Point(value: index));
            sum += term;
        }
        Assert.True(condition: (sum.Lower > FixedQ4816.FromInteger(value: 3)));
        Assert.Equal(expected: Point(value: 1), actual: SdfTapeCertificate.SuperellipsoidNormFactor(exponent: 2f));
        foreach (var value in new[] { float.BitDecrement(x: 2f), float.BitIncrement(x: 3f), float.NaN, float.PositiveInfinity }) {
            Assert.True(condition: SdfTapeCertificate.SuperellipsoidNormFactor(exponent: value).IsUnbounded);
        }
    }
    [InlineData(2.05f)]
    [InlineData(2.5f)]
    [InlineData(2.7f)]
    [InlineData(3f)]
    [Theory]
    public void ClampedPowerCertificatesIncludeTheWholeNormBand(float exponent) {
        var shape = Shape(exponent: exponent);
        var program = Program(instruction: shape);
        var certificate = program.TapeCertificate(instruction: 0);

        Assert.Equal(expected: SdfTapeCertificate.Certified | SdfTapeCertificate.OmissibleSegment | SdfTapeCertificate.CenteredNormEnvelope, actual: certificate.Flags);
        Assert.InRange(actual: certificate.Lipschitz, low: 1f, high: 1.001f);
        var quadratic = Program(instruction: Shape(exponent: 2f)).TapeCertificate(instruction: 0);

        Assert.InRange(actual: certificate.ErrorSlope, low: quadratic.ErrorSlope, high: (quadratic.ErrorSlope * 4f));
        var factor = (1d - ((7d / 25d) * (exponent - 2d)));

        foreach (var point in new[] { new Vector3(value: 1f), new Vector3(x: -2f, y: 3f, z: -1f), new Vector3(x: 100f, y: 20f, z: -30f) }) {
            var norm = Math.Sqrt(d: (((((double)point.X) * point.X) + ((4d * point.Y) * point.Y)) + ((16d * point.Z) * point.Z)));
            var lower = (((factor * norm) - 1d) * 0.25d);
            var upper = ((norm - 1d) * 0.25d);
            var magnitude = MathF.Max(x: MathF.Abs(x: point.X), y: MathF.Max(x: MathF.Abs(x: point.Y), y: MathF.Abs(x: point.Z)));

            Contains(interval: certificate.Enclose(centreMagnitude: magnitude, centreValue: ((float)lower), powerExponent: exponent, radius: 0f), value: upper);
            Contains(interval: certificate.Enclose(centreMagnitude: magnitude, centreValue: ((float)upper), powerExponent: exponent, radius: 0f), value: lower);
            var arithmeticOnly = certificate with { ErrorSlope = quadratic.ErrorSlope, ErrorOffset = quadratic.ErrorOffset, Flags = quadratic.Flags };
            var withoutBand = arithmeticOnly.Enclose(centreMagnitude: magnitude, centreValue: ((float)lower), radius: 0f);

            Assert.True(condition: (((double)withoutBand.Upper) < upper),
                userMessage: "This case must distinguish the complete norm band from a guessed pow rounding margin.");
        }
    }
    [Fact]
    public void CenteredNormCertificatesRequireThePackedExponent() {
        var certificate = Program(instruction: Shape(exponent: 2.5f)).TapeCertificate(instruction: 0);

        Assert.True(condition: certificate.Enclose(centreValue: 1f, centreMagnitude: 1f, radius: 0f).IsUnbounded);
        foreach (var exponent in new[] { 2f, float.BitIncrement(x: 3f), float.NaN, float.PositiveInfinity }) {
            Assert.True(condition: certificate.Enclose(centreMagnitude: 1f, centreValue: 1f, powerExponent: exponent, radius: 0f).IsUnbounded);
        }
    }
    [Fact]
    public void ArithmeticErrorIncludesTheInverseNormFactor() {
        var nearQuadratic = Program(instruction: Shape(exponent: 2.0001f)).TapeCertificate(instruction: 0);
        var cubic = Program(instruction: Shape(exponent: 3f)).TapeCertificate(instruction: 0);

        Assert.InRange(actual: (cubic.ErrorSlope / nearQuadratic.ErrorSlope), low: 1.38f, high: 1.40f);
    }
    [Fact]
    public void ThinGaugeArithmeticScalesByMinimumRadiusAndChecksRawNormSeparately() {
        var thin = Shape(exponent: 2.7f) with {
            Data0 = new Vector4(w: 2.7f, x: 1f, y: 0.01f, z: 0.002f),
            Data1 = new Vector4(w: 500f, x: 0f, y: 1f, z: 100f),
        };
        var certificate = Program(instruction: thin).TapeCertificate(instruction: 0);

        Assert.NotEqual(expected: 0u, actual: certificate.Flags & SdfTapeCertificate.CenteredNormEnvelope);
        Assert.InRange(actual: certificate.ErrorSlope, low: 0.000001f, high: 0.0002f);
        Assert.InRange(actual: certificate.ErrorOffset, low: 0.000001f, high: 0.002f);
        var rawNormTooLarge = thin with {
            Data0 = new Vector4(w: 2.7f, x: 1f, y: 1f, z: 0.00001f),
            Data1 = new Vector4(w: 1e9f, x: 0f, y: 1f, z: 1f),
        };

        Assert.Equal(expected: 0u, actual: Program(instruction: rawNormTooLarge).TapeCertificate(instruction: 0).Flags);
    }
    [Fact]
    public void ZeroDistanceStillPaysTheScaledMinimumRadiusBand() {
        var certificate = Program(instruction: Shape(exponent: 3f)).TapeCertificate(instruction: 0);
        var alpha = (18d / 25d);
        var upperEndpoint = ((0.25d / alpha) - 0.25d);
        var magnitude = ((float)(1d / alpha));

        Contains(interval: certificate.Enclose(centreMagnitude: magnitude, centreValue: 0f, powerExponent: 3f, radius: 0f), value: upperEndpoint);
        var arithmeticOnlyOffset = Program(instruction: Shape(exponent: 2f)).TapeCertificate(instruction: 0).ErrorOffset;
        var withoutRadius = certificate with { ErrorOffset = arithmeticOnlyOffset };
        var missing = withoutRadius.Enclose(centreMagnitude: magnitude, centreValue: 0f, powerExponent: 3f, radius: 0f);

        Assert.True(condition: (((double)missing.Upper) < upperEndpoint));
        Contains(interval: SdfTapeCertificate.SuperellipsoidCenterGain(exponent: 3f), value: (7d / 36d));
    }
    [Fact]
    public void CenteredBandUsesScaledRadiusWithoutChargingTheWorldOrigin() {
        var program = new SdfProgram(instructions: [
            new SdfInstruction(Op: SdfOp.ResetPoint, Shape: 0u, Blend: 0u, Material: 0u, Data0: default, Data1: default),
            new SdfInstruction(Op: SdfOp.Translate, Shape: 0u, Blend: 0u, Material: 0u, Data0: new Vector4(w: 0f, x: 1000f, y: 0f, z: 0f), Data1: default),
            new SdfInstruction(Op: SdfOp.Scale, Shape: 0u, Blend: 0u, Material: 0u, Data0: new Vector4(value: 8f), Data1: default),
            Shape(exponent: 2.7f),
        ], materials: [new SdfMaterial(Albedo: Vector3.One)]);
        var certificate = program.TapeCertificate(instruction: 3);
        var norm = (Math.Sqrt(d: 77d) * 2d);
        var alpha = (1d - ((7d / 25d) * (2.7f - 2d)));
        var lower = ((alpha * norm) - 2d);
        var upper = (norm - 2d);
        var bounds = certificate.Enclose(centreMagnitude: 1024f, centreValue: ((float)lower), powerExponent: 2.7f, radius: 0f);

        Contains(interval: bounds, value: upper);
        Assert.InRange(actual: ((double)(bounds.Upper - bounds.Lower)), low: (upper - lower), high: 20d);
    }
    [InlineData(2.05f)]
    [InlineData(2.7f)]
    [InlineData(3f)]
    [Theory]
    public void CenteredNormBallContainsEveryClampEndpoint(float exponent) {
        var certificate = Program(instruction: Shape(exponent: exponent)).TapeCertificate(instruction: 0);
        var alpha = (1d - ((7d / 25d) * (exponent - 2d)));
        var centreNorm = (Math.Sqrt(d: 12d) * 0.25d);
        var centre = ((float)((alpha * centreNorm) - 0.25d));
        var bounds = certificate.Enclose(centreMagnitude: 2f, centreValue: centre, powerExponent: exponent, radius: 0.5f);
        var noRadius = (certificate with { Lipschitz = 0f }).Enclose(centreMagnitude: 2f, centreValue: centre,
            powerExponent: exponent, radius: 0.5f);
        var needsRadius = false;

        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    var qx = (2d + (x * 0.25d));
                    var qy = (2d + (y * 0.5d));
                    var qz = (2d + z);
                    var norm = (Math.Sqrt(d: (((qx * qx) + (qy * qy)) + (qz * qz))) * 0.25d);

                    Contains(interval: bounds, value: ((alpha * norm) - 0.25d));
                    Contains(interval: bounds, value: (norm - 0.25d));
                    needsRadius |= ((norm - 0.25d) > ((double)noRadius.Upper));
                }
            }
        }
        Assert.True(condition: needsRadius);
    }
    [InlineData(2.05f)]
    [InlineData(2.5f)]
    [InlineData(2.7f)]
    [InlineData(3f)]
    [Theory]
    public void PrimitiveIntervalsIncludeTheIdealGaugeAndBothClampExtremes(float exponent) {
        var shape = Shape(exponent: exponent);

        for (var x = -2; (x <= 2); x++) {
            for (var y = -2; (y <= 2); y++) {
                for (var z = -2; (z <= 2); z++) {
                    var interval = SdfFieldEvaluator.EnclosePrimitive(instruction: shape, x: Point(value: x), y: Point(value: y), z: Point(value: z));
                    var norm = Math.Sqrt(d: (((x * x) + ((4d * y) * y)) + ((16d * z) * z)));
                    var factor = (1d - ((7d / 25d) * (exponent - 2d)));
                    var ideal = Math.Pow(x: ((Math.Pow(x: Math.Abs(value: x), y: exponent) + Math.Pow(x: Math.Abs(value: (2d * y)), y: exponent))
                        + Math.Pow(x: Math.Abs(value: (4d * z)), y: exponent)), y: (1d / exponent));

                    if (((((x != 0) ? 1 : 0) + ((y != 0) ? 1 : 0)) + ((z != 0) ? 1 : 0)) <= 1) {
                        // Every Lp norm equals the single nonzero component on an axis; avoid a rounded pow oracle there.
                        ideal = Math.Max(val1: Math.Abs(value: x), val2: Math.Max(val1: Math.Abs(value: (2d * y)), val2: Math.Abs(value: (4d * z))));
                    }

                    Contains(interval: interval, value: ((ideal - 1d) * 0.25d));
                    Contains(interval: interval, value: (((factor * norm) - 1d) * 0.25d));
                    Contains(interval: interval, value: ((norm - 1d) * 0.25d));
                }
            }
        }
    }
    [Fact]
    public void PowerCertificatesRefuseParametersOutsideTheirClampModel() {
        foreach (var exponent in new[] { float.BitDecrement(x: 2f), float.BitIncrement(x: 3f) }) {
            Assert.Equal(expected: 0u, actual: Program(instruction: Shape(exponent: exponent)).TapeCertificate(instruction: 0).Flags);
        }
        var valid = Shape(exponent: 2.5f);

        foreach (var value in new[] { 0f, -1f }) {
            var radii = valid with { Data0 = new Vector4(w: 2.5f, x: value, y: 0.5f, z: 0.25f) };
            var inverses = valid with { Data1 = new Vector4(w: 4f, x: 0f, y: value, z: 2f) };

            Assert.Equal(expected: 0u, actual: Program(instruction: radii).TapeCertificate(instruction: 0).Flags);
            Assert.Equal(expected: 0u, actual: Program(instruction: inverses).TapeCertificate(instruction: 0).Flags);
        }
        foreach (var value in new[] { float.NaN, float.PositiveInfinity }) {
            Assert.Throws<ArgumentException>(testCode: () => Program(instruction: Shape(exponent: value)));
            Assert.Throws<ArgumentException>(testCode: () => Program(instruction: valid with { Data1 = new Vector4(w: 4f, x: 0f, y: value, z: 2f) }));
        }
    }

    private static SdfInstruction Shape(float exponent) => new(Op: SdfOp.ShapeBlend, Shape: ((uint)SdfShapeType.Superellipsoid),
        Blend: ((uint)SdfBlendOp.Union), Material: 0u, Data0: new Vector4(w: exponent, x: 1f, y: 0.5f, z: 0.25f), Data1: new Vector4(w: 4f, x: 0f, y: 1f, z: 2f));
    private static SdfProgram Program(SdfInstruction instruction) => new(instructions: [instruction], materials: [new SdfMaterial(Albedo: Vector3.One)]);
    private static FixedInterval Point(int value) => FixedInterval.FromPoint(value: FixedQ4816.FromInteger(value: value));
    private static void Contains(FixedInterval interval, double value) {
        Assert.False(condition: interval.IsUnbounded);
        Assert.InRange(actual: value, low: ((double)interval.Lower), high: ((double)interval.Upper));
    }
}
