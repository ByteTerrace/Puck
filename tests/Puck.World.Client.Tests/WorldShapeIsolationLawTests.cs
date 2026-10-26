using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary><c>view.isolate</c>'s emission filter: an isolation keeps the named shapes and every shape parented beneath
/// them, transitively, a prefix pattern included, and hides the rest of that prototype from the program, the paints
/// parented to hidden shapes with them; a static copy's first kept shape still opens its own bake union.</summary>
public sealed class WorldShapeIsolationLawTests {
    private static readonly PalettePaintDocument ShinPaint = new(Color: "#FF0000", Parent: "shin-left", Stroke: 0.01f, Type: SdfSolidPrimitive.Plane);
    private static readonly PalettePaintDocument PelvisPaint = new(Color: "#00FF00", Parent: "pelvis", Stroke: 0.01f, Type: SdfSolidPrimitive.Plane);

    // A pelvis carrying two legs, and an arm beside it: a rigid asset on one shared slot.
    private static WorldPrototype Creature() {
        ShapeDocument Part(int id, string name, string? parent) => (CreationFixtures.UnitSphereShape with {
            Id = id,
            Name = name,
            Parent = parent,
            Position = new DocumentVector3(value: new Vector3(x: id, y: 0f, z: 0f)),
        });

        return CreationFixtures.Prototype(document: CreationFixtures.Document(
            name: "creature",
            palette: [new PaletteEntryDocument("#FFFFFF", null, null, null, Paints: [ShinPaint, PelvisPaint])],
            shapes: [
                Part(id: 0, name: "pelvis", parent: null),
                Part(id: 1, name: "thigh-left", parent: "pelvis"),
                Part(id: 2, name: "shin-left", parent: "thigh-left"),
                Part(id: 3, name: "foot-left", parent: "shin-left"),
                Part(id: 4, name: "thigh-right", parent: "pelvis"),
                Part(id: 5, name: "arm", parent: null),
            ]
        ), id: "creature");
    }

    [Fact]
    public void ThePoolEmitsOnlyTheKeptShapesAndTheirDescendantsAndTheirPaints() {
        var creation = Creature();
        var pool = CreationDomainParentLawTests.Pool(creation: creation);
        var builder = new SdfProgramBuilder();

        pool.Isolation.Set(selection: new WorldShapeIsolation.Selection(Names: ["thigh-l*"], Prototype: "creature"));
        pool.Emit(
            builder: builder,
            colors: WorldBakedColors.Of(definition: CreationDomainParentLawTests.Definition(creation: creation)),
            maxPlacementScale: 1f,
            probeWorstCase: false,
            slotBase: 0
        );

        var program = builder.Build();

        Assert.Equal(0, Assert.Single(program.Instances).Slot);
        Assert.Equal(3, program.Instructions.Count(instruction => instruction.Op == SdfOp.ShapeBlend));

        Assert.All(program.Materials.Where(material => material.Paints is not null), material => {
            var paint = Assert.Single(material.Paints!);
            Assert.Contains(paint.Slot, new[] { -1, 0 }); // shape-local material variants bake their paint coordinates
            Assert.Equal(new Vector3(1, 0, 0), paint.Color);
        });
    }
    [Fact]
    public void AStaticCopyEmitsTheKeptShapesAsOneFreshUnion() {
        var creation = Creature();
        var definition = (Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [new WorldPlacement(Id: "creature-static", Position: new DocumentVector3(value: Vector3.Zero), PrototypeId: creation.Id, Scale: 1f, YawDegrees: 0f)],
        });
        var isolation = new WorldShapeIsolation();
        var builder = new SdfProgramBuilder();

        isolation.Set(selection: new WorldShapeIsolation.Selection(Names: ["thigh-right", "arm"], Prototype: "creature"));
        WorldPlacementStamper.EmitStatic(builder: builder, creations: definition.Creations, definition: definition, isolation: isolation, placements: definition.Placements);

        var instances = builder.Build().Instances;

        Assert.Equal(expected: [false, true], actual: instances.Select(selector: static instance => instance.BakesWithPrevious));
    }
}
