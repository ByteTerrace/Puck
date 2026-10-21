using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a glass material (<see cref="SdfMaterial.Glass"/>) packs its absorption per world unit and its index of
/// refraction into the material's last row, so that light through <see cref="SdfGlass.Depth"/> of it is the albedo;
/// an opaque material packs zeros there, so the kernels tell glass by a positive index; both admission doors refuse a
/// depth that is not positive, an index outside [<see cref="SdfGlass.MinIor"/>, <see cref="SdfGlass.MaxIor"/>] and a
/// tint outside [0, 1]; and a program knows which of its shapes are glass (<see cref="SdfProgram.HasGlass"/>,
/// <see cref="SdfProgram.IsGlassRange"/>).
/// </summary>
public sealed class SdfGlassLawTests {
    private const int GlassRow = 21;

    private static readonly SdfGlass Acrylic = new(Depth: 0.004f, Ior: 1.49f);

    private static SdfInstruction Shape(int material) => new(
        Blend: ((uint)SdfBlendOp.Union),
        Data0: new Vector4(x: 0.5f, y: 0f, z: 0f, w: 0f),
        Data1: Vector4.Zero,
        Material: ((uint)material),
        Op: SdfOp.ShapeBlend,
        Shape: ((uint)SdfShapeType.Sphere)
    );
    private static Vector4 Row(SdfProgram program, int material, int row) {
        var word = ((((int)program.Words[SdfProgram.ProgramMaterialOffsetLane]) + (SdfProgram.MaterialVectorsPerEntry * material) + row) * 4);

        return new Vector4(
            x: BitConverter.UInt32BitsToSingle(value: program.Words[word]),
            y: BitConverter.UInt32BitsToSingle(value: program.Words[(word + 1)]),
            z: BitConverter.UInt32BitsToSingle(value: program.Words[(word + 2)]),
            w: BitConverter.UInt32BitsToSingle(value: program.Words[(word + 3)])
        );
    }

    [Fact]
    public void LightThroughOneDepthOfTheGlassIsItsTint() {
        var tint = new Vector3(x: 0.96f, y: 0.51f, z: 0.13f);
        var absorption = Acrylic.Absorption(tint: tint);
        var through = new Vector3(x: MathF.Exp(x: (-absorption.X * Acrylic.Depth)), y: MathF.Exp(x: (-absorption.Y * Acrylic.Depth)), z: MathF.Exp(x: (-absorption.Z * Acrylic.Depth)));

        Assert.True(condition: (Vector3.Distance(value1: tint, value2: through) < 1e-5f), userMessage: $"{through}");
        // Twice the depth is the tint squared: deeper, never a stacked blend.
        Assert.True(condition: (MathF.Abs(x: (MathF.Exp(x: (-absorption.Y * (2f * Acrylic.Depth))) - (tint.Y * tint.Y))) < 1e-5f));
        // A black channel absorbs within a few depths rather than at once.
        Assert.True(condition: float.IsFinite(f: Acrylic.Absorption(tint: Vector3.Zero).X));
        Assert.Equal(expected: 0.04f, actual: new SdfGlass(Depth: 1f, Ior: 1.5f).NormalReflectance(), tolerance: 1e-6f);
    }
    [Fact]
    public void AGlassMaterialPacksItsAbsorptionAndIndexAndAnOpaqueOnePacksZeros() {
        var tint = new Vector3(x: 0.9f, y: 0.6f, z: 0.2f);
        var program = new SdfProgram(
            instructions: [Shape(material: 0), Shape(material: 1)],
            materials: [new SdfMaterial(Albedo: tint, Glass: Acrylic), new SdfMaterial(Albedo: Vector3.One)]
        );
        var glass = Row(material: 0, program: program, row: GlassRow);
        var opaque = Row(material: 1, program: program, row: GlassRow);
        var absorption = Acrylic.Absorption(tint: tint);

        Assert.Equal(expected: new Vector4(value: absorption, w: Acrylic.Ior), actual: glass);
        Assert.Equal(expected: Vector4.Zero, actual: opaque);
        Assert.True(condition: program.Materials[0].IsGlass);
        Assert.False(condition: program.Materials[1].IsGlass);
    }
    [Fact]
    public void BothAdmissionDoorsRefuseAnInadmissibleGlass() {
        foreach (var material in new SdfMaterial[] {
            new(Albedo: Vector3.One, Glass: new SdfGlass(Depth: 0f, Ior: 1.5f)),
            new(Albedo: Vector3.One, Glass: new SdfGlass(Depth: float.NaN, Ior: 1.5f)),
            new(Albedo: Vector3.One, Glass: new SdfGlass(Depth: 0.01f, Ior: 0.9f)),
            new(Albedo: Vector3.One, Glass: new SdfGlass(Depth: 0.01f, Ior: 5f)),
            new(Albedo: new Vector3(x: 1.5f, y: 0f, z: 0f), Glass: Acrylic),
        }) {
            Assert.False(condition: material.IsGlassValid);
            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgramBuilder().AddMaterial(material: material));
            Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new SdfProgram(instructions: [Shape(material: 0)], materials: [material]));
        }
        // An opaque material's albedo may exceed one; only a tint is bounded.
        Assert.True(condition: new SdfMaterial(Albedo: new Vector3(value: 2f)).IsGlassValid);
        _ = new SdfProgramBuilder().AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.5f, z: 0.5f), Glass: Acrylic));
    }
    [Fact]
    public void AProgramKnowsItsGlassShapesAndRanges() {
        var glass = new SdfMaterial(Albedo: Vector3.One, Glass: Acrylic);
        var opaque = new SdfMaterial(Albedo: Vector3.One);
        var mixed = new SdfProgram(instructions: [Shape(material: 0), Shape(material: 1), Shape(material: 1)], materials: [opaque, glass]);
        var plain = new SdfProgram(instructions: [Shape(material: 0)], materials: [opaque]);

        Assert.True(condition: mixed.HasGlass);
        Assert.False(condition: plain.HasGlass);
        Assert.True(condition: mixed.IsGlassMaterial(material: 1));
        Assert.False(condition: mixed.IsGlassMaterial(material: 0));
        Assert.False(condition: mixed.IsGlassMaterial(material: SdfProgramBuilder.ScreenMaterialId));
        Assert.True(condition: mixed.IsGlassRange(first: 1, end: 3));
        Assert.False(condition: mixed.IsGlassRange(first: 0, end: 3));
        Assert.False(condition: mixed.IsGlassRange(first: 0, end: 1));
        Assert.False(condition: mixed.IsGlassRange(first: 1, end: 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => mixed.IsGlassRange(first: 0, end: 4));
    }
}
