using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: every authored primitive, lowered through the document door at a centimetre and at a metre, reads a field
/// whose change between two nearby points never exceeds their distance over the evaluator's step scale, beyond a few
/// raws of rounding. A field that moves faster overstates the distance somewhere, and a march or a certified contact
/// sweep that trusts it steps through the surface. Rounding counts too: a centimetre superellipsoid whose ratio rounded
/// a product of two centimetres to the carrier read a millimetre of noise, 26 times its bracketed distance from its own
/// surface.
/// </summary>
public sealed class SdfPrimitiveLipschitzLawTests {
    // Positions and every primitive's last operations round to the carrier; four raws cover both ends of a pair.
    private const double Slack = (4.0 / 65536.0);

    public static TheoryData<SdfSolidPrimitive, float, float, float, SdfPrismProfileKind> Primitives() {
        var data = new TheoryData<SdfSolidPrimitive, float, float, float, SdfPrismProfileKind>();

        foreach (var type in Enum.GetValues<SdfSolidPrimitive>()) {
            if (type == SdfSolidPrimitive.Sweep) { continue; }

            data.Add(type, 2f, 0f, 0f, SdfPrismProfileKind.Trapezoid);
        }
        data.Add(SdfSolidPrimitive.Superellipsoid, 3f, 0f, 0f, SdfPrismProfileKind.Trapezoid);
        data.Add(SdfSolidPrimitive.Superellipsoid, 8f, 0f, 0f, SdfPrismProfileKind.Trapezoid);
        data.Add(SdfSolidPrimitive.Box, 2f, 0.2f, 0f, SdfPrismProfileKind.Trapezoid);
        data.Add(SdfSolidPrimitive.Box, 2f, 0f, 0.2f, SdfPrismProfileKind.Trapezoid);
        data.Add(SdfSolidPrimitive.Cylinder, 2f, 0.2f, 0f, SdfPrismProfileKind.Trapezoid);
        data.Add(SdfSolidPrimitive.Cylinder, 2f, 0f, 0.2f, SdfPrismProfileKind.Trapezoid);
        data.Add(SdfSolidPrimitive.Prism, 2f, 0.2f, 0f, SdfPrismProfileKind.Trapezoid);
        // The convex profile rounds by its own corner radius, never the door's rounding.
        foreach (var kind in (ReadOnlySpan<SdfPrismProfileKind>)[SdfPrismProfileKind.RoundedRectangle, SdfPrismProfileKind.Polygon, SdfPrismProfileKind.Ellipse, SdfPrismProfileKind.ChamferedRectangle, SdfPrismProfileKind.Convex]) {
            data.Add(SdfSolidPrimitive.Prism, 2f, 0f, 0f, kind);
            if (kind != SdfPrismProfileKind.Convex) { data.Add(SdfSolidPrimitive.Prism, 2f, 0.2f, 0f, kind); }
        }

        return data;
    }

    [Theory]
    [MemberData(memberName: nameof(Primitives))]
    public void APrimitiveMovesNoFasterThanItsStepScaleAtACentimetreAndAMetre(SdfSolidPrimitive type, float exponent, float roundingShare, float chamferShare, SdfPrismProfileKind kind) {
        var checkedScales = 0;
        // A clockwise pentagon inside the unit square for the convex profile; the trapezoid is the door's default.
        var profile = ((kind == SdfPrismProfileKind.Trapezoid)
            ? null
            : new SdfPrismProfile(Kind: kind, Vertices: ((kind == SdfPrismProfileKind.Convex) ? [new Vector2(x: 0f, y: 1f), new Vector2(x: 0.9f, y: 0.3f), new Vector2(x: 0.6f, y: -0.8f), new Vector2(x: -0.6f, y: -0.8f), new Vector2(x: -0.9f, y: 0.3f)] : null)));

        foreach (var scale in (ReadOnlySpan<Vector3>)[new Vector3(x: 0.019f, y: 0.012f, z: 0.01f), new Vector3(value: 0.012f), new Vector3(x: 1f, y: 0.6f, z: 0.8f)]) {
            var smallest = MathF.Min(x: scale.X, y: MathF.Min(x: scale.Y, y: scale.Z));
            var rounding = (roundingShare * smallest);
            var chamfer = (chamferShare * smallest);

            if (!SdfSolidGeometry.TryValidateScaledPrimitive(chamfer: chamfer, exponent: exponent, profile: profile, refusal: out _, rounding: rounding, scale: scale, type: type)) { continue; }

            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = SdfSolidGeometry.AppendScaledPrimitive(chain: builder.ResetPoint(), chamfer: chamfer, exponent: exponent, material: material, profile: profile, rounding: rounding, scale: scale, type: type);

            var program = builder.Build();
            // The contact reading refuses a non-uniform Scale (a primitive the door spells through one); the render
            // reading takes its minimum-axis bound, as the kernel does.
            var anisotropic = program.Instructions.Any(predicate: static instruction => ((instruction.Op == SdfOp.Scale) && ((instruction.Data0.X != instruction.Data0.Y) || (instruction.Data0.Y != instruction.Data0.Z))));

            foreach (var reading in (ReadOnlySpan<SdfFieldReading>)[SdfFieldReading.Render, SdfFieldReading.Contact]) {
                if (anisotropic && (reading == SdfFieldReading.Contact)) { continue; }

                Check(field: new SdfFieldEvaluator(program: program, reading: reading), label: $"{type} ({kind}, exponent {exponent}, rounding {rounding}, chamfer {chamfer}, {reading}) at scale {scale}", scale: scale);
                checkedScales++;
            }
        }

        Assert.True(condition: (checkedScales > 0), userMessage: $"{type} ({kind}): the door refused every scale.");
    }

    private static void Check(SdfFieldEvaluator field, Vector3 scale, string label) {
        var smallest = MathF.Min(x: scale.X, y: MathF.Min(x: scale.Y, y: scale.Z));
        var stepScale = (double)field.StepScale;
        var random = new Random(Seed: 0x5EED);
        var worst = 0.0;

        Assert.True(condition: (stepScale > 0.0), userMessage: $"{label}: step scale {stepScale}.");

        for (var sample = 0; (sample < 600); sample++) {
            var a = (Spread(random: random) * scale * 1.6f);
            var separation = (((sample % 2) == 0) ? 0.05f : 0.3f) * smallest;
            var b = (a + (Vector3.Normalize(value: (Spread(random: random) + new Vector3(value: 1e-3f))) * separation));
            var (pa, pb) = (Position(point: a), Position(point: b));

            if (!field.TryDistance(distance: out var da, material: out _, position: pa) || !field.TryDistance(distance: out var db, material: out _, position: pb)) { continue; }

            var apart = Distance(a: pa.Local, b: pb.Local);
            var change = Math.Abs(value: ((double)da - (double)db));

            worst = Math.Max(val1: worst, val2: ((change * stepScale) / apart));
            Assert.True(
                condition: (change <= ((apart / stepScale) + Slack)),
                userMessage: $"{label}: the field changes {change} m over {apart} m between {a} and {b}, past the step scale {stepScale}."
            );
        }

        Assert.True(condition: (worst > 0.5), userMessage: $"{label}: the samples' steepest change was {worst} of the bound; they never reached the surface's slope.");
    }

    private static Vector3 Spread(Random random) => new(x: ((float)((random.NextDouble() * 2.0) - 1.0)), y: ((float)((random.NextDouble() * 2.0) - 1.0)), z: ((float)((random.NextDouble() * 2.0) - 1.0)));
    private static FixedPosition Position(Vector3 point) => FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromDouble(value: point.X), Y: FixedQ4816.FromDouble(value: point.Y), Z: FixedQ4816.FromDouble(value: point.Z)));
    private static double Distance(FixedVector3 a, FixedVector3 b) {
        var (x, y, z) = (((double)a.X - (double)b.X), ((double)a.Y - (double)b.Y), ((double)a.Z - (double)b.Z));

        return Math.Sqrt(d: (((x * x) + (y * y)) + (z * z)));
    }
}
