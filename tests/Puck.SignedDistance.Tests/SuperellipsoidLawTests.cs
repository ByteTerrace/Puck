using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: <c>SdfProgramBuilder.Superellipsoid</c>'s field <c>d = min(r)*(sum(|p_i/r_i|^e))^(1/e) - min(r)</c> is
/// exact at its own zero set and its gradient never exceeds 1 in Euclidean norm — the field's own proof of
/// admission, since sphere tracing only needs the packed distance to be a sound lower bound on true distance
/// (equivalent, given the zero set is correct, to the field being 1-Lipschitz). The exponent's boundary case (e = 2)
/// is the ISA's one ellipsoid: packed as this same shape, on a pow-free fast path that computes the same gauge.
/// </summary>
public sealed class SuperellipsoidLawTests {
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));
    private static SdfFieldEvaluator Superellipsoid(Vector3 radii, float exponent) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ResetPoint().Superellipsoid(
            radii,
            exponent,
            material
        );

        return new SdfFieldEvaluator(program: builder.Build());
    }

    // THE LAW: the fixed-point mirror computes the l_e gauge in its factored form, so a query FAR from a small
    // superellipsoid still reads the true distance. Along an axis the gauge is exact: d = min(r) * (|z|/r_z - 1). The
    // un-factored sum(q_i^e) saturates FixedQ4816.Pow at |p|/r ~ 60 for e = 8 (MaxValue^(1/8) ~ 58.7 radii), which
    // read a 200-radius query as ~5.8 radii — a march starved to a crawl across every far field, and TryGroundHeight
    // answering "no ground" once the budget ran out.
    [Theory]
    [InlineData(2f, 200.0)]
    [InlineData(2.5f, 200.0)]
    [InlineData(4f, 200.0)]
    [InlineData(8f, 200.0)]
    [InlineData(8f, 2000.0)]
    public void AFarQueryReadsTheTrueDistanceInsteadOfSaturating(float exponent, double radiiAway) {
        var radii = new Vector3(
            x: 0.1f,
            y: 0.05f,
            z: 0.08f
        );
        var evaluator = Superellipsoid(
            exponent: exponent,
            radii: radii
        );
        var z = (radii.Z * radiiAway);
        var expected = (radii.Y * (radiiAway - 1.0));

        Assert.True(condition: evaluator.TryDistance(
            Position(
                x: 0,
                y: 0,
                z: z
            ),
            out var axial,
            out _
        ));
        Assert.Equal(
            actual: ((double)axial),
            expected: expected,
            precision: 1
        );

        // A diagonal far point: q = (k, k, k) with k = |p_i|/r_i, so the gauge is k * 3^(1/e) exactly.
        var k = radiiAway;
        var diagonalExpected = (radii.Y * ((k * Math.Pow(
            x: 3.0,
            y: (1.0 / exponent)
        )) - 1.0));

        Assert.True(condition: evaluator.TryDistance(
            Position(
                x: (radii.X * k),
                y: (radii.Y * k),
                z: (radii.Z * k)
            ),
            out var diagonal,
            out _
        ));
        Assert.InRange(
            actual: (((double)diagonal) / diagonalExpected),
            high: 1.005,
            low: 0.995
        );
    }
    [Fact]
    public void AppendScaledPrimitiveEmitsTheAuthoredExponentAtAnisotropicScale() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var scale = new Vector3(
            x: 2f,
            y: 0.5f,
            z: 1f
        );

        _ = SdfSolidGeometry.AppendScaledPrimitive(
            chain: builder.ResetPoint(),
            type: SdfSolidPrimitive.Superellipsoid,
            scale: scale,
            material: material,
            exponent: 4f
        );

        var program = builder.Build();
        var instruction = Assert.Single(
            collection: program.Instructions,
            predicate: candidate => (candidate.Op == SdfOp.ShapeBlend)
        );

        Assert.Equal(
            ((uint)SdfShapeType.Superellipsoid),
            instruction.Shape
        );
        Assert.Equal(
            scale.X,
            instruction.Data0.X,
            tolerance: 1e-5f
        );
        Assert.Equal(
            scale.Y,
            instruction.Data0.Y,
            tolerance: 1e-5f
        );
        Assert.Equal(
            scale.Z,
            instruction.Data0.Z,
            tolerance: 1e-5f
        );
        Assert.Equal(
            4f,
            instruction.Data0.W,
            tolerance: 1e-5f
        );
    }
    [InlineData(1.9f)]
    [InlineData(8.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [Theory]
    public void ExponentOutsideTheAdmittedRangeIsRefusedByTheBuilder(float exponent) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Superellipsoid(
            Vector3.One,
            exponent,
            material
        ));

        Assert.Contains(
            "exponent",
            refusal.Message
        );

        // The control: the same call at the admitted range's own edges emits.
        _ = builder.Superellipsoid(
            Vector3.One,
            SdfProgramBuilder.MinSuperellipsoidExponent,
            material
        );
        _ = builder.Superellipsoid(
            Vector3.One,
            SdfProgramBuilder.MaxSuperellipsoidExponent,
            material
        );
        Assert.Equal(
            2,
            builder.Build().Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.ShapeBlend))
        );
    }
    [InlineData(1.9f)]
    [InlineData(8.1f)]
    [Theory]
    public void ExponentOutsideTheAdmittedRangeIsRefusedByTheDoor(float exponent) {
        Assert.False(condition: SdfSolidGeometry.TryValidateScaledPrimitive(
            type: SdfSolidPrimitive.Superellipsoid,
            scale: Vector3.One,
            refusal: out var refusal,
            exponent: exponent
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "exponent"
        );

        // The control: the door admits the same scale at an in-range exponent.
        Assert.True(condition: SdfSolidGeometry.TryValidateScaledPrimitive(
            type: SdfSolidPrimitive.Superellipsoid,
            scale: Vector3.One,
            refusal: out _,
            exponent: 5f
        ));
    }
    // THE LAW: exponent 2 is the ellipsoid, and the ISA has no other: the builder packs it as the Superellipsoid shape
    // (never a separate approximate primitive), and the program carries no step clamp however eccentric its radii —
    // the gauge is exactly 1-Lipschitz at e = 2 like every admitted exponent.
    [Fact]
    public void ExponentTwoPacksTheExactGaugeWithNoStepClamp() {
        var radii = new Vector3(
            x: 1.3f,
            y: 0.05f,
            z: 2.1f
        );
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.Superellipsoid(
            radii,
            SdfProgramBuilder.MinSuperellipsoidExponent,
            material
        ).Build();
        var instruction = Assert.Single(collection: program.Instructions);

        Assert.Equal(
            expected: ((uint)SdfShapeType.Superellipsoid),
            actual: instruction.Shape
        );
        Assert.Equal(
            expected: SdfProgramBuilder.MinSuperellipsoidExponent,
            actual: instruction.Data0.W
        );
        Assert.Equal(
            expected: 1f,
            actual: program.StepScale
        );
    }
    // THE LAW: the fixed-point mirror's e == 2 fast path is the same scaled L2 gauge (|p/r| - 1) * min(r) the general
    // exponent path computes, read against an independent double-precision oracle off-axis, inside, and at the centre.
    [Fact]
    public void ExponentTwoFastPathIsTheScaledL2Gauge() {
        var radii = new Vector3(
            x: 1.3f,
            y: 0.4f,
            z: 2.1f
        );
        var evaluator = Superellipsoid(
            exponent: SdfProgramBuilder.MinSuperellipsoidExponent,
            radii: radii
        );
        double[][] points = [
            [0.0, 0.0, 0.0], [0.3, 0.1, -0.4], [1.0, 0.5, 1.0], [-2.0, 0.7, 3.5], [0.0, 0.0, 2.1],
        ];

        foreach (var point in points) {
            var qx = (point[0] / radii.X);
            var qy = (point[1] / radii.Y);
            var qz = (point[2] / radii.Z);
            var expected = ((Math.Sqrt(d: (((qx * qx) + (qy * qy)) + (qz * qz))) - 1.0) * radii.Y);

            Assert.True(condition: evaluator.TryDistance(
                Position(
                    x: point[0],
                    y: point[1],
                    z: point[2]
                ),
                out var distance,
                out _
            ));
            Assert.Equal(
                actual: ((double)distance),
                expected: expected,
                precision: 3
            );
        }
    }
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(4f)]
    [InlineData(8f)]
    [Theory]
    public void FieldSignsAtKnownPoints(float exponent) {
        var radii = new Vector3(
            x: 1f,
            y: 0.6f,
            z: 1.4f
        );
        var evaluator = Superellipsoid(
            exponent: exponent,
            radii: radii
        );

        // The centre is strictly inside every admitted exponent's solid.
        Assert.True(condition: evaluator.TryDistance(
            Position(
                x: 0,
                y: 0,
                z: 0
            ),
            out var centre,
            out _
        ));
        Assert.True(condition: (centre < FixedQ4816.Zero));

        // A point twice the largest radius out on every axis is strictly outside.
        Assert.True(condition: evaluator.TryDistance(
            Position(
                x: 0,
                y: 0,
                z: (radii.Z * 3.0)
            ),
            out var far,
            out _
        ));
        Assert.True(condition: (far > FixedQ4816.Zero));

        // Exactly on an axis at its own radius is the zero set (S(p) = 1 exactly there).
        Assert.True(condition: evaluator.TryDistance(
            Position(
                x: 0,
                y: radii.Y,
                z: 0
            ),
            out var onAxis,
            out _
        ));
        Assert.InRange(
            actual: ((double)onAxis),
            high: 0.01,
            low: -0.01
        );

        // A point authored well inside the smallest radius on every axis stays inside for any admitted exponent
        // (|p_i| < r_i on every axis is a subset of S(p) < 1 for e >= 1).
        var insideEverywhere = (radii * 0.3f);

        Assert.True(condition: evaluator.TryDistance(
            Position(
                x: insideEverywhere.X,
                y: insideEverywhere.Y,
                z: insideEverywhere.Z
            ),
            out var inside,
            out _
        ));
        Assert.True(condition: (inside < FixedQ4816.Zero));
    }
    // THE PROOF: SdfProgramBuilder.Superellipsoid's remarks derive |grad d| == 1 exactly at every point, for every
    // exponent in the admitted [2, 8] range and every radius. TryFieldGradient's 6-tap central difference is an
    // independent numeric channel (the same one the field evaluator's own gradient/normal consumers use), so a
    // sweep across exponents, radius ratios, and both on- and off-axis points is a real check of the claim, not a
    // restatement of it — a wrong sign or a dropped scale in the formula would show up here as a gradient
    // meaningfully above 1.
    [Fact]
    public void GradientNeverExceedsOneAcrossAGridOfExponentsAndPoints() {
        float[] exponents = [2f, 3f, 4f, 5f, 6f, 7f, 8f];
        Vector3[] radiiSet = [
            new Vector3(
                x: 1f,
                y: 1f,
                z: 1f
            ),
            new Vector3(
                x: 1f,
                y: 0.5f,
                z: 1f
            ),
            new Vector3(
                x: 1f,
                y: 0.2f,
                z: 0.6f
            ),
            new Vector3(
                x: 2f,
                y: 0.1f,
                z: 1f
            ),
        ];
        // A grid of directions (not required to sit on the surface — TryFieldGradient probes wherever it lands),
        // spanning axis-aligned, face-diagonal, and general points, both inside and outside each solid.
        double[][] points = [
            [1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0],
            [0.7, 0.7, 0.0], [0.7, 0.0, 0.7], [0.0, 0.7, 0.7],
            [0.5, 0.5, 0.5], [1.5, 0.3, 0.2], [0.2, 1.5, 0.3], [0.3, 0.2, 1.5],
            [2.5, 2.5, 2.5], [0.05, 0.05, 0.05],
        ];
        // The 6-tap central difference has its own truncation/quantization error (documented on
        // SdfFieldEvaluator.GradientEpsilon); this margin absorbs that noise without hiding a real >1 gradient —
        // the analytic proof claims exactly 1, so anything meaningfully above 1.02 would be the proof failing, not
        // the probe.
        const double Tolerance = 1.02;
        var worstCase = 0.0;

        foreach (var exponent in exponents) {
            foreach (var radii in radiiSet) {
                var evaluator = Superellipsoid(
                    exponent: exponent,
                    radii: radii
                );

                foreach (var point in points) {
                    var position = Position(
                        x: point[0],
                        y: point[1],
                        z: point[2]
                    );

                    if (!evaluator.TryFieldGradient(
                        gradient: out var gradient,
                        position: position
                    )) {
                        continue; // A point this evaluator's fixed-point frame cannot express — not a claim about the field.
                    }

                    var length = ((double)gradient.Length);

                    worstCase = Math.Max(
                        val1: worstCase,
                        val2: length
                    );

                    Assert.True(
                        (length <= Tolerance),
                        userMessage: $"e={exponent}, radii={radii}, p=({point[0]},{point[1]},{point[2]}): |grad d| = {length}, over the {Tolerance} tolerance around the proven exact 1."
                    );
                }
            }
        }

        // The proof's bound is tight (attained, not merely an upper bound) — a suite that never gets close to 1
        // would be evidence the grid missed the worst case (the smallest-radius axis), not evidence of a smaller
        // true bound.
        Assert.True(
            (worstCase > 0.9),
            userMessage: $"the grid's worst observed |grad d| was only {worstCase}; it should approach the proven exact 1."
        );
    }
}
