using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the board-mask vocabulary: occupancy read as a 64-bit mask, the topology-aware shift that drops bits
/// at an edge, the mask landing back on a board, cell-wise board algebra, and the ceilings that refuse.</summary>
public sealed class WorldBoardMaskLawTests {
    [Fact]
    public void OccupancyReadsAsAMaskAndABoardShiftFollowsTheTopologyWithoutWrapping() {
        var board = new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0", 1), Cell("1", 2), Cell("2", 2), Cell("3", 1)], Board: new("map"));
        var definition = Document([board, Slot("mask"), Slot("east"), Slot("north")], [
            new WorldRule(Name("mask"), [new ActionEffect.SetState(State: "mask", FromState: "$board:mask:board:2:2")]),
            new WorldRule(Name("east"), [new ActionEffect.SetState(State: "east", Expression: new WorldValueExpression(Tokens: [
                new WorldValueToken.State(Name: "$board:mask:board:1:1"), new WorldValueToken.BoardShift(Topology: "map", Direction: "E"),
            ]))]),
            new WorldRule(Name("north"), [new ActionEffect.SetState(State: "north", Expression: new WorldValueExpression(Tokens: [
                new WorldValueToken.State(Name: "$board:mask:board:1:1"), new WorldValueToken.BoardShift(Topology: "map", Direction: "N"),
            ]))]),
        ]);

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        var topology = WorldTopologyCompilation.Find(definition.StateRaw, "map")!;
        var east = topology.Direction("E");
        var north = topology.Direction("N");
        Assert.Equal(0b0110L, Value(fixture, "mask"));
        var expectedEast = Shift(topology, 0b1001L, east);
        Assert.Equal(expectedEast, Value(fixture, "east"));
        Assert.NotEqual(0L, expectedEast);
        // Row 0 is an edge row northward in one of the two orientations, or a shift away from it: either way the
        // result is the topology's own answer, and nothing wrapped.
        Assert.Equal(Shift(topology, 0b1001L, north), Value(fixture, "north"));
    }

    [Fact]
    public void AMaskLandsBackOnTheBoardAndBoardsCombineCellWise() {
        var board = new WorldStateRow(Name("board"), CellKind.Int, Cells: [Cell("0", 1)], Board: new("map"));
        var other = new WorldStateRow(Name("other"), CellKind.Int, Cells: [Cell("0", 1), Cell("5", 1)], Board: new("map"));
        var target = new WorldStateRow(Name("target"), CellKind.Bool, Board: new("map"));
        var definition = Document([board, other, target, Slot("mask", 0b1010L)], [], []);

        var painted = Apply(definition, new WorldStateTransform.SetMask("board", "mask", Value: 7));
        var cells = Find(painted, "board").Cells!;
        Assert.Equal(7L, WorldDefinitionRows.FindCell(cells, Name("1"))!.Value);
        Assert.Equal(7L, WorldDefinitionRows.FindCell(cells, Name("3"))!.Value);
        Assert.Equal(1L, WorldDefinitionRows.FindCell(cells, Name("0"))!.Value);

        var both = Apply(painted, new WorldStateTransform.Combine("target", "board", WorldBoardCombine.And, "other"));
        Assert.Equal(new[] { "0" }, Members(both, "target"));
        var either = Apply(painted, new WorldStateTransform.Combine("target", "board", WorldBoardCombine.Or, "other"));
        Assert.Equal(new[] { "0", "1", "3", "5" }, Members(either, "target"));
        var onlyLeft = Apply(painted, new WorldStateTransform.Combine("target", "board", WorldBoardCombine.AndNot, "other"));
        Assert.Equal(new[] { "1", "3" }, Members(onlyLeft, "target"));
        var complement = Apply(painted, new WorldStateTransform.Combine("target", "other", WorldBoardCombine.Not));
        Assert.Equal(14, Members(complement, "target").Length);
        // A zero-empty target stays sparse: only members are written; a nonzero empty forces every cell explicit.
        Assert.Single(Find(both, "target").Cells!);
        var loud = painted with { StateRaw = painted.StateRaw! with { World = [.. painted.State.Select(r => r.Name.Value == "target" ? r with { Board = new("map", Empty: 1) } : r)] } };
        var dense = Apply(loud, new WorldStateTransform.Combine("target", "board", WorldBoardCombine.And, "other"));
        Assert.Equal(16, Find(dense, "target").Cells!.Count);
        Assert.Equal(new[] { "0" }, Members(dense, "target"));

        Assert.False(WorldStateTransforms.TryApply(painted, new WorldStateTransform.Combine("target", "board", WorldBoardCombine.Not, "other"), WorldPrincipal.World, 0, "test", out _, out var notReason));
        Assert.Contains("takes no right", notReason);
    }

    [Fact]
    public void MasksRefuseTopologiesPastSixtyFourCellsAndCombineRefusesMixedTopologies() {
        var wide = new WorldStateRow(Name("wide"), CellKind.Int, Board: new("big"));
        var small = new WorldStateRow(Name("small"), CellKind.Int, Board: new("map"));
        var definition = Document([wide, small, Slot("mask")], [new WorldRule(Name("mask"), [new ActionEffect.SetState(State: "mask", FromState: "$board:mask:wide:1:1")])], [],
            lattices: [Grid("map", 4), Grid("big", 9)]);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var maskReason));
        Assert.Contains("at most 64", maskReason);

        var shifted = Document([wide, small, Slot("mask")], [new WorldRule(Name("shift"), [new ActionEffect.SetState(State: "mask", Expression: new WorldValueExpression(Tokens: [
            new WorldValueToken.Constant(Value: 1m), new WorldValueToken.BoardShift(Topology: "big", Direction: "E"),
        ]))])], [], lattices: [Grid("map", 4), Grid("big", 9)]);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(shifted, out var shiftReason));
        Assert.Contains("at most 64", shiftReason);

        var mixed = Document([wide, small, Slot("mask")], [], [], lattices: [Grid("map", 4), Grid("big", 9)]);
        Assert.False(WorldStateTransforms.TryApply(mixed, new WorldStateTransform.Combine("small", "wide", WorldBoardCombine.Or, "small"), WorldPrincipal.World, 0, "test", out _, out var mixedReason));
        Assert.Contains("same topology", mixedReason);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(mixed with { Rules = [new WorldRule(Name("bad"), [new ActionEffect.TransformState(new WorldStateTransform.SetMask("wide", "mask"))])] }, out var setReason));
        Assert.Contains("at most 64", setReason);
    }

    private static long Shift(CompiledWorldTopology topology, long mask, int direction) {
        var result = 0L;
        for (var cell = 0; cell < topology.CellCount; cell++) {
            if (((mask >> cell) & 1L) != 0L && topology.Neighbour(cell, direction) is var next && next >= 0) {
                result |= 1L << next;
            }
        }
        return result;
    }
    private static string[] Members(WorldDefinition document, string row) =>
        (Find(document, row).Cells ?? []).Where(c => c.Value != 0L).Select(c => c.Key.Value).ToArray();
    private static WorldStateLatticeTopology Grid(string name, int side) =>
        new(name, new DocumentVector3(0, 0, 0), 1, side, side, Kind: WorldTopologyKind.Grid);
    private static WorldDefinition Document(WorldStateRow[] rows, WorldRule[] rules, WorldPatternRow[]? patterns = null, WorldStateLatticeTopology[]? lattices = null) => Fixtures.BuildDocument() with {
        StateRaw = new(World: rows, Lattices: lattices ?? [Grid("map", 4)]),
        PatternsRaw = patterns ?? [],
        Rules = rules,
    };
    private static WorldDefinition Apply(WorldDefinition definition, WorldStateTransform transform) {
        Assert.True(WorldStateTransforms.TryApply(definition, transform, WorldPrincipal.World, 1, "test", out var candidate, out var reason), reason);
        return candidate!;
    }
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Slot(string name, long value = 0) => new(Name(name), CellKind.Int, Cells: [new WorldStateCell(WorldStateRow.SlotKey, value)]);
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;
    private static long Value(WorldFixture fixture, string row) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, row)!.Cells, WorldStateRow.SlotKey)!.Value;
}
