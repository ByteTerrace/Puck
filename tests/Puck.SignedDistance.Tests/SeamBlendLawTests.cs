using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SeamBlendLawTests {
    [Theory]
    [InlineData(SdfBlendOp.GrooveUnion)]
    [InlineData(SdfBlendOp.PipeUnion)]
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
            var expected = blend == SdfBlendOp.GrooveUnion ? Math.Max(Math.Min(x,y), -tube) : Math.Min(Math.Min(x,y), tube);
            Assert.InRange((double)distance, expected - 0.00005, expected + 0.00005);
        }
    }
    [Theory]
    [InlineData(SdfBlendOp.GrooveUnion)]
    [InlineData(SdfBlendOp.PipeUnion)]
    public void RepeatedSeamsFoldTheUnequalOperandBounds(SdfBlendOp blend) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        for (var i=0; i<5; i++) { builder.ResetPoint().Sphere(1f, material, blend, 0.2f); }
        Assert.InRange(builder.Build().StepScale, 1f / MathF.Sqrt(5f) - 0.000001f, 1f / MathF.Sqrt(5f) + 0.000001f);
    }
}
