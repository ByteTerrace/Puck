using System.Numerics;
using Xunit;
namespace Puck.SignedDistance.Tests;

public sealed class SdfProgramMaterialSnapshotLawTests {
    [Fact]
    public void MaterialAndNestedInputsCannotChangeTheProgramOrItsReferenceAfterConstruction() {
        var stops = new[] { new SdfRadialStop(0, Vector3.One) };
        var under = new[] { new SdfRevealStage(0.5f, new SdfSurface(Vector3.One, 0.5f, 0)) };
        var materials = new[] { new SdfMaterial(Vector3.One, Weathering: new SdfWeathering(Edge: 0.2f, Under: under),
            Inset: new SdfInset(Vector3.Zero, Quaternion.Identity, 0.1f, 1, new SdfRadialPaint(stops))) };
        var program = new SdfProgram([], materials);
        var packed = program.Words.ToArray();

        materials[0] = new SdfMaterial(Vector3.Zero);
        stops[0] = new SdfRadialStop(10, Vector3.Zero);
        under[0] = new SdfRevealStage(1, new SdfSurface(Vector3.Zero, 1, 1));
        Assert.Equal(Vector3.One, program.Materials[0].Albedo);
        Assert.Equal(new SdfRadialStop(0, Vector3.One), program.Materials[0].Inset!.Paint.Stops[0]);
        Assert.Equal(0.5f, program.Materials[0].Weathering!.Under![0].Threshold);
        Assert.Equal(packed, program.Words.ToArray());
        Assert.Throws<NotSupportedException>(testCode: () => ((IList<SdfMaterial>)program.Materials)[0] = new SdfMaterial(Vector3.Zero));
        Assert.Throws<NotSupportedException>(testCode: () => ((IList<SdfRadialStop>)program.Materials[0].Inset!.Paint.Stops)[0] = stops[0]);
    }
}
