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
    public void YResolvesToALayerAndASpaceDiagonalIsALine() {
        var topology = WorldTopologyCompilation.Find(new WorldStateSection(Lattices: [Box(4, 4, 4)]), "box")!;
        Assert.True(topology.TryCellOf(new FixedVector3(FixedQ4816.FromDouble(0.6), FixedQ4816.FromDouble(0.8), FixedQ4816.FromDouble(0.1)), out var cell));
        Assert.Equal((1 * 4 + 0) * 4 + 1, cell);
        Assert.False(topology.TryCellOf(new FixedVector3(FixedQ4816.FromDouble(0.6), FixedQ4816.FromDouble(-0.1), FixedQ4816.FromDouble(0.1)), out _));
        Assert.False(topology.TryCellOf(new FixedVector3(FixedQ4816.FromDouble(0.6), FixedQ4816.FromDouble(2.5), FixedQ4816.FromDouble(0.1)), out _));

        static WorldStateCell Mark(int x, int y, int z) => new(WorldCellName.Parse((((z * 4) + y) * 4 + x).ToString()), 1L);
        var board = new WorldStateRow(WorldCellName.Parse("cube"), CellKind.Int, Cells: [Mark(0, 0, 0), Mark(1, 1, 1), Mark(2, 2, 2), Mark(3, 3, 3)], Board: new("box"));
        var winner = new WorldStateRow(WorldCellName.Parse("winner"), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [board, winner], Lattices: [Box(4, 4, 4)]),
            Rules = [new WorldRule(WorldCellName.Parse("win"), [new ActionEffect.SetState(State: "winner", FromState: "$board:line:cube:4:1:atLeast")])],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();
        Assert.Equal(1L, Value(fixture, "winner"));

        var broken = definition with { StateRaw = definition.StateRaw! with { World = [board with { Cells = [Mark(0, 0, 0), Mark(1, 1, 1), Mark(2, 2, 2)] }, winner] } };
        using var control = Fixtures.FreshServer(definition: broken);
        control.Step();
        Assert.Equal(0L, Value(control, "winner"));

        Assert.False(WorldTopologyCompilation.TryValidate(Box(4, 4, 4) with { LayerHeight = 0f }, out var heightReason));
        Assert.Contains("layerHeight", heightReason);
        Assert.False(WorldTopologyCompilation.TryValidate(new WorldStateLatticeTopology("g", new DocumentVector3(0, 0, 0), 1, 4, 4, Kind: WorldTopologyKind.Grid, LayerHeight: 1f), out var gridReason));
        Assert.Contains("belongs to a box", gridReason);
    }

    [Fact]
    public void AnExactLineOfFourIsTrueAndAContinuingFifthCellIsFalse() {
        // Two boards over the same 5-wide, 2-layer box: "isolated" marks only x0..x3 along E/W (a genuine run of
        // exactly four, flanked by real but unmarked/absent cells both ways) and "extended" marks x0..x4 (a run of
        // five, so no four-cell window along it is isolated). Both live at layer 0; layer 1 exists only so a
        // direction whose opposite the old (direction + DirectionCount/2) % DirectionCount formula miscomputed as a
        // layer-shifted cell resolves to a real, unmarked cell instead of an out-of-range one — the case that used
        // to slip past HasLine's exact check on "extended" and report a false positive.
        static WorldStateRow Row(string name, params int[] indices) => new(
            WorldCellName.Parse(name), CellKind.Int,
            Cells: [.. indices.Select(i => new WorldStateCell(WorldCellName.Parse(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), 7L))],
            Board: new("box")
        );
        static WorldStateRow Winner(string name) => new(WorldCellName.Parse(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, 0L)]);

        var isolated = Row("isolated", 0, 1, 2, 3);
        var extended = Row("extended", 0, 1, 2, 3, 4);
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [isolated, extended, Winner("winnerIsolated"), Winner("winnerExtended")], Lattices: [Box(5, 1, 2)]),
            Rules = [
                new WorldRule(WorldCellName.Parse("markIsolated"), [new ActionEffect.SetState(State: "winnerIsolated", FromState: "$board:line:isolated:4:7:exact")]),
                new WorldRule(WorldCellName.Parse("markExtended"), [new ActionEffect.SetState(State: "winnerExtended", FromState: "$board:line:extended:4:7:exact")]),
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
