using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Laws over <see cref="SdfInstruction.Secondary"/>: the packed-word flag bit, its opposite exclusion set
/// from <see cref="SdfInstruction.Detail"/> (a non-secondary shape is ordinary CONTACT geometry — only the GPU
/// soft-shadow/AO field walks in sdf-world.hlsli drop it, unverified by machine here), and the builder's
/// <see cref="SdfProgramBuilder.MarkSecondary"/> chain contract.</summary>
public sealed class SdfSecondaryShapeLawTests {
    private static readonly SdfMaterial[] OneMaterial = [new SdfMaterial(Albedo: Vector3.One)];

    private static SdfInstruction Shape(bool secondary, uint shape = ((uint)SdfShapeType.Sphere), uint blend = ((uint)SdfBlendOp.Union), uint material = 0u) => new(
        Blend: blend,
        Data0: new Vector4(
            w: 0f,
            x: 1f,
            y: 0f,
            z: 0f
        ),
        Data1: Vector4.Zero,
        Material: material,
        Op: SdfOp.ShapeBlend,
        Secondary: secondary,
        Shape: shape
    );
    private static SdfProgram Build(IReadOnlyList<SdfInstruction> instructions) => new(
        instances: null,
        instructions: instructions,
        materials: OneMaterial,
        screenSurfaces: null
    );

    /// <summary>The flag packs into the Shape lane's next-highest bit (below Detail's), and the shape id stays
    /// readable underneath both — three independent lanes in one word.</summary>
    [Fact]
    public void PackingOrsTheNoSecondaryFlagOntoTheShapeLaneNextBit() {
        var excluded = Build(instructions: [Shape(secondary: false, shape: ((uint)SdfShapeType.Box))]);
        // Header word 0 is (instructionCount, ...); instruction headers start one uvec4 in, lane 1 = the shape.
        var packedShape = excluded.Words[5];

        Assert.Equal(
            actual: (packedShape & 0x40000000u),
            expected: 0x40000000u
        );
        Assert.Equal(
            actual: (packedShape & 0x80000000u),
            expected: 0u
        );
        Assert.Equal(
            actual: (packedShape & 0x3FFFFFFFu),
            expected: ((uint)SdfShapeType.Box)
        );

        // Control: the same shape with Secondary true (the default) packs the bare id, no high bits.
        var plain = Build(instructions: [Shape(secondary: true, shape: ((uint)SdfShapeType.Box))]);

        Assert.Equal(
            actual: plain.Words[5],
            expected: ((uint)SdfShapeType.Box)
        );
    }
    /// <summary>The packed contract refuses a hand-assembled stream that carries Secondary=false on anything but a
    /// ShapeBlend instruction — the flag is meaningless anywhere else.</summary>
    [Fact]
    public void ConstructorRefusesNonSecondaryOnANonShapeInstruction() {
        var translate = new SdfInstruction(
            Blend: 0,
            Data0: new Vector4(
                value: Vector3.UnitX,
                w: 0f
            ),
            Data1: Vector4.Zero,
            Material: 0,
            Op: SdfOp.Translate,
            Secondary: false,
            Shape: 0
        );

        var refusal = Assert.Throws<ArgumentException>(testCode: () => Build(instructions: [translate, Shape(secondary: true)]));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "Secondary",
            comparisonType: StringComparison.Ordinal
        );

        // Control: the same Translate with Secondary true (the default) builds.
        var plainTranslate = translate with { Secondary = true };

        _ = Build(instructions: [plainTranslate, Shape(secondary: true)]);
    }
    /// <summary>Opposite of <see cref="SdfDetailShapeLawTests"/>'s Detail-only-program law: a non-secondary shape is
    /// ordinary contact geometry to <see cref="SdfFieldEvaluator"/>, which has no notion of shadow/AO march mode —
    /// TryDistance/Overlap answer exactly as they would for the same shape with Secondary true.</summary>
    [Fact]
    public void FieldEvaluatorTreatsANonSecondaryShapeAsOrdinaryContactGeometry() {
        SdfFieldEvaluator BuildScene(bool secondary) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.Sphere(
                material: material,
                radius: 1f
            ).MarkSecondary(secondary: secondary);

            return new SdfFieldEvaluator(program: builder.Build());
        }

        var origin = FixedPosition.FromLocal(local: FixedVector3.Zero);
        var excluded = BuildScene(secondary: false);
        var ordinary = BuildScene(secondary: true);

        Assert.True(condition: excluded.TryDistance(
            distance: out var excludedDistance,
            material: out var excludedMaterial,
            position: origin
        ));
        Assert.True(condition: ordinary.TryDistance(
            distance: out var ordinaryDistance,
            material: out var ordinaryMaterial,
            position: origin
        ));
        Assert.Equal(actual: excludedDistance.Value, expected: ordinaryDistance.Value);
        Assert.Equal(actual: excludedMaterial, expected: ordinaryMaterial);

        var radius = FixedQ4816.FromDouble(value: 0.5);

        Assert.Equal(
            actual: excluded.Overlap(
                center: origin,
                radius: radius
            ),
            expected: ordinary.Overlap(
                center: origin,
                radius: radius
            )
        );
    }
    /// <summary>Detail and Secondary=false are independent bits: a shape carrying both packs both flags and neither
    /// masks the other off the shape-type lane.</summary>
    [Fact]
    public void DetailAndNonSecondaryComposeAsIndependentBits() {
        var both = Build(instructions: [
            (Shape(secondary: false, shape: ((uint)SdfShapeType.Box)) with { Detail = true })
        ]);
        var packedShape = both.Words[5];

        Assert.Equal(actual: (packedShape & 0x80000000u), expected: 0x80000000u);
        Assert.Equal(actual: (packedShape & 0x40000000u), expected: 0x40000000u);
        Assert.Equal(actual: (packedShape & 0x3FFFFFFFu), expected: ((uint)SdfShapeType.Box));
    }
    /// <summary><see cref="SdfProgramBuilder.MarkSecondary"/> must chain directly onto the shape method that emitted
    /// the instruction it marks — a field op appended first moves "most recent" past the ShapeBlend and the call is
    /// refused rather than silently marking the wrong (or no) instruction.</summary>
    [Fact]
    public void MarkSecondaryRefusesWhenTheMostRecentInstructionIsNotAShapeBlend() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Sphere(
            material: material,
            radius: 1f
        ).Dilate(radius: 0.1f);

        Assert.Throws<InvalidOperationException>(testCode: () => builder.MarkSecondary(secondary: false));

        // Control: an empty builder (no instruction at all) is refused the same way.
        Assert.Throws<InvalidOperationException>(testCode: () => new SdfProgramBuilder().MarkSecondary(secondary: false));
    }
    /// <summary><see cref="SdfProgramBuilder.MarkSecondary"/> chained immediately after the shape it marks packs the
    /// flag onto that exact instruction and leaves every earlier instruction untouched.</summary>
    [Fact]
    public void MarkSecondaryPatchesOnlyTheMostRecentShapeInstruction() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Sphere(
            material: material,
            radius: 1f
        );
        _ = builder
            .ResetPoint()
            .Translate(offset: Vector3.UnitX)
            .Sphere(
            material: material,
            radius: 1f
        ).MarkSecondary(secondary: false);

        var program = builder.Build();
        // Header word 0 is (instructionCount, ...); each instruction header is one uvec4, lane 1 = the shape.
        var instructionCount = program.Words[0];
        var firstShapeLane = program.Words[5];
        var secondShapeLane = program.Words[(int)(1 + (instructionCount - 1)) * 4 + 1];

        Assert.Equal(actual: (firstShapeLane & 0x40000000u), expected: 0u);
        Assert.Equal(actual: (secondShapeLane & 0x40000000u), expected: 0x40000000u);
    }
}
