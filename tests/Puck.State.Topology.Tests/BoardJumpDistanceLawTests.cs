using System.Numerics;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Topology.Tests;

public sealed class BoardJumpDistanceLawTests {
    private static CompiledTopology Board(int width, int depth = 1) => TopologyCompilation.Compile(
        new LatticeTopology.Grid("hops", new DocumentVector3(0, 0, 0), 1, width, depth), Vector3.Zero);

    [Fact]
    public void AChainHasNoArtificialHopLimitAndWorksAboveSixtyFourCells() {
        var board = Board(121);
        var values = Enumerable.Range(0, 121).Select(i => i % 2 == 1 ? 2L : 0L).ToArray();
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
        var board = Board(5, 5);
        var values = new long[25];
        values[0] = 1;
        values[1] = values[7] = values[13] = values[5] = values[11] = 2;
        var before = values.ToArray();
        var query = new BoardJumpDistanceQuery(board, 14);
        Assert.Equal(3, BoardQueries.Evaluate(query, values, 0, 0));
        Assert.Equal(-1, BoardQueries.Evaluate(new BoardJumpDistanceQuery(board, 24), values, 0, 0));
        Assert.Equal(before, values);
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
        var graph = TopologyCompilation.Compile(new LatticeTopology.Graph("loop", new DocumentVector3(0, 0, 0), 1,
            Enumerable.Range(0, 6).Select(i => new GraphCell(i.ToString(System.Globalization.CultureInfo.InvariantCulture), new DocumentVector3(i, 0, 0))).ToArray(),
            [new("E", "W"), new("W", "E"), new("N", "S"), new("S", "N")],
            [new("0", "1", "E"), new("1", "2", "E"), new("2", "3", "N"), new("3", "4", "N"), new("4", "0", "N"), new("0", "5", "N")]), Vector3.Zero);
        Assert.Equal(2, BoardQueries.Evaluate(new BoardJumpDistanceQuery(graph, 4), [1, 2, 0, 2, 0, 0], 0, 0));
        Assert.Equal(-1, BoardQueries.Evaluate(new BoardJumpDistanceQuery(graph, 5), [1, 2, 0, 2, 0, 0], 0, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void InvalidLiveTargetsAreUnreachable(int target) {
        var board = Board(5);
        Assert.Equal(-1, BoardQueries.Evaluate(new BoardJumpDistanceQuery(board, 0, true), [1, 2, 0, 0, 0], 0, 0, target));
    }
}
