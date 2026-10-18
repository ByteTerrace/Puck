using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Laws over <see cref="SdfInstruction.Secondary"/>: its validation, that a non-secondary shape is ordinary
/// contact geometry (unlike <see cref="SdfInstruction.Detail"/>), and the builder's
/// <see cref="SdfProgramBuilder.MarkSecondary"/> chain contract.</summary>
public sealed class SdfSecondaryShapeLawTests {
    private static readonly SdfMaterial[] OneMaterial = [new SdfMaterial(Albedo: Vector3.One)];

    private static SdfProgram Build(IReadOnlyList<SdfInstruction> instructions) => new(
        instances: null,
        instructions: instructions,
        materials: OneMaterial,
        screenSurfaces: null
    );
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
        Assert.Equal(
            actual: excludedDistance.Value,
            expected: ordinaryDistance.Value
        );
        Assert.Equal(
            actual: excludedMaterial,
            expected: ordinaryMaterial
        );

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
    /// <summary><see cref="SdfProgramBuilder.MarkSecondary"/> chained immediately after the shape it marks sets the
    /// flag on that exact instruction and leaves every earlier instruction untouched.</summary>
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

        Assert.True(condition: program.Instructions[0].Secondary);
        Assert.False(condition: program.Instructions[^1].Secondary);
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
}
