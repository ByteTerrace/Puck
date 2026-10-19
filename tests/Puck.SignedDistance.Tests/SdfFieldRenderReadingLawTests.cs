using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// The render reading (<see cref="SdfFieldReading.Render"/>) answers the field the renderer's interpreter samples: a flare
/// (<see cref="SdfOp.AxialProfile"/>) and a twist (<see cref="SdfOp.RotatePlane"/>) warp the point as <c>mapCore</c>'s
/// cases do, written here in single precision from the kernel's own formulas, and a non-uniform
/// <see cref="SdfOp.Scale"/> reads as its minimum-axis bound; the box bounds enclose every point answer under each warp.
/// The contact reading still refuses all three by name, and an excerpt of a program answers as the instructions it holds.
/// </summary>
public sealed class SdfFieldRenderReadingLawTests {
    private const float Tolerance = 2e-3f;
    private static readonly Vector3 HalfExtents = new(x: 0.2f, y: 0.4f, z: 0.1f);

    // The points the laws read the field at: a lattice through the warped box and the space about it.
    private static IEnumerable<Vector3> Points() {
        for (var x = -3; (x <= 3); x++) {
            for (var y = -3; (y <= 3); y++) {
                for (var z = -3; (z <= 3); z++) {
                    yield return new Vector3(x: (0.11f * x), y: (0.17f * y), z: (0.07f * z));
                }
            }
        }
    }
    private static SdfProgram Program(Func<SdfProgramBuilder, SdfProgramBuilder> warp) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return warp(arg: builder.ResetPoint()).Box(halfExtents: HalfExtents, material: material, round: 0f).Build();
    }
    private static float Box(Vector3 p) {
        var q = (Vector3.Abs(value: p) - HalfExtents);

        return (Vector3.Max(value1: q, value2: Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f));
    }
    private static float Read(SdfFieldEvaluator field, Vector3 point) {
        Assert.True(condition: field.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))));

        return (float)(double)distance;
    }
    private static void AssertAnswers(SdfProgram program, Func<Vector3, float> reference) {
        var field = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render);

        foreach (var point in Points()) {
            var expected = reference(arg: point);
            var actual = Read(field: field, point: point);

            Assert.True(condition: (MathF.Abs(x: (actual - expected)) <= Tolerance), userMessage: $"at {point}: the render reading answers {actual}, the kernel's formula {expected}");
        }
    }
    private static void AssertBoundsEnclose(SdfProgram program) {
        var field = new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render);
        var low = new Vector3(x: -0.15f, y: -0.3f, z: -0.12f);
        var high = new Vector3(x: 0.25f, y: 0.2f, z: 0.05f);

        Assert.True(condition: field.TryDistanceBounds(distance: out var bounds, lower: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: low)), upper: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: high))));
        for (var corner = 0; (corner < 8); corner++) {
            var point = new Vector3(x: (((corner & 1) == 0) ? low.X : high.X), y: (((corner & 2) == 0) ? low.Y : high.Y), z: (((corner & 4) == 0) ? low.Z : high.Z));

            Assert.True(condition: field.TryDistance(distance: out var distance, material: out _, position: FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point))));
            Assert.True(condition: ((bounds.Lower <= distance) && (distance <= bounds.Upper)), userMessage: $"at {point}: {distance} lies outside the box's bounds {bounds}");
        }
    }

    [Fact]
    public void TheRenderReadingFlaresAsTheKernelDoes() {
        var program = Program(warp: static builder => builder.AxialProfile(amount: 0.3f, bulge: 0.2f, span: 0.6f, startScale: 0.9f, top: 0.3f));
        var flare = program.Instructions.Single(predicate: static instruction => (instruction.Op == SdfOp.AxialProfile));

        AssertAnswers(program: program, reference: point => {
            var t = Math.Clamp(value: ((flare.Data0.Z - point.Y) * flare.Data0.W), min: 0f, max: 1f);
            var scale = MathF.Max(((flare.Data1.Y + (flare.Data0.X * t)) + (flare.Data0.Y * MathF.Sin(x: (MathF.PI * t)))), SdfProgramBuilder.FlareMinScale);

            return (Box(p: new Vector3(x: (point.X / scale), y: point.Y, z: (point.Z / scale))) * flare.Data1.X);
        });
        AssertBoundsEnclose(program: program);
    }
    [Fact]
    public void TheRenderReadingTwistsAsTheKernelDoes() {
        const float Rate = 1.7f;
        const float Origin = 0.05f;
        var program = Program(warp: static builder => builder.RotatePlane(driver: SdfAxis.Y, origin: Origin, plane: SdfPlane.XZ, rate: Rate));

        AssertAnswers(program: program, reference: point => {
            var angle = (Rate * (point.Y - Origin));
            var (sine, cosine) = MathF.SinCos(x: angle);

            return Box(p: new Vector3(x: ((cosine * point.X) + (sine * point.Z)), y: point.Y, z: ((cosine * point.Z) - (sine * point.X))));
        });
        AssertBoundsEnclose(program: program);
    }
    [Fact]
    public void TheRenderReadingTwistsAboutEachPlane() {
        AssertAnswers(program: Program(warp: static builder => builder.RotatePlane(driver: SdfAxis.Z, plane: SdfPlane.XY, rate: 1.1f)), reference: static point => {
            var (sine, cosine) = MathF.SinCos(x: (1.1f * point.Z));

            return Box(p: new Vector3(x: ((cosine * point.X) + (sine * point.Y)), y: ((cosine * point.Y) - (sine * point.X)), z: point.Z));
        });
        AssertAnswers(program: Program(warp: static builder => builder.RotatePlane(driver: SdfAxis.X, plane: SdfPlane.YZ, rate: 1.1f)), reference: static point => {
            var (sine, cosine) = MathF.SinCos(x: (1.1f * point.X));

            return Box(p: new Vector3(x: point.X, y: ((cosine * point.Y) + (sine * point.Z)), z: ((cosine * point.Z) - (sine * point.Y))));
        });
        AssertBoundsEnclose(program: Program(warp: static builder => builder.RotatePlane(driver: SdfAxis.X, plane: SdfPlane.YZ, rate: 1.1f)));
    }
    [Fact]
    public void TheRenderReadingTakesANonUniformScaleAsItsMinimumAxisBound() {
        var scale = new Vector3(x: 1f, y: 2f, z: 0.5f);
        var program = Program(warp: builder => builder.Scale(scale: scale));

        AssertAnswers(program: program, reference: point => (Box(p: (point / scale)) * 0.5f));
    }
    [Fact]
    public void TheContactReadingRefusesWhatOnlyTheRenderReadingAdmits() {
        SdfProgram[] programs = [
            Program(warp: static builder => builder.AxialProfile(amount: 0.3f, bulge: 0f, span: 0.6f, top: 0.3f)),
            Program(warp: static builder => builder.TwistY(rate: 1f)),
            Program(warp: static builder => builder.Scale(scale: new Vector3(x: 1f, y: 2f, z: 1f))),
        ];

        foreach (var program in programs) {
            _ = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program));
            Assert.False(condition: new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render).Capabilities.WarpFree);
        }

        Assert.True(condition: new SdfFieldEvaluator(program: Program(warp: static builder => builder)).Capabilities.WarpFree);
        Assert.Equal(expected: SdfFieldReading.Contact, actual: new SdfFieldEvaluator(program: Program(warp: static builder => builder)).Reading);
        Assert.All(collection: SdfFieldEvaluator.RenderWarpOps, action: static op => Assert.True(condition: (SdfFieldEvaluator.IsSupportedOp(op: op, reading: SdfFieldReading.Render) && !SdfFieldEvaluator.IsSupportedOp(op: op, reading: SdfFieldReading.Contact))));
    }
    [Fact]
    public void AnExcerptAnswersAsTheInstructionsItHolds() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder
            .ResetPoint().Translate(offset: new Vector3(x: 1f, y: 0f, z: 0f)).Sphere(material: material, radius: 0.25f)
            .ResetPoint().ConvexPolygon(cornerRadius: 0f, lift: SdfLift.Extrude, liftAmount: 0.1f, material: material, vertices: [new Vector2(x: -0.2f, y: -0.2f), new Vector2(x: 0f, y: 0.3f), new Vector2(x: 0.2f, y: -0.2f)])
            .Build();
        var polygon = Enumerable.Range(start: 0, count: program.Instructions.Count).Where(predicate: index => (program.Instructions[index].Op == SdfOp.ShapeBlend)).Last();
        var excerpt = program.Excerpt(indices: [.. Enumerable.Range(start: 3, count: (program.Instructions.Count - 3))]);
        var moved = program.Excerpt(indices: [0, 1, 2], rewrite: (_, instruction) => ((instruction.Op == SdfOp.Translate) ? [instruction with { Data0 = new Vector4(x: -1f, y: 0f, z: 0f, w: 0f) }] : [instruction]));
        var whole = new SdfFieldEvaluator(program: program);
        var alone = new SdfFieldEvaluator(program: excerpt);
        var turned = new SdfFieldEvaluator(program: moved);

        Assert.Equal(expected: program.Instructions[polygon] with { Data0 = default }, actual: excerpt.Instructions[^1] with { Data0 = default });
        Assert.Equal(expected: Read(field: whole, point: new Vector3(x: 0f, y: 0f, z: 0.3f)), actual: Read(field: alone, point: new Vector3(x: 0f, y: 0f, z: 0.3f)));
        Assert.Equal(expected: -0.25f, actual: Read(field: turned, point: new Vector3(x: -1f, y: 0f, z: 0f)), tolerance: 1e-4f);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => program.Excerpt(indices: [program.Instructions.Count]));
    }
    [Fact]
    public void AnExcerptCarriesThePathsAndSweepsOfTheShapesItKeeps() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var square = new SdfPathContour(new Vector2(x: -0.2f, y: -0.2f), [new(new Vector2(x: 0.2f, y: -0.2f)), new(new Vector2(x: 0.2f, y: 0.2f)), new(new Vector2(x: -0.2f, y: 0.2f))]);
        var program = builder
            .ResetPoint().Sphere(material: material, radius: 0.1f)
            .ResetPoint().Path(halfDepth: 0.05f, material: material, profile: new SdfPathProfile([square]), scale: Vector2.One)
            .ResetPoint().Sweep(a: Vector3.Zero, b: new Vector3(x: 0.5f, y: 0.5f, z: 0f), bulge: 0f, c: Vector3.UnitY, material: material, radiusEnd: 0.05f, radiusStart: 0.05f, strandOffset: 0f, strands: 1, twist: 0f)
            .Build();
        var sweep = Enumerable.Range(start: 0, count: program.Instructions.Count).Last(predicate: index => (program.Instructions[index].Op == SdfOp.ShapeBlend));
        var excerpt = program.Excerpt(indices: [.. Enumerable.Range(start: 2, count: (program.Instructions.Count - 2))]);
        var swept = new SdfFieldEvaluator(program: program.Excerpt(indices: [.. Enumerable.Range(start: (sweep - 1), count: 2)]));

        // The path and the sweep keep their tables, so each excerpt builds; the sweep alone reads its own curve, and the
        // path, which no reading interprets, is refused by name.
        Assert.Equal(expected: 2, actual: excerpt.Instructions.Count(predicate: static instruction => (instruction.Op == SdfOp.ShapeBlend)));
        Assert.Equal(expected: -0.05f, actual: Read(field: swept, point: Vector3.Zero), tolerance: 1e-3f);
        _ = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: excerpt, reading: SdfFieldReading.Render));
    }
}
