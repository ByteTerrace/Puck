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

    private static readonly Vector3 PlateScale = new(
        x: 0.4f,
        y: 0.3f,
        z: 0.2f
    );

    private static CreationDocument Document(params ShapeDocument[] shapes) => CreationFixtures.Document(
        name: PrototypeId,
        shapes: shapes
    );
    // The render seam: both emission paths funnel every shape through SdfProgramBuilder's Shape() core plus
    // MarkSecondary, which packs Secondary as a real flag on the ShapeBlend instruction — never dropped.
    private static SdfProgram EmitStatic(ShapeDocument shape) => CreationFixtures.EmitStatic(document: Document(shape));
    // The animated pool seam: WorldStampPool.EmitShape threads the same flag through MarkSecondary. The compact live
    // pool emits only the authored Box.
    private static SdfInstruction PoolShapeInstruction(ShapeDocument shape) => CreationFixtures.EmitPool(
        name: PrototypeId,
        shapes: [shape]
    ).Instructions.Single(predicate: static instruction => ((instruction.Op == SdfOp.ShapeBlend) && (instruction.Shape == ((uint)SdfShapeType.Box))));
    private static ShapeDocument Shape(SdfSolidPrimitive type, Vector3 scale, bool? secondary = null, string? name = null, Vector3? position = null, int id = 0) =>
        new(
            Id: id,
            Name: ((name is null)
            ? null
            : (DocumentIdentifier)name),
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

    [Fact]
    public void ANonSecondaryShapeMayStillCarryAPanelOrTrims() {
        var panel = new ShapePanelDocument(
            Inset: 0.05f,
            Depth: 0.1f,
            Material: 1
        );
        var trim = new ShapeTrimDocument(
            Inset: 0.003f,
            Material: 1,
            Shape: "cutter",
            Width: 0.05f
        );

        CreationFixtures.AssertAccepts(document: Document(
            Shape(
                SdfSolidPrimitive.Sphere,
                PlateScale,
                name: "cutter",
                id: 0
            ),
            (Shape(
                SdfSolidPrimitive.Box,
                PlateScale,
                secondary: false,
                name: "host",
                id: 1
            ) with {
                Panel = panel,
                Trims = [trim],
            })
        ));
    }
    [InlineData(SdfSolidPrimitive.Box)]
    [InlineData(SdfSolidPrimitive.Sphere)]
    [InlineData(SdfSolidPrimitive.Capsule)]
    [Theory]
    public void ANonSecondaryShapeOfAnyPrimitiveTypeIsAccepted(SdfSolidPrimitive type) =>
        CreationFixtures.AssertAccepts(document: Document(Shape(
            type,
            PlateScale,
            secondary: false
        )));
    // The contact/collider seam: EmitFixed/VisitFixedPrimitiveCopies visit a non-secondary shape exactly like an
    // ordinary one — the opposite of ShapeDetailLawTests' exclusion law.
    [Fact]
    public void ContactCopiesAreIdenticalWhetherOrNotAShapeIsSecondary() {
        var secondary = Document(Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            secondary: true,
            name: "plate",
            id: 0
        ));
        var nonSecondary = Document(Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            secondary: false,
            name: "plate",
            id: 0
        ));
        var transform = new FixedCreationStampTransform(
            Origin: FixedVector3.Zero,
            Rotation: FixedQuaternion.Identity,
            Scale: FixedQ4816.One,
            ReflectionNormal: null
        );
        var secondaryCopies = new List<FixedCreationStampPrimitiveCopy>();
        var nonSecondaryCopies = new List<FixedCreationStampPrimitiveCopy>();

        CreationStampEmitter.VisitFixedPrimitiveCopies(
            document: secondary,
            transform: transform,
            visitor: secondaryCopies.Add
        );
        CreationStampEmitter.VisitFixedPrimitiveCopies(
            document: nonSecondary,
            transform: transform,
            visitor: nonSecondaryCopies.Add
        );

        Assert.Single(collection: secondaryCopies);
        Assert.Single(collection: nonSecondaryCopies);
        Assert.Equal(
            actual: nonSecondaryCopies[0].Center,
            expected: secondaryCopies[0].Center
        );
        Assert.Equal(
            actual: nonSecondaryCopies[0].HalfExtents,
            expected: secondaryCopies[0].HalfExtents
        );
    }
    [Fact]
    public void EmitStaticPacksTheNonSecondaryFlagOnTheShapeInstruction() {
        var excluded = EmitStatic(shape: Shape(
            SdfSolidPrimitive.Sphere,
            PlateScale,
            secondary: false
        ));
        var shapeInstruction = Assert.Single(
            collection: excluded.Instructions,
            predicate: instruction => (instruction.Op == SdfOp.ShapeBlend)
        );

        Assert.False(condition: shapeInstruction.Secondary);

        // Control: an unauthored (null) Secondary defaults true, packing the plain, unflagged instruction.
        var plain = EmitStatic(shape: Shape(
            SdfSolidPrimitive.Sphere,
            PlateScale
        ));
        var plainInstruction = Assert.Single(
            collection: plain.Instructions,
            predicate: instruction => (instruction.Op == SdfOp.ShapeBlend)
        );

        Assert.True(condition: plainInstruction.Secondary);
    }
    [Fact]
    public void WorldStampPoolPacksTheNonSecondaryFlagOnTheShapeInstruction() {
        var excluded = PoolShapeInstruction(shape: Shape(
            SdfSolidPrimitive.Box,
            PlateScale,
            secondary: false
        ));

        Assert.False(condition: excluded.Secondary);

        var plain = PoolShapeInstruction(shape: Shape(
            SdfSolidPrimitive.Box,
            PlateScale
        ));

        Assert.True(condition: plain.Secondary);
    }
}
