using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="ShapeDocument.Detail"/> — a shading-only shape, skipped by the march and included only at an
/// already-found hit — is admitted on any primitive type, refused by name alongside <see cref="ShapeDocument.Panel"/>
/// or <see cref="ShapeDocument.Trims"/> on the same shape (both emit a second, independently-composed copy Detail
/// cannot describe), reaches the packed program's ShapeBlend instruction unchanged on both emission paths, and never
/// reaches the deterministic fixed-point contact evaluator either path feeds — so a detail shape's presence never
/// changes the collider a creation compiles.
/// </summary>
public sealed class ShapeDetailLawTests {
    private const string PrototypeId = "detailed";
    private static readonly Vector3 PlateScale = new(x: 0.4f, y: 0.3f, z: 0.2f);
    private static readonly ShapePanelDocument SomePanel = new(Inset: 0.05f, Depth: 0.1f, Material: 1);
    private static readonly ShapeTrimDocument SomeTrim = new(Shape: "cutter", Width: 0.05f, Material: 1, Inset: 0.003f);

    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, bool? detail = null, ShapePanelDocument? panel = null, IReadOnlyList<ShapeTrimDocument>? trims = null, string? name = null, Vector3? position = null, int id = 0) =>
        new(
            Id: id,
            Name: ((name is null) ? null : (DocumentIdentifier)name),
            Type: type,
            Position: (position ?? Vector3.Zero),
            Rotation: Quaternion.Identity,
            Scale: scale,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: null,
            Detail: detail,
            Panel: panel,
            Trims: trims
        );
    private static CreationDocument Document(params ShapeDocument[] shapes) =>
        new(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: null,
            Shapes: shapes,
            Frames: null
        );
    private static void AssertCanonicalizerRefusesNaming(CreationDocument document, string needle) {
        var violations = CreationCanonicalizer.Validate(document: document);

        Assert.NotEmpty(collection: violations);
        Assert.Contains(
            collection: violations,
            filter: violation => violation.Message.Contains(comparisonType: StringComparison.Ordinal, value: needle)
        );
    }
    private static void AssertCanonicalizerAccepts(CreationDocument document) {
        var violations = CreationCanonicalizer.Validate(document: document);

        Assert.Empty(collection: violations);
    }

    [Theory]
    [InlineData(SdfSolidPrimitive.Box)]
    [InlineData(SdfSolidPrimitive.Sphere)]
    [InlineData(SdfSolidPrimitive.Capsule)]
    public void ADetailShapeOfAnyPrimitiveTypeIsAccepted(SdfSolidPrimitive type) =>
        AssertCanonicalizerAccepts(document: Document(Shape(type, PlateScale, detail: true)));

    [Fact]
    public void ADetailShapeWithAPanelIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(Shape(SdfSolidPrimitive.Box, PlateScale, detail: true, panel: SomePanel)),
            needle: "detail shape cannot also carry a panel"
        );

    [Fact]
    public void ADetailShapeWithTrimsIsRefusedByName() =>
        AssertCanonicalizerRefusesNaming(
            document: Document(
                Shape(SdfSolidPrimitive.Sphere, PlateScale, name: "cutter", id: 0),
                Shape(SdfSolidPrimitive.Box, PlateScale, detail: true, trims: [SomeTrim], name: "host", id: 1)
            ),
            needle: "detail shape cannot also carry trims"
        );

    [Fact]
    public void APanelOrTrimHostIsUnaffectedByAnUnrelatedDetailSibling() =>
        AssertCanonicalizerAccepts(document: Document(
            Shape(SdfSolidPrimitive.Sphere, PlateScale, detail: true, name: "rivet", id: 0),
            Shape(SdfSolidPrimitive.Box, PlateScale, panel: SomePanel, name: "host", id: 1)
        ));

    // The render seam: both emission paths funnel every shape through SdfProgramBuilder's Shape() core, which packs
    // Detail as a real flag on the ShapeBlend instruction — never dropped, never approximated.
    private static SdfProgram EmitStatic(ShapeDocument shape) {
        var builder = new SdfProgramBuilder();
        var albedo = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        CreationStampEmitter.Emit(
            builder: builder,
            document: Document(shape),
            transform: new CreationStampTransform(Origin: Vector3.Zero, Rotation: Quaternion.Identity, Scale: 1f, ReflectionNormal: null),
            materialFor: _ => albedo
        );

        return builder.Build(buildInstanceGrid: false);
    }

    [Fact]
    public void EmitStaticPacksTheDetailFlagOnTheShapeInstruction() {
        var detailed = EmitStatic(shape: Shape(SdfSolidPrimitive.Sphere, PlateScale, detail: true));
        var shapeInstruction = Assert.Single(collection: detailed.Instructions, predicate: instruction => (instruction.Op == SdfOp.ShapeBlend));

        Assert.True(condition: shapeInstruction.Detail);

        // Control: the same shape with no Detail packs the plain, unflagged instruction.
        var plain = EmitStatic(shape: Shape(SdfSolidPrimitive.Sphere, PlateScale, detail: false));
        var plainInstruction = Assert.Single(collection: plain.Instructions, predicate: instruction => (instruction.Op == SdfOp.ShapeBlend));

        Assert.False(condition: plainInstruction.Detail);
    }

    // The contact/collider seam: EmitFixed/VisitFixedPrimitiveCopies never visit a Detail shape at all — the copies
    // a plate with an added detail rivet visits are identical to the plate alone.
    [Fact]
    public void ContactCopiesAreIdenticalWithAndWithoutADetailSibling() {
        var withRivet = Document(
            Shape(SdfSolidPrimitive.Box, PlateScale, name: "plate", id: 0),
            Shape(SdfSolidPrimitive.Sphere, new Vector3(value: 0.05f), detail: true, name: "rivet", position: new Vector3(0f, PlateScale.Y, 0f), id: 1)
        );
        var bare = Document(Shape(SdfSolidPrimitive.Box, PlateScale, name: "plate", id: 0));
        var transform = new FixedCreationStampTransform(
            Origin: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity,
            Scale: FixedQ4816.One,
            ReflectionNormal: null
        );
        var withRivetCopies = new List<FixedCreationStampPrimitiveCopy>();
        var bareCopies = new List<FixedCreationStampPrimitiveCopy>();

        CreationStampEmitter.VisitFixedPrimitiveCopies(document: withRivet, transform: transform, visitor: withRivetCopies.Add);
        CreationStampEmitter.VisitFixedPrimitiveCopies(document: bare, transform: transform, visitor: bareCopies.Add);

        Assert.Single(collection: bareCopies);
        Assert.Single(collection: withRivetCopies);
        Assert.Equal(actual: withRivetCopies[0].Center, expected: bareCopies[0].Center);
        Assert.Equal(actual: withRivetCopies[0].HalfExtents, expected: bareCopies[0].HalfExtents);
    }
}
