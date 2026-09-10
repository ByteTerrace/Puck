using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SeamBlendLawTests {
    [Theory]
    [InlineData(SdfBlendOp.GrooveUnion)]
    [InlineData(SdfBlendOp.PipeUnion)]
    [InlineData(SdfBlendOp.GrooveSubtraction)]
    [InlineData(SdfBlendOp.PipeSubtraction)]
    public void FixedFieldMatchesTheRoundTubeFormulaInsideOutsideAndOnTheSeam(SdfBlendOp blend) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        builder.Plane(Vector3.UnitX, 0f, material);
        builder.ResetPoint().Plane(Vector3.UnitY, 0f, material, blend, 0.25f);
        var program = builder.Build();
        var field = new SdfFieldEvaluator(program);
        Assert.InRange(program.StepScale, 0.70710f, 0.70711f);
        foreach (var (x, y) in new (double, double)[] { (0,0), (.25,0), (-.1,-.1), (.1,.1), (-2,.01), (.01,-2), (3,4), (-3,-4) }) {
            var point = FixedPosition.FromLocal(new(FixedQ4816.FromDouble(x), FixedQ4816.FromDouble(y), FixedQ4816.Zero));
            Assert.True(field.TryDistance(point, out var distance, out _));
            var tube = Math.Sqrt(x*x + y*y) - .25;
            var expected = blend switch {
                SdfBlendOp.GrooveUnion => Math.Max(Math.Min(x, y), -tube),
                SdfBlendOp.PipeUnion => Math.Min(Math.Min(x, y), tube),
                SdfBlendOp.GrooveSubtraction => Math.Max(Math.Max(x, -y), -tube),
                SdfBlendOp.PipeSubtraction => Math.Min(Math.Max(x, -y), tube),
                _ => throw new ArgumentOutOfRangeException(nameof(blend))
            };
            Assert.InRange((double)distance, expected - 0.00005, expected + 0.00005);
        }
    }
    [Theory]
    [InlineData(SdfBlendOp.GrooveUnion)]
    [InlineData(SdfBlendOp.PipeUnion)]
    [InlineData(SdfBlendOp.GrooveSubtraction)]
    [InlineData(SdfBlendOp.PipeSubtraction)]
    public void RepeatedSeamsFoldTheUnequalOperandBounds(SdfBlendOp blend) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        for (var i=0; i<5; i++) { builder.ResetPoint().Sphere(1f, material, blend, 0.2f); }
        Assert.InRange(builder.Build().StepScale, 1f / MathF.Sqrt(5f) - 0.000001f, 1f / MathF.Sqrt(5f) + 0.000001f);
    }
    [Fact]
    public void MorphFieldMatchesLinearInterpolationAndPreservesUnitStepScale() {
        var builder = new SdfProgramBuilder();
        var materialA = builder.AddMaterial(new(Vector3.One));
        var materialB = builder.AddMaterial(new(Vector3.Zero));

        // Field evaluator has no dynamic transforms, so lane 0 reads as 0.
        // from = -1, to = 1 -> t = (0 - (-1)) / (1 - (-1)) = 0.5.
        builder.Sphere(1f, materialA);
        builder.PushFieldMorph(laneIndex: 0, from: -1f, to: 1f);
        builder.Sphere(3f, materialB);
        builder.PopField();

        var program = builder.Build();
        Assert.Equal(1f, program.StepScale);

        var field = new SdfFieldEvaluator(program);
        var origin = FixedPosition.FromLocal(FixedVector3.Zero);
        Assert.True(field.TryDistance(origin, out var distance, out var material));

        // Sphere(1) distance at origin is -1, Sphere(3) distance is -3. Lerp with t=0.5 gives -2.
        Assert.InRange((double)distance, -2.0001, -1.9999);
        // At t = 0.5, tie-break gives candidate (materialB).
        Assert.Equal(materialB, material);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StairsFieldMatchesTriangleWaveAndPreservesUnitStepScale(bool subtraction) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        var radius = 0.5f;
        var steps = 2;

        builder.Plane(Vector3.UnitX, 0f, material);
        builder.PushFieldStairs(radius: radius, steps: steps, subtraction: subtraction);
        builder.ResetPoint().Plane(Vector3.UnitY, 0f, material);
        builder.PopField();

        var program = builder.Build();
        Assert.Equal(1f, program.StepScale);

        var field = new SdfFieldEvaluator(program);
        var s = (double)radius / steps;
        var period = 2.0 * s;

        foreach (var (x, y) in new (double, double)[] { (0, 0), (.25, 0), (-.1, -.1), (.1, .1), (-2, .01), (.01, -2), (3, 4), (-3, -4) }) {
            var point = FixedPosition.FromLocal(new(FixedQ4816.FromDouble(x), FixedQ4816.FromDouble(y), FixedQ4816.Zero));
            Assert.True(field.TryDistance(point, out var distance, out _));

            double expected;
            if (subtraction) {
                var u = -y - radius;
                var arg = u - x + s;
                var m = arg - period * Math.Floor(arg / period);
                var w = m - s;
                var dStairs = 0.5 * (u + x + Math.Abs(w));
                expected = Math.Max(Math.Max(x, -y), -dStairs);
            } else {
                var u = y - radius;
                var arg = u - x + s;
                var m = arg - period * Math.Floor(arg / period);
                var w = m - s;
                var dStairs = 0.5 * (u + x + Math.Abs(w));
                expected = Math.Min(Math.Min(x, y), dStairs);
            }

            Assert.InRange((double)distance, expected - 0.0001, expected + 0.0001);
        }
    }
}
