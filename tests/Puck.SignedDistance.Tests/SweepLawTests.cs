using System.Numerics;

using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The <see cref="SdfShapeType.Sweep"/> contract: the closed-form closest point on a known quadratic Bezier, the
/// radius profile at its three named parameters, the admission refusals <see cref="SdfProgramBuilder.Sweep"/>
/// enforces, and — the load-bearing proof — that the shape's own conservative margin
/// (<see cref="SdfProgramBuilder.SweepConservativeMargin"/>) keeps the field from overestimating true distance to a
/// fine-sampled reference tube, for both a single strand and a multi-strand braid, over a randomized grid.
/// </summary>
public sealed class SweepLawTests {
    // A C# mirror of the CLOSED-FORM closest-point-on-quadratic-bezier the shader implements (sdfSweepClosestT,
    // sdf-vm.hlsli) — an INDEPENDENT re-derivation from iq's construction, not a call into production code, so this
    // law tests the algorithm the render path actually runs.
    private static float ClosestT(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        var coefA = (b - a);
        var coefB = ((a - (2f * b)) + c);
        var coefC = (coefA * 2f);
        var d = (a - p);
        var bb = Vector3.Dot(coefB, coefB);

        if (bb < 1e-10f) {
            var ac = (c - a);
            var acLengthSquared = MathF.Max(Vector3.Dot(ac, ac), 1e-20f);

            return Math.Clamp(value: (Vector3.Dot((p - a), ac) / acLengthSquared), min: 0f, max: 1f);
        }

        var kk = (1f / bb);
        var kx = (kk * Vector3.Dot(coefA, coefB));
        var ky = ((kk * ((2f * Vector3.Dot(coefA, coefA)) + Vector3.Dot(d, coefB))) / 3f);
        var kz = (kk * Vector3.Dot(d, coefA));
        var p1 = (ky - (kx * kx));
        var p3 = ((p1 * p1) * p1);
        var q = ((kx * ((2f * kx * kx) - (3f * ky))) + kz);
        var h = ((q * q) + (4f * p3));

        if (h >= 0f) {
            h = MathF.Sqrt(x: h);

            var xh = (((h - q)) * 0.5f);
            var xl = (((-h - q)) * 0.5f);
            var uv = new Vector2(
                x: (MathF.Sign(xh) * MathF.Pow(x: MathF.Abs(xh), y: (1f / 3f))),
                y: (MathF.Sign(xl) * MathF.Pow(x: MathF.Abs(xl), y: (1f / 3f)))
            );

            return Math.Clamp(value: ((uv.X + uv.Y) - kx), min: 0f, max: 1f);
        }

        var z = MathF.Sqrt(x: -p1);
        var v = (MathF.Acos(x: Math.Clamp(value: (q / ((p1 * z) * 2f)), min: -1f, max: 1f)) / 3f);
        var m = MathF.Cos(x: v);
        var n = (MathF.Sin(x: v) * 1.7320508f);
        var t0 = Math.Clamp(value: (((m + m) * z) - kx), min: 0f, max: 1f);
        var t1 = Math.Clamp(value: (((-n - m) * z) - kx), min: 0f, max: 1f);
        var t2 = Math.Clamp(value: (((n - m) * z) - kx), min: 0f, max: 1f);
        var q0 = (d + ((coefC + (coefB * t0)) * t0));
        var q1 = (d + ((coefC + (coefB * t1)) * t1));
        var q2 = (d + ((coefC + (coefB * t2)) * t2));
        var d0 = Vector3.Dot(q0, q0);
        var d1 = Vector3.Dot(q1, q1);
        var d2 = Vector3.Dot(q2, q2);
        var best = MathF.Min(d0, MathF.Min(d1, d2));

        return ((best == d0) ? t0 : ((best == d1) ? t1 : t2));
    }
    private static Vector3 BezierPoint(Vector3 a, Vector3 b, Vector3 c, float t) => Vector3.Lerp(
        value1: Vector3.Lerp(value1: a, value2: b, amount: t),
        value2: Vector3.Lerp(value1: b, value2: c, amount: t),
        amount: t
    );
    private static Vector3 BezierDerivative(Vector3 a, Vector3 b, Vector3 c, float t) =>
        (2f * Vector3.Lerp(value1: (b - a), value2: (c - b), amount: t));
    private static float RadiusAt(float t, float radiusStart, float radiusEnd, float bulge) {
        var taper = float.Lerp(value1: radiusStart, value2: radiusEnd, amount: t);
        var s = MathF.Max(x: MathF.Sin(x: (MathF.PI * t)), y: 0f);

        return (taper + (bulge * MathF.Pow(x: s, y: 0.65f)));
    }
    // The RENDER-PATH field: closest-t on the centerline, evaluate the orbit offset and radius there, minus the
    // strand count, minus the conservative margin — mirrors sdfSweep (sdf-vm.hlsli) exactly.
    private static float FieldDistance(Vector3 p, Vector3 a, Vector3 b, Vector3 c, float radiusStart, float radiusEnd, float bulge, int strands, float twist, float strandOffset) {
        var t = ClosestT(p: p, a: a, b: b, c: c);
        var basePoint = BezierPoint(a: a, b: b, c: c, t: t);
        var radius = RadiusAt(t: t, radiusStart: radiusStart, radiusEnd: radiusEnd, bulge: bulge);
        var tangent = BezierDerivative(a: a, b: b, c: c, t: t);
        var tangentDirection = ((tangent.LengthSquared() > 1e-16f) ? Vector3.Normalize(tangent) : Vector3.UnitY);
        var referenceAxis = ((MathF.Abs(tangentDirection.Y) < 0.999f) ? Vector3.UnitY : Vector3.UnitX);
        var u = Vector3.Normalize(Vector3.Cross(tangentDirection, referenceAxis));
        var v = Vector3.Cross(tangentDirection, u);
        var best = float.PositiveInfinity;

        for (var strand = 0; (strand < strands); strand++) {
            var phase = ((t * twist * MathF.Tau) + ((strand * MathF.Tau) / strands));
            var offsetPoint = (basePoint + (((u * MathF.Cos(x: phase)) + (v * MathF.Sin(x: phase))) * strandOffset));

            best = MathF.Min(best, (Vector3.Distance(p, offsetPoint) - radius));
        }

        var margin = SdfProgramBuilder.SweepConservativeMargin(
            bulge: bulge,
            radiusEnd: radiusEnd,
            radiusStart: radiusStart,
            strandOffset: strandOffset,
            twist: twist
        );

        return (best - margin);
    }
    // A fine-sampled brute-force reference: the true minimum of "distance to the strand curve minus the radius
    // profile" over a dense t grid and every strand — the ground truth the conservative margin must never exceed.
    private static float ReferenceDistance(Vector3 p, Vector3 a, Vector3 b, Vector3 c, float radiusStart, float radiusEnd, float bulge, int strands, float twist, float strandOffset, int samples = 1200) {
        var best = float.PositiveInfinity;

        for (var i = 0; (i <= samples); i++) {
            var t = (i / (float)samples);
            var basePoint = BezierPoint(a: a, b: b, c: c, t: t);
            var radius = RadiusAt(t: t, radiusStart: radiusStart, radiusEnd: radiusEnd, bulge: bulge);
            var tangent = BezierDerivative(a: a, b: b, c: c, t: t);
            var tangentDirection = ((tangent.LengthSquared() > 1e-16f) ? Vector3.Normalize(tangent) : Vector3.UnitY);
            var referenceAxis = ((MathF.Abs(tangentDirection.Y) < 0.999f) ? Vector3.UnitY : Vector3.UnitX);
            var u = Vector3.Normalize(Vector3.Cross(tangentDirection, referenceAxis));
            var v = Vector3.Cross(tangentDirection, u);

            for (var strand = 0; (strand < strands); strand++) {
                var phase = ((t * twist * MathF.Tau) + ((strand * MathF.Tau) / strands));
                var offsetPoint = (basePoint + (((u * MathF.Cos(x: phase)) + (v * MathF.Sin(x: phase))) * strandOffset));

                best = MathF.Min(best, (Vector3.Distance(p, offsetPoint) - radius));
            }
        }

        return best;
    }
    private static SdfProgram BuildSweep(Vector3 a, Vector3 b, Vector3 c, float radiusStart, float radiusEnd, float bulge, int strands, float twist, float strandOffset) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder
            .ResetPoint()
            .Sweep(
                a: a,
                b: b,
                c: c,
                radiusStart: radiusStart,
                radiusEnd: radiusEnd,
                bulge: bulge,
                strands: strands,
                twist: twist,
                strandOffset: strandOffset,
                material: material
            )
            .Build();
    }

    [Fact]
    public void ClosestPointOnAKnownParabolaMatchesBruteForce() {
        // A = (-1, 0, 0), B = (0, 2, 0), C = (1, 0, 0): the bezier traces a symmetric arch. At p directly above the
        // apex, the closest point is exactly t = 0.5 by symmetry.
        var a = new Vector3(-1f, 0f, 0f);
        var b = new Vector3(0f, 2f, 0f);
        var c = new Vector3(1f, 0f, 0f);
        var p = new Vector3(0f, 3f, 0f);

        var t = ClosestT(p: p, a: a, b: b, c: c);

        Assert.Equal(actual: t, expected: 0.5f, precision: 4);

        // Off-axis: brute-force a fine grid and confirm the closed form lands within one grid step.
        var probe = new Vector3(0.6f, 1.3f, -0.2f);
        var bruteBestT = 0f;
        var bruteBestDistance = float.PositiveInfinity;

        for (var i = 0; (i <= 20000); i++) {
            var sampleT = (i / 20000f);
            var distance = Vector3.Distance(probe, BezierPoint(a: a, b: b, c: c, t: sampleT));

            if (distance < bruteBestDistance) {
                bruteBestDistance = distance;
                bruteBestT = sampleT;
            }
        }

        var closedFormT = ClosestT(p: probe, a: a, b: b, c: c);

        Assert.True(condition: (MathF.Abs(closedFormT - bruteBestT) < 0.001f));
    }
    [Theory]
    [InlineData(0f, 0.1f, 0.5f, 0f, 0.1f)]
    [InlineData(0.5f, 0.1f, 0.5f, 0.05f, 0.35f)]
    [InlineData(1f, 0.1f, 0.5f, 0f, 0.5f)]
    public void TheRadiusProfileMatchesItsFormulaAtNamedParameters(float t, float radiusStart, float radiusEnd, float bulge, float expected) {
        var radius = RadiusAt(t: t, radiusStart: radiusStart, radiusEnd: radiusEnd, bulge: bulge);

        Assert.Equal(actual: radius, expected: expected, precision: 5);
    }
    [Fact]
    public void TheConservativeMarginStaysBelowTrueDistanceOnAGridForOneStrand() {
        var a = new Vector3(0.12f, 4.148f, 0.325f);
        var b = new Vector3(-0.16f, 4.17f, 0.57f);
        var c = new Vector3(-0.387f, 3.844f, 0.38f);
        const float RadiusStart = 0.05f;
        const float RadiusEnd = 0.05f;
        const float Bulge = 0.05f;
        const int Strands = 1;
        const float Twist = 0f;
        const float StrandOffset = 0f;
        var random = new Random(Seed: 1);

        for (var trial = 0; (trial < 400); trial++) {
            var t = (float)random.NextDouble();
            var center = BezierPoint(a: a, b: b, c: c, t: t);
            var radius = RadiusAt(t: t, radiusStart: RadiusStart, radiusEnd: RadiusEnd, bulge: Bulge);
            var direction = Vector3.Normalize(new Vector3(
                x: ((float)random.NextDouble() - 0.5f),
                y: ((float)random.NextDouble() - 0.5f),
                z: ((float)random.NextDouble() - 0.5f)
            ));
            var offset = MathF.Max(0f, (radius + (((float)random.NextDouble() - 0.5f) * radius)));
            var p = (center + (direction * offset));

            var fieldDistance = FieldDistance(p: p, a: a, b: b, c: c, radiusStart: RadiusStart, radiusEnd: RadiusEnd, bulge: Bulge, strands: Strands, twist: Twist, strandOffset: StrandOffset);
            var trueDistance = ReferenceDistance(p: p, a: a, b: b, c: c, radiusStart: RadiusStart, radiusEnd: RadiusEnd, bulge: Bulge, strands: Strands, twist: Twist, strandOffset: StrandOffset);

            Assert.True(
                condition: (fieldDistance <= (trueDistance + 1e-4f)),
                userMessage: $"trial {trial}: field {fieldDistance} exceeded true distance {trueDistance} at p={p}"
            );
        }
    }
    [Fact]
    public void TheConservativeMarginStaysBelowTrueDistanceOnAGridForThreeStrands() {
        var a = new Vector3(-0.42f, 3.48f, -0.30f);
        var b = new Vector3(-0.77f, 3.40f, -0.74f);
        var c = new Vector3(-1.12f, 3.32f, -1.18f);
        const float RadiusStart = 0.060f;
        const float RadiusEnd = 0.046f;
        const float Bulge = 0f;
        const int Strands = 3;
        const float Twist = 4f;
        const float StrandOffset = 0.06f;
        var random = new Random(Seed: 2);

        for (var trial = 0; (trial < 400); trial++) {
            var t = (float)random.NextDouble();
            var center = BezierPoint(a: a, b: b, c: c, t: t);
            var radius = RadiusAt(t: t, radiusStart: RadiusStart, radiusEnd: RadiusEnd, bulge: Bulge);
            var reach = (radius + StrandOffset);
            var direction = Vector3.Normalize(new Vector3(
                x: ((float)random.NextDouble() - 0.5f),
                y: ((float)random.NextDouble() - 0.5f),
                z: ((float)random.NextDouble() - 0.5f)
            ));
            var offset = MathF.Max(0f, (reach + (((float)random.NextDouble() - 0.5f) * reach)));
            var p = (center + (direction * offset));

            var fieldDistance = FieldDistance(p: p, a: a, b: b, c: c, radiusStart: RadiusStart, radiusEnd: RadiusEnd, bulge: Bulge, strands: Strands, twist: Twist, strandOffset: StrandOffset);
            var trueDistance = ReferenceDistance(p: p, a: a, b: b, c: c, radiusStart: RadiusStart, radiusEnd: RadiusEnd, bulge: Bulge, strands: Strands, twist: Twist, strandOffset: StrandOffset);

            Assert.True(
                condition: (fieldDistance <= (trueDistance + 1e-4f)),
                userMessage: $"trial {trial}: field {fieldDistance} exceeded true distance {trueDistance} at p={p}"
            );
        }
    }
    [Fact]
    public void AZeroStrandOffsetSingleStrandIsTheExactTubeMinusMargin() {
        var a = Vector3.Zero;
        var b = new Vector3(1f, 1f, 0f);
        var c = new Vector3(2f, 0f, 0f);
        const float Radius = 0.2f;
        var p = new Vector3(0.5f, 1.5f, 0.3f);

        var expected = (FieldDistance(p: p, a: a, b: b, c: c, radiusStart: Radius, radiusEnd: Radius, bulge: 0f, strands: 1, twist: 0f, strandOffset: 0f));
        var t = ClosestT(p: p, a: a, b: b, c: c);
        var directTube = (Vector3.Distance(p, BezierPoint(a: a, b: b, c: c, t: t)) - Radius);

        Assert.Equal(actual: expected, expected: directTube, precision: 5);
    }
    [Fact]
    public void BulgeExceedingTheAdmittedRatioRefusesByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: (SdfProgramBuilder.MaxSweepBulgeRatio * 0.2f),
            strands: 1,
            twist: 0f,
            strandOffset: 0f,
            material: material
        ));

        Assert.Equal(actual: exception.ParamName, expected: "bulge");
    }
    [Fact]
    public void TaperExceedingTheAdmittedRatioRefusesByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.01f,
            radiusEnd: (0.01f + (SdfProgramBuilder.MaxSweepTaperRatio * 0.02f)),
            bulge: 0f,
            strands: 1,
            twist: 0f,
            strandOffset: 0f,
            material: material
        ));

        Assert.Equal(actual: exception.ParamName, expected: "radiusEnd");
    }
    [Fact]
    public void StrandOffsetExceedingTheAdmittedRatioRefusesByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: 0f,
            strands: 3,
            twist: 1f,
            strandOffset: (SdfProgramBuilder.MaxSweepStrandOffsetRatio * 0.2f),
            material: material
        ));

        Assert.Equal(actual: exception.ParamName, expected: "strandOffset");
    }
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void AStrandCountOutsideTheAdmittedRangeRefusesByName(int strands) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: 0f,
            strands: strands,
            twist: 0f,
            strandOffset: 0f,
            material: material
        ));

        Assert.Equal(actual: exception.ParamName, expected: "strands");
    }
    [Fact]
    public void ANonPositiveRadiusRefusesByName() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Sweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0f,
            radiusEnd: 0.1f,
            bulge: 0f,
            strands: 1,
            twist: 0f,
            strandOffset: 0f,
            material: material
        ));

        Assert.Equal(actual: exception.ParamName, expected: "radiusStart");
    }
    [Fact]
    public void TheFixedPointEvaluatorAcceptsOneStrand() {
        var program = BuildSweep(
            a: Vector3.Zero,
            b: new Vector3(0.5f, 1f, 0f),
            c: new Vector3(1f, 0f, 0f),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: 0.05f,
            strands: 1,
            twist: 0.5f,
            strandOffset: 0.03f
        );
        var evaluator = new SdfFieldEvaluator(program: program);
        var probe = new Vector3(0.5f, 0.4f, 0.1f);

        var found = evaluator.TryDistance(
            position: FixedPositionOf(probe),
            distance: out var distance,
            material: out _
        );

        Assert.True(condition: found);

        var expectedFloat = FieldDistance(p: probe, a: Vector3.Zero, b: new Vector3(0.5f, 1f, 0f), c: new Vector3(1f, 0f, 0f), radiusStart: 0.1f, radiusEnd: 0.1f, bulge: 0.05f, strands: 1, twist: 0.5f, strandOffset: 0.03f);

        Assert.True(condition: (MathF.Abs((float)distance - expectedFloat) < 0.02f));
    }
    [Fact]
    public void TheFixedPointEvaluatorRefusesMoreThanOneStrandByName() {
        var program = BuildSweep(
            a: Vector3.Zero,
            b: Vector3.UnitY,
            c: (2f * Vector3.UnitY),
            radiusStart: 0.1f,
            radiusEnd: 0.1f,
            bulge: 0f,
            strands: 3,
            twist: 1f,
            strandOffset: 0.05f
        );
        var exception = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program));

        Assert.Contains(actualString: exception.Message, expectedSubstring: "Sweep");
    }
    private static Puck.Maths.FixedPosition FixedPositionOf(Vector3 value) => Puck.Maths.FixedPosition.FromLocal(local: new Puck.Maths.FixedVector3(
        X: Puck.Maths.FixedQ4816.FromDouble(value: value.X),
        Y: Puck.Maths.FixedQ4816.FromDouble(value: value.Y),
        Z: Puck.Maths.FixedQ4816.FromDouble(value: value.Z)
    ));
}
