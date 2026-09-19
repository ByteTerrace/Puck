using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a job with per-seat scores maximizes the mover seat's own entry rather than
/// negating the reply, over arena scopes. A scope rewind discards what the position carried, so each ply keeps its
/// best line's whole vector in a side buffer of its own, and the checkpoint carries those buffers.</summary>
public sealed class ArenaSearchSeatScoreLawTests {
    private static StateRow[] Seats() => [
        Keyed(
            name: "piece",
            ("p", 0L)
        ),
        Slot(
            name: "turn",
            value: 0L
        ),
        Slot(
            name: "branch",
            value: 0L
        ),
        Slot(
            name: "isRoot",
            value: 0L
        ),
        Slot(
            name: "isReply",
            value: 0L
        ),
        Slot(
            name: "accepted",
            value: 0L
        ),
        Slot(
            name: "verdict",
            value: 0L
        ),
        Keyed(
            name: "scores",
            ("0", 0L),
            ("1", 0L),
            ("2", 0L)
        ),
        Keyed(
            name: "best",
            ("token", -1L),
            ("to", -1L),
            ("score", 0L)
        ),
    ];
    private static Rule Judge() =>
        new(
            Name: Name(value: "judge"),
            Effects: [
                new ActionEffect.SetState(
                    State: "isRoot",
                    Expression: ExpressionProgram.Parse(text: "(turn == 0) & ((piece[p] == 1) | (piece[p] == 2))")
                ),
                new ActionEffect.SetState(
                    State: "isReply",
                    Expression: ExpressionProgram.Parse(text: "(turn == 1) & (((branch == 1) & (piece[p] == 3)) | ((branch == 2) & (piece[p] == 4)))")
                ),
                new ActionEffect.SetState(
                    State: "accepted",
                    Expression: ExpressionProgram.Parse(text: "isRoot | isReply")
                ),
                new ActionEffect.SetState(
                    State: "verdict",
                    Expression: ExpressionProgram.Parse(text: "accepted")
                ),
                new ActionEffect.SetState(
                    State: "turn",
                    Expression: ExpressionProgram.Parse(text: "turn + accepted")
                ),
                new ActionEffect.SetState(
                    State: "branch",
                    Expression: ExpressionProgram.Parse(text: "isRoot ? piece[p] : branch")
                ),
                new ActionEffect.SetState(
                    State: "scores",
                    Key: "0",
                    Expression: ExpressionProgram.Parse(text: "(piece[p] == 3) ? 10 : ((piece[p] == 4) ? 8 : 0)")
                ),
                new ActionEffect.SetState(
                    State: "scores",
                    Key: "1",
                    Expression: ExpressionProgram.Parse(text: "(piece[p] == 3) ? 9 : ((piece[p] == 4) ? 1 : 0)")
                ),
                new ActionEffect.SetState(
                    State: "scores",
                    Key: "2",
                    Value: 0m
                ),
            ]
        );
    private static SearchPlan Plan(int nodes = 256) =>
        new(
            Name: "maxn",
            Tokens: "piece",
            Topology: null,
            Zones: [],
            CellCount: 6,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: nodes,
            JudgeCost: 1L,
            Depth: 2,
            Best: "best",
            Shapes: [new SearchShapePlan(
                    Kind: SearchShapeKind.Relocate,
                    Displace: false,
                    Directions: [],
                    PairWithIndex: -1
                )],
            Scores: "scores"
        );

    // Depth 2, three seats, and a scores row: root (seat 0) moves to cell 1 or 2; whichever it picks, seat 1 then
    // has exactly one reply (cell 1 to 3, or cell 2 to 4), landing the position the scores row is read from. Seat
    // 0's own value is 10 through cell 1 and 8 through cell 2 — max-n prefers cell 1. A two-sided reading that
    // instead negated seat 1's own value at the reply (9 through cell 1, 1 through cell 2, so -9 and -1) would have
    // preferred cell 2, since -1 exceeds -9: the two readings disagree, and only max-n's is asked for here.
    [Fact]
    public void MaxNMaximizesTheMoversOwnSeatWhereNegatingTheReplyWouldChooseDifferently() {
        var arena = RunRules(
            makePlan: static _ => Plan(),
            rows: Seats(),
            rules: [Judge()]
        );

        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "to", 1L))
        );
        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "score", 10L))
        );
        // The reading this fold rejects, stated as arithmetic rather than asserted against the engine: negating
        // seat 1's own reply value prefers cell 2 over cell 1.
        Assert.True(condition: (-1L > -9L));
    }
    // The seat vector a ply remembers is its own, not the position's: a job suspended mid-descent carries those
    // buffers through a checkpoint and lands the same answer.
    [Fact]
    public void ASuspendedMaxNJobCarriesItsPerLevelSeatBuffersThroughACheckpoint() {
        var rows = Seats();
        var carried = new Position(rows: rows);
        var whole = new Position(rows: rows);

        ArenaSearch Make(Position position) => SearchFixture.Build(
            judge: RuleJudge(
                position: position,
                rules: [Judge()]
            ),
            plan: Plan(nodes: 2),
            position: position
        );

        var partial = Make(position: carried);

        for (var step = 0; (step < 3); step++) {
            _ = partial.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );
        }

        var captured = partial.Capture();
        var restored = Make(position: carried);

        Assert.True(
            condition: restored.TryRestore(
                checkpoint: captured,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: SearchFixture.RunArena(
                catalog: whole.Catalog,
                search: SearchFixture.Build(
                    judge: RuleJudge(
                        position: whole,
                        rules: [Judge()]
                    ),
                    plan: Plan(),
                    position: whole
                )
            ),
            actual: SearchFixture.RunArena(
                catalog: carried.Catalog,
                search: restored
            )
        );
    }
    [Fact]
    public void APlanDeclaringPerSeatScoresBesideAScoreOrATreeMethodIsRefusedByName() {
        var position = new Position(rows: Seats());

        Assert.False(condition: ArenaSearchPlan.TryResolve(
            catalog: position.Catalog,
            plan: Plan() with {
                Scored = true,
            },
            reason: out var scored,
            resolved: out _
        ));
        Assert.Contains(
            actualString: scored,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "per-seat scores"
        );
        Assert.False(condition: ArenaSearchPlan.TryResolve(
            catalog: position.Catalog,
            plan: Plan() with {
                Iterations = 8,
                Method = SearchMethod.Tree,
            },
            reason: out var treed,
            resolved: out _
        ));
        Assert.Contains(
            actualString: treed,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "per-seat scores"
        );
    }
}
