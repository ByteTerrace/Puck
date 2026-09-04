using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins a topology's derived point group: it closes under composition, every element permutes the cells,
/// the canonical fingerprint and mask are invariant under every element, a board maps through an element sparsely,
/// and the mask image token agrees with the mapped board.</summary>
public sealed class WorldBoardSymmetryLawTests {
    [Theory]
    [InlineData(WorldTopologyKind.Grid, 4, 4, 8)]
    [InlineData(WorldTopologyKind.Grid, 4, 2, 4)]
    [InlineData(WorldTopologyKind.Hex, 0, 0, 12)]
    public void ThePointGroupClosesAndEveryElementPermutesTheCells(WorldTopologyKind kind, int width, int depth, int elements) {
        var topology = WorldTopologyCompilation.Find(new WorldStateSection(Lattices: [Topology(kind, width, depth)]), "t")!;
        Assert.Equal(elements, topology.ElementCount);
        Assert.Equal("identity", topology.ElementName(0));

        var tables = Enumerable.Range(0, elements).Select(e => Enumerable.Range(0, topology.CellCount).Select(c => topology.Image(e, c)).ToArray()).ToList();
        foreach (var table in tables) {
            Assert.Equal(topology.CellCount, table.Distinct().Count());
        }
        for (var a = 0; a < elements; a++) {
            for (var b = 0; b < elements; b++) {
                var composed = Enumerable.Range(0, topology.CellCount).Select(c => tables[a][tables[b][c]]).ToArray();
                Assert.Contains(tables, table => table.SequenceEqual(composed));
            }
        }
        Assert.Equal(-1, topology.Element("rot45"));
    }

    [Fact]
    public void CanonicalFormsAreInvariantAndMapBoardCarriesAPositionThroughAnElement() {
        var board = new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0", 1), Cell("1", 2)], Board: new("map"));
        var mirror = new WorldStateRow(Name("mirror"), CellKind.Int, Board: new("map"));
        var definition = Document([board, mirror, Slot("print"), Slot("least"), Slot("printMirror"), Slot("leastMirror"), Slot("imageMask"), Slot("tokenMask")], [
            new WorldRule(Name("print"), [new ActionEffect.SetState(State: "print", FromState: "$board:canonical:board")]),
            new WorldRule(Name("least"), [new ActionEffect.SetState(State: "least", FromState: "$board:canonicalMask:board:1:2")]),
            new WorldRule(Name("printMirror"), [new ActionEffect.SetState(State: "printMirror", FromState: "$board:canonical:mirror")]),
            new WorldRule(Name("leastMirror"), [new ActionEffect.SetState(State: "leastMirror", FromState: "$board:canonicalMask:mirror:1:2")]),
            new WorldRule(Name("imageMask"), [new ActionEffect.SetState(State: "imageMask", FromState: "$board:image:board:rot90:1:2")]),
            new WorldRule(Name("tokenMask"), [new ActionEffect.SetState(State: "tokenMask", Expression: new WorldValueExpression(Tokens: [
                new WorldValueToken.State(Name: "$board:mask:board:1:2"), new WorldValueToken.BoardImage(Topology: "map", Element: "rot90"),
            ]))]),
        ]);
        var topology = WorldTopologyCompilation.Find(definition.StateRaw, "map")!;

        foreach (var element in Enumerable.Range(0, topology.ElementCount).Select(topology.ElementName)) {
            var mapped = Apply(definition, new WorldStateTransform.MapBoard("mirror", "board", element));
            var image = Find(mapped, "mirror").Cells!;
            Assert.Equal(2, image.Count);
            var rot = topology.Element(element);
            Assert.Equal(1L, WorldDefinitionRows.FindCell(image, topology.CellName(topology.Image(rot, 0)))!.Value);
            Assert.Equal(2L, WorldDefinitionRows.FindCell(image, topology.CellName(topology.Image(rot, 1)))!.Value);

            using var fixture = Fixtures.FreshServer(definition: mapped);
            fixture.Step();
            Assert.Equal(Value(fixture, "print"), Value(fixture, "printMirror"));
            Assert.Equal(Value(fixture, "least"), Value(fixture, "leastMirror"));
            Assert.Equal(Value(fixture, "imageMask"), Value(fixture, "tokenMask"));
            Assert.Equal((1L << topology.Image(topology.Element("rot90"), 0)) | (1L << topology.Image(topology.Element("rot90"), 1)), Value(fixture, "imageMask"));
        }

        var different = definition with { StateRaw = definition.StateRaw! with { World = [.. definition.State.Select(r => r.Name.Value == "board" ? r with { Cells = [Cell("0", 1), Cell("5", 2)] } : r)] } };
        using var other = Fixtures.FreshServer(definition: different);
        other.Step();
        using var baseline = Fixtures.FreshServer(definition: definition);
        baseline.Step();
        Assert.NotEqual(Value(baseline, "print"), Value(other, "print"));

        Assert.Contains("elements=8", baseline.Server.DescribeSymmetry("map", null));
        Assert.Contains("rot180→3", baseline.Server.DescribeSymmetry("map", "12"));
        Assert.False(WorldStateTransforms.TryApply(definition, new WorldStateTransform.MapBoard("mirror", "board", "rot45"), WorldPrincipal.World, 0, "test", out _, out var reason));
        Assert.Contains("not a symmetry element", reason);
    }

    private static WorldStateLatticeTopology Topology(WorldTopologyKind kind, int width, int depth) => kind == WorldTopologyKind.Hex
        ? new("t", new DocumentVector3(0, 0, 0), 1, 1, 1, Kind: WorldTopologyKind.Hex, Radius: 2)
        : new("t", new DocumentVector3(0, 0, 0), 1, width, depth, Kind: WorldTopologyKind.Grid);
    private static WorldDefinition Document(WorldStateRow[] rows, WorldRule[] rules) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows, Lattices: [new WorldStateLatticeTopology("map", new DocumentVector3(0, 0, 0), 1, 4, 4, Kind: WorldTopologyKind.Grid)]),
        Rules = rules,
    };
    private static WorldDefinition Apply(WorldDefinition definition, WorldStateTransform transform) {
        Assert.True(WorldStateTransforms.TryApply(definition, transform, WorldPrincipal.World, 1, "test", out var candidate, out var reason), reason);
        return candidate!;
    }
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Slot(string name) => new(Name(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;
}
