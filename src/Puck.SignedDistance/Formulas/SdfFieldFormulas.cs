using Condition = Puck.SignedDistance.SdfValue<Puck.SignedDistance.SdfCondition>;
using Scalar = Puck.SignedDistance.SdfValue<Puck.SignedDistance.SdfScalar>;
using Vector2 = Puck.SignedDistance.SdfValue<Puck.SignedDistance.SdfVector2>;
using Vector3 = Puck.SignedDistance.SdfValue<Puck.SignedDistance.SdfVector3>;
using static Puck.SignedDistance.SdfFormula;

namespace Puck.SignedDistance;

// The field's shared primitive and blend formulas: each lowers to the query evaluator's fixed-point method and the
// kernels' precise function of the same name (sdfX in HLSL, SdfX in C#). A catalogue entry selects one by naming its
// field here (SdfFieldShapes, SdfFieldBlends); the Roslyn generator reads that field from its own compiled copy of this
// source, so both readers lower one object. Interval enclosures stay hand-written facets on each entry.
// Helpers precede the formulas that call them: static fields initialize in textual order.
internal static partial class SdfFieldFormulas {
    /// <summary>The top of the exponents whose superellipsoid power norm is clamped to its certified band.</summary>
    internal const double SuperellipsoidClampedExponent = 3.0;
    /// <summary>The band floor's slope per unit of exponent past two: for 2 &lt; e &lt;= 3, convexity of
    /// 3^(1/e - 1/2) and ln(3)/4 &lt; 7/25 bound the exact Lp norm below by (1 - (7/25)(e - 2))·|q|.</summary>
    internal const double SuperellipsoidNormSlope = (7.0 / 25.0);

    private static readonly Scalar Zero = Literal(0);
    private static readonly Scalar Half = Literal(0.5);
    private static readonly Scalar One = Literal(1);
    private static readonly Scalar Two = Literal(2);
    // SdfProgram.SqrtHalf and SdfProgram.SmoothRadiusMin, held by the evaluator in fixed point and generated into the
    // kernels' ISA declarations.
    private static readonly Scalar SqrtHalf = Constant("SqrtHalf", "SDF_SQRT_HALF");
    private static readonly Scalar SmoothRadiusMin = Constant("SmoothRadiusMin", "SDF_SMOOTH_RADIUS_MIN");

    private static readonly SdfFormulaOperand<SdfVector3> Point = new("p", "p");
    private static readonly SdfFormulaOperand<SdfVector3> Data0Xyz = new("Vector(instruction)", "data0.xyz");
    // The superellipsoid's packed reciprocal radii, which only the shader reads (the fixed carrier divides instead).
    private static readonly SdfFormulaOperand<SdfVector3> Data1Yzw = new(null, "data1.yzw");
    private static readonly SdfFormulaOperand<SdfScalar> Data0X = new("instruction.Data0X", "data0.x");
    private static readonly SdfFormulaOperand<SdfScalar> Data0Y = new("instruction.Data0Y", "data0.y");
    private static readonly SdfFormulaOperand<SdfScalar> Data0Z = new("instruction.Data0Z", "data0.z");
    private static readonly SdfFormulaOperand<SdfScalar> Data0W = new("instruction.Data0W", "data0.w");
    private static readonly SdfFormulaOperand<SdfScalar> Data1Y = new("instruction.Data1Y", "data1.y");
    private static readonly SdfFormulaOperand<SdfScalar> Data1Z = new("instruction.Data1Z", "data1.z");
    private static readonly SdfFormulaOperand<SdfScalar> Data1W = new("instruction.Data1W", "data1.w");
    private static readonly SdfFormulaOperand<SdfScalar> Current = new("current", "current");
    private static readonly SdfFormulaOperand<SdfScalar> Candidate = new("candidate", "candidate");
    private static readonly SdfFormulaOperand<SdfScalar> SmoothRadius = new("smoothRadius", "smoothRadius");

    private static Scalar X(Vector2 value) => Scalar.Of(SdfFormulaOp.X, value);
    private static Scalar Y(Vector2 value) => Scalar.Of(SdfFormulaOp.Y, value);
    private static Scalar X(Vector3 value) => Scalar.Of(SdfFormulaOp.X, value);
    private static Scalar Y(Vector3 value) => Scalar.Of(SdfFormulaOp.Y, value);
    private static Scalar Z(Vector3 value) => Scalar.Of(SdfFormulaOp.Z, value);
    private static Vector2 Vec2(Scalar x, Scalar y) => Vector2.Of(SdfFormulaOp.Vector2, x, y);
    private static Vector3 Vec3(Scalar x, Scalar y, Scalar z) => Vector3.Of(SdfFormulaOp.Vector3, x, y, z);
    private static Vector2 XY(Vector3 value) => Vec2(X(value), Y(value));
    private static Scalar Abs(Scalar value) => Scalar.Of(SdfFormulaOp.Abs, value);
    private static Vector3 Abs(Vector3 value) => Vector3.Of(SdfFormulaOp.Abs, value);
    private static Scalar Min(Scalar a, Scalar b) => Scalar.Of(SdfFormulaOp.Min, a, b);
    private static Scalar Max(Scalar a, Scalar b) => Scalar.Of(SdfFormulaOp.Max, a, b);
    private static Scalar Clamp(Scalar value, Scalar low, Scalar high) => Scalar.Of(SdfFormulaOp.Clamp, value, low, high);
    private static Scalar Lerp(Scalar from, Scalar to, Scalar amount) => Scalar.Of(SdfFormulaOp.Lerp, from, to, amount);
    private static Scalar Length(Vector2 value) => Scalar.Of(SdfFormulaOp.Length, value);
    private static Scalar Length(Vector3 value) => Scalar.Of(SdfFormulaOp.Length, value);
    // The distance from the Y axis, the radius every revolved primitive measures.
    private static Scalar Radial(Vector3 value) => Length(Vec2(X(value), Z(value)));
    private static Scalar Dot(Vector2 a, Vector2 b) => Scalar.Of(SdfFormulaOp.Dot, a, b);
    private static Scalar Dot(Vector3 a, Vector3 b) => Scalar.Of(SdfFormulaOp.Dot, a, b);
    private static Vector2 Scale(Vector2 value, Scalar scale) => Vector2.Of(SdfFormulaOp.Multiply, value, scale);
    private static Vector3 Scale(Vector3 value, Scalar scale) => Vector3.Of(SdfFormulaOp.Multiply, value, scale);
    private static Scalar SegmentParameter(Vector2 point, Vector2 segment) => Scalar.Of(SdfFormulaOp.SegmentParameter, point, segment);
    private static Scalar SegmentParameter(Vector3 point, Vector3 segment) => Scalar.Of(SdfFormulaOp.SegmentParameter, point, segment);
    private static Scalar MinLength(Vector2 a, Vector2 b) => Scalar.Of(SdfFormulaOp.MinLength, a, b);
    private static Scalar ScaledRatio(Scalar value, Scalar scale, Scalar radius, Scalar inverseRadius) =>
        Scalar.Of(SdfFormulaOp.ScaledRatio, value, scale, radius, inverseRadius);
    private static Scalar Power(Scalar value, Scalar exponent) => Scalar.Of(SdfFormulaOp.Power, value, exponent);
    private static Scalar FiniteOr(Scalar value, Scalar fallback) => Scalar.Of(SdfFormulaOp.FiniteOr, value, fallback);
    private static Scalar Ellipse2D(Vector2 point, Scalar semiX, Scalar semiY) => Scalar.Of(SdfFormulaOp.Ellipse2D, point, semiX, semiY);
    private static Scalar Select(Condition condition, Scalar whenTrue, Scalar whenFalse) => Scalar.Of(SdfFormulaOp.Select, condition, whenTrue, whenFalse);
    private static Condition Equal(Scalar a, Scalar b) => Condition.Of(SdfFormulaOp.Equal, a, b);

    // === Helpers ======================================================================================================

    // The exact distance to an origin-centred box from q = |p| - halfExtents: the outside part's length plus the inside
    // part's largest (never positive) component.
    private static readonly SdfFormula<SdfScalar> BoxDistance = Helper("sdfBoxDistance", static f => {
        var q = f.ParameterOf<SdfVector3>("q");
        f.Return(Length(Vec3(Max(X(q), Zero), Max(Y(q), Zero), Max(Z(q), Zero))) + Min(Max(X(q), Max(Y(q), Z(q))), Zero));
    });
    // The 2D box distance from q = |p| - halfSize.
    private static readonly SdfFormula<SdfScalar> BoxDistance2 = Helper("sdfBoxDistance2", static f => {
        var q = f.ParameterOf<SdfVector2>("q");
        f.Return(Length(Vec2(Max(X(q), Zero), Max(Y(q), Zero))) + Min(Max(X(q), Y(q)), Zero));
    });
    // The meridian point for revolving a 2D profile around Y at radial offset o.
    private static readonly SdfFormula<SdfVector2> Revolve2D = Helper<SdfVector2>("sdfRevolve2D", static f => {
        var p = f.ParameterOf<SdfVector3>("p");
        var o = f.ParameterOf<SdfScalar>("o");
        f.Return(Vec2(Radial(p) - o, Y(p)));
    });
    // An exact 2D distance d extruded to half-height h along Z and joined across the cap seam by a 45-degree bevel of
    // size c: the chamfer-intersection of the profile and the ±Z slab. Exact inside and on the surface, a 1-Lipschitz
    // lower bound outside past the bevel's vertex. c = 0 is the plain extrude to the bit: w.x + w.y never exceeds
    // √2·|max(w, 0)| plus the non-positive inside term, so the bevel arm never wins.
    private static readonly SdfFormula<SdfScalar> ExtrudeChamfer2D = Helper("sdfExtrudeChamfer2D", static f => {
        var d = f.ParameterOf<SdfScalar>("d");
        var pz = f.ParameterOf<SdfScalar>("pz");
        var h = f.ParameterOf<SdfScalar>("h");
        var c = f.ParameterOf<SdfScalar>("c");
        var w = f.Bind("w", Vec2(d, Abs(pz) - h));
        f.Return(Max(BoxDistance2.Call(w), ((X(w) + Y(w)) + c) * SqrtHalf));
    });
    // The rounded 2D box: half-extents (hx, hy), corners rounded by r within them.
    private static readonly SdfFormula<SdfScalar> RoundBox2D = Helper("sdfRoundBox2D", static f => {
        var p = f.ParameterOf<SdfVector2>("p");
        var hx = f.ParameterOf<SdfScalar>("hx");
        var hy = f.ParameterOf<SdfScalar>("hy");
        var r = f.ParameterOf<SdfScalar>("r");
        f.Return(BoxDistance2.Call(Vec2((Abs(X(p)) - hx) + r, (Abs(Y(p)) - hy) + r)) - r);
    });
    // The 45-degree-chamfered 2D box: the plain box intersected with a diagonal bevel offset by c. Exact inside and on
    // the surface, a 1-Lipschitz lower bound outside past each bevel vertex; c = 0 is the plain box to the bit, by the
    // extrude's argument.
    private static readonly SdfFormula<SdfScalar> ChamferBox2D = Helper("sdfChamferBox2D", static f => {
        var p = f.ParameterOf<SdfVector2>("p");
        var hx = f.ParameterOf<SdfScalar>("hx");
        var hy = f.ParameterOf<SdfScalar>("hy");
        var c = f.ParameterOf<SdfScalar>("c");
        var q = f.Bind("q", Vec2(Abs(X(p)) - hx, Abs(Y(p)) - hy));
        f.Return(Max(BoxDistance2.Call(q), ((X(q) + Y(q)) + c) * SqrtHalf));
    });
    // The isosceles trapezoid: r1 the bottom half-width, r2 the top, he the half-height.
    private static readonly SdfFormula<SdfScalar> Trapezoid2D = Helper("sdfTrapezoid2D", static f => {
        var p = f.ParameterOf<SdfVector2>("p");
        var r1 = f.ParameterOf<SdfScalar>("r1");
        var r2 = f.ParameterOf<SdfScalar>("r2");
        var he = f.ParameterOf<SdfScalar>("he");
        var px = f.Bind("px", Abs(X(p)));
        var py = f.Bind("py", Y(p));
        var q = f.Bind("q", Vec2(px, py));
        var k1 = f.Bind("k1", Vec2(r2, he));
        // The slanted side; the projection divides by its squared length.
        var k2 = f.Bind("k2", Vec2(r2 - r1, Two * he));
        var ca = f.Bind("ca", Vec2(px - Min(px, Select(py < Zero, r1, r2)), Abs(py) - he));
        var cb = f.Bind("cb", (q - k1) + Scale(k2, SegmentParameter(k1 - q, k2)));
        var m = f.Bind("m", MinLength(ca, cb));
        f.Return(Select((X(cb) < Zero) & (Y(ca) < Zero), -m, m));
    });
    // The superellipsoid's norm for 2 < e <= 3, clamped to its certified band around |q|, so the field stays a
    // conservative distance without assuming an error bound for the power; an overflowed float takes |q|.
    private static readonly SdfFormula<SdfScalar> ClampSuperellipsoidNorm = Helper("sdfClampSuperellipsoidNorm", static f => {
        var approximate = f.ParameterOf<SdfScalar>("approximate");
        var norm = f.ParameterOf<SdfScalar>("norm");
        var exponent = f.ParameterOf<SdfScalar>("exponent");
        var floor = (One - (Literal(SuperellipsoidNormSlope) * (exponent - Two))) * norm;
        f.Return(Clamp(FiniteOr(approximate, norm), floor, norm));
    });
    // The polynomial smooth minimum every smooth blend derives from. Both saturated endpoints return their input to the
    // bit. Far (b at least k beyond a): h is exactly one and the answer is a, so a smooth instance skipped past its
    // k-inflated cull bound leaves the accumulator exactly as evaluating it would. Near (a at least k beyond b): h is
    // exactly zero and the answer is b, where a + (b - a) is not b when |a| >> |b| — the accumulator's seed is the far
    // distance (1e9), so a segment whose first shape smooth-unions would otherwise collapse to a surface at the sample.
    private static readonly SdfFormula<SdfScalar> SmoothMinimum = Helper("sdfBlendSmoothMinimum", static f => {
        var a = f.ParameterOf<SdfScalar>("a");
        var b = f.ParameterOf<SdfScalar>("b");
        var k = f.ParameterOf<SdfScalar>("k");
        var h = f.Bind("h", Clamp(Half + ((Half * (b - a)) / k), Zero, One));
        f.ReturnIf(h <= Zero, b);
        f.ReturnIf(h >= One, a);
        f.Return(Lerp(a, b, One - h) - ((k * h) * (One - h)));
    });

    // === Shapes =======================================================================================================

    // The rounded box: halfExtents inset by the corner radius, which the result offsets back out.
    internal static readonly SdfFormula<SdfScalar> Box = Shape("sdfBox", static f => {
        var p = f.Input("p", Point);
        var halfExtents = f.Input("halfExtents", Data0Xyz);
        var r = f.Input("cornerRadius", Data0W);
        var q = f.Bind("q", Abs(p) - (halfExtents - Vec3(r, r, r)));
        f.Return(BoxDistance.Call(q) - r);
    });
    // The segment from the origin to the endpoint, widened by the radius; a zero endpoint is a sphere. The parameter is
    // projected by the carriers' widening primitive, since a rounded dot of two centimetre vectors keeps a few bits.
    internal static readonly SdfFormula<SdfScalar> Capsule = Shape("sdfCapsule", static f => {
        var p = f.Input("p", Point);
        var endpoint = f.Input("endpoint", Data0Xyz);
        var radius = f.Input("radius", Data0W);
        var h = f.Bind("h", SegmentParameter(p, endpoint));
        f.Return(Length(p - Scale(endpoint, h)) - radius);
    });
    internal static readonly SdfFormula<SdfScalar> Sphere = Shape("sdfSphere", static f => {
        var p = f.Input("p", Point);
        var radius = f.Input("radius", Data0X);
        f.Return(Length(p) - radius);
    });
    internal static readonly SdfFormula<SdfScalar> Torus = Shape("sdfTorus", static f => {
        var p = f.Input("p", Point);
        var major = f.Input("major", Data0X);
        var minor = f.Input("minor", Data0Y);
        f.Return(Length(Vec2(Radial(p) - major, Y(p))) - minor);
    });
    // Upright and centred; the radius and half-height arrive inset by the edge-rounding radius, which the result
    // offsets back out.
    internal static readonly SdfFormula<SdfScalar> Cylinder = Shape("sdfCylinder", static f => {
        var p = f.Input("p", Point);
        var radius = f.Input("radius", Data0X);
        var halfHeight = f.Input("halfHeight", Data0Y);
        var rounding = f.Input("rounding", Data1W);
        f.Return(BoxDistance2.Call(Vec2(Radial(p) - radius, Abs(Y(p)) - halfHeight)) - rounding);
    });
    internal static readonly SdfFormula<SdfScalar> Plane = Shape("sdfPlane", static f => {
        var p = f.Input("p", Point);
        var normal = f.Input("normal", Data0Xyz);
        var offset = f.Input("offset", Data0W);
        f.Return(Dot(p, normal) + offset);
    });
    // The exact 2D vesica revolved around Y: r the two circles' radius, d their half-separation (d < r), b the tip
    // half-height √(r² - d²), host-baked. Exact and convex, so the revolution is a true 3D distance.
    internal static readonly SdfFormula<SdfScalar> Vesica = Shape("sdfVesica", static f => {
        var p = f.Input("p", Point);
        var r = f.Input("r", Data0X);
        var d = f.Input("d", Data0Y);
        var b = f.Input("b", Data0Z);
        var q = f.Bind("q", Vec2(Radial(p), Abs(Y(p))));
        f.Return(Select(((Y(q) - b) * d) > (X(q) * b), Length(q - Vec2(Zero, b)), Length(q - Vec2(-d, Zero)) - r));
    });
    // The slope b = (lowerRadius - upperRadius) / height and its complement a = √(1 - b²) are host-baked.
    internal static readonly SdfFormula<SdfScalar> RoundCone = Shape("sdfRoundCone", static f => {
        var p = f.Input("p", Point);
        var lowerRadius = f.Input("lowerRadius", Data0X);
        var upperRadius = f.Input("upperRadius", Data0Y);
        var height = f.Input("height", Data0Z);
        var b = f.Input("b", Data0W);
        var a = f.Input("a", Data1Y);
        var q = f.Bind("q", Vec2(Radial(p), Y(p)));
        var k = f.Bind("k", Dot(q, Vec2(-b, a)));
        f.ReturnIf(k < Zero, Length(q) - lowerRadius);
        f.ReturnIf(k > (a * height), Length(q - Vec2(Zero, height)) - upperRadius);
        f.Return(Dot(q, Vec2(a, b)) - lowerRadius);
    });
    // The 2D family: an exact 2D profile lifted to a solid (see Lifted). Data1.z bevels an extrude's cap rims.
    internal static readonly SdfFormula<SdfScalar> RoundedRectangle = Lifted("sdfRoundedRectangle", static f => {
        var halfWidth = f.Input("halfWidth", Data0X);
        var halfHeight = f.Input("halfHeight", Data0Y);
        var cornerRadius = f.Input("cornerRadius", Data0Z);
        var cap = f.Input("capChamfer", Data1Z);
        return (point => RoundBox2D.Call(point, halfWidth, halfHeight, cornerRadius), cap);
    });
    // The profile bevels its own four corners at c and the extrude's cap rims at the same c, so a chamfered box reads
    // chamfered on all twelve edges.
    internal static readonly SdfFormula<SdfScalar> ChamferedRectangle = Lifted("sdfChamferedRectangle", static f => {
        var halfWidth = f.Input("halfWidth", Data0X);
        var halfHeight = f.Input("halfHeight", Data0Y);
        var chamfer = f.Input("chamfer", Data0Z);
        return (point => ChamferBox2D.Call(point, halfWidth, halfHeight, chamfer), chamfer);
    });
    internal static readonly SdfFormula<SdfScalar> Trapezoid = Lifted("sdfTrapezoid", static f => {
        var bottomHalfWidth = f.Input("bottomHalfWidth", Data0X);
        var topHalfWidth = f.Input("topHalfWidth", Data0Y);
        var halfHeight = f.Input("halfHeight", Data0Z);
        var cap = f.Input("capChamfer", Data1Z);
        return (point => Trapezoid2D.Call(point, bottomHalfWidth, topHalfWidth, halfHeight), cap);
    });
    internal static readonly SdfFormula<SdfScalar> Ellipse = Lifted("sdfEllipse", static f => {
        var semiX = f.Input("semiX", Data0X);
        var semiY = f.Input("semiY", Data0Y);
        var cap = f.Input("capChamfer", Data1Z);
        return (point => Ellipse2D(point, semiX, semiY), cap);
    });
    // With q = |p| · min(r) / r, d = (Σ q_i^e)^(1/e) - min(r): exactly 1-Lipschitz for every radius and every e >= 1
    // (SdfProgramBuilder.Superellipsoid's remarks hold the proof). e = 2, every ellipsoid, takes the pow-free scaled L2
    // gauge. The norm is factored as m · (Σ (q_i/m)^e)^(1/e) with m = max(q): every power's argument stays in [0, 1]
    // and the sum in [1, 3], where the plain sum reaches float infinity at |p|/r ~ 1e5 for e = 8 and saturates Q48.16
    // at |p|/r ~ 60, collapsing a far field to a few radii.
    internal static readonly SdfFormula<SdfScalar> Superellipsoid = Shape("sdfSuperellipsoid", static f => {
        var p = f.Input("p", Point);
        var radii = f.Input("radii", Data0Xyz);
        var inverseRadii = f.Input("inverseRadii", Data1Yzw);
        var exponent = f.Input("exponent", Data0W);
        var minRadius = f.Bind("minRadius", Min(X(radii), Min(Y(radii), Z(radii))));
        var absolute = f.Bind("absolute", Abs(p));
        var q = f.Bind("q", Vec3(ScaledRatio(X(absolute), minRadius, X(radii), X(inverseRadii)),
            ScaledRatio(Y(absolute), minRadius, Y(radii), Y(inverseRadii)),
            ScaledRatio(Z(absolute), minRadius, Z(radii), Z(inverseRadii))));
        f.ReturnIf(Equal(exponent, Two), Length(q) - minRadius);
        var m = f.Bind("m", Max(X(q), Max(Y(q), Z(q))));
        f.ReturnIf(m <= Zero, -minRadius);
        var ux = f.Bind("ux", Power(X(q) / m, exponent));
        var uy = f.Bind("uy", Power(Y(q) / m, exponent));
        var uz = f.Bind("uz", Power(Z(q) / m, exponent));
        var norm = f.Bind("norm", m * Power((ux + uy) + uz, One / exponent));
        f.ReturnIf((Two < exponent) & (exponent <= Literal(SuperellipsoidClampedExponent)), ClampSuperellipsoidNorm.Call(norm, Length(q), exponent) - minRadius);
        f.Return(norm - minRadius);
    });

    // === Blends =======================================================================================================
    // Each composes the candidate into the running field. The smooth family floors its radius at SmoothRadiusMin; the
    // chamfer and round-seam families floor theirs at zero, so a zero bevel is exactly the hard seam.

    internal static readonly SdfFormula<SdfScalar> Union = Blend("sdfBlendUnion", static (f, current, candidate) => Min(current, candidate));
    internal static readonly SdfFormula<SdfScalar> Subtraction = Blend("sdfBlendSubtraction", static (f, current, candidate) => Max(current, -candidate));
    internal static readonly SdfFormula<SdfScalar> Intersection = Blend("sdfBlendIntersection", static (f, current, candidate) => Max(current, candidate));
    internal static readonly SdfFormula<SdfScalar> Xor = Blend("sdfBlendXor", static (f, current, candidate) =>
        Max(Min(current, candidate), -Max(current, candidate)));
    internal static readonly SdfFormula<SdfScalar> SmoothUnion = Blend("sdfBlendSmoothUnion", static (f, current, candidate) =>
        SmoothMinimum.Call(current, candidate, SmoothK(f)));
    internal static readonly SdfFormula<SdfScalar> SmoothIntersection = Blend("sdfBlendSmoothIntersection", static (f, current, candidate) =>
        -SmoothMinimum.Call(-current, -candidate, SmoothK(f)));
    internal static readonly SdfFormula<SdfScalar> SmoothSubtraction = Blend("sdfBlendSmoothSubtraction", static (f, current, candidate) =>
        -SmoothMinimum.Call(candidate, -current, SmoothK(f)));
    internal static readonly SdfFormula<SdfScalar> ChamferUnion = Blend("sdfBlendChamferUnion", static (f, current, candidate) =>
        Min(Min(current, candidate), ((current + candidate) - Chamfer(f)) * SqrtHalf));
    internal static readonly SdfFormula<SdfScalar> ChamferIntersection = Blend("sdfBlendChamferIntersection", static (f, current, candidate) =>
        Max(Max(current, candidate), ((current + candidate) + Chamfer(f)) * SqrtHalf));
    internal static readonly SdfFormula<SdfScalar> ChamferSubtraction = Blend("sdfBlendChamferSubtraction", static (f, current, candidate) =>
        Max(Max(current, -candidate), ((current - candidate) + Chamfer(f)) * SqrtHalf));
    internal static readonly SdfFormula<SdfScalar> GrooveUnion = Blend("sdfBlendGrooveUnion", static (f, current, candidate) =>
        Max(Min(current, candidate), Chamfer(f) - Length(Vec2(current, candidate))));
    internal static readonly SdfFormula<SdfScalar> PipeUnion = Blend("sdfBlendPipeUnion", static (f, current, candidate) =>
        Min(Min(current, candidate), Length(Vec2(current, candidate)) - Chamfer(f)));
    internal static readonly SdfFormula<SdfScalar> GrooveSubtraction = Blend("sdfBlendGrooveSubtraction", static (f, current, candidate) =>
        Max(Max(current, -candidate), Chamfer(f) - Length(Vec2(current, candidate))));
    internal static readonly SdfFormula<SdfScalar> PipeSubtraction = Blend("sdfBlendPipeSubtraction", static (f, current, candidate) =>
        Min(Max(current, -candidate), Length(Vec2(current, candidate)) - Chamfer(f)));

    // === Builders =====================================================================================================

    private static SdfFormula<SdfScalar> Helper(string name, Action<SdfFormula<SdfScalar>> build) => Helper<SdfScalar>(name, build);
    private static SdfFormula<T> Helper<T>(string name, Action<SdfFormula<T>> build) where T : struct, ISdfFormulaType {
        var formula = new SdfFormula<T>(name);
        build(formula);
        return formula;
    }
    private static SdfFormula<SdfScalar> Shape(string name, Action<SdfFormula<SdfScalar>> build) => Helper(name, build);

    // A 2D profile lifted to a solid, the family's one lift (data1.y > 0.5 extrudes along Z to ±liftAmount, else
    // revolves around Y at radial offset liftAmount). The profile parameters arrive inset by the edge-rounding radius
    // (data1.w), which the result offsets back out: the outward half of a morphological opening, so the solid keeps its
    // authored extent and its edges fillet. Extruding an exact profile is exact; revolving is exact where the profile
    // clears the axis and a conservative underestimate near it. Revolution has no cap seam, so it ignores the bevel.
    private static SdfFormula<SdfScalar> Lifted(string name, Func<SdfFormula<SdfScalar>, (Func<Vector2, Scalar> Profile, Scalar Cap)> declare) {
        var f = new SdfFormula<SdfScalar>(name);
        var p = f.Input("p", Point);
        var (profile, cap) = declare(f);
        var liftAmount = f.Input("liftAmount", Data0W);
        var lift = f.Input("lift", Data1Y);
        var rounding = f.Input("rounding", Data1W);
        f.ReturnIf(lift > Half, ExtrudeChamfer2D.Call(profile(XY(p)), Z(p), liftAmount, cap) - rounding);
        f.Return(profile(Revolve2D.Call(p, liftAmount)) - rounding);
        return f;
    }

    // A blend of the running field (current) and the shape's candidate. A blend that reads its radius declares it
    // through SmoothK or Chamfer, which bind the floored radius once.
    private static SdfFormula<SdfScalar> Blend(string name, Func<BlendRadius, Scalar, Scalar, Scalar> compose) {
        var f = new SdfFormula<SdfScalar>(name);
        var current = f.Input("current", Current);
        var candidate = f.Input("candidate", Candidate);
        f.Return(compose(new BlendRadius(f), current, candidate));
        return f;
    }

    private static Scalar SmoothK(BlendRadius radius) => radius.Floored("k", SmoothRadiusMin);
    private static Scalar Chamfer(BlendRadius radius) => radius.Floored("chamfer", Zero);

    // The blend's radius operand, declared and floored on first use.
    private sealed class BlendRadius(SdfFormula<SdfScalar> formula) {
        private Scalar? m_floored;

        internal Scalar Floored(string name, Scalar floor) {
            if (m_floored is { } bound) { return bound; }
            var radius = formula.Input("smoothRadius", SmoothRadius);
            m_floored = formula.Bind(name, Max(radius, floor));
            return m_floored.Value;
        }
    }
}
