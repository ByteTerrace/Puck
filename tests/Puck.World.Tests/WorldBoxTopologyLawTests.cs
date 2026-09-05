using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the box topology: 26 named space directions, a layer resolved from Y, the octahedral group of a
/// cube against the smaller groups of a prism and a box, and a space-diagonal line of four read by the same line
/// query a flat board uses.</summary>
public sealed class WorldBoxTopologyLawTests {
    [Theory]
    [InlineData(4, 4, 4, 48)]
    [InlineData(4, 4, 2, 16)]
    [InlineData(4, 3, 2, 8)]
    public void ABoxDerivesItsSignedAxisPermutationsAndTwentySixDirections(int width, int depth, int layers, int elements) {
        var topology = WorldTopologyCompilation.Find(new WorldStateSection(Lattices: [Box(width, depth, layers)]), "box")!;
        Assert.Equal(width * depth * layers, topology.CellCount);
        Assert.Equal(26, topology.DirectionCount);
        Assert.Equal(elements, topology.ElementCount);
        Assert.Equal("identity", topology.ElementName(0));
        Assert.Equal(topology.Direction("UNE"), Array.IndexOf(CompiledWorldTopology.BoxDirectionNames, "UNE"));
        Assert.Equal(-1, topology.Direction("X"));

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

        // U from the bottom layer's origin cell is one layer up; D from it is off the box; the ordinal law holds.
        Assert.Equal(width * depth, topology.Neighbour(0, topology.Direction("U")));
        Assert.Equal(-1, topology.Neighbour(0, topology.Direction("D")));
    }

    [Fact]
    public void YResolvesToALayer() {
        var topology = WorldTopologyCompilation.Find(new WorldStateSection(Lattices: [Box(4, 4, 4)]), "box")!;
        Assert.True(topology.TryCellOf(new FixedVector3(FixedQ4816.FromDouble(0.6), FixedQ4816.FromDouble(0.8), FixedQ4816.FromDouble(0.1)), out var cell));
        Assert.Equal((1 * 4 + 0) * 4 + 1, cell);
        Assert.False(topology.TryCellOf(new FixedVector3(FixedQ4816.FromDouble(0.6), FixedQ4816.FromDouble(-0.1), FixedQ4816.FromDouble(0.1)), out _));
        Assert.False(topology.TryCellOf(new FixedVector3(FixedQ4816.FromDouble(0.6), FixedQ4816.FromDouble(2.5), FixedQ4816.FromDouble(0.1)), out _));

        Assert.False(WorldTopologyCompilation.TryValidate(Box(4, 4, 4) with { LayerHeight = 0f }, out var heightReason));
        Assert.Contains("layerHeight", heightReason);
        Assert.False(WorldTopologyCompilation.TryValidate(new WorldStateLatticeTopology("g", new DocumentVector3(0, 0, 0), 1, 4, 4, Kind: WorldTopologyKind.Grid, LayerHeight: 1f), out var gridReason));
        Assert.Contains("belongs to a box", gridReason);
    }

    [Fact]
    public void ASpaceDiagonalRunIsReadThroughMatchOverARayInsteadOfABoardLineQuery() {
        // "USE" (up, south, east) is the box's signed step (+1,+1,+1) — the space diagonal from the cube's own
        // origin corner. A pattern of "one or more of the marked value" read with the prefix facet answers how far
        // the run continues past the origin cell exactly as a dedicated line query would, generalized to any
        // authored run shape rather than only exact-length equality.
        static WorldStateCell Mark(int x, int y, int z) => new(WorldCellName.Parse((((z * 4) + y) * 4 + x).ToString()), 1L);
        var board = new WorldStateRow(WorldCellName.Parse("cube"), CellKind.Int, Cells: [Mark(0, 0, 0), Mark(1, 1, 1), Mark(2, 2, 2), Mark(3, 3, 3)], Domain: new WorldStateDomain.CellsOf("box"));
        var runOfOnes = new WorldPatternRow(WorldCellName.Parse("runOfOnes"), CellKind.Int, Symbols: [new(WorldCellName.Parse("one"), 1, 1)], Pattern: new WorldPatternNode.Star(new WorldPatternNode.Symbol("one")));
        var run = new WorldStateRow(WorldCellName.Parse("run"), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [board, run], Lattices: [Box(4, 4, 4)]),
            PatternsRaw = [runOfOnes],
            Rules = [new WorldRule(WorldCellName.Parse("read"), [new ActionEffect.SetState(State: "run", FromState: "$match:runOfOnes:cube:USE:prefix", FromKey: "0")])],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();
        Assert.Equal(3L, Value(fixture, "run"));

        var broken = definition with { StateRaw = definition.StateRaw! with { World = [board with { Cells = [Mark(0, 0, 0), Mark(1, 1, 1), Mark(2, 2, 2)] }, run] } };
        using var control = Fixtures.FreshServer(definition: broken);
        control.Step();
        Assert.Equal(2L, Value(control, "run"));
    }

    [Fact]
    public void AnExactRunOfFourIsAcceptedAndAContinuingFifthCellIsNot() {
        // Two boards over the same 5-wide, 2-layer box: "isolated" marks only x0..x3 along E (a genuine run of
        // exactly four, flanked by a real but unmarked cell), and "extended" marks x0..x4 (a run of five, so the
        // same window is followed by a fifth marked cell). Reading east from x0's own cell with a pattern of
        // "exactly three more of the marked value, then never another" tells the two apart on one $match read: it
        // needs the ray's WHOLE remainder, not just a prefix length, which is why the facet here is the plain
        // accept (no suffix) rather than prefix/cell/distance. Layer 1 exists only so a wrapped-opposite direction
        // resolves to a real, unmarked cell rather than an out-of-range one.
        static WorldStateRow Row(string name, params int[] indices) => new(
            WorldCellName.Parse(name), CellKind.Int,
            Cells: [.. indices.Select(i => new WorldStateCell(WorldCellName.Parse(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), 7L))],
            Domain: new WorldStateDomain.CellsOf("box")
        );
        static WorldStateRow Winner(string name) => new(WorldCellName.Parse(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
        var runTerminated = new WorldPatternRow(WorldCellName.Parse("runTerminated"), CellKind.Int, Symbols: [new(WorldCellName.Parse("seven"), 7, 7)],
            Pattern: new WorldPatternNode.Sequence([
                new WorldPatternNode.Repeat(new WorldPatternNode.Symbol("seven"), 3, 3),
                new WorldPatternNode.Star(new WorldPatternNode.Except("seven")),
            ]));

        var isolated = Row("isolated", 0, 1, 2, 3);
        var extended = Row("extended", 0, 1, 2, 3, 4);
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [isolated, extended, Winner("winnerIsolated"), Winner("winnerExtended")], Lattices: [Box(5, 1, 2)]),
            PatternsRaw = [runTerminated],
            Rules = [
                new WorldRule(WorldCellName.Parse("markIsolated"), [new ActionEffect.SetState(State: "winnerIsolated", FromState: "$match:runTerminated:isolated:E", FromKey: "0")]),
                new WorldRule(WorldCellName.Parse("markExtended"), [new ActionEffect.SetState(State: "winnerExtended", FromState: "$match:runTerminated:extended:E", FromKey: "0")]),
            ],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();
        Assert.Equal(1L, Value(fixture, "winnerIsolated"));
        Assert.Equal(0L, Value(fixture, "winnerExtended"));
    }

    [Fact]
    public void OppositeIsDerivedFromEachDirectionsOwnVectorRatherThanHalvingTheOrdinal() {
        var topology = WorldTopologyCompilation.Find(new WorldStateSection(Lattices: [Box(4, 4, 4)]), "box")!;
        // The old (direction + DirectionCount/2) % DirectionCount trick puts N's (0) "opposite" at ordinal 13
        // ("US"), not S (4) — the 26 box directions are not paired by half-count offset the way Grid/Hex/Ring are.
        Assert.Equal(topology.Direction("S"), topology.Opposite(topology.Direction("N")));
        Assert.Equal(topology.Direction("N"), topology.Opposite(topology.Direction("S")));
        for (var direction = 0; direction < topology.DirectionCount; direction++) {
            Assert.Equal(direction, topology.Opposite(topology.Opposite(direction)));
        }
    }

    private static WorldStateLatticeTopology Box(int width, int depth, int layers) =>
        new("box", new DocumentVector3(0, 0, 0), 0.5f, width, depth, Layers: layers, Kind: WorldTopologyKind.Box, LayerHeight: 0.5f);
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;
}
