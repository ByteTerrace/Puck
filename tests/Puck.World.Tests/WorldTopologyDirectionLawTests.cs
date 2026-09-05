using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins directions as content: an unauthored topology compiles exactly the fixed set every kind carried
/// before <see cref="IDiscreteLatticeTopology.Directions"/> existed, while an authored list replaces it wholesale —
/// a 4-connected grid orthogonal-only, or a renamed box vocabulary — and the validator refuses a list that would
/// leave <see cref="CompiledWorldTopology.Opposite"/> unable to close.</summary>
public sealed class WorldTopologyDirectionLawTests {
    [Fact]
    public void AnUnauthoredGridStillCompilesTheEightCompassNames() {
        var topology = WorldTopologyCompilation.Find(new WorldStateSection(Lattices: [Grid()]), "grid")!;
        Assert.Equal(8, topology.DirectionCount);
        Assert.Equal(0, topology.Direction("N"));
        Assert.Equal(7, topology.Direction("NW"));
        Assert.Equal(-1, topology.Direction("orthoNorth"));
    }

    [Fact]
    public void AFourConnectedGridReplacesTheDefaultCompassVocabularyWholesale() {
        var orthogonal = new WorldTopologyDirection[] {
            new("north", 0, -1), new("south", 0, 1), new("east", 1, 0), new("west", -1, 0),
        };
        var topology = WorldTopologyCompilation.Find(new WorldStateSection(Lattices: [Grid() with { Directions = orthogonal }]), "grid")!;
        Assert.Equal(4, topology.DirectionCount);
        // The default compass names are gone entirely — an authored list is the topology's ONLY vocabulary.
        Assert.Equal(-1, topology.Direction("N"));
        Assert.Equal(-1, topology.Direction("NE"));
        var north = topology.Direction("north");
        Assert.True(north >= 0);
        Assert.Equal(topology.Direction("south"), topology.Opposite(north));
        // Origin cell (0,0) has no north neighbour but does have an east one.
        Assert.Equal(-1, topology.Neighbour(0, north));
        Assert.Equal(1, topology.Neighbour(0, topology.Direction("east")));
    }

    [Fact]
    public void ARenamedBoxVocabularyStillDerivesACorrectClosedOppositeTable() {
        var renamed = new WorldTopologyDirection[] { new("up", 0, 0, 1), new("down", 0, 0, -1) };
        var topology = WorldTopologyCompilation.Find(new WorldStateSection(Lattices: [Box() with { Directions = renamed }]), "box")!;
        Assert.Equal(2, topology.DirectionCount);
        Assert.Equal(topology.Direction("down"), topology.Opposite(topology.Direction("up")));
        Assert.Equal(4, topology.Neighbour(0, topology.Direction("up"))); // one layer up: 2x2 footprint.
    }

    [Fact]
    public void AnUnclosedOrDuplicateOrMisplacedDirectionListRefuses() {
        var missingOpposite = new WorldTopologyDirection[] { new("east", 1, 0) };
        Assert.False(WorldTopologyCompilation.TryValidate(Grid() with { Directions = missingOpposite }, out var noOppositeReason));
        Assert.Contains("opposite", noOppositeReason);

        var duplicateName = new WorldTopologyDirection[] { new("east", 1, 0), new("east", -1, 0) };
        Assert.False(WorldTopologyCompilation.TryValidate(Grid() with { Directions = duplicateName }, out var duplicateReason));
        Assert.Contains("distinct", duplicateReason);

        var zeroStep = new WorldTopologyDirection[] { new("east", 1, 0), new("nowhere", 0, 0) };
        Assert.False(WorldTopologyCompilation.TryValidate(Grid() with { Directions = zeroStep }, out var zeroReason));
        Assert.Contains("zero step", zeroReason);

        var layerOnAGrid = new WorldTopologyDirection[] { new("up", 0, 0, 1), new("down", 0, 0, -1) };
        Assert.False(WorldTopologyCompilation.TryValidate(Grid() with { Directions = layerOnAGrid }, out var layerReason));
        Assert.Contains("layer step outside a box", layerReason);

        // Control: the same four-direction list validates and compiles when it IS closed under negation.
        var closed = new WorldTopologyDirection[] { new("east", 1, 0), new("west", -1, 0) };
        Assert.True(WorldTopologyCompilation.TryValidate(Grid() with { Directions = closed }, out _));
    }

    [Fact]
    public void ARuleCompilesAgainstAnAuthoredDirectionAndRefusesTheRetiredDefaultName() {
        var orthogonal = new WorldTopologyDirection[] {
            new("north", 0, -1), new("south", 0, 1), new("east", 1, 0), new("west", -1, 0),
        };
        var board = new WorldStateRow(CellName.Parse("board"), CellKind.Int, Domain: new WorldStateDomain.CellsOf("grid"));
        var slot = new WorldStateRow(CellName.Parse("neighbour"), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
        WorldDefinition Document(string direction) => Fixtures.BuildDocument() with {
            StateRaw = new(World: [board, slot], Lattices: [Grid() with { Directions = orthogonal }]),
            Rules = [new WorldRule(CellName.Parse("read"), [new ActionEffect.SetState(State: "neighbour", FromState: $"$board:neighbour:board:{direction}", FromKey: "0")])],
        };

        Assert.True(WorldDefinitionValidator.TryValidateLocally(Document("north"), out var authoredReason), authoredReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(Document("N"), out var retiredReason));
        Assert.Contains("is not a direction of 'grid'", retiredReason);
    }

    [Fact]
    public void ARingRefusesAYStepAndAWrappedAxisRefusesAStepAtOrPastItsExtent() {
        var ring = new WorldStateLatticeTopology.Ring("ring", new DocumentVector3(0, 0, 0), 1, 5);
        Assert.False(WorldTopologyCompilation.TryValidate(ring with { Directions = [new("skew", 1, 1)] }, out var ringReason));
        Assert.Contains("no second axis", ringReason);
        Assert.False(WorldTopologyCompilation.TryValidate(ring with { Directions = [new("around", 5, 0), new("back", -5, 0)] }, out var wideReason));
        Assert.Contains("magnitude must be under the width", wideReason);
        // Control: a step under the ring's own width is admitted.
        Assert.True(WorldTopologyCompilation.TryValidate(ring with { Directions = [new("step", 4, 0), new("back", -4, 0)] }, out _));

        var wrappedGrid = Grid() with { Wrap = WorldTopologyWrap.Both };
        Assert.False(WorldTopologyCompilation.TryValidate(wrappedGrid with { Directions = [new("far", 0, 4), new("near", 0, -4)] }, out var deepReason));
        Assert.Contains("magnitude must be under the depth", deepReason);
    }

    // A physical field's case type carries no 'directions' property to author in the first place; the invariant
    // the old runtime-validator check named is now enforced by the document's own strict-parsed JSON shape instead.
    [Fact]
    public void APhysicalFieldRefusesAnAuthoredDirectionVocabulary() {
        var field = new WorldStateLatticeTopology.Field("field", new DocumentVector3(0, 0, 0), 1, 4, 4);
        var definition = Fixtures.BuildDocument() with { StateRaw = new(Lattices: [field]) };
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(WorldDefinitionSerialization.Serialize(definition)))!.AsObject();
        var lattice = node["state"]!["lattices"]!.AsArray()[0]!.AsObject();
        lattice["directions"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "east", ["x"] = 1, ["y"] = 0 });

        var exception = Assert.Throws<InvalidDataException>(() => WorldDefinitionSerialization.Deserialize(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString())));
        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public void AnAuthoredDirectionListRoundTripsThroughJson() {
        var orthogonal = new WorldTopologyDirection[] { new("east", 1, 0), new("west", -1, 0) };
        var definition = Fixtures.BuildDocument() with { StateRaw = new(Lattices: [Grid() with { Directions = orthogonal }]) };
        var restored = WorldDefinitionSerialization.Deserialize(WorldDefinitionSerialization.Serialize(definition: definition));
        var topology = WorldTopologyCompilation.Find(restored.StateRaw, "grid")!;
        Assert.Equal(2, topology.DirectionCount);
        Assert.Equal(-1, topology.Direction("N"));
        Assert.True(topology.Direction("east") >= 0);
    }

    private static WorldStateLatticeTopology.Grid Grid() =>
        new("grid", new DocumentVector3(0, 0, 0), 1, 4, 4);
    private static WorldStateLatticeTopology.Box Box() =>
        new("box", new DocumentVector3(0, 0, 0), 0.5f, 2, 2, Layers: 2, LayerHeight: 0.5f);
}
