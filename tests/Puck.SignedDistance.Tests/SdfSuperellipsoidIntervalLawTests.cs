using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfSuperellipsoidIntervalLawTests {
    [Fact]
    public void RationalNormFactorHasAProvedLogarithmSlope() {
        var factor = SdfIntervalBounds.SuperellipsoidNormFactor(exponent: 3f);

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
        Assert.Equal(expected: Point(value: 1), actual: SdfIntervalBounds.SuperellipsoidNormFactor(exponent: 2f));
        foreach (var value in new[] { float.BitDecrement(x: 2f), float.BitIncrement(x: 3f), float.NaN, float.PositiveInfinity }) {
            Assert.True(condition: SdfIntervalBounds.SuperellipsoidNormFactor(exponent: value).IsUnbounded);
        }
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
    public void ProgramsRefuseNonFinitePowerParameters() {
        var valid = Shape(exponent: 2.5f);

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
