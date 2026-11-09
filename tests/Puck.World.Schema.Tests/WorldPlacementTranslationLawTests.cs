using System.Numerics;
using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldPlacementTranslationLawTests {
    private static WorldPlacement Template(DocumentVector3 step) => new("rack", "card", Vector3.Zero, 0, 1,
        Distribution: new WorldDistribution(new WorldDistributionRegion.Lattice(step, 4, Vector3.Zero, 1),
            new WorldSequence(Name: WorldSequence.None, Offset: 0, Step: 0)), Deal: new WorldPlacementDeal("cards"));

    [Fact]
    public void ReflowStorageAndSerializationRetainTheExactInverseAtALargeSlot() {
        var template = Template(step: new Vector3(x: (128f + (1f / 65536f)), y: 0, z: 0));
        var child = WorldPlacementDeal.Child(id: "rack/a", prototype: "card", slot: 3, template: template);

        child = child with { Position = WorldPlacementFrameCompilation.StoredPosition(template, child, FixedVector3.Zero, 0) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(child, WorldJsonContext.Default.WorldPlacement);
        var restored = JsonSerializer.Deserialize(bytes, WorldJsonContext.Default.WorldPlacement)!;

        Assert.Equal(child.Position, restored.Position);
        Assert.Equal(FixedVector3.Zero, WorldPlacementFrameCompilation.LocalFrame(inheritScale: out _, parent: template, row: restored, worldSeed: 0).FixedPosition);
        Assert.Equal(((WorldPlacementTranslation)new FixedVector3(X: FixedQ4816.FromRawBits(value: -25165827), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)), restored.Position);
    }

    private static WorldDefinition BoundSource(string text, WorldPlacement template, WorldPlacement child) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: [new WorldStateRow(CellName.Parse(candidate: "step"), CellKind.Text,
            Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Text(value: text))])]),
        PlacementsRaw: new WorldPlacementsSection([template, child]));

    [Fact]
    public void BoundStepsInvalidateOffsetsFramesAndSpatialGeometryWithoutReplacingTheSection() {
        var step = JsonSerializer.Deserialize<DocumentVector3>("\"state.step\"")!;
        var template = Template(step: step);
        var child = WorldPlacementDeal.Child(id: "rack/a", prototype: "card", slot: 1, template: template) with {
            Spatial = [new WorldPlacementSpatialVolume("occupation", WorldPlacementSpatialRole.Occupation,
                new WorldSpatialShape(WorldSpatialShapeKind.Box, Vector3.Zero, new Vector3(value: .1f)))],
        };
        var source = BoundSource(child: child, template: template, text: "[1,0,0]");
        var live = new WorldLiveValues(clock: static () => ArenaTime.Origin) { Arena = WorldStateReader.Seed(definition: source) };
        var bound = WorldStateDocumentValues.BindLive(definition: source, live: live);

        Assert.Equal(FixedVector3.UnitX, WorldPlacementDeal.Offsets(template: bound.Placements[0], worldSeed: 0)[1]);
        Assert.Equal(FixedVector3.UnitX, bound.PlacementFrames[child.Id].FixedPosition);
        Assert.Equal(FixedVector3.UnitX, bound.SpatialQueryIndex.Volumes[0].Shape.Center);
        live.Arena = WorldStateReader.Seed(definition: BoundSource(child: child, template: template, text: "[2,0,0]"));
        var expected = (FixedVector3.UnitX * FixedQ4816.FromInteger(value: 2));

        Assert.Equal(expected, WorldPlacementDeal.Offsets(template: bound.Placements[0], worldSeed: 0)[1]);
        Assert.Equal(expected, bound.PlacementFrames[child.Id].FixedPosition);
        Assert.Equal(expected, bound.SpatialQueryIndex.Volumes[0].Shape.Center);
    }
}
