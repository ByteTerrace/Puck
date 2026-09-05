using System.Numerics;
using Xunit;
using Puck.Assets.Documents;

namespace Puck.State.Tests;

/// <summary>A component is the flood of in-range cells from the key cell along the topology's directions, its
/// liberties the distinct adjacent cells in a second range, both under a settled-cell budget that reads -2 when it
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
    public void AComponentFloodsAlongTheDirectionsAndLibertiesCountDistinctAdjacentEmptyCells() {
        var topology = Board();
        var component = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, libertyLower: 0, libertyUpper: 0, liberties: false);
        var liberties = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, libertyLower: 0, libertyUpper: 0, liberties: true);

        Assert.Equal(5L, BoardQueries.Evaluate(component, s_position, 0, source: 1));
        Assert.Equal(5L, BoardQueries.Evaluate(component, s_position, 0, source: 11));
        Assert.Equal(1L, BoardQueries.Evaluate(component, s_position, 0, source: 20));
        Assert.Equal(0L, BoardQueries.Evaluate(component, s_position, 0, source: 0));
        // The black group at {1, 2, 5, 6, 11}: empty neighbours are 0, 3, 7, 10, 16 — each once, however many
        // members touch it.
        Assert.Equal(5L, BoardQueries.Evaluate(liberties, s_position, 0, source: 6));
        Assert.Equal(2L, BoardQueries.Evaluate(liberties, s_position, 0, source: 20));
        var white = new BoardComponentQuery(topology, lower: 2, upper: 2, maxVisits: 25, libertyLower: 0, libertyUpper: 0, liberties: true);
        Assert.Equal(7L, BoardQueries.Evaluate(white, s_position, 0, source: 12));
    }

    // Row-major 5×5: black 1 at cell 0 with white 2 at cell 1; cell 5 is black's last liberty.
    private static readonly long[] s_atari = [
        1, 2, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
    ];

    [Fact]
    public void APlacementKnowsItsOwnLibertiesAndWhatItCaptures() {
        var topology = Board();
        var whiteLibertiesAt = new BoardComponentQuery(topology, lower: 2, upper: 2, maxVisits: 25, libertyLower: 0, libertyUpper: 0, BoardQueryKind.LibertiesAt);
        var whiteCapturesAt = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, libertyLower: 0, libertyUpper: 0, BoardQueryKind.CapturesAt);
        // White at 5 takes black's last liberty: one capture, and the stone itself breathes at 6 and 10.
        Assert.Equal(1L, BoardQueries.Evaluate(whiteCapturesAt, s_atari, 0, source: 5));
        Assert.Equal(2L, BoardQueries.Evaluate(whiteLibertiesAt, s_atari, 0, source: 5));
        // White at 2 joins the stone at 1: liberties 3, 6, 7 (never the key cell); black keeps its liberty at 5.
        Assert.Equal(3L, BoardQueries.Evaluate(whiteLibertiesAt, s_atari, 0, source: 2));
        Assert.Equal(0L, BoardQueries.Evaluate(whiteCapturesAt, s_atari, 0, source: 2));
        // An occupied key cell is no placement.
        Assert.Equal(-1L, BoardQueries.Evaluate(whiteLibertiesAt, s_atari, 0, source: 0));

        // Black at 0 with white at 1 and 5: no empty neighbour, no friendly group, and neither white stone loses its
        // last liberty — suicide, and both facets say so.
        long[] surrounded = [
            0, 2, 0, 0, 0,
            2, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0,
        ];
        var blackLibertiesAt = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, libertyLower: 0, libertyUpper: 0, BoardQueryKind.LibertiesAt);
        var blackCapturesAt = new BoardComponentQuery(topology, lower: 2, upper: 2, maxVisits: 25, libertyLower: 0, libertyUpper: 0, BoardQueryKind.CapturesAt);
        Assert.Equal(0L, BoardQueries.Evaluate(blackLibertiesAt, surrounded, 0, source: 0));
        Assert.Equal(0L, BoardQueries.Evaluate(blackCapturesAt, surrounded, 0, source: 0));
    }

    [Fact]
    public void TheBudgetBoundsTheFloodAndReadsMinusTwoWhenItRunsOut() {
        var topology = Board();
        var budgeted = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 4, libertyLower: 0, libertyUpper: 0, liberties: false);
        var exact = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 5, libertyLower: 0, libertyUpper: 0, liberties: false);

        Assert.Equal(-2L, BoardQueries.Evaluate(budgeted, s_position, 0, source: 1));
        Assert.Equal(5L, BoardQueries.Evaluate(exact, s_position, 0, source: 1));
        Assert.Equal(((5L * 4L) + 25L), budgeted.Visits);
    }

    [Fact]
    public void WarmComponentQueriesAllocateNothing() {
        var topology = Board();
        var liberties = new BoardComponentQuery(topology, lower: 1, upper: 1, maxVisits: 25, libertyLower: 0, libertyUpper: 0, liberties: true);
        _ = BoardQueries.Evaluate(liberties, s_position, 0, source: 6);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var repeat = 0; repeat < 100; repeat++) {
            _ = BoardQueries.Evaluate(liberties, s_position, 0, source: 6);
        }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
