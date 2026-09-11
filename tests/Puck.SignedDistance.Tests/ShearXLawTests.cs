using System.Numerics;

using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The conservativeness contract of <see cref="SdfOp.Shear"/>: the reach-dependent operator-norm bound
/// (<c>TwistOperatorNorm(|linear| + 2·|quadratic|·reach)</c>) mirrored against the packed step scale, a zero-shear
/// identity, an exact-everywhere point check (not just a local linearization), a finite-difference proof the sheared
/// field never changes faster than true distance can over a grid, and the warp-free evaluator's refusal by name.
/// </summary>
public sealed class ShearLawTests {
    private static SdfProgram BuildShearedSphere(float linear, float quadratic, float radius = 1.0f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder
            .ResetPoint()
            .Shear(linear: linear, quadratic: quadratic)
            .Sphere(
                radius: radius,
                material: material
            )
            .Build();
    }
    // Reproduces SDF_OP_SHEAR's mapCore case plus the trailing stepScale multiply exactly.
    private static float ShearedFieldDistance(Vector3 p, float linear, float quadratic, float radius, float stepScale) {
        var sheared = new Vector3(
            x: (p.X + ((linear * p.Y) + ((quadratic * p.Y) * p.Y))),
            y: p.Y,
            z: p.Z
        );

        return ((sheared.Length() - radius) * stepScale);
    }
    // KEEP IN SYNC with SdfProgram.TwistOperatorNorm (the shared formula ShearOperatorNorm delegates to).
    private static float TwistOperatorNorm(float a) {
        var aSquared = (a * a);

        return MathF.Sqrt(x: (((2.0f + aSquared) + (a * MathF.Sqrt(x: (aSquared + 4.0f)))) / 2.0f));
    }
    // KEEP IN SYNC with SdfProgram.ShearOperatorNorm.
    private static float MirrorOperatorNorm(float linear, float quadratic, float reach) =>
        TwistOperatorNorm(a: (MathF.Abs(x: linear) + ((2.0f * MathF.Abs(x: quadratic)) * reach)));

    [Fact]
    public void TheStepClampMatchesTheDerivedOperatorNorm() {
        const float Linear = 0.4f;
        const float Quadratic = 0.6f;
        const float Radius = 1.0f;

        var program = BuildShearedSphere(
            linear: Linear,
            quadratic: Quadratic
        );
        // The chain's reach is the sphere's own bounding radius (no Translate on the chain).
        var expected = (1.0f / MathF.Max(
            x: MirrorOperatorNorm(
                linear: Linear,
                quadratic: Quadratic,
                reach: Radius
            ),
            y: 1.0f
        ));

        Assert.Equal(
            actual: program.StepScale,
            expected: expected,
            precision: 5
        );
        Assert.True(condition: (program.StepScale < 1.0f));
    }
    [Fact]
    public void ZeroLinearAndQuadraticIsAnExactIdentity() {
        var program = BuildShearedSphere(
            linear: 0f,
            quadratic: 0f
        );

        Assert.Equal(
            actual: program.StepScale,
            expected: 1.0f
        );

        Vector3[] points = [
            new(x: 3f, y: 0f, z: 0f),
            new(x: 0f, y: 5f, z: 0f),
            new(x: -2f, y: -4f, z: 1.5f),
            new(x: 0.2f, y: 0.5f, z: -0.3f),
        ];

        foreach (var point in points) {
            var expected = (point.Length() - 1.0f);
            var actual = ShearedFieldDistance(
                p: point,
                linear: 0f,
                quadratic: 0f,
                radius: 1.0f,
                stepScale: program.StepScale
            );

            Assert.Equal(
                actual: actual,
                expected: expected
            );
        }
    }
    [Fact]
    public void ThePolynomialIsExactAtAnArbitraryPointNotOnlyNearTheOrigin() {
        // Unlike Bend/Twist (small-angle rotations), Shear's formula is the shape's map EXACTLY, everywhere — so a
        // point far from the origin obeys the same closed form as one near it, with no accumulated approximation.
        const float Linear = 1.3f;
        const float Quadratic = -0.7f;
        var p = new Vector3(x: 2f, y: 6f, z: -3f);
        var expectedSheared = new Vector3(
            x: (p.X + ((Linear * p.Y) + (Quadratic * p.Y * p.Y))),
            y: p.Y,
            z: p.Z
        );

        var actual = ShearedFieldDistance(
            p: p,
            linear: Linear,
            quadratic: Quadratic,
            radius: 1.0f,
            stepScale: 1.0f
        );

        Assert.Equal(
            actual: actual,
            expected: (expectedSheared.Length() - 1.0f),
            precision: 5
        );
    }
    // Confined to roughly the chain's own reach (the sphere's radius, the SAME rho ShearOperatorNorm bounds the
    // warp's local slope over) — unlike AxialProfile's bounded profile (clamped outside its span, so its own grid test
    // safely runs far past reach), Shear's polynomial grows without bound in y exactly like Bend/Twist's rotation
    // angle does, so the derived bound is proven only within the reach it was computed against, not at an
    // arbitrarily distant y.
    [Fact]
    public void TheShearedFieldStaysAConservativeLowerBoundOnAGrid() {
        const float Linear = 0.5f;
        const float Quadratic = 0.3f;
        const float Radius = 1.0f;
        const float Step = 0.2f;
        const float Tolerance = 5.0e-4f;

        var program = BuildShearedSphere(
            linear: Linear,
            quadratic: Quadratic
        );
        var stepScale = program.StepScale;
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

        for (var x = -1.2f; (x <= 1.2f); x += Step) {
            for (var y = -1.2f; (y <= 1.2f); y += Step) {
                for (var z = -1.2f; (z <= 1.2f); z += Step) {
                    var p = new Vector3(x: x, y: y, z: z);
                    var d0 = ShearedFieldDistance(
                        p: p,
                        linear: Linear,
                        quadratic: Quadratic,
                        radius: Radius,
                        stepScale: stepScale
                    );

                    foreach (var axis in axes) {
                        var d1 = ShearedFieldDistance(
                            p: (p + (axis * Step)),
                            linear: Linear,
                            quadratic: Quadratic,
                            radius: Radius,
                            stepScale: stepScale
                        );

                        Assert.True(condition: (MathF.Abs(x: (d1 - d0)) <= (Step + Tolerance)));
                    }
                }
            }
        }
    }
    [Fact]
    public void ANonFiniteLinearRefusesByName() {
        var builder = new SdfProgramBuilder();
        var exception = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => builder.Shear(
            linear: float.NaN,
            quadratic: 0f
        ));

        Assert.Equal(
            actual: exception.ParamName,
            expected: "linear"
        );
    }
    [Fact]
    public void TheWarpFreeEvaluatorRefusesTheOpByName() {
        var program = BuildShearedSphere(
            linear: 0.5f,
            quadratic: 0.2f
        );
        var exception = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "Shear"
        );
    }
}
