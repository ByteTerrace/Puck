using System.Numerics;

using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class PathProfileLawTests {
    private static SdfPathContour Square(float r) => new(new(-r, -r), [new(new(r, -r)), new(new(r, r)), new(new(-r, r))]);

    [Fact]
    public void FilledContoursKeepConcavityAndHoles() {
        var path = new SdfPathProfile([Square(r: 0.8f), Square(r: 0.2f)]);
        var edges = path.Compile();

        Assert.Equal(8, edges.Length);
        Assert.Equal(0.2f, FillDistance(Vector2.Zero, edges), 6);
        Assert.Equal(-0.2f, FillDistance(new(x: 0.4f, y: 0), edges), 6);
        Assert.Equal(0.2f, FillDistance(new(x: 1, y: 0), edges), 6);

        var concave = new SdfPathProfile([new(new(-0.8f, -0.8f), [
            new(new(0.8f, -0.8f)), new(new(0.8f, 0.8f)), new(new(0, 0)), new(new(-0.8f, 0.8f))])]).Compile();

        Assert.True(condition: (FillDistance(new(x: 0, y: 0.5f), concave) > 0));
        Assert.True(condition: (FillDistance(new(x: 0, y: -0.5f), concave) < 0));
    }
    [Fact]
    public void ArcOutlineStaysWithinItsDeclaredCircleError() {
        const float Radius = 0.6f;
        var edges = new SdfPathProfile([new(new(Radius, 0), [new(new(Radius, 0), ArcCenter: Vector2.Zero)])], 0.001f).Compile();

        for (var i = 0; (i < 2048); i++) {
            var angle = ((Math.Tau * i) / 2048);
            var p = (new Vector2(x: ((float)Math.Cos(d: angle)), y: ((float)Math.Sin(a: angle))) * Radius);

            Assert.InRange(MathF.Abs(x: FillDistance(p, edges)), 0f, 0.001001f);
        }
    }
    [Fact]
    public void QuadraticSmoothStrokeBoundsCenterAndRadiusError() {
        var a = new Vector2(x: 0.05f, y: -0.16f);
        var b = new Vector2(x: 0.16f, y: -0.27f);
        var c = new Vector2(x: 0.24f, y: -0.21f);
        var edges = new SdfPathProfile([new(a, [new(c, b)])], 0.0001f,
            new(0.038f, 0.014f, Smooth: true, From: 0.2f, To: 0.95f)).Compile();

        Assert.True(condition: (edges.Length < SdfPathProfile.MaxEdges));
        // Compare sampled disks of the analytic curve to the interpolated endpoint disks. The union's
        // Hausdorff error is bounded by center discrepancy plus radius discrepancy for corresponding disks.
        for (var i = 0; (i <= 2000); i++) {
            var t = (i / 2000f);
            var center = (((((1 - t) * (1 - t)) * a) + (((2 * t) * (1 - t)) * b)) + ((t * t) * c));
            var u = Math.Clamp(max: 1, min: 0, value: ((t - 0.2f) / 0.75f));
            var radius = (0.038f + ((((0.014f - 0.038f) * u) * u) * (3 - (2 * u))));
            var best = float.PositiveInfinity;

            foreach (var edge in edges) {
                var d = (edge.B - edge.A);
                var f = Math.Clamp((Vector2.Dot((center - edge.A), d) / d.LengthSquared()), 0, 1);
                var error = (Vector2.Distance(value1: center, value2: Vector2.Lerp(edge.A, edge.B, f)) + MathF.Abs((radius - (edge.RadiusA + (f * (edge.RadiusB - edge.RadiusA))))));

                best = MathF.Min(best, error);
            }
            Assert.InRange(actual: best, high: 0.000101f, low: 0f);
        }
    }
    [Fact]
    public void ClampedShearOfArcsPreservesTheOutlineWithinTolerance() {
        // Two circular lobes and two lines. The oracle evaluates the original analytic boundary,
        // then independently applies the clamped polynomial used to bend it.
        var path = new SdfPathProfile([new(new(0, -0.55f), [
            new(new(0.5f, -0.05f)),
            new(new(0, 0.45f), ArcCenter: new(0.25f, 0.20f)),
            new(new(-0.5f, -0.05f), ArcCenter: new(-0.25f, 0.20f)),
            new(new(0, -0.55f))])], 0.001f, Shear: new(0.028f, 0.28f, Offset: -0.0693f, From: -0.55f, To: 0.45f));
        var edges = path.Compile();

        foreach (var side in new[] { -1, 1 }) {
            for (var i = 0; (i <= 500); i++) {
                var angle = ((-Math.PI / 4) + ((Math.PI * i) / 500));
                var p = new Vector2(x: ((float)(side * (0.25 + (Math.Sqrt(d: 0.125) * Math.Cos(d: angle))))),
                    y: ((float)(0.2 + (Math.Sqrt(d: 0.125) * Math.Sin(a: angle)))));
                var y = Math.Clamp(max: 1, min: 0, value: (p.Y + 0.55f));

                p.X -= ((0.28f * y) * (1 - y));
                Assert.InRange(MathF.Abs(x: FillDistance(p, edges)), 0f, 0.001002f);
            }
        }
    }
    [Fact]
    public void BudgetAndMalformedGeometryRefuse() {
        Assert.Throws<ArgumentException>(() => new SdfPathProfile([new(new(0.6f, 0), [new(new(0.6f, 0), ArcCenter: Vector2.Zero)])], 0.00001f).Compile());
        Assert.Throws<ArgumentException>(() => new SdfPathProfile([new(new(-0.5f, -0.5f), [new(new(0.5f, 0.5f)), new(new(-0.5f, 0.5f)), new(new(0.5f, -0.5f))])]).Compile());
        Assert.Throws<ArgumentException>(() => new SdfPathProfile([Square(r: 0.5f), Square(r: 0.5f)]).Compile());
        Assert.Throws<ArgumentException>(() => new SdfPathProfile([Square(r: 0.5f)], float.NaN).Compile());
        Assert.Throws<ArgumentException>(() => new SdfPathProfile([new(Vector2.Zero, [new(new(0.5f, 0), Control2: Vector2.One)])]).Compile());
        Assert.Throws<ArgumentException>(() => new SdfPathProfile([new(Vector2.Zero, [new(Vector2.One)])], Stroke: new(0.1f, 0.1f)).Compile());
        Assert.Throws<ArgumentException>(() => new SdfPathProfile([new(new(0.5f, 0),
            [new(new(0.5f, 0), ArcCenter: Vector2.Zero)], Closed: true)], Stroke: new(0.1f, 0.05f)).Compile());
    }
    [Fact]
    public void PackedTablesAreOwnedAndBudgetedAndContactIsExplicitlyRefused() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.Path(new([Square(r: 0.5f)]), Vector2.One, 0.04f, material);
        var program = builder.Build();
        var instruction = Assert.Single(program.Instructions, i => ((i.Shape == ((uint)SdfShapeType.Path)) && (i.Op == SdfOp.ShapeBlend)));

        Assert.Equal(4, instruction.Data0.Y);
        Assert.Equal(0.04f, instruction.Data0.W);
        var offset = (((int)BitConverter.SingleToUInt32Bits(instruction.Data0.X)) * 4);

        Assert.Equal(-0.5f, BitConverter.UInt32BitsToSingle(value: program.Words[offset]));
        Assert.Equal(1f, program.StepScale);
        Assert.Throws<ArgumentException>(testCode: () => new SdfProgram(program.Instructions, [new SdfMaterial(Albedo: Vector3.One)]));
        Assert.Contains("Path", Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program)).Message);
        builder.ReservePathTables(2);
        var probe = builder.Build();

        Assert.Equal(program.Words.Length, probe.Words.Length);
        Assert.Equal((program.PartCompilationWordCapacity + ((2 * SdfPathProfile.MaxEdges) * 8)), probe.PartCompilationWordCapacity);
    }

    private static float FillDistance(Vector2 p, IReadOnlyList<SdfPathEdge> edges) {
        var distance = float.PositiveInfinity;
        var crossings = 0;

        foreach (var edge in edges) {
            var delta = (edge.B - edge.A);
            var t = Math.Clamp((Vector2.Dot((p - edge.A), delta) / delta.LengthSquared()), 0, 1);

            distance = MathF.Min(x: distance, y: Vector2.Distance(p, (edge.A + (t * delta))));
            if (((edge.A.Y > p.Y) != (edge.B.Y > p.Y)) &&
                (p.X < (edge.A.X + (((p.Y - edge.A.Y) * (edge.B.X - edge.A.X)) / (edge.B.Y - edge.A.Y))))) { crossings++; }
        }
        return (((crossings % 2) == 0) ? distance : -distance);
    }
}
