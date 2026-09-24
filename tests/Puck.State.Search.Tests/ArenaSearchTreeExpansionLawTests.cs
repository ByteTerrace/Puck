using Puck.Maths;

using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a tree iteration grows at most one node and judges a handful of candidates however wide
/// the position is; a node holds at most one child more than the integer square root of its visits; a pool that fills
/// keeps the search playing out until it lands; and the same plan grows the same tree on every run.</summary>
public sealed class ArenaSearchTreeExpansionLawTests {
    private const int Wide = 64;

    private static ArenaSearch Make(Position position, int depth, int iterations) {
        var piece = position.Ordinal(name: "piece");
        var turn = position.Ordinal(name: "turn");
        var verdict = position.Ordinal(name: "verdict");
        var slot = position.SlotKey;
        var token = position.Key(name: "t0");

        return Build(
            judge: new DelegateJudge(
                arena: position.Arena,
                judge: (in ArenaSearchView view) => {
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
                // A score that differs by cell, so selection has means to tell apart.
                score: (in ArenaSearchView view) => (view.Arena.TryReadLiveNumber(
                    key: token,
                    rowOrdinal: piece,
                    time: ArenaTime.At(
                        engineTick: view.EngineTick,
                        tick: view.Tick
                    ),
                    value: out var cell
                )
                    ? ((cell % 7) * 100L)
                    : 0L
                )
            ),
            plan: new SearchPlan(
                Name: "moves",
                Tokens: "piece",
                Topology: null,
                Zones: [],
                CellCount: Wide,
                Turn: "turn",
                Verdict: "verdict",
                Off: -1L,
                Nodes: 4096,
                Work: SearchWork.NodeBounded(judge: 1L),
                Depth: depth,
                Best: "best",
                Shapes: [new SearchShapePlan(
                        Kind: SearchShapeKind.Relocate,
                        Displace: true,
                        Directions: [],
                        CompanionIndex: -1
                    )],
                Iterations: iterations,
                Method: SearchMethod.MonteCarlo
            ) {
                Scored = true,
            },
            position: position
        );
    }
    private static void RunToLanding(ArenaSearch search) {
        for (var step = 0; ((step < 100_000) && !search.Status(index: 0).Done); step++) {
            _ = search.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );
        }

        Assert.True(condition: search.Status(index: 0).Done);
    }
    private static int Children(ArenaSearchTreeCheckpoint tree, int node) {
        var count = 0;

        for (var child = tree.FirstChild[node]; (child >= 0); child = tree.NextSibling[child]) {
            count++;
        }

        return count;
    }

    // The root walk judges each of the root's 63 candidates once; after it, an iteration judges at most one candidate
    // per ply of the path it selects, the one it grows, and one per playout ply below that. Judging a whole board per
    // grown node would spend 63 judges on each.
    [Fact]
    public void AnIterationJudgesAHandfulOfCandidatesOnAWideBoard() {
        const int Depth = 3;
        const int Iterations = 128;

        var search = Make(
            depth: Depth,
            iterations: Iterations,
            position: new Position(rows: Board(
                cells: Wide,
                tokens: 1
            ))
        );

        RunToLanding(search: search);

        var status = search.Status(index: 0);
        var tree = (status.Nodes - (Wide - 1));
        Puck.Abstractions.Counting.IWorkCounterSource counts = search;

        Assert.InRange(
            actual: tree,
            high: (Iterations * ((2L * Depth) + 1L)),
            low: Iterations
        );

        // The search's own counters tell the same story: every judged candidate, one grown node per iteration here
        // (the pool never fills), and one playout ply for each of the depth's plies below the grown node.
        Assert.True(condition: counts.TryRead(kind: SearchWorkKinds.Candidates, value: out var candidates));
        Assert.True(condition: counts.TryRead(kind: SearchWorkKinds.Expansions, value: out var expansions));
        Assert.True(condition: counts.TryRead(kind: SearchWorkKinds.PlayoutPlies, value: out var plies));
        Assert.Equal(expected: status.Nodes, actual: candidates);
        Assert.Equal(actual: expansions, expected: ((long)Iterations));
        Assert.Equal(expected: (status.TreeNodes - 1L), actual: expansions);
        Assert.Equal(actual: plies, expected: (Iterations * (Depth - 1L)));
    }
    // Progressive widening: every node holds at most one child more than the integer square root of its visits, and
    // the root, visited once per iteration, grows past one child.
    [Fact]
    public void ANodeHoldsAtMostOneChildMoreThanTheRootOfItsVisits() {
        const int Iterations = 256;

        var search = Make(
            depth: 3,
            iterations: Iterations,
            position: new Position(rows: Board(
                cells: Wide,
                tokens: 1
            ))
        );

        RunToLanding(search: search);

        var tree = search.Capture().Jobs[0].Tree!;

        Assert.Equal(
            actual: tree.Visits[0],
            expected: Iterations
        );
        Assert.InRange(
            actual: Children(node: 0, tree: tree),
            high: (1L + ((long)((ulong)Iterations).SquareRoot())),
            low: 2L
        );

        for (var node = 0; (node < tree.Count); node++) {
            Assert.Equal(
                actual: Children(node: node, tree: tree),
                expected: tree.ChildCount[node]
            );
            Assert.True(
                condition: (tree.ChildCount[node] <= (1L + ((long)((ulong)tree.Visits[node]).SquareRoot()))),
                userMessage: $"node {node}: {tree.ChildCount[node]} children after {tree.Visits[node]} visits"
            );
        }
    }
    // More iterations than the pool has nodes: once the pool is full no node grows, every later iteration plays out
    // from where selection stops, and the job still lands a move.
    [Fact]
    public void AFullPoolKeepsPlayingOutAndLands() {
        const int Iterations = (SearchCapacity.TreeNodes + 1024);

        var position = new Position(rows: Board(
            cells: Wide,
            tokens: 1
        ));
        var search = Make(
            depth: 4,
            iterations: Iterations,
            position: position
        );

        RunToLanding(search: search);

        var tree = search.Capture().Jobs[0].Tree!;

        Assert.Equal(
            actual: tree.Count,
            expected: SearchCapacity.TreeNodes
        );
        Assert.Equal(
            actual: tree.Visits[0],
            expected: Iterations
        );
        Assert.InRange(
            actual: search.Status(index: 0).BestTarget,
            high: (Wide - 1),
            low: 1
        );
    }
    // The expansion order and the playouts draw from the job's own seeded stream, so two runs of one plan over one
    // position grow the same tree, land the same move, and hash the same.
    [Fact]
    public void TheSamePlanGrowsTheSameTree() {
        ulong Grow() {
            var search = Make(
                depth: 3,
                iterations: 200,
                position: new Position(rows: Board(
                    cells: Wide,
                    tokens: 1
                ))
            );

            RunToLanding(search: search);

            var hash = Fnv1aHash.Create();

            search.AppendStateHash(hash: ref hash);

            return hash.Value;
        }

        Assert.Equal(
            actual: Grow(),
            expected: Grow()
        );
    }
}
