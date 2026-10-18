using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Laws over <see cref="SdfInstruction.Detail"/>: its validation and its exclusion from
/// <see cref="SdfFieldEvaluator"/>'s contact field.</summary>
public sealed class SdfDetailShapeLawTests {
    private static readonly SdfMaterial[] OneMaterial = [new SdfMaterial(Albedo: Vector3.One)];

    private static SdfProgram Build(IReadOnlyList<SdfInstruction> instructions) => new(
        instances: null,
        instructions: instructions,
        materials: OneMaterial,
        screenSurfaces: null
    );
    private static SdfInstruction Shape(bool detail, uint shape = ((uint)SdfShapeType.Sphere), uint blend = ((uint)SdfBlendOp.Union), uint material = 0u) => new(
        Blend: blend,
        Data0: new Vector4(
            w: 0f,
            x: 1f,
            y: 0f,
            z: 0f
        ),
        Data1: Vector4.Zero,
        Detail: detail,
        Material: material,
        Op: SdfOp.ShapeBlend,
        Shape: shape
    );

    /// <summary>The packed contract refuses a hand-assembled stream that carries Detail on anything but a ShapeBlend
    /// instruction — the flag is meaningless anywhere else, so a caller reaching it directly is told.</summary>
    [Fact]
    public void ConstructorRefusesDetailOnANonShapeInstruction() {
        var translate = new SdfInstruction(
            Blend: 0,
            Data0: new Vector4(
                value: Vector3.UnitX,
                w: 0f
            ),
            Data1: Vector4.Zero,
            Detail: true,
            Material: 0,
            Op: SdfOp.Translate,
            Shape: 0
        );

        var refusal = Assert.Throws<ArgumentException>(testCode: () => Build(instructions: [translate, Shape(detail: false)]));

        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "Detail",
            comparisonType: StringComparison.Ordinal
        );

        // Control: the same Translate without Detail builds.
        var plainTranslate = translate with { Detail = false };

        _ = Build(instructions: [plainTranslate, Shape(detail: false)]);
    }
    /// <summary>A detail shape composed alongside real geometry never changes the contact field it would otherwise
    /// win: a Detail sphere placed EXACTLY at the query point (so an included copy would report zero and win the
    /// material) leaves both the distance and the material exactly as the base plate alone reports.</summary>
    [Fact]
    public void FieldEvaluatorContactIsUnchangedByANearerDetailShape() {
        var plateMaterial = 0;
        var detailMaterial = 1;

        SdfFieldEvaluator BuildScene(bool detail) {
            var builder = new SdfProgramBuilder();
            var resolvedPlate = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            var resolvedDetail = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

            Assert.Equal(
                actual: resolvedPlate,
                expected: plateMaterial
            );
            Assert.Equal(
                actual: resolvedDetail,
                expected: detailMaterial
            );

            _ = builder
                .Box(
                halfExtents: new Vector3(
                    x: 10f,
                    y: 0.1f,
                    z: 10f
                ),
                material: resolvedPlate,
                round: 0f
            )
                .ResetPoint()
                .Translate(offset: new Vector3(
                x: 0f,
                y: 0.1f,
                z: 0f
            ))
                .Sphere(
                detail: detail,
                material: resolvedDetail,
                radius: 0.05f
            );

            return new SdfFieldEvaluator(program: builder.Build());
        }

        var queryPoint = FixedPosition.FromLocal(local: new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.FromDouble(value: 0.1),
            Z: FixedQ4816.Zero
        ));

        var withDetail = BuildScene(detail: true);

        Assert.True(condition: withDetail.TryDistance(
            distance: out var detailDistance,
            material: out var detailWinningMaterial,
            position: queryPoint
        ));
        Assert.Equal(
            actual: detailWinningMaterial,
            expected: plateMaterial
        );

        // Control: the SAME rivet without Detail wins the point outright (proving the query is not vacuously on the
        // plate already) — it sits exactly at the query point, so it reports distance zero and its own material.
        var withoutDetail = BuildScene(detail: false);

        Assert.True(condition: withoutDetail.TryDistance(
            distance: out var undetailedDistance,
            material: out var undetailedWinningMaterial,
            position: queryPoint
        ));
        Assert.Equal(
            actual: undetailedWinningMaterial,
            expected: detailMaterial
        );
        Assert.True(condition: (undetailedDistance < detailDistance));
    }
    /// <summary>A program whose only shape carries Detail has no contact geometry at all: <see cref="SdfFieldEvaluator"/>
    /// mirrors the GPU march's exclusion, so it reads exactly as shape-free — the same answer an empty stream
    /// gives.</summary>
    [Fact]
    public void FieldEvaluatorTreatsADetailOnlyProgramAsShapeFree() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Sphere(
            detail: true,
            material: material,
            radius: 1f
        );

        var evaluator = new SdfFieldEvaluator(program: builder.Build());
        var origin = FixedPosition.FromLocal(local: FixedVector3.Zero);

        Assert.False(condition: evaluator.TryDistance(
            distance: out _,
            material: out _,
            position: origin
        ));
        Assert.False(condition: evaluator.Overlap(
            center: origin,
            radius: FixedQ4816.FromDouble(value: 0.1)
        ));

        // Control: the identical sphere without Detail is real contact geometry, at the same query point.
        var controlBuilder = new SdfProgramBuilder();
        var controlMaterial = controlBuilder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = controlBuilder.Sphere(
            material: controlMaterial,
            radius: 1f
        );

        var controlEvaluator = new SdfFieldEvaluator(program: controlBuilder.Build());

        Assert.True(condition: controlEvaluator.TryDistance(
            distance: out _,
            material: out _,
            position: origin
        ));
        Assert.True(condition: controlEvaluator.Overlap(
            center: origin,
            radius: FixedQ4816.FromDouble(value: 0.1)
        ));
    }
    /// <summary>Superellipsoids preserve Detail at every exponent, the ellipsoid's exponent 2 included: the flag does
    /// not depend on a shape-specific lane.</summary>
    [Theory]
    [InlineData(2f)]
    [InlineData(4f)]
    [InlineData(8f)]
    public void SuperellipsoidPreservesDetailAtEveryExponent(float exponent) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Superellipsoid(
            radii: new Vector3(
                x: 1f,
                y: 2f,
                z: 3f
            ),
            exponent: exponent,
            material: material,
            detail: true
        );

        var program = builder.Build();

        Assert.True(condition: program.Instructions[0].Detail);
        var evaluator = new SdfFieldEvaluator(program: program);

        Assert.False(condition: evaluator.TryDistance(
            distance: out _,
            material: out _,
            position: FixedPosition.FromLocal(local: FixedVector3.Zero)
        ));
    }
}
