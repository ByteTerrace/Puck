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
    [Fact]
    public void RoundedProfileControlsCornersWithoutBulgingTheCaps() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        _ = SdfSolidGeometry.AppendScaledPrimitive(builder, SdfSolidPrimitive.Prism, new Vector3(1f, .5f, .1f), material,
            profile: new(SdfPrismProfileKind.RoundedRectangle, CornerRadius: .5f));
        var evaluator = new SdfFieldEvaluator(builder.Build());
        Assert.True(evaluator.TryDistance(Position(0, 0, .1), out var cap, out _));
        Assert.InRange((double)cap, -.0001, .0001);
        Assert.True(evaluator.TryDistance(Position(1, .5, 0), out var corner, out _));
        Assert.True(corner > FixedQ4816.Zero);
        Assert.True(evaluator.TryDistance(Position(.9, 0, 0), out var face, out _));
        Assert.True(face < FixedQ4816.Zero);
    }

    [Theory]
    [InlineData(SdfPrismProfileKind.Polygon, SdfShapeType.RegularPolygon)]
    [InlineData(SdfPrismProfileKind.Ellipse, SdfShapeType.Ellipse)]
    public void ProfileUsesTheExistingRendererInstruction(SdfPrismProfileKind kind, SdfShapeType expected) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        _ = SdfSolidGeometry.AppendScaledPrimitive(builder, SdfSolidPrimitive.Prism, new Vector3(.3f, .2f, .05f), material,
            profile: new(kind, Sides: 8));
        Assert.Contains(builder.Build().Instructions, instruction => instruction.Shape == (uint)expected);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.35f)]
    [InlineData(1f)]
    public void PrismTaperAndExtrusionHaveTheAuthoredSurface(float taper) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        _ = SdfSolidGeometry.AppendScaledPrimitive(builder, SdfSolidPrimitive.Prism,
            new Vector3(2f, 1f, 0.4f), material, taper: taper);
        var evaluator = new SdfFieldEvaluator(builder.Build());
        // Halfway up the profile, the width interpolates linearly between its bottom and top widths.
        var middleWidth = 1d + taper;
        Assert.True(evaluator.TryDistance(Position(middleWidth, 0, 0), out var side, out _));
        Assert.InRange((double)side, -0.0001, 0.0001);
        Assert.True(evaluator.TryDistance(Position(0, 0, 0.4), out var cap, out _));
        Assert.InRange((double)cap, -0.0001, 0.0001);
        Assert.True(evaluator.TryDistance(Position(middleWidth + 0.1, 0, 0), out var outside, out _));
        Assert.True(outside > FixedQ4816.Zero);
        Assert.True(SdfSolidGeometry.Reach(SdfSolidPrimitive.Prism, new Vector3(2f, 1f, .4f)) >= new Vector3(2f, -1f, .4f).Length());
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidPrismTaperIsRefusedAtBothDoors(float taper) {
        Assert.False(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, Vector3.One, out _, taper));
        Assert.Throws<ArgumentOutOfRangeException>(() => SdfSolidGeometry.AppendScaledPrimitive(
            new SdfProgramBuilder(), SdfSolidPrimitive.Prism, Vector3.One, 0, taper: taper));
    }

    [Fact]
    public void ARevolvedPrismIsASolidOfRevolutionAboutY() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        // A revolved rectangle profile at offset 0: radial half-extent 1, axial half-extent 0.5 — a disc, not a slab.
        _ = SdfSolidGeometry.AppendScaledPrimitive(builder, SdfSolidPrimitive.Prism, new Vector3(1f, .5f, 0f), material,
            taper: 1f, lift: SdfLift.Revolve);

        var evaluator = new SdfFieldEvaluator(builder.Build());

        // Rotational symmetry is the whole claim: the same radius reads the same distance on every azimuth.
        foreach (var (x, z) in new[] { (0.9d, 0d), (0d, 0.9d), (-0.9d, 0d), (0.6364d, 0.6364d) }) {
            Assert.True(evaluator.TryDistance(Position(x, 0, z), out var inside, out _));
            Assert.True(inside < FixedQ4816.Zero, userMessage: $"({x}, 0, {z}) read {(double)inside} outside the disc");
        }

        foreach (var (x, z) in new[] { (1.2d, 0d), (0d, 1.2d), (0.8485d, 0.8485d) }) {
            Assert.True(evaluator.TryDistance(Position(x, 0, z), out var outside, out _));
            Assert.True(outside > FixedQ4816.Zero, userMessage: $"({x}, 0, {z}) read {(double)outside} inside the disc");
        }

        // An extrusion of the same profile would be solid out to z = 0 only over |x| <= 1 and hollow nowhere; the
        // discriminating point is above the disc, which a revolve leaves empty and a slab of depth 0 also would —
        // so the axial cap is what separates them.
        Assert.True(evaluator.TryDistance(Position(0, .7, 0), out var above, out _));
        Assert.True(above > FixedQ4816.Zero);
    }

    [Fact]
    public void ARevolvedPrismAtAPositiveOffsetIsARing() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(builder, SdfSolidPrimitive.Prism, new Vector3(.25f, .25f, 2f), material,
            taper: 1f, lift: SdfLift.Revolve);

        var evaluator = new SdfFieldEvaluator(builder.Build());

        Assert.True(evaluator.TryDistance(Position(2, 0, 0), out var onRing, out _));
        Assert.True(onRing < FixedQ4816.Zero);
        // The hole at the axis is what makes it a ring rather than a disc.
        Assert.True(evaluator.TryDistance(Position(0, 0, 0), out var hole, out _));
        Assert.True(hole > FixedQ4816.Zero);
    }

    [Fact]
    public void RoundingFilletsThePrismRimWithoutMovingItsFaces() {
        var scale = new Vector3(1f, 1f, 1f);
        var sharp = Prism(scale, 0f);
        var rounded = Prism(scale, .25f);

        // The faces stay where they were authored: a mid-face point sits on both surfaces.
        foreach (var probe in new[] { Position(0, 0, 1), Position(1, 0, 0) }) {
            Assert.True(sharp.TryDistance(probe, out var sharpFace, out _));
            Assert.True(rounded.TryDistance(probe, out var roundedFace, out _));
            Assert.InRange((double)sharpFace, -.002, .002);
            Assert.InRange((double)roundedFace, -.002, .002);
        }

        // The rim corner the fillet cuts away: inside the sharp prism, outside the rounded one.
        Assert.True(sharp.TryDistance(Position(.99, 0, .99), out var sharpRim, out _));
        Assert.True(rounded.TryDistance(Position(.99, 0, .99), out var roundedRim, out _));
        Assert.True(sharpRim < FixedQ4816.Zero, userMessage: $"the sharp rim read {(double)sharpRim}");
        Assert.True(roundedRim > FixedQ4816.Zero, userMessage: $"the rounded rim read {(double)roundedRim}");
    }

    [Fact]
    public void RoundingIsRefusedPastTheShapeItFits() {
        var scale = new Vector3(1f, 1f, .2f);
        var ceiling = SdfSolidGeometry.MaxRounding(SdfSolidPrimitive.Prism, scale);

        // An extrude spends its half-depth as well as the profile inradius, so 0.2 is the binding constraint here.
        Assert.Equal(.2f, ceiling, tolerance: 1e-6f);
        Assert.True(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, scale, out _, rounding: (ceiling - .01f)));
        Assert.False(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, scale, out var refusal, rounding: (ceiling + .01f)));
        Assert.Contains("edge-rounding", refusal);
    }

    [Fact]
    public void AnUnroundedShapeLeavesTheRoundingLaneAtZero() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(builder, SdfSolidPrimitive.Prism, new Vector3(1f, .5f, .25f), material);
        _ = builder.Cylinder(1f, 2f, material);

        foreach (var instruction in builder.Build().Instructions.Where(instruction => instruction.Op == SdfOp.ShapeBlend)) {
            Assert.Equal(0f, instruction.Data1.W);
        }
    }

    private static SdfFieldEvaluator Prism(Vector3 scale, float rounding) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(builder.ResetPoint(), SdfSolidPrimitive.Prism, scale, material,
            taper: 1f, rounding: rounding);

        return new SdfFieldEvaluator(builder.Build());
    }
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

        // Sweep's reach/admission depend on its own curve, not (only) scale — this scale-driven law does not
            // apply to it; SweepLawTests covers its admission/emission agreement separately.
            foreach (var type in Enum.GetValues<SdfSolidPrimitive>().Where(predicate: (SdfSolidPrimitive candidate) => (candidate != SdfSolidPrimitive.Sweep))) {
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

    [Fact]
    public void RoundingNeverGrowsAThinProfilePastItsAuthoredWidth() {
        // A profile thinner than it is tall: the half-height would admit 0.5, but the inset width reaches zero at 0.1,
        // and flooring it there while offsetting back out by 0.5 would put a point 0.3 off the axis INSIDE a 0.1-wide
        // solid. Both the door's ceiling and the builder's clamp bind at the width.
        var scale = new Vector3(.1f, 1f, 1f);

        Assert.Equal(.1f, SdfSolidGeometry.MaxRounding(SdfSolidPrimitive.Prism, scale, taper: 1f), tolerance: 1e-5f);
        Assert.Equal(.1f, SdfProgramBuilder.TrapezoidRoundingCeiling(.1f, .1f, 1f), tolerance: 1e-5f);
        // A triangle's apex has no room at all.
        Assert.Equal(0f, SdfProgramBuilder.TrapezoidRoundingCeiling(1f, 0f, 1f));

        var sharp = Prism(scale, 0f);
        var rounded = Prism(scale, .5f);

        Assert.True(sharp.TryDistance(Position(.3, 0, 0), out var sharpOutside, out _));
        Assert.True(rounded.TryDistance(Position(.3, 0, 0), out var roundedOutside, out _));
        Assert.InRange((double)sharpOutside, .19, .21);
        Assert.True(roundedOutside > FixedQ4816.Zero, userMessage: $"the rounded prism grew past its width: {(double)roundedOutside}");
        // The clamped fillet still cuts the rim corner the sharp prism keeps.
        Assert.True(sharp.TryDistance(Position(.09, 0, .99), out var sharpRim, out _));
        Assert.True(rounded.TryDistance(Position(.09, 0, .99), out var roundedRim, out _));
        Assert.True(sharpRim < FixedQ4816.Zero, userMessage: $"sharp rim {(double)sharpRim}, rounded rim {(double)roundedRim}, rounded outside {(double)roundedOutside}");
        Assert.True(roundedRim > FixedQ4816.Zero, userMessage: $"sharp rim {(double)sharpRim}, rounded rim {(double)roundedRim}");
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ANonFiniteRoundingIsRefusedByEveryProfileBuilder(float rounding) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Ellipse(1f, .5f, SdfLift.Extrude, 1f, material, rounding: rounding));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.RegularPolygon(6, 1f, SdfLift.Extrude, 1f, material, rounding: rounding));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Trapezoid(1f, .5f, 1f, SdfLift.Extrude, 1f, material, rounding: rounding));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.RoundedRectangle(1f, .5f, .1f, SdfLift.Extrude, 1f, material, rounding: rounding));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.ChamferedRectangle(1f, .5f, .1f, SdfLift.Extrude, 1f, material, rounding: rounding));
        // The control: the same calls with a finite radius emit.
        _ = builder.Ellipse(1f, .5f, SdfLift.Extrude, 1f, material, rounding: .1f);
        _ = builder.RegularPolygon(6, 1f, SdfLift.Extrude, 1f, material, rounding: .1f);
        _ = builder.Trapezoid(1f, .5f, 1f, SdfLift.Extrude, 1f, material, rounding: .1f);
        _ = builder.RoundedRectangle(1f, .5f, .1f, SdfLift.Extrude, 1f, material, rounding: .1f);
        _ = builder.ChamferedRectangle(1f, .5f, .1f, SdfLift.Extrude, 1f, material, rounding: .1f);
        Assert.Equal(5, builder.Build().Instructions.Count(instruction => instruction.Op == SdfOp.ShapeBlend));
    }
}
