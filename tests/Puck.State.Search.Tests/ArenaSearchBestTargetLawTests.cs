using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: the best move a job lands names the cell its candidate lands on at every depth. A
/// promotion offers several candidates a cell, so its candidate index is not its cell, and a reply folded up from a
/// deeper ply must still name the cell.</summary>
public sealed class ArenaSearchBestTargetLawTests {
    private static StateRow[] Rows() => [
        Keyed(
            name: "piece",
            ("t0", 0L)
        ),
        Keyed(
            name: "codes",
            ("t0", 0L)
        ),
        Slot(
            name: "turn",
            value: 0L
        ),
        Slot(
            name: "verdict",
            value: 0L
        ),
        Keyed(
            name: "best",
            ("token", -1L),
            ("to", -1L),
            ("score", 0L)
        ),
    ];
    private static SearchPlan Promote(int depth) =>
        new(
            Name: "promotes",
            Tokens: "piece",
            Topology: null,
            Zones: [],
            CellCount: 4,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: 256,
            Work: SearchWork.NodeBounded(judge: 1L),
            Depth: depth,
            Best: "best",
            Shapes: [new SearchShapePlan(
                    Codes: "codes",
                    Directions: [],
                    Displace: false,
                    Kind: SearchShapeKind.Promote,
                    PairWithIndex: -1,
                    PromoteTo: [5L, 6L]
                )]
        ) {
            Scored = true,
        };

    // The score is ten times the piece's cell for whoever moved it there. At depth one the mover takes cell 3; at
    // depth two the opponent answers with the highest free cell, so the mover still takes cell 3 and leaves it 2.
    [Theory]
    [InlineData(1, 30L)]
    [InlineData(2, -20L)]
    public void APromotionsBestMoveNamesItsCellAtEveryDepth(int depth, long score) {
        var landed = RunRules(
            makePlan: _ => Promote(depth: depth),
            rows: Rows(),
            rules: [AcceptEveryCandidate()],
            score: "piece[t0] * 10"
        );

        Assert.Contains(
            collection: landed,
            filter: write => (write == ("best", "to", 3L))
        );
        Assert.Contains(
            collection: landed,
            filter: write => (write == ("best", "score", score))
        );
    }
}
