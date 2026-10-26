using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A palette slot's paints reach the program's materials: a creation resolves a parented paint into its
/// parent's motion-owner frame. Rigid templates keep local lengths; articulated emission scales them by the placement.
/// An unparented paint stays in the
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACreationPaintRidesItsParentsMotionFrame(bool articulated) {
        var creation = Creation();
        if (articulated) {
            creation = CreationFixtures.Prototype(id: creation.Id, document: creation.Document with {
                Drivers = [new CreationDriverDocument(Name: "clock", Signal: CreationDriverDocument.SignalTime, Cadence: 1f)]
            });
        }
        var program = CreationDomainParentLawTests.EmitProgram(creation: creation, scale: Scale);
        var material = program.Instructions.First(instruction => instruction.Op == SdfOp.ShapeBlend).Material;
        var paints = program.Materials[(int)material].Paints!;
        var localScale = articulated ? Scale : 1f;

        Assert.Equal(expected: 2, actual: paints.Count);
        Assert.Equal(expected: articulated ? CreationDomainParentLawTests.RootSlot : SdfPaint.WinningFrame, actual: paints[0].Slot);
        // The parent's authored +X is -X in the engine's root frame.
        Assert.Equal(expected: new Vector3(x: (-0.1f * localScale), y: localScale, z: 0f), actual: paints[0].Origin);
        Assert.Equal(expected: (0.01f * localScale), actual: paints[0].Stroke);
        Assert.Equal(expected: SdfPaint.WinningFrame, actual: paints[1].Slot);
        Assert.Equal(expected: (0.02f * localScale), actual: paints[1].Stroke);
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
