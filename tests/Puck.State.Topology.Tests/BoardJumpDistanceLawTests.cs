using System.Numerics;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Topology.Tests;

public sealed class BoardJumpDistanceLawTests {
    private static CompiledTopology Board(int width, int depth = 1) => TopologyCompilation.Compile(
        new LatticeTopology.Grid("hops", new DocumentVector3(x: 0, y: 0, z: 0), 1, width, depth), Vector3.Zero);

    [Fact]
    public void AChainHasNoArtificialHopLimitAndWorksAboveSixtyFourCells() {
        var board = Board(121);
        var values = Enumerable.Range(count: 121, start: 0).Select(selector: i => (((i % 2) == 1) ? 2L : 0L)).ToArray();

        values[0] = 1;
        var query = new BoardJumpDistanceQuery(board, 120);

        Assert.Equal(60, BoardQueries.Evaluate(query, values, 0, 0));
        values[117] = 0;
        Assert.Equal(-1, BoardQueries.Evaluate(query, values, 0, 0));
        values[117] = 2;
        values[118] = 3;
        Assert.Equal(-1, BoardQueries.Evaluate(query, values, 0, 0));
    }
    [Fact]
    public void ChainsTurnAndCyclesTerminateWithoutMovingOrCapturingAnyBlocker() {
        var board = Board(depth: 5, width: 5);
        var values = new long[25];

        values[0] = 1;
        values[1] = values[7] = values[13] = values[5] = values[11] = 2;
        var before = values.ToArray();
        var query = new BoardJumpDistanceQuery(board, 14);

        Assert.Equal(3, BoardQueries.Evaluate(query, values, 0, 0));
        Assert.Equal(-1, BoardQueries.Evaluate(new BoardJumpDistanceQuery(board, 24), values, 0, 0));
        Assert.Equal(actual: values, expected: before);
    }
    [Fact]
    public void TheMoverCannotJumpOverItsOwnVacatedSource() {
        var board = Board(5);
        long[] values = [0, 0, 1, 2, 0];

        Assert.Equal(1, BoardQueries.Evaluate(new BoardJumpDistanceQuery(board, 4), values, 0, 2));
        Assert.Equal(-1, BoardQueries.Evaluate(new BoardJumpDistanceQuery(board, 0), values, 0, 2));
        Assert.Equal(0, BoardQueries.Evaluate(new BoardJumpDistanceQuery(board, 2), values, 0, 2));
        // A graph can reach a neighbour of its source: 0 -> 2 -> 4 is valid,
        // but the next hop 4 -> 5 would use the vacated 0 as a blocker.
        var graph = TopologyCompilation.Compile(new LatticeTopology.Graph("loop", new DocumentVector3(x: 0, y: 0, z: 0), 1,
            Enumerable.Range(count: 6, start: 0).Select(selector: i => new GraphCell(i.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), new DocumentVector3(x: i, y: 0, z: 0))).ToArray(),
            [new(Name: "E", Opposite: "W"), new(Name: "W", Opposite: "E"), new(Name: "N", Opposite: "S"), new(Name: "S", Opposite: "N")],
            [new("0", "1", CellName.Parse(candidate: "E")), new("1", "2", CellName.Parse(candidate: "E")), new("2", "3", CellName.Parse(candidate: "N")), new("3", "4", CellName.Parse(candidate: "N")), new("4", "0", CellName.Parse(candidate: "N")), new("0", "5", CellName.Parse(candidate: "N"))]), Vector3.Zero);

        Assert.Equal(2, BoardQueries.Evaluate(new BoardJumpDistanceQuery(graph, 4), [1, 2, 0, 2, 0, 0], 0, 0));
        Assert.Equal(-1, BoardQueries.Evaluate(new BoardJumpDistanceQuery(graph, 5), [1, 2, 0, 2, 0, 0], 0, 0));
    }
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    [Theory]
    public void InvalidLiveTargetsAreUnreachable(int target) {
        var board = Board(5);

        Assert.Equal(-1, BoardQueries.Evaluate(new BoardJumpDistanceQuery(board, 0, true), [1, 2, 0, 0, 0], 0, 0, target));
    }
}
