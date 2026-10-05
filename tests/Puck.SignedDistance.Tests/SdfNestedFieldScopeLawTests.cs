using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfNestedFieldScopeLawTests {
    [InlineData(0d, -1d, 0)]
    [InlineData(4d, .25d, 4)]
    [InlineData(4.5d, -.25d, 4)]
    [InlineData(6d, -.5d, 3)]
    [Theory]
    public void NestedCutsPreserveParentFieldsAndMaterialWinners(double x, double expected, int material) {
        var builder = new SdfProgramBuilder();

        for (var index = 0; (index < 5); index++) {
            _ = builder.AddMaterial(material: new SdfMaterial(new Vector3(value: (index / 4f))));
        }
        builder.Sphere(1f, 0)
            .PushField()
            .ResetPoint().Translate(offset: new Vector3(x: 4f, y: 0f, z: 0f)).Sphere(1f, 1)
            .PushField()
            .ResetPoint().Translate(offset: new Vector3(x: 6f, y: 0f, z: 0f)).Sphere(1f, 2)
            .ResetPoint().Translate(offset: new Vector3(x: 4f, y: 0f, z: 0f)).Sphere(1.5f, 3, blend: SdfBlendOp.Subtraction)
            .PopField()
            .ResetPoint().Translate(offset: new Vector3(x: 4f, y: 0f, z: 0f)).Sphere(.25f, 4, blend: SdfBlendOp.Subtraction)
            .PopField();
        var evaluator = new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        var point = FixedPosition.FromLocal(local: new FixedVector3(X: FixedQ4816.FromDouble(value: x), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero));

        Assert.True(condition: evaluator.TryDistance(distance: out var distance, material: out var actualMaterial, position: point));
        Assert.Equal(FixedQ4816.FromDouble(value: expected), distance);
        Assert.Equal(actual: actualMaterial, expected: material);
        Assert.True(condition: evaluator.TryDistanceBounds(distance: out var bounds, lower: point, upper: point));
        Assert.True(condition: ((bounds.Lower <= distance) && (bounds.Upper >= distance)));
        Assert.True(condition: ((bounds.Upper.Value - bounds.Lower.Value) <= 16L));
    }
    [Fact]
    public void TheBuilderAdmitsTwoLevelsAndRefusesAThirdBeforeChangingTheStream() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.PushField().PushField().Sphere(1f, material);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => builder.PushField());
        var program = builder.PopField().PopField().Build(buildInstanceGrid: false);

        Assert.Equal(2, program.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.PushField)));
        Assert.True(condition: program.IndirectInstancesComposable);
    }
}
