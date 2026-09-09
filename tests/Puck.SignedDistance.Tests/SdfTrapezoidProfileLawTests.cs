using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a trapezoid profile whose slant vector vanishes in the deterministic field's own representation is
/// refused where the shape is admitted, and every profile the door does admit evaluates to a finite distance. The
/// exact 2D core projects onto the slanted side by dividing by that side's squared length, and Q48.16 rounds that
/// length to zero across a whole window of near-degenerate profiles — not just the exactly-degenerate one — so the
/// admission rule is sized to the representation, not to exact equality.
/// <para>Each arm pairs a denial with a control differing in one authored dimension.</para>
/// </summary>
public sealed class SdfTrapezoidProfileLawTests {
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));
    private static SdfProgram Trapezoid(float bottomHalfWidth, float topHalfWidth, float halfHeight) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Trapezoid(
            bottomHalfWidth: bottomHalfWidth,
            halfHeight: halfHeight,
            lift: SdfLift.Revolve,
            liftAmount: 0f,
            material: material,
            topHalfWidth: topHalfWidth
        );

        return builder.Build();
    }
    private static SdfProgram ScaledCone(Vector3 scale) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(
            chain: builder.ResetPoint(),
            material: material,
            scale: scale,
            type: SdfSolidPrimitive.Cone
        );

        return builder.Build();
    }
    // Every point a query could land on around a sliver: inside, on each face, and out along each axis.
    private static void AssertAnswersEverywhere(SdfProgram program) {
        var evaluator = new SdfFieldEvaluator(program: program);

        foreach (var x in new[] { -1d, -0.001d, 0d, 0.001d, 1d }) {
            foreach (var y in new[] { -1d, -0.001d, 0d, 0.001d, 1d }) {
                Assert.True(condition: evaluator.TryDistance(
                    distance: out var distance,
                    material: out _,
                    position: Position(
                        x: x,
                        y: y,
                        z: 0d
                    )
                ));
                Assert.True(
                    condition: (FixedQ4816.Abs(value: distance) < FixedQ4816.FromInteger(value: 1000L)),
                    userMessage: $"the field answered {distance} at ({x}, {y}, 0)"
                );
            }
        }
    }

    [Fact]
    public void AVanishingProfileSlantIsRefusedByName() {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Trapezoid(
            bottomHalfWidth: 1f,
            halfHeight: 0.001f,
            topHalfWidth: 1f
        ));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "slant"
        );
    }
    [Fact]
    public void AProfileSlantPastTheBoundEvaluates() {
        // The same equal-half-width profile at a half-height the representation resolves: 2*0.002 = 0.004 slant.
        AssertAnswersEverywhere(program: Trapezoid(
            bottomHalfWidth: 1f,
            halfHeight: 0.002f,
            topHalfWidth: 1f
        ));
    }
    [Fact]
    public void ATallProfileWithEqualHalfWidthsIsNotDegenerate() {
        // The slant is 2*halfHeight, so a plain cylinder-shaped trapezoid is admitted at any real height: the rule is
        // a conjunction over both profile directions, not a ban on equal half-widths.
        AssertAnswersEverywhere(program: Trapezoid(
            bottomHalfWidth: 1f,
            halfHeight: 1f,
            topHalfWidth: 1f
        ));
    }
    [Fact]
    public void AFlatProfileWithUnequalHalfWidthsIsNotDegenerate() {
        AssertAnswersEverywhere(program: Trapezoid(
            bottomHalfWidth: 1f,
            halfHeight: 0f,
            topHalfWidth: 0f
        ));
    }
    [Fact]
    public void AConeScaledIntoTheDegenerateWindowIsRefusedByName() {
        Assert.False(condition: SdfSolidGeometry.TryValidateScaledPrimitive(
            refusal: out var refusal,
            scale: new Vector3(
                x: 0.0001f,
                y: 0.0002f,
                z: 0.0001f
            ),
            type: SdfSolidPrimitive.Cone
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "cone"
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ScaledCone(scale: new Vector3(
            x: 0.0001f,
            y: 0.0002f,
            z: 0.0001f
        )));
    }
    [Fact]
    public void AConeScaledPastTheDegenerateWindowEmitsAndEvaluates() {
        var scale = new Vector3(
            x: 0.001f,
            y: 0.002f,
            z: 0.001f
        );

        Assert.True(
            condition: SdfSolidGeometry.TryValidateScaledPrimitive(
                refusal: out var refusal,
                scale: scale,
                type: SdfSolidPrimitive.Cone
            ),
            userMessage: refusal
        );
        AssertAnswersEverywhere(program: ScaledCone(scale: scale));
    }
    [Fact]
    public void AUniformlyTinyConeRidesAScaleTransformAndIsAdmitted() {
        // The uniform arm emits the unit primitive under one Scale op, so no authored dimension reaches the shape's
        // own admission rule however small the scale is — the rule must not refuse it.
        var scale = new Vector3(value: 0.0001f);

        Assert.True(
            condition: SdfSolidGeometry.TryValidateScaledPrimitive(
                refusal: out var refusal,
                scale: scale,
                type: SdfSolidPrimitive.Cone
            ),
            userMessage: refusal
        );
        AssertAnswersEverywhere(program: ScaledCone(scale: scale));
    }
    [Fact]
    public void TheAdmissionRuleAgreesWithWhatEmissionAccepts() {
        // The rule and the emission it guards live in two methods; a sweep across both sides of the window, on every
        // primitive, is what keeps them from drifting apart silently.
        float[] steps = [0f, 0.00005f, 0.0001f, 0.0005f, 0.001f, 0.002f, 0.003f, 0.01f, 0.5f, 1f, 7f];

        foreach (var type in Enum.GetValues<SdfSolidPrimitive>()) {
            foreach (var radial in steps) {
                foreach (var axial in steps) {
                    var scale = new Vector3(
                        x: radial,
                        y: axial,
                        z: radial
                    );
                    var admitted = SdfSolidGeometry.TryValidateScaledPrimitive(
                        refusal: out _,
                        scale: scale,
                        type: type
                    );
                    var emitted = true;

                    try {
                        var builder = new SdfProgramBuilder();

                        _ = SdfSolidGeometry.AppendScaledPrimitive(
                            chain: builder.ResetPoint(),
                            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                            scale: scale,
                            type: type
                        );
                        _ = builder.Build();
                    } catch (ArgumentException) {
                        emitted = false;
                    }

                    Assert.True(
                        condition: (admitted == emitted),
                        userMessage: $"{type} at {scale}: the admission rule says {admitted} and emission says {emitted}"
                    );
                }
            }
        }
    }
}
