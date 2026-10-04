using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfNestedFieldScopeLawTests {
    [Theory]
    [InlineData(0d, -1d, 0)]
    [InlineData(4d, .25d, 4)]
    [InlineData(4.5d, -.25d, 4)]
    [InlineData(6d, -.5d, 3)]
    public void NestedCutsPreserveParentFieldsAndMaterialWinners(double x, double expected, int material) {
        var builder = new SdfProgramBuilder();
        for (var index = 0; index < 5; index++) {
            _ = builder.AddMaterial(new SdfMaterial(new Vector3(index / 4f)));
        }
        builder.Sphere(1f, 0)
            .PushField()
            .ResetPoint().Translate(new Vector3(4f, 0f, 0f)).Sphere(1f, 1)
            .PushField()
            .ResetPoint().Translate(new Vector3(6f, 0f, 0f)).Sphere(1f, 2)
            .ResetPoint().Translate(new Vector3(4f, 0f, 0f)).Sphere(1.5f, 3, blend: SdfBlendOp.Subtraction)
            .PopField()
            .ResetPoint().Translate(new Vector3(4f, 0f, 0f)).Sphere(.25f, 4, blend: SdfBlendOp.Subtraction)
            .PopField();
        var evaluator = new SdfFieldEvaluator(builder.Build(buildInstanceGrid: false));
        var point = FixedPosition.FromLocal(new FixedVector3(FixedQ4816.FromDouble(x), FixedQ4816.Zero, FixedQ4816.Zero));
        Assert.True(evaluator.TryDistance(point, out var distance, out var actualMaterial));
        Assert.Equal(FixedQ4816.FromDouble(expected), distance);
        Assert.Equal(material, actualMaterial);
        Assert.True(evaluator.TryDistanceBounds(point, point, out var bounds));
        Assert.True(bounds.Lower <= distance && bounds.Upper >= distance);
        Assert.True((bounds.Upper.Value - bounds.Lower.Value) <= 16L);
    }

    [Fact]
    public void TheBuilderAdmitsTwoLevelsAndRefusesAThirdBeforeChangingTheStream() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.PushField().PushField().Sphere(1f, material);
        _ = Assert.Throws<InvalidOperationException>(() => builder.PushField());
        var program = builder.PopField().PopField().Build(buildInstanceGrid: false);
        Assert.Equal(2, program.Instructions.Count(instruction => instruction.Op == SdfOp.PushField));
        Assert.True(program.IndirectInstancesComposable);
    }
}
