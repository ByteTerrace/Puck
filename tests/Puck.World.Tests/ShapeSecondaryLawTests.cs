using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="ShapeDocument.Secondary"/> — false excludes a shape from ONLY the soft-shadow/AO field walks,
/// the opposite exclusion set from <see cref="ShapeDocument.Detail"/> — is admitted on any primitive type with no
/// Panel/Trims restriction, reaches the packed program's ShapeBlend instruction unchanged on both the static
/// (<see cref="CreationStampEmitter"/>) and animated (<see cref="WorldStampPool"/>) emission paths, and never changes
/// the deterministic fixed-point contact evaluator's collider — a false shape still carves and still collides.
/// </summary>
public sealed class ShapeSecondaryLawTests {
    private const string PrototypeId = "secondary";
    private static readonly Vector3 PlateScale = new(x: 0.4f, y: 0.3f, z: 0.2f);

    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, bool? secondary = null, string? name = null, Vector3? position = null, int id = 0) =>
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
            Secondary: secondary
        );
    private static CreationDocument Document(params ShapeDocument[] shapes) =>
        new(
            Schema: CreationDocument.CurrentSchema,
            Name: PrototypeId,
            Palette: null,
            Shapes: shapes,
            Frames: null
        );
    private static void AssertCanonicalizerAccepts(CreationDocument document) {
        var violations = CreationCanonicalizer.Validate(document: document);

        Assert.Empty(collection: violations);
    }

    [Theory]
    [InlineData(SdfSolidPrimitive.Box)]
    [InlineData(SdfSolidPrimitive.Sphere)]
    [InlineData(SdfSolidPrimitive.Capsule)]
    public void ANonSecondaryShapeOfAnyPrimitiveTypeIsAccepted(SdfSolidPrimitive type) =>
        AssertCanonicalizerAccepts(document: Document(Shape(type, PlateScale, secondary: false)));

    [Fact]
    public void ANonSecondaryShapeMayStillCarryAPanelOrTrims() {
        var panel = new ShapePanelDocument(Inset: 0.05f, Depth: 0.1f, Material: 1);
        var trim = new ShapeTrimDocument(Shape: "cutter", Width: 0.05f, Material: 1, Inset: 0.003f);

        AssertCanonicalizerAccepts(document: Document(
            Shape(SdfSolidPrimitive.Sphere, PlateScale, name: "cutter", id: 0),
            (Shape(SdfSolidPrimitive.Box, PlateScale, secondary: false, name: "host", id: 1) with {
                Panel = panel,
                Trims = [trim],
            })
        ));
    }

    // The render seam: both emission paths funnel every shape through SdfProgramBuilder's Shape() core plus
    // MarkSecondary, which packs Secondary as a real flag on the ShapeBlend instruction — never dropped.
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
    public void EmitStaticPacksTheNonSecondaryFlagOnTheShapeInstruction() {
        var excluded = EmitStatic(shape: Shape(SdfSolidPrimitive.Sphere, PlateScale, secondary: false));
        var shapeInstruction = Assert.Single(collection: excluded.Instructions, predicate: instruction => (instruction.Op == SdfOp.ShapeBlend));

        Assert.False(condition: shapeInstruction.Secondary);

        // Control: an unauthored (null) Secondary defaults true, packing the plain, unflagged instruction.
        var plain = EmitStatic(shape: Shape(SdfSolidPrimitive.Sphere, PlateScale));
        var plainInstruction = Assert.Single(collection: plain.Instructions, predicate: instruction => (instruction.Op == SdfOp.ShapeBlend));

        Assert.True(condition: plainInstruction.Secondary);
    }

    // The contact/collider seam: EmitFixed/VisitFixedPrimitiveCopies visit a non-secondary shape exactly like an
    // ordinary one — the opposite of ShapeDetailLawTests' exclusion law.
    [Fact]
    public void ContactCopiesAreIdenticalWhetherOrNotAShapeIsSecondary() {
        var secondary = Document(Shape(SdfSolidPrimitive.Box, PlateScale, secondary: true, name: "plate", id: 0));
        var nonSecondary = Document(Shape(SdfSolidPrimitive.Box, PlateScale, secondary: false, name: "plate", id: 0));
        var transform = new FixedCreationStampTransform(
            Origin: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity,
            Scale: FixedQ4816.One,
            ReflectionNormal: null
        );
        var secondaryCopies = new List<FixedCreationStampPrimitiveCopy>();
        var nonSecondaryCopies = new List<FixedCreationStampPrimitiveCopy>();

        CreationStampEmitter.VisitFixedPrimitiveCopies(document: secondary, transform: transform, visitor: secondaryCopies.Add);
        CreationStampEmitter.VisitFixedPrimitiveCopies(document: nonSecondary, transform: transform, visitor: nonSecondaryCopies.Add);

        Assert.Single(collection: secondaryCopies);
        Assert.Single(collection: nonSecondaryCopies);
        Assert.Equal(actual: nonSecondaryCopies[0].Center, expected: secondaryCopies[0].Center);
        Assert.Equal(actual: nonSecondaryCopies[0].HalfExtents, expected: secondaryCopies[0].HalfExtents);
    }

    // The animated pool seam: WorldStampPool.EmitShape threads the same flag through MarkSecondary.
    private static SdfInstruction PoolShapeInstruction(ShapeDocument shape) {
        var canonical = CreationCanonicalizer.Canonicalize(
            document: new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: PrototypeId,
                Palette: [new("#AAAAAA", null, null, null)],
                Shapes: [shape],
                Frames: null
            ),
            source: PrototypeId
        );
        var creation = new WorldPrototype(Id: PrototypeId, Document: canonical.Document, HashRaw: canonical.Hash);
        var definition = (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
            CreationsRaw = [creation],
            LookRowsRaw = [new WorldLook(Name: "rig", Source: new WorldLookSource.Creation(PrototypeId: PrototypeId), Scale: 1f, Motion: WorldLookMotion.Default)],
        });
        var pool = new WorldStampPool();

        pool.Reconcile(
            placements: [],
            creations: [creation],
            dynamics: [],
            bodyStamps: [new WorldStampPool.BodyStamp(BodyIndex: 0, Creation: creation, Scale: 1f, Motion: WorldLookMotion.Default)]
        );

        var builder = new SdfProgramBuilder();

        pool.Emit(builder: builder, definition: definition, probeWorstCase: false, maxPlacementScale: 1f, slotBase: 0);

        var program = builder.Build(buildInstanceGrid: false);

        // The compact live pool emits only the authored Box.
        return program.Instructions.Single(predicate: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (instruction.Shape == ((uint)SdfShapeType.Box))));
    }

    [Fact]
    public void WorldStampPoolPacksTheNonSecondaryFlagOnTheShapeInstruction() {
        var excluded = PoolShapeInstruction(shape: Shape(SdfSolidPrimitive.Box, PlateScale, secondary: false));

        Assert.False(condition: excluded.Secondary);

        var plain = PoolShapeInstruction(shape: Shape(SdfSolidPrimitive.Box, PlateScale));

        Assert.True(condition: plain.Secondary);
    }
}
