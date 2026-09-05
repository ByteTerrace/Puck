using System.Numerics;
using Xunit;
using Puck.Assets.Documents;

namespace Puck.State.Tests;

/// <summary>A component is the flood of in-range cells from the key cell along the topology's directions, its
/// boundary the distinct adjacent cells in a second range, both under a settled-cell budget that reads -2 when it
/// runs out.</summary>
public sealed class BoardComponentLawTests {
    private static readonly TopologyDirection[] s_orthogonal = [new("N", 0, -1), new("E", 1, 0), new("S", 0, 1), new("W", -1, 0)];

    private static CompiledTopology Board() => TopologyCompilation.Compile(
        topology: new LatticeTopology.Grid(Name: "go", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 5, Depth: 5, Directions: s_orthogonal),
        anchorOffset: Vector3.Zero
    );

    // Row-major 5×5: 1 = black, 2 = white, 0 = empty.
    private static readonly long[] s_position = [
        0, 1, 1, 0, 0,
        1, 1, 0, 2, 0,
        0, 1, 2, 2, 0,
        0, 0, 2, 0, 0,
        1, 0, 0, 0, 1,
    ];

    [Fact]
    public void AComponentFloodsAlongTheDirectionsAndItsBoundaryCountsDistinctAdjacentEmptyCells() {
        var topology = Board();
        var component = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, boundaryLower: 0, boundaryUpper: 0, boundary: false);
        var boundary = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, boundaryLower: 0, boundaryUpper: 0, boundary: true);

        Assert.Equal(5L, BoardQueries.Evaluate(component, s_position, 0, source: 1));
        Assert.Equal(5L, BoardQueries.Evaluate(component, s_position, 0, source: 11));
        Assert.Equal(1L, BoardQueries.Evaluate(component, s_position, 0, source: 20));
        Assert.Equal(0L, BoardQueries.Evaluate(component, s_position, 0, source: 0));
        // The black group at {1, 2, 5, 6, 11}: empty neighbours are 0, 3, 7, 10, 16 — each once, however many
        // members touch it.
        Assert.Equal(5L, BoardQueries.Evaluate(boundary, s_position, 0, source: 6));
        Assert.Equal(2L, BoardQueries.Evaluate(boundary, s_position, 0, source: 20));
        var white = new BoardComponentQuery(topology, lower: 2, upper: 2, maxVisits: 25, boundaryLower: 0, boundaryUpper: 0, boundary: true);
        Assert.Equal(7L, BoardQueries.Evaluate(white, s_position, 0, source: 12));
    }

    // Row-major 5×5: black 1 at cell 0 with white 2 at cell 1; cell 5 is black's last boundary cell.
    private static readonly long[] s_atari = [
        1, 2, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
    ];

    [Fact]
    public void APlacementKnowsItsOwnBoundaryAndWhatItEncloses() {
        var topology = Board();
        var whiteBoundaryAt = new BoardComponentQuery(topology, lower: 2, upper: 2, maxVisits: 25, boundaryLower: 0, boundaryUpper: 0, BoardQueryKind.BoundaryAt);
        var whiteEnclosedAt = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, boundaryLower: 0, boundaryUpper: 0, BoardQueryKind.EnclosedAt);
        // White at 5 closes black's last gap: one cell enclosed, and the value itself has boundary at 6 and 10.
        Assert.Equal(1L, BoardQueries.Evaluate(whiteEnclosedAt, s_atari, 0, source: 5));
        Assert.Equal(2L, BoardQueries.Evaluate(whiteBoundaryAt, s_atari, 0, source: 5));
        // White at 2 joins the value at 1: boundary 3, 6, 7 (never the key cell); black keeps its boundary cell at 5.
        Assert.Equal(3L, BoardQueries.Evaluate(whiteBoundaryAt, s_atari, 0, source: 2));
        Assert.Equal(0L, BoardQueries.Evaluate(whiteEnclosedAt, s_atari, 0, source: 2));
        // An occupied key cell is no placement.
        Assert.Equal(-1L, BoardQueries.Evaluate(whiteBoundaryAt, s_atari, 0, source: 0));

        // Black at 0 with white at 1 and 5: no empty neighbour, no friendly component, and neither white value loses its
        // last boundary cell, and both facets say so.
        long[] surrounded = [
            0, 2, 0, 0, 0,
            2, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
        ];
        var blackBoundaryAt = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, boundaryLower: 0, boundaryUpper: 0, BoardQueryKind.BoundaryAt);
        var blackEnclosedAt = new BoardComponentQuery(topology, lower: 2, upper: 2, maxVisits: 25, boundaryLower: 0, boundaryUpper: 0, BoardQueryKind.EnclosedAt);
        Assert.Equal(0L, BoardQueries.Evaluate(blackBoundaryAt, surrounded, 0, source: 0));
        Assert.Equal(0L, BoardQueries.Evaluate(blackEnclosedAt, surrounded, 0, source: 0));
    }

    // Row-major 5×5: the black pair at 0 and 1 breathes only at 6; white holds 2 and 5.
    private static readonly long[] s_pair = [
        1, 1, 2, 0, 0,
        2, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
    ];

    [Fact]
    public void EnclosedAtCountsCellsAndClearEnclosedSweepsThem() {
        var topology = Board();
        var whiteEnclosedAt = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, boundaryLower: 0, boundaryUpper: 0, BoardQueryKind.EnclosedAt);
        Assert.Equal(2L, BoardQueries.Evaluate(whiteEnclosedAt, s_pair, 0, source: 6));

        // The value lands, then the transform sweeps the pair; white's own cells stand.
        var board = (long[])s_pair.Clone();
        board[6] = 2;
        Assert.Equal(2L, BoardQueries.ClearEnclosed(topology, board, source: 6, lower: 1, upper: 1, empty: 0));
        Assert.Equal(0L, board[0]);
        Assert.Equal(0L, board[1]);
        Assert.Equal(2L, board[2]);
        Assert.Equal(2L, board[5]);
        Assert.Equal(2L, board[6]);
        // Nothing beside a value that closed no last gap is touched.
        Assert.Equal(0L, BoardQueries.ClearEnclosed(topology, board, source: 2, lower: 1, upper: 1, empty: 0));
        // An empty origin encloses nothing: it is every neighbour's boundary cell.
        var untouched = (long[])s_pair.Clone();
        Assert.Equal(0L, BoardQueries.ClearEnclosed(topology, untouched, source: 6, lower: 1, upper: 1, empty: 0));
        Assert.Equal(s_pair, untouched);
    }

    [Fact]
    public void TheBudgetBoundsTheFloodAndReadsMinusTwoWhenItRunsOut() {
        var topology = Board();
        var budgeted = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 4, boundaryLower: 0, boundaryUpper: 0, boundary: false);
        var exact = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 5, boundaryLower: 0, boundaryUpper: 0, boundary: false);

        Assert.Equal(-2L, BoardQueries.Evaluate(budgeted, s_position, 0, source: 1));
        Assert.Equal(5L, BoardQueries.Evaluate(exact, s_position, 0, source: 1));
        Assert.Equal(((5L * 4L) + 25L), budgeted.Visits);
    }

    [Fact]
    public void WarmComponentQueriesAllocateNothing() {
        var topology = Board();
        var boundary = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, boundaryLower: 0, boundaryUpper: 0, boundary: true);
        _ = BoardQueries.Evaluate(boundary, s_position, 0, source: 6);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var repeat = 0; repeat < 100; repeat++) {
            _ = BoardQueries.Evaluate(boundary, s_position, 0, source: 6);
        }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
