using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>The frame <see cref="SdfProgram"/> writes into a log-sphere fold's Data1, which the march reads to cross
/// the fold's shells exactly: the fold's local origin in the chain head's frame and a flag that its shells are spheres
/// there. A wrong origin would cross a wall the field never had, so each case maps the written origin forward through
/// the chain as the shader does and requires it to land on the fold's own origin; any chain that bends the shells
/// carries no flag.</summary>
public sealed class SdfLogSphereFrameLawTests {
    [Fact]
    public void ASimilarityChainMapsTheWrittenOriginOntoTheFoldsOrigin() {
        var rotation = Quaternion.Normalize(value: new Quaternion(w: 0.8f, x: 0.3f, y: -0.4f, z: 0.2f));
        var program = Build(chain: (builder, _) => builder
            .Translate(offset: new Vector3(x: 3, y: -1, z: 2))
            .Rotate(rotation: rotation)
            .Scale(scale: new Vector3(value: 2.5f))
            .Translate(offset: new Vector3(x: -0.5f, y: 0.25f, z: 1)));
        var fold = FoldOf(program: program);

        Assert.Equal(expected: 1f, actual: fold.Data1.W);
        var local = new Vector3(x: fold.Data1.X, y: fold.Data1.Y, z: fold.Data1.Z);

        local -= new Vector3(x: 3, y: -1, z: 2);
        local = Vector3.Transform(rotation: Quaternion.Conjugate(value: rotation), value: local);
        local /= 2.5f;
        local -= new Vector3(x: -0.5f, y: 0.25f, z: 1);
        Assert.True(condition: (local.Length() < 1.0e-5f), userMessage: $"the written origin maps to {local}");
    }
    [Fact]
    public void ADynamicHeadLeavesTheOriginInTheSlotsFrame() {
        var fold = FoldOf(program: Build(chain: (builder, _) => builder.TransformDynamic(slot: 0).Translate(offset: new Vector3(x: 1, y: 2, z: 3))));

        Assert.Equal(expected: new Vector4(w: 1, x: 1, y: 2, z: 3), actual: fold.Data1);
    }
    [Fact]
    public void AChainBeginsAtItsResetPoint() {
        var fold = FoldOf(program: Build(chain: static (builder, material) => builder
            .TwistY(rate: 0.5f).Sphere(material: material, radius: 1)
            .ResetPoint().Translate(offset: new Vector3(x: 4, y: 0, z: 0))));

        Assert.Equal(expected: new Vector4(w: 1, x: 4, y: 0, z: 0), actual: fold.Data1);
    }
    [Fact]
    public void AWarpANonUniformScaleOrALateDynamicTransformBendsTheShells() {
        Assert.Equal(expected: Vector4.Zero, actual: FoldOf(program: Build(chain: static (builder, _) => builder.TwistY(rate: 0.5f))).Data1);
        Assert.Equal(expected: Vector4.Zero, actual: FoldOf(program: Build(chain: static (builder, _) => builder.Scale(scale: new Vector3(x: 1, y: 2, z: 1)))).Data1);
        Assert.Equal(expected: Vector4.Zero, actual: FoldOf(program: Build(chain: static (builder, _) => builder.Translate(offset: Vector3.UnitX).TransformDynamic(slot: 0))).Data1);
        Assert.Equal(expected: Vector4.Zero, actual: FoldOf(program: Build(chain: static (builder, _) => builder.LogSphere(shellRatio: 3))).Data1);
    }

    private static SdfProgram Build(Func<SdfProgramBuilder, int, SdfProgramBuilder> chain) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = chain(arg1: builder, arg2: material).LogSphere(shellRatio: 2).Sphere(material: material, radius: 0.25f);

        return builder.Build();
    }
    private static SdfInstruction FoldOf(SdfProgram program) => program.Instructions.Last(predicate: static instruction => (instruction.Op == SdfOp.LogSphere));
}
