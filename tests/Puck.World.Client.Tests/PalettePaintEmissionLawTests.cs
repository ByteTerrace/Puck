using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A palette slot's paints reach the program's materials: a pooled placement resolves a parented paint to its
/// parent shape's own dynamic slot and scales its lengths by the placement, and keeps an unparented paint in the
/// winning frame; a static placement, whose one palette serves every placement and which rides no slot, draws only the
/// unparented paint.</summary>
public sealed class PalettePaintEmissionLawTests {
    private const float Scale = 2f;

    private static readonly PalettePaintDocument Parented = new(
        Color: "#FF0000",
        Parent: "second",
        Position: new DocumentVector3(value: new Vector3(x: 0.1f, y: 0f, z: 0f)),
        Scale: new DocumentVector3(value: new Vector3(value: 0.2f)),
        Stroke: 0.01f,
        Type: SdfSolidPrimitive.Sphere
    );
    private static readonly PalettePaintDocument Unparented = new(
        Color: "#00FF00",
        Stroke: 0.02f,
        Type: SdfSolidPrimitive.Plane
    );

    private static WorldPrototype Creation() => CreationFixtures.Prototype(document: CreationFixtures.Document(
        name: "painted",
        palette: [new PaletteEntryDocument("#FFFFFF", null, null, null, Paints: [Parented, Unparented])],
        shapes: [
            CreationFixtures.UnitSphereShape with { Name = "first" },
            CreationFixtures.UnitSphereShape with { Id = 1, Name = "second", Position = new DocumentVector3(value: Vector3.UnitY) },
        ]
    ), id: "painted");
    private static IReadOnlyList<SdfPaint> PaintsOf(SdfProgram program) =>
        program.Materials.Single(predicate: static material => (material.Paints is { Count: > 0 })).Paints!;

    [Fact]
    public void APooledPlacementRidesTheParentSlotAndScalesTheParentedPaint() {
        var paints = PaintsOf(program: CreationDomainParentLawTests.EmitProgram(creation: Creation(), scale: Scale));

        Assert.Equal(expected: 2, actual: paints.Count);
        Assert.Equal(expected: (CreationDomainParentLawTests.RootSlot + 2), actual: paints[0].Slot);
        Assert.Equal(expected: new Vector3(x: (0.1f * Scale), y: 0f, z: 0f), actual: paints[0].Origin);
        Assert.Equal(expected: (0.01f * Scale), actual: paints[0].Stroke);
        Assert.Equal(expected: SdfPaint.WinningFrame, actual: paints[1].Slot);
        Assert.Equal(expected: (0.02f * Scale), actual: paints[1].Stroke);
    }
    [Fact]
    public void AStaticPlacementDrawsOnlyTheUnparentedPaint() {
        var creation = Creation();
        var definition = (Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(
                Id: "painted-static",
                Position: new DocumentVector3(value: Vector3.Zero),
                PrototypeId: creation.Id,
                Scale: Scale,
                YawDegrees: 0f
            )],
        });
        var builder = new SdfProgramBuilder();

        WorldPlacementStamper.EmitStatic(
            builder: builder,
            creations: definition.Creations,
            definition: definition,
            placements: definition.Placements
        );

        var paint = Assert.Single(collection: PaintsOf(program: builder.Build()));

        Assert.Equal(expected: SdfPaint.WinningFrame, actual: paint.Slot);
        Assert.Equal(expected: 0.02f, actual: paint.Stroke);
    }
}
