using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfFieldScopeClampLawTests {
    [Fact]
    public void SharedSeamClampIsReportedEvenWhenEveryChainAndTheGlobalScaleAreUnit() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.BeginInstance(Vector3.Zero, 4f);
        builder.PushField().Sphere(1f, material).ResetPoint().Translate(Vector3.UnitX)
            .Sphere(1f, material, blend: SdfBlendOp.PipeUnion, smooth: .2f).PopField();
        builder.EndInstance();
        var program = builder.Build(buildInstanceGrid: false);

        Assert.Equal(1f, program.StepScale);
        Assert.Null(program.StepScaleBinder);
        var clamp = Assert.Single(program.FieldScopeClamps);
        Assert.Equal(0, clamp.InstanceIndex);
        Assert.Equal(2, clamp.ShapeCount);
        Assert.Equal(SdfOp.PushField, program.Instructions[clamp.PushInstructionIndex].Op);
        Assert.Equal(SdfOp.PopField, program.Instructions[clamp.PopInstructionIndex].Op);
        Assert.Equal(program.Instructions[clamp.PopInstructionIndex].Data1.Y, clamp.StepScale);
        Assert.InRange(clamp.StepScale, .70710f, .70711f);
        Assert.Throws<NotSupportedException>(() => ((IList<SdfFieldScopeClamp>)program.FieldScopeClamps).Clear());
    }

    [Fact]
    public void FieldOnlyScopeHasAClampWithoutAShapeBinder() {
        var builder = new SdfProgramBuilder();
        builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.PushField().CellDisplace(1f, 2f, 0, SdfCellMode.F2MinusF1, .2f).PopField();
        var program = builder.Build(buildInstanceGrid: false);

        Assert.Equal(1f, program.StepScale);
        Assert.Null(program.StepScaleBinder);
        var clamp = Assert.Single(program.FieldScopeClamps);
        Assert.Equal(-1, clamp.InstanceIndex);
        Assert.Equal(0, clamp.ShapeCount);
        Assert.Equal(.25f, clamp.StepScale);
    }

    [Fact]
    public void SequentialScopesKeepTheirOwnCountsAndOmitUnitClamps() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.PushField().Sphere(1f, material).PopField();
        builder.ResetPoint().PushField().Ellipsoid(new Vector3(2f, 1f, 1f), material).PopField();
        builder.ResetPoint().PushField().Ellipsoid(new Vector3(4f, 1f, 1f), material).PopField();
        var program = builder.Build(buildInstanceGrid: false);

        Assert.Equal(1f, program.StepScale);
        Assert.Collection(program.FieldScopeClamps,
            clamp => { Assert.Equal(1, clamp.ShapeCount); Assert.Equal(.5f, clamp.StepScale); },
            clamp => { Assert.Equal(1, clamp.ShapeCount); Assert.Equal(.25f, clamp.StepScale); });
        Assert.True(program.FieldScopeClamps[0].PopInstructionIndex < program.FieldScopeClamps[1].PushInstructionIndex);
    }
}
