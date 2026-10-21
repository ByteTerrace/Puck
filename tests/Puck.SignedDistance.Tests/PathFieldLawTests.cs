using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The fixed-point evaluator reads a <see cref="SdfShapeType.Path"/> as the kernel draws it (<c>sdfPathSolid</c>,
/// <c>sdfPathStrokeEdge</c> in <c>field/sdf-shapes.hlsli</c>): each law writes the kernel's formula in single precision over
/// the program's own packed edges and reads both readings' answers against it on a lattice through the solid and the
/// space about it, a fill with a concave outline and a hole, a stroke whose radii taper, each at an uneven scale and
/// turned off the lattice's axes. The bounds over a box enclose the answers inside it.
/// </summary>
public sealed class PathFieldLawTests {
    private const float Tolerance = 2e-3f;
    private static readonly Vector2 Scale = new(x: 0.8f, y: 0.6f);
    private static readonly Quaternion Turn = Quaternion.Normalize(value: new Quaternion(w: 0.9f, x: 0.2f, y: -0.35f, z: 0.1f));
    private static readonly SdfPathContour Outline = new(new Vector2(x: -0.8f, y: -0.8f), [new(new Vector2(x: 0.8f, y: -0.8f)), new(new Vector2(x: 0.8f, y: 0.8f)), new(new Vector2(x: 0f, y: 0.1f)), new(new Vector2(x: -0.8f, y: 0.8f))]);
    private static readonly SdfPathContour Hole = new(new Vector2(x: -0.3f, y: -0.5f), [new(new Vector2(x: 0.3f, y: -0.5f)), new(new Vector2(x: 0.3f, y: -0.2f)), new(new Vector2(x: -0.3f, y: -0.2f))]);

    private static IEnumerable<Vector3> Points() {
        for (var x = -5; (x <= 5); x++) {
            for (var y = -5; (y <= 5); y++) {
                for (var z = -3; (z <= 3); z++) {
                    yield return new Vector3(x: (0.17f * x), y: (0.13f * y), z: (0.11f * z));
                }
            }
        }
    }
    private static (SdfProgram Program, int Shape) Program(SdfPathProfile profile, float halfDepth) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.ResetPoint().Rotate(rotation: Turn).Path(halfDepth: halfDepth, material: material, profile: profile, scale: Scale).Build();

        return (program, Enumerable.Range(start: 0, count: program.Instructions.Count).Single(predicate: index => (program.Instructions[index].Op == SdfOp.ShapeBlend)));
    }
    // sdfPathSolid over the program's own packed edges, in single precision, at the point in the shape's frame.
    private static float Kernel(SdfProgram program, int shape, Vector3 local) {
        var words = program.SideTableValues(instructionIndex: shape);
        var stroke = (program.Instructions[shape].Data1.Y > 0.5f);
        var p = new Vector2(x: local.X, y: local.Y);
        var distance = float.MaxValue;
        var squared = float.MaxValue;
        var inside = false;

        for (var edge = 0; (edge < words.Length); edge += 6) {
            var a = new Vector2(x: words[edge], y: words[(edge + 1)]);
            var b = new Vector2(x: words[(edge + 2)], y: words[(edge + 3)]);

            if (stroke) {
                distance = MathF.Min(distance, StrokeEdge(a: a, b: b, p: p, ra: words[(edge + 4)], rb: words[(edge + 5)]));
            } else {
                var e = (b - a);
                var w = (p - a);
                var d = (w - (e * Math.Clamp(value: (Vector2.Dot(w, e) / Vector2.Dot(e, e)), min: 0f, max: 1f)));

                squared = MathF.Min(squared, Vector2.Dot(d, d));
                var c1 = (p.Y >= a.Y);
                var c2 = (p.Y < b.Y);
                var c3 = ((e.X * w.Y) > (e.Y * w.X));

                if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) { inside = !inside; }
            }
        }
        if (!stroke) { distance = ((inside ? -1f : 1f) * MathF.Sqrt(squared)); }

        var wz = (MathF.Abs(local.Z) - program.Instructions[shape].Data0.W);

        return (MathF.Min(MathF.Max(distance, wz), 0f) + new Vector2(x: MathF.Max(distance, 0f), y: MathF.Max(wz, 0f)).Length());
    }
    private static float StrokeEdge(Vector2 p, Vector2 a, Vector2 b, float ra, float rb) {
        var ab = (b - a);
        var length = ab.Length();

        if (MathF.Abs(ra - rb) >= length) { return ((ra >= rb) ? ((p - a).Length() - ra) : ((p - b).Length() - rb)); }
        var u = (ab / length);
        var v = (p - a);
        var x = Vector2.Dot(v, u);
        var y = MathF.Abs((v.X * u.Y) - (v.Y * u.X));
        var k = ((ra - rb) / length);
        var c = MathF.Sqrt(MathF.Max(0f, (1f - (k * k))));
        var along = ((x * c) - (y * k));

        if (along < 0f) { return (v.Length() - ra); }
        if (along > (length * c)) { return ((p - b).Length() - rb); }
        return (((x * k) + (y * c)) - ra);
    }
    private static float Read(SdfFieldEvaluator field, Vector3 point) {
        Assert.True(condition: field.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))));

        return (float)(double)distance;
    }
    private static void AssertReadsAsTheKernel(SdfPathProfile profile, float halfDepth) {
        var (program, shape) = Program(halfDepth: halfDepth, profile: profile);
        var inverse = Quaternion.Inverse(value: Turn);

        foreach (var reading in ((SdfFieldReading[])[SdfFieldReading.Contact, SdfFieldReading.Render])) {
            var field = new SdfFieldEvaluator(program: program, reading: reading);
            var (inside, outside) = (0, 0);

            foreach (var point in Points()) {
                var expected = Kernel(local: Vector3.Transform(value: point, rotation: inverse), program: program, shape: shape);
                var actual = Read(field: field, point: point);

                Assert.True(condition: (MathF.Abs(actual - expected) <= Tolerance), userMessage: $"{reading} at {point}: the evaluator answers {actual}, the kernel's formula {expected}");
                if (expected < 0f) { inside++; } else { outside++; }
            }

            Assert.True(condition: ((inside > 20) && (outside > 20)), userMessage: $"the lattice reads {inside} points inside and {outside} outside");
        }
    }
    private static void AssertBoundsEnclose(SdfPathProfile profile, float halfDepth) {
        var (program, _) = Program(halfDepth: halfDepth, profile: profile);
        var field = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render);
        var random = new Random(Seed: 19);

        for (var box = 0; (box < 64); box++) {
            var centre = new Vector3(x: (0.9f * ((2f * random.NextSingle()) - 1f)), y: (0.9f * ((2f * random.NextSingle()) - 1f)), z: (0.6f * ((2f * random.NextSingle()) - 1f)));
            var half = new Vector3(x: (0.3f * random.NextSingle()), y: (0.3f * random.NextSingle()), z: (0.2f * random.NextSingle()));
            var low = (centre - half);
            var high = (centre + half);

            Assert.True(condition: field.TryDistanceBounds(distance: out var bounds, lower: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: low)), upper: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: high))));
            Assert.False(condition: bounds.IsUnbounded);
            for (var sample = 0; (sample < 16); sample++) {
                var point = (low + ((high - low) * new Vector3(x: random.NextSingle(), y: random.NextSingle(), z: random.NextSingle())));
                var answer = Read(field: field, point: point);

                Assert.True(condition: ((answer >= ((float)(double)bounds.Lower - 1e-4f)) && (answer <= ((float)(double)bounds.Upper + 1e-4f))), userMessage: $"at {point} in [{low}, {high}]: {answer} outside [{bounds.Lower}, {bounds.Upper}]");
            }
        }
    }

    [Fact]
    public void AFillReadsAsTheKernelsEdgeParity() => AssertReadsAsTheKernel(halfDepth: 0.25f, profile: new SdfPathProfile([Outline, Hole]));
    [Fact]
    public void AStrokeReadsAsTheKernelsHullOfDisks() => AssertReadsAsTheKernel(halfDepth: 0.2f, profile: new SdfPathProfile([Outline], Stroke: new SdfPathStroke(RadiusStart: 0.12f, RadiusEnd: 0.04f)));
    [Fact]
    public void AFillsBoundsEncloseItsAnswers() => AssertBoundsEnclose(halfDepth: 0.25f, profile: new SdfPathProfile([Outline, Hole]));
    [Fact]
    public void AStrokesBoundsEncloseItsAnswers() => AssertBoundsEnclose(halfDepth: 0.2f, profile: new SdfPathProfile([Outline], Stroke: new SdfPathStroke(RadiusStart: 0.12f, RadiusEnd: 0.04f)));
}
