using System.Numerics;

using Puck.Forge.Authoring;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Queries;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the fixed-point transform boundary used by mirrored solid placements.</summary>
public sealed class MirroredSolidFieldDeterminismLawTests {
    [Fact]
    public void AnisotropicBoxScaleBakesIntoMetricShapeDimensions() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: "floor",
            Type: AvatarPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: new Vector3(x: 24f, y: 0.1f, z: 24f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "metric-box-law",
            Intent: CreatorIntent.Object,
            BakeStyle: null,
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

        var program = builder.Build(buildInstanceGrid: false);
        var scales = program.Instructions.Where(predicate: candidate => candidate.Op == SdfOp.Scale).ToArray();
        var box = Assert.Single(collection: program.Instructions, predicate: candidate =>
            (candidate.Op == SdfOp.ShapeBlend) && ((SdfShapeType)candidate.Shape == SdfShapeType.Box));
        var fixedThinAxis = (float)((double)FixedQ4816.FromDouble(value: 0.1));

        Assert.Single(collection: scales);
        Assert.Equal(expected: new Vector4(x: 1f, y: 1f, z: 1f, w: 1f), actual: scales[0].Data0);
        var expectedRound = (0.04f * fixedThinAxis);
        Assert.Equal(expected: new Vector4(x: ((0.38f * 24f) - expectedRound), y: (0.34f * fixedThinAxis), z: ((0.38f * 24f) - expectedRound), w: expectedRound), actual: box.Data0);
        _ = new SdfFieldEvaluator(program: program);
    }

    [Fact]
    public void QueryEvaluatorRejectsAConservativeAnisotropicScaleBound() {
        var builder = NewMaterialBuilder(material: out var material);
        var program = builder
            .Scale(scale: new Vector3(x: 24f, y: 0.1f, z: 24f))
            .Sphere(radius: 1f, material: material)
            .Build(buildInstanceGrid: false);

        var exception = Assert.Throws<ArgumentException>(() => new SdfFieldEvaluator(program: program));

        Assert.Contains(expectedSubstring: "non-uniform Scale", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "conservative march bound", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AxiallyScaledCylinderBakesIntoMetricShapeDimensions() {
        var builder = NewMaterialBuilder(material: out var material);

        _ = CreationGeometry.AppendScaledPrimitive(
            chain: builder,
            type: AvatarPrimitive.Cylinder,
            scale: new Vector3(x: 0.4f, y: 1.6f, z: 0.4f),
            material: material
        );

        var program = builder.Build(buildInstanceGrid: false);
        var cylinder = Assert.Single(collection: program.Instructions, predicate: candidate =>
            (candidate.Op == SdfOp.ShapeBlend) && ((SdfShapeType)candidate.Shape == SdfShapeType.Cylinder));

        Assert.DoesNotContain(collection: program.Instructions, filter: candidate => candidate.Op == SdfOp.Scale);
        Assert.Equal(expected: new Vector4(x: (0.30f * 0.4f), y: (0.36f * 1.6f), z: 0f, w: 0f), actual: cylinder.Data0);
        _ = new SdfFieldEvaluator(program: program);
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
            .Sphere(radius: 1f, material: material)
            .Build(buildInstanceGrid: false);
        var instruction = Assert.Single(collection: program.Instructions, predicate: candidate => candidate.Op == SdfOp.Rotate);

        Assert.NotEqual(expected: expected, actual: platformNormalized);
        Assert.Equal(
            expected: new Vector4(x: expected.X, y: expected.Y, z: expected.Z, w: expected.W),
            actual: instruction.Data0
        );
    }

    [Fact]
    public void FixedStampEmissionReflectsPositionAndProperFrameBeforeFloatEncoding() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: AvatarPrimitive.Box,
            Position: new Vector3(x: 1f, y: 2f, z: 3f),
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
            Intent: CreatorIntent.Object,
            BakeStyle: null,
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
                ReflectionNormal: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero)
            ),
            materialFor: _ => material
        );

        var program = builder.Build(buildInstanceGrid: false);
        var translations = program.Instructions.Where(predicate: candidate => candidate.Op == SdfOp.Translate).ToArray();
        var rotations = program.Instructions.Where(predicate: candidate => candidate.Op == SdfOp.Rotate).ToArray();

        Assert.Equal(expected: 2, actual: translations.Length);
        Assert.Equal(expected: new Vector4(x: 1f, y: -2f, z: 3f, w: 0f), actual: translations[1].Data0);
        Assert.Equal(expected: 2, actual: rotations.Length);
        // Reflection in Y followed by the legacy X-axis handedness repair is a half turn around Z.
        Assert.Equal(expected: new Vector4(x: 0f, y: 0f, z: 1f, w: 0f), actual: rotations[1].Data0);
    }

    [Fact]
    public void FixedMirroredFrameMatchesTheEstablishedRenderMeaningForAnArbitraryOrientation() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: AvatarPrimitive.Box,
            Position: new Vector3(x: 1.25f, y: -0.75f, z: 2.5f),
            Rotation: Quaternion.Normalize(value: new Quaternion(x: 0.2f, y: -0.3f, z: 0.4f, w: 0.8f)),
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "mirrored-frame-equivalence-law",
            Intent: CreatorIntent.Object,
            BakeStyle: null,
            Palette: null,
            Shapes: [shape],
            Frames: null
        );
        var normal = Vector3.Normalize(value: new Vector3(x: 1f, y: 2f, z: -3f));
        var floatBuilder = NewMaterialBuilder(material: out var floatMaterial);
        var fixedBuilder = NewMaterialBuilder(material: out var fixedMaterial);

        CreationStampEmitter.Emit(
            builder: floatBuilder,
            document: document,
            transform: new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: normal),
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

        var floatProgram = floatBuilder.Build(buildInstanceGrid: false);
        var fixedProgram = fixedBuilder.Build(buildInstanceGrid: false);
        var floatShapeTranslation = floatProgram.Instructions.Where(predicate: candidate => candidate.Op == SdfOp.Translate).ElementAt(index: 1).Data0;
        var fixedShapeTranslation = fixedProgram.Instructions.Where(predicate: candidate => candidate.Op == SdfOp.Translate).ElementAt(index: 1).Data0;
        var floatShapeRotation = RotationOf(program: floatProgram, index: 1);
        var fixedShapeRotation = RotationOf(program: fixedProgram, index: 1);

        Assert.InRange(
            actual: Vector3.Distance(
                value1: new Vector3(x: floatShapeTranslation.X, y: floatShapeTranslation.Y, z: floatShapeTranslation.Z),
                value2: new Vector3(x: fixedShapeTranslation.X, y: fixedShapeTranslation.Y, z: fixedShapeTranslation.Z)
            ),
            low: 0f,
            high: 0.001f
        );
        Assert.InRange(actual: MathF.Abs(x: Quaternion.Dot(quaternion1: floatShapeRotation, quaternion2: fixedShapeRotation)), low: 0.999f, high: 1.001f);
    }

    private static SdfProgramBuilder NewMaterialBuilder(out int material) {
        var builder = new SdfProgramBuilder();

        material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        return builder;
    }

    private static Quaternion RotationOf(SdfProgram program, int index) {
        var data = program.Instructions.Where(predicate: candidate => candidate.Op == SdfOp.Rotate).ElementAt(index: index).Data0;

        return new Quaternion(x: data.X, y: data.Y, z: data.Z, w: data.W);
    }
}
