using System.Numerics;

using Puck.World.Authoring;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Laws for the fixed-point transform boundary used by mirrored solid placements.</summary>
public sealed class MirroredSolidFieldDeterminismLawTests {
    private static SdfProgramBuilder NewMaterialBuilder(out int material) {
        var builder = new SdfProgramBuilder();

        material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder;
    }
    private static Quaternion LastRotationOf(SdfProgram program) {
        var data = program.Instructions.Last(predicate: candidate => (candidate.Op == SdfOp.Rotate)).Data0;

        return new Quaternion(
            w: data.W,
            x: data.X,
            y: data.Y,
            z: data.Z
        );
    }

    [Fact]
    public void AnisotropicBoxScaleBakesIntoMetricShapeDimensions() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: "floor",
            Type: SdfSolidPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(
                x: 24f,
                y: 0.1f,
                z: 24f
            ),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "metric-box-law",
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var builder = NewMaterialBuilder(material: out var material);

        CreationStampEmitter.EmitFixed(
            builder: builder,
            document: document,
            transform: new FixedCreationStampTransform(
                Origin: FixedVector3.Zero,
                Rotation: FixedQuaternion.Identity,
                Scale: FixedQ4816.One,
                ReflectionNormal: null
            ),
            materialFor: _ => material
        );

        var program = builder.Build();
        var box = Assert.Single(
            collection: program.Instructions,
            predicate: candidate =>
            ((candidate.Op == SdfOp.ShapeBlend) && (((SdfShapeType)candidate.Shape) == SdfShapeType.Box))
        );
        var fixedThinAxis = ((float)((double)FixedQ4816.FromDouble(value: 0.1)));

        // The stamp's unit scale and the shape's own unit scale are identity point operations, which the builder does
        // not emit: the authored (24, 0.1, 24) lives only in the box's own dimensions.
        Assert.DoesNotContain(
            collection: program.Instructions,
            filter: candidate => (candidate.Op == SdfOp.Scale)
        );
        // A box is sharp unless its document rounds it, so the fillet lane carries nothing here.
        Assert.Equal(
            expected: new Vector4(
                w: 0f,
                x: 24f,
                y: fixedThinAxis,
                z: 24f
            ),
            actual: box.Data0
        );
        _ = new SdfFieldEvaluator(program: program);
    }
    [Fact]
    public void AxiallyScaledCylinderBakesIntoMetricShapeDimensions() {
        var builder = NewMaterialBuilder(material: out var material);

        _ = SdfSolidGeometry.AppendScaledPrimitive(
            chain: builder,
            type: SdfSolidPrimitive.Cylinder,
            scale: new Vector3(
                x: 0.4f,
                y: 1.6f,
                z: 0.4f
            ),
            material: material
        );

        var program = builder.Build();
        var cylinder = Assert.Single(
            collection: program.Instructions,
            predicate: candidate =>
            ((candidate.Op == SdfOp.ShapeBlend) && (((SdfShapeType)candidate.Shape) == SdfShapeType.Cylinder))
        );

        Assert.DoesNotContain(
            collection: program.Instructions,
            filter: candidate => (candidate.Op == SdfOp.Scale)
        );
        Assert.Equal(
            expected: new Vector4(
                w: 0f,
                x: (1f * 0.4f),
                y: (1f * 1.6f),
                z: 0f
            ),
            actual: cylinder.Data0
        );
        _ = new SdfFieldEvaluator(program: program);
    }
    [Fact]
    public void FixedMirroredFrameMatchesTheEstablishedRenderMeaningForAnArbitraryOrientation() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Box,
            Position: new Vector3(
                x: 1.25f,
                y: -0.75f,
                z: 2.5f
            ),
            Rotation: Quaternion.Normalize(value: new Quaternion(
                w: 0.8f,
                x: 0.2f,
                y: -0.3f,
                z: 0.4f
            )),
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "mirrored-frame-equivalence-law",
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var normal = Vector3.Normalize(value: new Vector3(
            x: 1f,
            y: 2f,
            z: -3f
        ));
        var floatBuilder = NewMaterialBuilder(material: out var floatMaterial);
        var fixedBuilder = NewMaterialBuilder(material: out var fixedMaterial);

        CreationStampEmitter.Emit(
            builder: floatBuilder,
            document: document,
            transform: new CreationStampTransform(
                Origin: Vector3.Zero,
                Rotation: Quaternion.Identity,
                Scale: 1f,
                ReflectionNormal: normal
            ),
            materialFor: _ => floatMaterial
        );
        CreationStampEmitter.EmitFixed(
            builder: fixedBuilder,
            document: document,
            transform: new FixedCreationStampTransform(
                Origin: FixedVector3.Zero,
                Rotation: FixedQuaternion.Identity,
                Scale: FixedQ4816.One,
                ReflectionNormal: FixedVector3.FromVector3(value: normal)
            ),
            materialFor: _ => fixedMaterial
        );

        var floatProgram = floatBuilder.Build();
        var fixedProgram = fixedBuilder.Build();
        // The stamp's own zero origin and identity rotation are not emitted, so the shape's pose is the last of each.
        var floatShapeTranslation = floatProgram.Instructions.Last(predicate: candidate => (candidate.Op == SdfOp.Translate)).Data0;
        var fixedShapeTranslation = fixedProgram.Instructions.Last(predicate: candidate => (candidate.Op == SdfOp.Translate)).Data0;
        var floatShapeRotation = LastRotationOf(program: floatProgram);
        var fixedShapeRotation = LastRotationOf(program: fixedProgram);

        Assert.InRange(
            actual: Vector3.Distance(
                value1: new Vector3(
                    x: floatShapeTranslation.X,
                    y: floatShapeTranslation.Y,
                    z: floatShapeTranslation.Z
                ),
                value2: new Vector3(
                    x: fixedShapeTranslation.X,
                    y: fixedShapeTranslation.Y,
                    z: fixedShapeTranslation.Z
                )
            ),
            low: 0f,
            high: 0.001f
        );
        Assert.InRange(
            actual: MathF.Abs(x: Quaternion.Dot(
                quaternion1: floatShapeRotation,
                quaternion2: fixedShapeRotation
            )),
            low: 0.999f,
            high: 1.001f
        );
    }
    [Fact]
    public void FixedRotationOverloadPacksTheFixedNormalizedQuaternionWithoutRenormalizingInFloat() {
        var authored = new FixedQuaternion(
            X: FixedQ4816.FromRawBits(value: 10_000L),
            Y: FixedQ4816.FromRawBits(value: 20_000L),
            Z: FixedQ4816.FromRawBits(value: 30_000L),
            W: FixedQ4816.FromRawBits(value: 40_000L)
        );
        var expected = authored.Normalize().ToQuaternion();
        var platformNormalized = Quaternion.Normalize(value: authored.ToQuaternion());
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder
            .Rotate(rotation: authored)
            .Sphere(
            radius: 1f,
            material: material
        )
            .Build();
        var instruction = Assert.Single(
            collection: program.Instructions,
            predicate: candidate => (candidate.Op == SdfOp.Rotate)
        );

        Assert.NotEqual(
            actual: platformNormalized,
            expected: expected
        );
        Assert.Equal(
            expected: new Vector4(
                w: expected.W,
                x: expected.X,
                y: expected.Y,
                z: expected.Z
            ),
            actual: instruction.Data0
        );
    }
    [Fact]
    public void FixedStampEmissionReflectsThePointOnceAfterTheStampScale() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Box,
            Position: new Vector3(
                x: 1f,
                y: 2f,
                z: 3f
            ),
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "mirrored-solid-law",
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.EmitFixed(
            builder: builder,
            document: document,
            transform: new FixedCreationStampTransform(
                Origin: FixedVector3.Zero,
                Rotation: FixedQuaternion.Identity,
                Scale: FixedQ4816.One,
                ReflectionNormal: new FixedVector3(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.Zero
                )
            ),
            materialFor: _ => material
        );

        var program = builder.Build();
        // The stamp's reflection as the fixed alignment carrying X onto +Y, a mirror of X and the alignment back, then
        // the shape's own authored position, unreflected. The stamp's zero origin, identity rotation and unit scale, and
        // the shape's identity rotation and unit scale, are identity point operations the builder does not emit.
        var alignment = new FixedQuaternion(
            W: FixedQ4816.One,
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.One
        ).Normalize().ToQuaternion();
        var chain = program.Instructions.TakeWhile(predicate: candidate => (candidate.Op != SdfOp.ShapeBlend)).Select(selector: candidate => (candidate.Op, candidate.Data0)).ToArray();

        Assert.Equal(
            expected: [
                (SdfOp.ResetPoint, Vector4.Zero),
                (SdfOp.Rotate, new Vector4(
                    w: alignment.W,
                    x: alignment.X,
                    y: alignment.Y,
                    z: alignment.Z
                )),
                (SdfOp.Scale, new Vector4(
                    w: 1f,
                    x: -1f,
                    y: 1f,
                    z: 1f
                )),
                (SdfOp.Rotate, new Vector4(
                    w: alignment.W,
                    x: -alignment.X,
                    y: -alignment.Y,
                    z: -alignment.Z
                )),
                (SdfOp.Translate, new Vector4(
                    w: 0f,
                    x: 1f,
                    y: 2f,
                    z: 3f
                )),
            ],
            actual: chain
        );
    }
    [Fact]
    public void QueryEvaluatorRejectsAConservativeAnisotropicScaleBound() {
        var builder = NewMaterialBuilder(material: out var material);
        var program = builder
            .Scale(scale: new Vector3(
            x: 24f,
            y: 0.1f,
            z: 24f
        ))
            .Sphere(
            radius: 1f,
            material: material
        )
            .Build();

        var exception = Assert.Throws<ArgumentException>(testCode: () => new SdfFieldEvaluator(program: program));

        Assert.Contains(
            expectedSubstring: "non-uniform Scale",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: "conservative march bound",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }
}
