using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins a topology's derived point group: it closes under composition, every element permutes the cells,
/// and the canonical fingerprint — the one form a board's symmetry orbit folds to — is invariant under every
/// element.</summary>
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
    public void TheCanonicalFingerprintIsInvariantUnderEveryElementAndTheImageOpAgreesWithItsMask() {
        var board = new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0", 1), Cell("1", 2)], Board: new("map"));
        var definition = Document([board, Slot("print"), Slot("imageMask")], [
            new WorldRule(Name("print"), [new ActionEffect.SetState(State: "print", FromState: "$board:canonical:board")]),
            // The image op composes with the one board-set read ($board:mask) instead of a dedicated read+image
            // query: one spelling for "carry a mask through an element" everywhere it is needed.
            new WorldRule(Name("imageMask"), [new ActionEffect.SetState(State: "imageMask", Expression: new WorldValueExpression(Tokens: [
                new WorldValueToken.State(Name: "$board:mask:board:1:2"), new WorldValueToken.BoardImage(Topology: "map", Element: "rot90"),
            ]))]),
        ]);
        var topology = WorldTopologyCompilation.Find(definition.StateRaw, "map")!;

        using var baseline = Fixtures.FreshServer(definition: definition);
        baseline.Step();
        Assert.Equal((1L << topology.Image(topology.Element("rot90"), 0)) | (1L << topology.Image(topology.Element("rot90"), 1)), Value(baseline, "imageMask"));

        foreach (var element in Enumerable.Range(0, topology.ElementCount).Select(topology.ElementName)) {
            var rot = topology.Element(element);
            // The mirror board is built directly from the topology's own image map — the law under test is that
            // Canonical folds every element to the same fingerprint, not any one way of constructing a mirror.
            var mirror = new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell(topology.Key(topology.Image(rot, 0)), 1), Cell(topology.Key(topology.Image(rot, 1)), 2)], Board: new("map"));
            var mappedDefinition = definition with { StateRaw = definition.StateRaw! with { World = [.. definition.State.Select(r => r.Name.Value == "board" ? mirror : r)] } };
            using var fixture = Fixtures.FreshServer(definition: mappedDefinition);
            fixture.Step();
            Assert.Equal(Value(baseline, "print"), Value(fixture, "print"));
        }

        var different = definition with { StateRaw = definition.StateRaw! with { World = [.. definition.State.Select(r => r.Name.Value == "board" ? r with { Cells = [Cell("0", 1), Cell("5", 2)] } : r)] } };
        using var other = Fixtures.FreshServer(definition: different);
        other.Step();
        Assert.NotEqual(Value(baseline, "print"), Value(other, "print"));

        Assert.Contains("elements=8", baseline.Server.DescribeSymmetry("map", null));
        Assert.Contains("rot180→3", baseline.Server.DescribeSymmetry("map", "12"));
    }

    private static WorldStateLatticeTopology Topology(WorldTopologyKind kind, int width, int depth) => kind == WorldTopologyKind.Hex
        ? new("t", new DocumentVector3(0, 0, 0), 1, 1, 1, Kind: WorldTopologyKind.Hex, Radius: 2)
        : new("t", new DocumentVector3(0, 0, 0), 1, width, depth, Kind: WorldTopologyKind.Grid);
    private static WorldDefinition Document(WorldStateRow[] rows, WorldRule[] rules) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows, Lattices: [new WorldStateLatticeTopology("map", new DocumentVector3(0, 0, 0), 1, 4, 4, Kind: WorldTopologyKind.Grid)]),
        Rules = rules,
    };
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Slot(string name) => new(Name(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;
}
