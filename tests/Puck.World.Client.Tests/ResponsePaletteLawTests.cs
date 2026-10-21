using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A responding static placement registers the palette of every creation it can show, so the program's
/// materials stand whichever entry holds: a response swaps one row's materials and moves no other row's ids, and the
/// mesh library hands every other bake group the mesh it already drew.</summary>
public sealed class ResponsePaletteLawTests {
    private static WorldPrototype Creation(string id, string color) => CreationFixtures.Prototype(document: CreationFixtures.Document(
        name: id,
        palette: [new PaletteEntryDocument(color, null, null, null)],
        shapes: [CreationFixtures.UnitSphereShape]
    ), id: id);

    private static IReadOnlyList<Vector3> Materials(int holding) {
        var creations = new[] { Creation(id: "lit", color: "#FFFFFF"), Creation(id: "dark", color: "#202020"), Creation(id: "after", color: "#FF0000") };
        WorldPlacement Row(string id, string prototype, float x) => new(
            Id: id,
            Position: new DocumentVector3(value: new Vector3(x: x, y: 0f, z: 0f)),
            PrototypeId: prototype,
            Scale: 1f,
            YawDegrees: 0f
        );
        var definition = Fixtures.BuildDocument() with {
            CreationsRaw = creations,
            PlacementRowsRaw = [
                Row(id: "swap", prototype: "lit", x: 0f) with {
                    Holding = holding,
                    Respond = [new WorldPlacementResponse(
                        PrototypeId: "dark",
                        When: new WorldPlacementResponseCondition.StateCondition(Comparison: Puck.State.ExpressionOp.Equal, State: "lights", Value: 0f)
                    )],
                },
                Row(id: "lit-again", prototype: "lit", x: 4f),
                Row(id: "after", prototype: "after", x: 8f),
            ],
        };
        var builder = new SdfProgramBuilder();

        WorldPlacementStamper.EmitStatic(
            builder: builder,
            creations: definition.Creations,
            definition: definition,
            placements: definition.Placements
        );

        return [.. builder.Build().Materials.Select(selector: static material => material.Albedo)];
    }

    [Fact]
    public void ARowShowingACreationWithNothingToDrawEmitsNoInstance() {
        var empty = CreationFixtures.Prototype(document: CreationFixtures.Document(name: "nothing", palette: [new PaletteEntryDocument("#FFFFFF", null, null, null)], shapes: []), id: "nothing");
        var definition = Fixtures.BuildDocument() with {
            CreationsRaw = [empty],
            PlacementRowsRaw = [new WorldPlacement(Id: "shown", Position: new DocumentVector3(value: Vector3.Zero), PrototypeId: "nothing", Scale: 1f, YawDegrees: 0f)],
        };
        var builder = new SdfProgramBuilder();

        WorldPlacementStamper.EmitStatic(builder: builder, creations: definition.Creations, definition: definition, placements: definition.Placements);

        Assert.Equal(expected: 0, actual: builder.InstanceCount);
    }
    [Fact]
    public void AResponseSwapLeavesTheProgramsMaterialsAsTheyStand() {
        var shown = Materials(holding: 0);

        Assert.Equal(expected: 3, actual: shown.Count);
        Assert.Equal(expected: shown, actual: Materials(holding: 1));
    }
}
