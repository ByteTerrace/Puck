using Puck.Assets.Documents;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Region compilation and presentation preserve topology membership and stable identities.</summary>
public sealed class WorldFieldRegionsLawTests {
    private static WorldFieldTopology Topology(string name, int width, float origin) => new(
        Name: name, Origin: new DocumentVector3(x: origin, y: 0, z: 0), CellSize: 1, Width: width, Depth: 1,
        Reactions: [new WorldReaction.Decay(Field: (name + "Value"), Rate: 0.25f)]);
    private static WorldStateRow Row(string region) => new(
        Name: CellName.Parse(candidate: (region + "Value")), Kind: CellKind.Fixed,
        Domain: new StateDomain.CellsOf(region),
        Field: new WorldStateFieldTrait(Initial: 1, Min: 0, Max: 10,
            Paint: [new WorldLatticeFill.Rect(MaxX: 1, MaxZ: 1, MinX: 0, MinZ: 0, Value: 2)]));

    [Fact]
    public void CompilationAndProjectionRetainEachRegionsIdentityRowsAndFootprint() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(Lattices: [Topology(name: "west", origin: 0, width: 2), Topology(name: "east", origin: 10, width: 3)],
                World: [Row(region: "east"), Row(region: "west")]),
        };

        Assert.Equal(new[] { "west", "east" }, definition.FieldRegions.Select(selector: region => region.Name));
        Assert.Equal("westValue", Assert.Single(collection: definition.FieldRegions[0].Fields).Name);
        Assert.Equal("eastValue", Assert.Single(collection: definition.FieldRegions[1].Fields).Name);
        Assert.Equal(2, definition.FieldPrograms[0].CellCount);
        Assert.Equal(3, definition.FieldPrograms[1].CellCount);
        var projection = Fixtures.Project(definition, WorldDisclosureTier.Presentation, "regional", 0);

        Assert.NotNull(@object: projection);
        Assert.True(condition: WorldProjection.TryDeserialize(WorldProjection.Serialize(projection: projection), out var decoded, out var reason), userMessage: reason);
        Assert.True(condition: WorldProjection.TryToDefinition(decoded!, out var hydrated, out reason), userMessage: reason);
        Assert.Equal(new[] { "west", "east" }, hydrated!.FieldRegions.Select(selector: region => region.Name));
        Assert.Equal(definition.FieldRegions[1].Lattice, hydrated.FieldRegions[1].Lattice);
        Assert.Equal(definition.FieldRegions[0].Paint, hydrated.FieldRegions[0].Paint);
        Assert.Equal(definition.FieldRegions[0].Reactions, hydrated.FieldRegions[0].Reactions);
        Assert.Equal(1f, hydrated.FieldRegions[0].Fields[0].Initial);
        Assert.True(condition: WorldBoundRow.TryResolve(definition: hydrated, length: out var length, row: out _, rowName: "eastValue"));
        Assert.Equal(actual: length, expected: 3);
    }
    [Fact]
    public void AReactionCannotResolveAFieldFromAnotherRegion() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                Lattices: [Topology(name: "west", origin: 0, width: 2) with { Reactions = [new WorldReaction.Decay(Field: "eastValue", Rate: 1f)] }, Topology(name: "east", origin: 10, width: 3)],
                World: [Row(region: "west"), Row(region: "east")]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition, out var reason, neighbours: null));
        Assert.Contains(actualString: reason, expectedSubstring: "eastValue");
    }
}
