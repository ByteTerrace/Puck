using Condition = Puck.SignedDistance.SdfValue<Puck.SignedDistance.SdfCondition>;
using Scalar = Puck.SignedDistance.SdfValue<Puck.SignedDistance.SdfScalar>;
using Vector3 = Puck.SignedDistance.SdfValue<Puck.SignedDistance.SdfVector3>;
using static Puck.SignedDistance.SdfFormula;

namespace Puck.SignedDistance;

// This source is also compiled into the Roslyn generator. The catalogue selects the same field on both targets.
internal static partial class SdfFieldFormulas {
    private static readonly Scalar Zero = Literal(0);
    private static readonly Scalar One = Literal(1);
    private static readonly Scalar Two = Literal(2);
    private static readonly SdfFormulaOperand<SdfVector3> Point = new("p", "p");
    private static readonly SdfFormulaOperand<SdfVector3> Data0Xyz = new("Vector(instruction)", "data0.xyz");
    private static readonly SdfFormulaOperand<SdfVector3> Data1Yzw = new(null, "data1.yzw");
    private static readonly SdfFormulaOperand<SdfScalar> Data0W = new("instruction.Data0W", "data0.w");

    private static Scalar X(Vector3 value) => Scalar.Of(SdfFormulaOp.X, value);
    private static Scalar Y(Vector3 value) => Scalar.Of(SdfFormulaOp.Y, value);
    private static Scalar Z(Vector3 value) => Scalar.Of(SdfFormulaOp.Z, value);
    private static Scalar Min(Scalar a, Scalar b) => Scalar.Of(SdfFormulaOp.Min, a, b);
    private static Scalar Max(Scalar a, Scalar b) => Scalar.Of(SdfFormulaOp.Max, a, b);
    private static Scalar Length(Vector3 value) => Scalar.Of(SdfFormulaOp.Length, value);
    private static Vector3 Abs(Vector3 value) => Vector3.Of(SdfFormulaOp.Abs, value);
    private static Vector3 Vec3(Scalar x, Scalar y, Scalar z) => Vector3.Of(SdfFormulaOp.Vector3, x, y, z);
    private static Vector3 Scale(Vector3 value, Scalar scale) => Vector3.Of(SdfFormulaOp.Multiply, value, scale);
    private static Scalar SegmentParameter(Vector3 point, Vector3 segment) => Scalar.Of(SdfFormulaOp.SegmentParameter, point, segment);
    private static Scalar ScaledRatio(Scalar value, Scalar scale, Scalar radius, Scalar inverseRadius) =>
        Scalar.Of(SdfFormulaOp.ScaledRatio, value, scale, radius, inverseRadius);
    private static Scalar Power(Scalar value, Scalar exponent) => Scalar.Of(SdfFormulaOp.Power, value, exponent);
    private static Scalar Clamp(Scalar value, Scalar low, Scalar high) => Scalar.Of(SdfFormulaOp.Clamp, value, low, high);
    private static Condition Equal(Scalar a, Scalar b) => Condition.Of(SdfFormulaOp.Equal, a, b);
    private static Condition Less(Scalar a, Scalar b) => Condition.Of(SdfFormulaOp.Less, a, b);
    private static Condition LessEqual(Scalar a, Scalar b) => Condition.Of(SdfFormulaOp.LessEqual, a, b);
    private static Condition And(Condition a, Condition b) => Condition.Of(SdfFormulaOp.And, a, b);

    internal static readonly SdfFormula<SdfScalar> Capsule = BuildCapsule();
    internal static readonly SdfFormula<SdfScalar> Superellipsoid = BuildSuperellipsoid();

    private static SdfFormula<SdfScalar> BuildCapsule() {
        var f = new SdfFormula<SdfScalar>("sdfCapsule");
        var p = f.Input("p", Point);
        var endpoint = f.Input("endpoint", Data0Xyz);
        var radius = f.Input("radius", Data0W);
        var h = f.Bind("h", SegmentParameter(p, endpoint));
        f.Return(Length(p - Scale(endpoint, h)) - radius);
        return f;
    }

    private static SdfFormula<SdfScalar> BuildSuperellipsoid() {
        var f = new SdfFormula<SdfScalar>("sdfSuperellipsoid");
        var p = f.Input("p", Point);
        var radii = f.Input("radii", Data0Xyz);
        var inverseRadii = f.Input("inverseRadii", Data1Yzw);
        var exponent = f.Input("exponent", Data0W);
        var minRadius = f.Bind("minRadius", Min(X(radii), Min(Y(radii), Z(radii))));
        var absolute = f.Bind("absolute", Abs(p));
        // The fixed carrier widens this ratio and rounds once. The shader uses the packed inverse radius.
        var q = f.Bind("q", Vec3(ScaledRatio(X(absolute), minRadius, X(radii), X(inverseRadii)),
            ScaledRatio(Y(absolute), minRadius, Y(radii), Y(inverseRadii)),
            ScaledRatio(Z(absolute), minRadius, Z(radii), Z(inverseRadii))));
        f.ReturnIf(Equal(exponent, Two), Length(q) - minRadius);
        var m = f.Bind("m", Max(X(q), Max(Y(q), Z(q))));
        f.ReturnIf(LessEqual(m, Zero), -minRadius);

        // Factoring out the largest component keeps the powers in range even at far samples.
        var ux = f.Bind("ux", Power(X(q) / m, exponent));
        var uy = f.Bind("uy", Power(Y(q) / m, exponent));
        var uz = f.Bind("uz", Power(Z(q) / m, exponent));
        var norm = f.Bind("norm", m * Power((ux + uy) + uz, One / exponent));
        f.ReturnIf(And(Less(Two, exponent), LessEqual(exponent, Literal(3))), BuildClampNorm().Call(norm, Length(q), exponent) - minRadius);
        f.Return(norm - minRadius);
        return f;
    }

    private static SdfFormula<SdfScalar> BuildClampNorm() {
        var f = new SdfFormula<SdfScalar>("sdfClampSuperellipsoidNorm");
        var approximate = f.ParameterOf<SdfScalar>("approximate");
        var norm = f.ParameterOf<SdfScalar>("norm");
        var exponent = f.ParameterOf<SdfScalar>("exponent");
        // For 2 < e <= 3, convexity of 3^(1/e - 1/2) and ln(3)/4 < 7/25 bound the exact Lp norm.
        var floor = (One - (Literal(7.0 / 25.0) * (exponent - Two))) * norm;
        f.Return(Clamp(Scalar.Of(SdfFormulaOp.FiniteOr, approximate, norm), floor, norm));
        return f;
    }
}
