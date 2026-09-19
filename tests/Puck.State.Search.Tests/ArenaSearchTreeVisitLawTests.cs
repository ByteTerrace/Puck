using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: every tree iteration that ends at a child counts that child's visit, whether the
/// child was kept or judged refused on the way back to it, so selection's unvisited-first rule moves on.</summary>
public sealed class ArenaSearchTreeVisitLawTests {
    // The judge accepts each position twice — once for the root's legal-move pass and once for the expansion that
    // enumerates it as a child — and refuses it ever after, so every child is refused the moment an iteration tries
    // to keep it: the drawn child first, and each selected child after it. Every iteration then ends at a child,
    // and the root's visits are its children's.
    [Fact]
    public void AChildRefusedOnTheWayBackToItStillCountsItsVisit() {
        var position = new Position(rows: Board(
            cells: 4,
            tokens: 1
        ));
        var piece = position.Ordinal(name: "piece");
        var slot = position.SlotKey;
        var token = position.Key(name: "t0");
        var turn = position.Ordinal(name: "turn");
        var verdict = position.Ordinal(name: "verdict");
        var seen = new Dictionary<long, int>();
        var compared = 0;
        var search = SearchFixture.Build(
            judge: new DelegateJudge(
                arena: position.Arena,
                judge: (in ArenaSearchView view) => {
                    _ = view.Arena.TryReadRaw(
                        key: token,
                        raw: out var cell,
                        rowOrdinal: piece
                    );

                    seen[cell] = (seen.GetValueOrDefault(key: cell) + 1);

                    if (seen[cell] > 2) {
                        return true;
                    }

                    _ = view.Arena.TryWrite(
                        key: slot,
                        operand: 1L,
                        reason: out _,
                        rowOrdinal: verdict,
                        write: StateWriteKind.Set
                    );
                    _ = view.Arena.TryWrite(
                        key: slot,
                        operand: 1L,
                        reason: out _,
                        rowOrdinal: turn,
                        write: StateWriteKind.Add
                    );

                    return true;
                },
                score: static (in ArenaSearchView _) => 0L
            ),
            plan: new SearchPlan(
                Name: "moves",
                Tokens: "piece",
                Topology: null,
                Zones: [],
                CellCount: 4,
                Turn: "turn",
                Verdict: "verdict",
                Off: -1L,
                Nodes: 64,
                JudgeCost: 1L,
                Depth: 2,
                Best: "best",
                Shapes: [new SearchShapePlan(
                        Kind: SearchShapeKind.Relocate,
                        Displace: true,
                        Directions: [],
                        PairWithIndex: -1
                    )],
                Iterations: 16,
                Method: SearchMethod.Tree
            ) {
                Scored = true,
            },
            position: position
        );

        for (var step = 0; ((step < 200) && !search.Status(index: 0).Done); step++) {
            _ = search.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );

            if (search.Capture().Jobs[0].Tree is { } tree && (tree.ChildCount[0] > 0)) {
                var children = 0L;

                for (var child = tree.FirstChild[0]; (child < (tree.FirstChild[0] + tree.ChildCount[0])); child++) {
                    children += tree.Visits[child];
                }

                Assert.Equal(
                    actual: children,
                    expected: tree.Visits[0]
                );
                compared++;
            }
        }

        Assert.True(condition: search.Status(index: 0).Done);
        Assert.True(condition: (compared > 0));
    }
}
