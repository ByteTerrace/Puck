using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: <see cref="SdfShapeType.ChamferedRectangle"/> is a 45-degree bevel of a rectangle's corners AND — for an
/// extrude — its two cap rims (the field exact inside and on the surface, a lower bound outside), and a chamfer of
/// zero is the identity: every consumer (the builder, the fixed evaluator, and the authoring doors in
/// <see cref="SdfSolidGeometry"/>) reduces to the plain box/slab form exactly.
/// </summary>
public sealed class ChamferLawTests {
    private static FixedPosition Position(double x, double y, double z) =>
        FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y),
            Z: FixedQ4816.FromDouble(value: z)
        ));
    private static SdfFieldEvaluator ChamferedBox(float halfWidth, float halfHeight, float chamfer, float liftAmount, SdfLift lift = SdfLift.Extrude, float rounding = 0f) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ChamferedRectangle(
            blend: SdfBlendOp.Union,
            chamfer: chamfer,
            halfHeight: halfHeight,
            halfWidth: halfWidth,
            lift: lift,
            liftAmount: liftAmount,
            material: material,
            rounding: rounding
        );

        return new SdfFieldEvaluator(program: builder.Build());
    }
    // The exact reference: q = abs(p) - b; max(box, (q.x+q.y+c)*sqrt(1/2)) — mirrors sdfChamferBox2D/sdfChamferedRect
    // in Assets/Shaders/Sdf/sdf-vm.hlsli, computed in double so a test failure indicts the evaluator, not this helper.
    private const double SqrtHalf = 0.70710678118654752440;
    private static double ChamferBox2D(double px, double py, double halfWidth, double halfHeight, double chamfer) {
        var qx = (Math.Abs(px) - halfWidth);
        var qy = (Math.Abs(py) - halfHeight);
        var boxDistance = (Math.Min(Math.Max(qx, qy), 0.0) + new Vector2(x: (float)Math.Max(qx, 0.0), y: (float)Math.Max(qy, 0.0)).Length());
        var bevel = ((qx + qy + chamfer) * SqrtHalf);

        return Math.Max(boxDistance, bevel);
    }
    // The uniform-scale emission path rides one Scale transform over the unit primitive; the non-uniform path bakes
    // its dimensions natively and emits none.
    private static float UniformScale(SdfProgram program) =>
        program.Instructions.Where(instruction => instruction.Op == SdfOp.Scale).Select(instruction => instruction.Data0.X).DefaultIfEmpty(1f).Single();
    private static double ExtrudeChamfer2D(double distance2D, double pz, double halfDepth, double chamfer) {
        var wx = distance2D;
        var wy = (Math.Abs(pz) - halfDepth);
        var plain = (Math.Min(Math.Max(wx, wy), 0.0) + new Vector2(x: (float)Math.Max(wx, 0.0), y: (float)Math.Max(wy, 0.0)).Length());
        var bevel = ((wx + wy + chamfer) * SqrtHalf);

        return Math.Max(plain, bevel);
    }

    [Fact]
    public void ChamferCutsTheDiagonalCornerWithoutMovingTheFlatFaces() {
        var evaluator = ChamferedBox(halfWidth: 1f, halfHeight: 1f, chamfer: .3f, liftAmount: 10f);

        // The flat +X face is unaffected: (q.x + q.y + c)*sqrt(1/2) = (0 - 1 + .3)*sqrt(1/2) < 0 = the box distance.
        Assert.True(evaluator.TryDistance(Position(1, 0, 0), out var face, out _));
        Assert.InRange((double)face, -.0002, .0002);

        // The diagonal corner (0.9, 0.9) sits INSIDE the sharp box (distance -0.1) but OUTSIDE the chamfered one:
        // the bevel plane x + y = 2 - .3 = 1.7 cuts it off.
        Assert.True(evaluator.TryDistance(Position(.9, .9, 0), out var corner, out _));
        Assert.True(corner > FixedQ4816.Zero, userMessage: $"the chamfer did not cut the corner: {(double)corner}");
        Assert.InRange((double)corner, .06, .09);
    }

    [Fact]
    public void ZeroChamferIsTheExactBoxIdentity() {
        var sharp = ChamferedBox(halfWidth: 1.3f, halfHeight: .7f, chamfer: 0f, liftAmount: .5f);

        foreach (var point in new[] { Position(0, 0, 0), Position(1.3, .7, .5), Position(.9, .9, .4), Position(2, 0, 0), }) {
            Assert.True(sharp.TryDistance(point, out var chamfered, out _));

            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.Box(halfExtents: new Vector3(1.3f, .7f, .5f), material: material, round: 0f);

            var box = new SdfFieldEvaluator(program: builder.Build());

            Assert.True(box.TryDistance(point, out var plain, out _));
            Assert.Equal(expected: plain, actual: chamfered);
        }
    }

    [Fact]
    public void ExtrudeBevelsTheCapRimAtTheSameChamferAsTheProfile() {
        const float chamfer = .3f;
        var chamfered = ChamferedBox(halfWidth: 1f, halfHeight: 1f, chamfer: chamfer, liftAmount: 1f);
        var sharp = ChamferedBox(halfWidth: 1f, halfHeight: 1f, chamfer: 0f, liftAmount: 1f);

        // Near the +X face, centered on Y, just past the cap plane: the 2D profile itself is untouched here
        // (q.y = 0 - 1 = -1, far from its own corner), so any difference from the sharp shape is the CAP join alone.
        var expectedChamfered = ExtrudeChamfer2D(distance2D: ChamferBox2D(.9, 0, 1, 1, chamfer), pz: 1.05, halfDepth: 1, chamfer: chamfer);
        var expectedSharp = ExtrudeChamfer2D(distance2D: ChamferBox2D(.9, 0, 1, 1, 0f), pz: 1.05, halfDepth: 1, chamfer: 0f);

        Assert.True(chamfered.TryDistance(Position(.9, 0, 1.05), out var chamferedDistance, out _));
        Assert.True(sharp.TryDistance(Position(.9, 0, 1.05), out var sharpDistance, out _));
        Assert.InRange((double)chamferedDistance, (expectedChamfered - .0005), (expectedChamfered + .0005));
        Assert.InRange((double)sharpDistance, (expectedSharp - .0005), (expectedSharp + .0005));
        Assert.True(chamferedDistance > sharpDistance, userMessage: $"the cap rim did not bevel: chamfered {(double)chamferedDistance}, sharp {(double)sharpDistance}");
    }

    [Fact]
    public void RevolveIgnoresTheCapJoinItHasNoSeamFor() {
        // A revolve has no Z-slab cap, so the whole solid comes from the 2D profile alone; two shapes differing only
        // in liftAmount (the revolve offset) still agree everywhere the offset does not reach.
        var evaluator = ChamferedBox(halfWidth: 1f, halfHeight: .3f, chamfer: .2f, liftAmount: 0f, lift: SdfLift.Revolve);

        Assert.True(evaluator.TryDistance(Position(0, 0, 0), out var axis, out _));
        Assert.True(axis < FixedQ4816.Zero, userMessage: $"expected the axis to sit inside the solid of revolution: {(double)axis}");
    }

    [Theory]
    [InlineData(.15, -.5, 0)]
    [InlineData(.9, .1, .2)]
    [InlineData(-.4, .95, .1)]
    [InlineData(1.2, 1.2, .3)]
    public void FixedEvaluatorAgreesWithTheFloatReferenceWithinTolerance(double x, double y, double z) {
        const float halfWidth = 1f;
        const float halfHeight = .8f;
        const float chamfer = .25f;
        const float liftAmount = .6f;
        var evaluator = ChamferedBox(halfWidth: halfWidth, halfHeight: halfHeight, chamfer: chamfer, liftAmount: liftAmount);
        var expected = ExtrudeChamfer2D(
            distance2D: ChamferBox2D(x, y, halfWidth, halfHeight, chamfer),
            pz: z,
            halfDepth: liftAmount,
            chamfer: chamfer
        );

        Assert.True(evaluator.TryDistance(Position(x, y, z), out var actual, out _));
        Assert.InRange((double)actual, (expected - .001), (expected + .001));
    }

    [Fact]
    public void RoundingErodesTheAlreadyChamferedProfileByTheSharedIsotropicFormula() {
        // Isotropic erosion of an intersection of half-planes shifts each constraint's offset by the SAME radius
        // along its own normal: the two rectangle faces shrink by round directly, and the chamfer plane's offset
        // (halfWidth + halfHeight - c)/sqrt(2) shifts by round too, which back-solves to c' = c - round*(2 - sqrt(2)).
        const float halfWidth = 1f;
        const float halfHeight = 1f;
        const float chamfer = .3f;
        const float round = .1f;
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ChamferedRectangle(halfWidth, halfHeight, chamfer, SdfLift.Extrude, 10f, material, rounding: round);

        var instruction = builder.Build().Instructions.Single(instruction => instruction.Op == SdfOp.ShapeBlend);
        var expectedChamfer = (chamfer - (round * (2f - MathF.Sqrt(2f))));

        Assert.Equal(expected: (halfWidth - round), actual: instruction.Data0.X, precision: 5);
        Assert.Equal(expected: (halfHeight - round), actual: instruction.Data0.Y, precision: 5);
        Assert.Equal(expected: expectedChamfer, actual: instruction.Data0.Z, precision: 5);
        Assert.Equal(expected: round, actual: instruction.Data1.W, precision: 5);

        // The clamp: a rounding request past ChamferedRectangleInradius(halfWidth, halfHeight, chamfer) narrows to
        // it rather than eroding the profile to nothing or negative.
        var overBuilder = new SdfProgramBuilder();
        var overMaterial = overBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = overBuilder.ChamferedRectangle(halfWidth, halfHeight, chamfer, SdfLift.Extrude, 10f, overMaterial, rounding: 10f);

        var overInstruction = overBuilder.Build().Instructions.Single(instruction => instruction.Op == SdfOp.ShapeBlend);
        var inradius = SdfProgramBuilder.ChamferedRectangleInradius(halfWidth, halfHeight, chamfer);

        Assert.Equal(expected: inradius, actual: overInstruction.Data1.W, precision: 5);
        Assert.True(overInstruction.Data0.X >= 0f);
        Assert.True(overInstruction.Data0.Y >= 0f);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ANonFiniteChamferIsRefused(float chamfer) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.ChamferedRectangle(1f, .5f, chamfer, SdfLift.Extrude, 1f, material));
        // The control: the same call with a finite chamfer emits.
        _ = builder.ChamferedRectangle(1f, .5f, .1f, SdfLift.Extrude, 1f, material);
        Assert.Single(builder.Build().Instructions, instruction => instruction.Op == SdfOp.ShapeBlend);
    }

    [Fact]
    public void ChamferAtTheHalfExtentDegeneratesToADiamondRatherThanRefusing() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        // chamfer == min(halfWidth, halfHeight) is ClampChamfer's own ceiling — allowed, not refused.
        _ = builder.ChamferedRectangle(1f, .6f, .6f, SdfLift.Extrude, 5f, material);

        var evaluator = new SdfFieldEvaluator(program: builder.Build());

        Assert.True(evaluator.TryDistance(Position(.5, .5, 0), out var distance, out _));
        Assert.InRange((double)distance, -5.0, 5.0);
    }

    [Fact]
    public void SdfSolidGeometryEmitsChamferedRectangleForABoxOrRevolvedCylinder() {
        var boxBuilder = new SdfProgramBuilder();
        var boxMaterial = boxBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(boxBuilder, SdfSolidPrimitive.Box, new Vector3(1f, 1f, 1f), boxMaterial, chamfer: .2f);
        Assert.Contains(boxBuilder.Build().Instructions, instruction => instruction.Shape == (uint)SdfShapeType.ChamferedRectangle);

        var cylinderBuilder = new SdfProgramBuilder();
        var cylinderMaterial = cylinderBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(cylinderBuilder, SdfSolidPrimitive.Cylinder, new Vector3(1f, 1f, 1f), cylinderMaterial, chamfer: .2f);
        Assert.Contains(cylinderBuilder.Build().Instructions, instruction => instruction.Shape == (uint)SdfShapeType.ChamferedRectangle);

        // The control: a zero chamfer keeps the native Box/Cylinder shapes.
        var plainBuilder = new SdfProgramBuilder();
        var plainMaterial = plainBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(plainBuilder, SdfSolidPrimitive.Box, new Vector3(1f, 1f, 1f), plainMaterial);
        Assert.Contains(plainBuilder.Build().Instructions, instruction => instruction.Shape == (uint)SdfShapeType.Box);
    }

    [Theory]
    [InlineData(0.4f)] // past MaxChamfer(Box, (1,1,1)) = 1
    public void ChamferPastMaxChamferIsRefusedAtTheAuthoringDoor(float overCeilingScale) {
        var scale = new Vector3(1f, 1f, 1f);

        Assert.True(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Box, scale, out _, chamfer: 1f));
        Assert.False(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Box, scale, out var refusal, chamfer: (1f + overCeilingScale)));
        Assert.Contains("chamfer", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChamferOnAPolygonProfilePrismHasNoRoomBecauseTheLaneIsTaken() {
        var scale = new Vector3(1f, 1f, .5f);
        var profile = new SdfPrismProfile(SdfPrismProfileKind.Polygon, Sides: 6);

        Assert.Equal(0f, SdfSolidGeometry.MaxChamfer(SdfSolidPrimitive.Prism, scale, profile: profile));
        Assert.False(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, scale, out var refusal, profile: profile, chamfer: .1f));
        Assert.Contains("chamfer", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChamferOnARevolvedPrismHasNoCapSeamToBevel() {
        var scale = new Vector3(1f, 1f, 0f);

        Assert.Equal(0f, SdfSolidGeometry.MaxChamfer(SdfSolidPrimitive.Prism, scale, lift: SdfLift.Revolve));
        Assert.False(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, scale, out var refusal, lift: SdfLift.Revolve, chamfer: .1f));
        Assert.Contains("chamfer", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AChamferPastTheExtrudeHalfHeightClampsToItSoTheMidPlaneOutlineStays() {
        // The cap bevel rides the same c as the profile: at z = 0 it is (d - h + c)/sqrt2, positive wherever
        // d > h - c, so a chamfer past the half-height would cut the side faces and shrink the authored outline.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.ChamferedRectangle(1f, 1f, .5f, SdfLift.Extrude, .25f, material);

        var instruction = builder.Build().Instructions.Single(instruction => instruction.Op == SdfOp.ShapeBlend);

        Assert.Equal(expected: .25f, actual: instruction.Data0.Z);
        Assert.Equal(expected: .25f, actual: SdfProgramBuilder.ClampChamfer(.5f, 1f, 1f, SdfLift.Extrude, .25f));

        var evaluator = new SdfFieldEvaluator(program: builder.Build());

        // The +X side face still sits at the authored half-width on the mid-plane.
        Assert.True(evaluator.TryDistance(Position(1, 0, 0), out var face, out _));
        Assert.InRange((double)face, -.0002, .0002);

        // The control: a revolve has no cap seam, so its chamfer keeps the profile's own ceiling.
        Assert.Equal(expected: .5f, actual: SdfProgramBuilder.ClampChamfer(.5f, 1f, 1f, SdfLift.Revolve, .25f));
    }

    [Fact]
    public void ATriangularPrismAdmitsACapChamferUpToItsInradiusWhereItAdmitsNoRounding() {
        // Base 2, height 2 (taper 0 at unit scale): inradius = area / semi-perimeter = 2 / (1 + sqrt5).
        var scale = new Vector3(1f, 1f, 1f);
        var inradius = (2f / (1f + MathF.Sqrt(5f)));

        Assert.Equal(inradius, SdfProgramBuilder.TrapezoidInradius(1f, 0f, 1f), tolerance: 1e-5f);
        Assert.Equal(inradius, SdfSolidGeometry.MaxChamfer(SdfSolidPrimitive.Prism, scale, taper: 0f), tolerance: 1e-5f);
        // The rounding lane cannot represent an eroded triangle, so ITS ceiling is zero — the two are not one number.
        Assert.Equal(0f, SdfSolidGeometry.MaxRounding(SdfSolidPrimitive.Prism, scale, taper: 0f));

        Assert.True(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, scale, out _, taper: 0f, chamfer: (inradius - .01f)));
        Assert.False(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, scale, out var refusal, taper: 0f, chamfer: (inradius + .01f)));
        Assert.Contains("chamfer", refusal, StringComparison.OrdinalIgnoreCase);

        // The builder clamps to the same inradius, and the cap chamfer reaches the packed lane.
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(builder, SdfSolidPrimitive.Prism, scale, material, taper: 0f, chamfer: 10f);

        var instruction = builder.Build().Instructions.Single(instruction => instruction.Op == SdfOp.ShapeBlend);

        Assert.Equal(inradius, instruction.Data1.Z, tolerance: 1e-5f);

        // A rectangle profile's inradius is its half-height; the extrude half-depth binds under it.
        Assert.Equal(1f, SdfProgramBuilder.TrapezoidInradius(1f, 1f, 1f), tolerance: 1e-6f);
        Assert.Equal(.2f, SdfSolidGeometry.MaxChamfer(SdfSolidPrimitive.Prism, new Vector3(1f, 1f, .2f), taper: 1f), tolerance: 1e-6f);
    }

    [Theory]
    [InlineData(1f, 1f, 1f)]
    [InlineData(1f, .5f, .25f)]
    public void ADocumentBoxChamferKeepsThePlainBoxZeroSetAndFillet(float x, float y, float z) {
        // The plain Box's zero set is its packed half-extents (the decoder insets them by the round and offsets the
        // field back out) under a BoxRound*min(scale) fillet; read both off its own packed instruction rather than
        // restating the constant here.
        var scale = new Vector3(x, y, z);
        var plainBuilder = new SdfProgramBuilder();
        var plainMaterial = plainBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(plainBuilder, SdfSolidPrimitive.Box, scale, plainMaterial);

        var plainProgram = plainBuilder.Build();
        var plain = plainProgram.Instructions.Single(instruction => instruction.Op == SdfOp.ShapeBlend);
        var plainScale = UniformScale(plainProgram);
        var fillet = (plain.Data0.W * plainScale);
        var extents = (new Vector3(plain.Data0.X, plain.Data0.Y, plain.Data0.Z) * plainScale);

        Assert.True(fillet > 0f);

        var chamferedBuilder = new SdfProgramBuilder();
        var chamferedMaterial = chamferedBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(chamferedBuilder, SdfSolidPrimitive.Box, scale, chamferedMaterial, chamfer: .1f);

        var chamferedProgram = chamferedBuilder.Build();
        var chamfered = chamferedProgram.Instructions.Single(instruction => instruction.Op == SdfOp.ShapeBlend);
        var chamferedScale = UniformScale(chamferedProgram);

        Assert.Equal((uint)SdfShapeType.ChamferedRectangle, chamfered.Shape);
        Assert.Equal(fillet, (chamfered.Data1.W * chamferedScale), tolerance: 1e-6f);
        Assert.Equal(extents.X, ((chamfered.Data0.X + chamfered.Data1.W) * chamferedScale), tolerance: 1e-5f);
        Assert.Equal(extents.Y, ((chamfered.Data0.Y + chamfered.Data1.W) * chamferedScale), tolerance: 1e-5f);
        Assert.Equal(extents.Z, ((chamfered.Data0.W + chamfered.Data1.W) * chamferedScale), tolerance: 1e-5f);

        // The fields agree on the face centres: the chamfered Box's zero set is the plain Box's.
        var plainEvaluator = new SdfFieldEvaluator(program: plainProgram);
        var chamferedEvaluator = new SdfFieldEvaluator(program: chamferedProgram);

        foreach (var point in new[] { Position(extents.X, 0, 0), Position(0, extents.Y, 0), Position(0, 0, extents.Z), }) {
            Assert.True(plainEvaluator.TryDistance(point, out var plainDistance, out _));
            Assert.True(chamferedEvaluator.TryDistance(point, out var chamferedDistance, out _));
            Assert.InRange((double)plainDistance, -.0005, .0005);
            Assert.InRange((double)chamferedDistance, -.0005, .0005);
        }

        // And the chamfer is still there: the corner the plain (filleted) Box keeps is cut away.
        var corner = Position((extents.X - .02), (extents.Y - .02), 0);

        Assert.True(plainEvaluator.TryDistance(corner, out var plainCorner, out _));
        Assert.True(chamferedEvaluator.TryDistance(corner, out var chamferedCorner, out _));
        Assert.True(chamferedCorner > plainCorner, userMessage: $"the chamfer did not cut the corner: plain {(double)plainCorner}, chamfered {(double)chamferedCorner}");
    }

    [Fact]
    public void ADocumentBoxChamferVanishingIntoTheFilletIsThePlainBox() {
        // A chamfer under fillet*(2 - sqrt2) sits inside the corner the fillet already removes, so the opening
        // absorbs it: the emitted shape IS the plain filleted Box, and chamfer -> 0 is continuous at the door.
        var scale = new Vector3(1f, .5f, .25f);
        var plainBuilder = new SdfProgramBuilder();
        var plainMaterial = plainBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(plainBuilder, SdfSolidPrimitive.Box, scale, plainMaterial);

        var chamferedBuilder = new SdfProgramBuilder();
        var chamferedMaterial = chamferedBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(chamferedBuilder, SdfSolidPrimitive.Box, scale, chamferedMaterial, chamfer: 1e-4f);

        var chamferedProgram = chamferedBuilder.Build();

        Assert.Equal(0f, chamferedProgram.Instructions.Single(instruction => instruction.Op == SdfOp.ShapeBlend).Data0.Z);

        var plainEvaluator = new SdfFieldEvaluator(program: plainBuilder.Build());
        var chamferedEvaluator = new SdfFieldEvaluator(program: chamferedProgram);

        foreach (var point in new[] { Position(0, 0, 0), Position(1.2, .6, .3), Position(.9, .45, 0), Position(2, 0, 0), Position(1.03, .51, .26), }) {
            Assert.True(plainEvaluator.TryDistance(point, out var plainDistance, out _));
            Assert.True(chamferedEvaluator.TryDistance(point, out var chamferedDistance, out _));
            Assert.InRange((double)chamferedDistance, ((double)plainDistance - .0005), ((double)plainDistance + .0005));
        }
    }

    [Fact]
    public void AChamferedRectangleProfileThickerThanItsExtrudeIsRefusedByName() {
        var scale = new Vector3(.5f, .5f, .1f);
        var thick = new SdfPrismProfile(SdfPrismProfileKind.ChamferedRectangle, CornerRadius: .5f); // c = .25 > .1

        Assert.False(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, scale, out var refusal, profile: thick));
        Assert.Contains("chamfered-rectangle profile", refusal, StringComparison.Ordinal);
        // The controls: a chamfer the half-depth carries, and a revolve (no cap seam), both admit.
        Assert.True(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, scale, out _, profile: new(SdfPrismProfileKind.ChamferedRectangle, CornerRadius: .1f)));
        Assert.True(SdfSolidGeometry.TryValidateScaledPrimitive(SdfSolidPrimitive.Prism, scale, out _, profile: thick, lift: SdfLift.Revolve));
    }

    [Fact]
    public void PrismCapChamferBevelsItsTrapezoidProfileRims() {
        var chamfered = new SdfProgramBuilder();
        var chamferedMaterial = chamfered.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(chamfered, SdfSolidPrimitive.Prism, new Vector3(1f, 1f, 1f), chamferedMaterial, taper: 1f, chamfer: .3f);

        var sharp = new SdfProgramBuilder();
        var sharpMaterial = sharp.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = SdfSolidGeometry.AppendScaledPrimitive(sharp, SdfSolidPrimitive.Prism, new Vector3(1f, 1f, 1f), sharpMaterial, taper: 1f);

        var chamferedEvaluator = new SdfFieldEvaluator(program: chamfered.Build());
        var sharpEvaluator = new SdfFieldEvaluator(program: sharp.Build());

        Assert.True(chamferedEvaluator.TryDistance(Position(.9, 0, 1.05), out var chamferedDistance, out _));
        Assert.True(sharpEvaluator.TryDistance(Position(.9, 0, 1.05), out var sharpDistance, out _));
        Assert.True(chamferedDistance > sharpDistance, userMessage: $"the prism's cap rim did not bevel: chamfered {(double)chamferedDistance}, sharp {(double)sharpDistance}");
    }
}
