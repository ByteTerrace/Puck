using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfPrimitiveIntervalLawTests {
    [Fact]
    public void PackedPrimitiveParametersRetainBitsBelowTheFixedGrid() {
        var radius = float.BitIncrement(x: 1f);
        var instruction = Shape(type: SdfShapeType.Sphere, a: new Vector4(w: 0f, x: radius, y: 0f, z: 0f));
        var interval = SdfFieldEvaluator.EnclosePrimitive(instruction: instruction, x: Point(value: 2), y: Point(value: 0), z: Point(value: 0));

        Contains(expected: (2d - radius), interval: interval);
        Assert.True(condition: (interval.Lower < FixedQ4816.One),
            userMessage: "Nearest fixed conversion erases the radius bit and falsely proves a lower bound of one.");
        var parameter = SdfTapeCertificate.EncloseFloat(value: radius);

        Contains(expected: radius, interval: parameter);
        Assert.True(condition: (parameter.Lower < parameter.Upper));
    }
    [InlineData(SdfShapeType.Sphere)]
    [InlineData(SdfShapeType.Box)]
    [InlineData(SdfShapeType.ScreenSlab)]
    [InlineData(SdfShapeType.Torus)]
    [InlineData(SdfShapeType.Plane)]
    [InlineData(SdfShapeType.Capsule)]
    [InlineData(SdfShapeType.Cylinder)]
    [InlineData(SdfShapeType.Superellipsoid)]
    [InlineData(SdfShapeType.RoundedRectangle)]
    [InlineData(SdfShapeType.ChamferedRectangle)]
    [InlineData(SdfShapeType.Trapezoid)]
    [Theory]
    public void AdmittedPrimitiveBoxesContainTheirAxisDistances(SdfShapeType type) {
        var instruction = type switch {
            SdfShapeType.Sphere => Shape(type, new Vector4(w: 0f, x: 1f, y: 0f, z: 0f)),
            SdfShapeType.Box or SdfShapeType.ScreenSlab => Shape(type, new Vector4(w: 0.25f, x: 1f, y: 2f, z: 3f)),
            SdfShapeType.Torus => Shape(type, new Vector4(w: 0f, x: 0.75f, y: 0.25f, z: 0f)),
            SdfShapeType.Plane => Shape(type, new Vector4(w: -1f, x: 1f, y: 0f, z: 0f)),
            SdfShapeType.Capsule => Shape(type, new Vector4(w: 0.25f, x: 0.75f, y: 0f, z: 0f), new Vector4(w: 0f, x: 0f, y: (16f / 9f), z: 0f)),
            SdfShapeType.Cylinder => Shape(type, new Vector4(w: 0f, x: 1f, y: 2f, z: 0f)),
            SdfShapeType.Superellipsoid => Shape(type, new Vector4(w: 2f, x: 1f, y: 2f, z: 4f), new Vector4(w: 0.25f, x: 0f, y: 1f, z: 0.5f)),
            SdfShapeType.RoundedRectangle or SdfShapeType.ChamferedRectangle =>
                Shape(type, new Vector4(w: 1f, x: 1f, y: 2f, z: 0.25f), new Vector4(w: 0f, x: 0f, y: 1f, z: 0f)),
            SdfShapeType.Trapezoid => Shape(type, new Vector4(w: 1f, x: 1f, y: 1f, z: 2f), new Vector4(w: 0f, x: 0f, y: 1f, z: 0f)),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(type)),
        };
        var interval = SdfFieldEvaluator.EnclosePrimitive(instruction: instruction,
            x: new FixedInterval(lower: FixedQ4816.FromInteger(value: 3), upper: FixedQ4816.FromInteger(value: 5)), y: Point(value: 0), z: Point(value: 0));

        Contains(expected: 2d, interval: interval);
        Contains(expected: 4d, interval: interval);
        Assert.InRange(actual: ((double)interval.Upper), low: 4d, high: 4.01d);
    }
    [Fact]
    public void UnmodeledAndDegeneratePrimitivesCannotProveAFieldBound() {
        var unknown = Shape(type: SdfShapeType.Glyph, a: default);
        var collapsed = Shape(type: SdfShapeType.Trapezoid, a: new Vector4(w: 1f, x: 1f, y: 1f, z: 1e-30f), b: new Vector4(w: 0f, x: 0f, y: 1f, z: 0f));
        var nonquadratic = Shape(type: SdfShapeType.Superellipsoid, a: new Vector4(w: 3.5f, x: 1f, y: 1f, z: 1f));

        foreach (var instruction in new[] { unknown, collapsed, nonquadratic }) {
            Assert.True(condition: SdfFieldEvaluator.EnclosePrimitive(instruction: instruction, x: Point(value: 4), y: Point(value: 0), z: Point(value: 0)).IsUnbounded);
        }
    }
    [Fact]
    public void FloatUpperBoundIncludesTheRawImmediatelyAboveARepresentableFloat() {
        var exactFloat = FixedQ4816.FromInteger(value: (1L << 40));
        var interval = FixedInterval.FromPoint(value: new FixedQ4816(Value: (exactFloat.Value + 1L)));

        Assert.Equal(expected: float.BitIncrement(x: 1099511627776f), actual: SdfTapeCertificate.UpperFloat(value: interval));
        Assert.True(condition: float.IsPositiveInfinity(f: SdfTapeCertificate.UpperFloat(binaryScale: 120, value: interval)));
        Assert.True(condition: SdfTapeCertificate.EncloseFloat(value: float.PositiveInfinity).IsUnbounded);
    }

    private static FixedInterval Point(int value) => FixedInterval.FromPoint(value: FixedQ4816.FromInteger(value: value));
    private static SdfInstruction Shape(SdfShapeType type, Vector4 a, Vector4 b = default) =>
        new(Op: SdfOp.ShapeBlend, Shape: ((uint)type), Blend: ((uint)SdfBlendOp.Union), Material: 0u, Data0: a, Data1: b);
    private static void Contains(FixedInterval interval, double expected) {
        Assert.False(condition: interval.IsUnbounded);
        Assert.InRange(actual: expected, low: ((double)interval.Lower), high: ((double)interval.Upper));
    }
}
