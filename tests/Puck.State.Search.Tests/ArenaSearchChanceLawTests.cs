using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a chance node over arena scopes averages exactly over its baked outcome table, at
/// the root and at a ply inside the walk, and a tree job samples one outcome per playout from the stream its plan
/// authors. Every average is compared against the hand-computed rational it must equal.</summary>
public sealed class ArenaSearchChanceLawTests {
    private static StateRow[] Roll() => [
        Keyed(
            name: "roll",
            ("only", 0L)
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
            ("token", 0L),
            ("to", 0L),
            ("score", 0L)
        ),
    ];
    private static SearchPlan RollPlan(long[] outcomes, ulong[] weights) =>
        new(
            Name: "chance",
            Tokens: "roll",
            Topology: null,
            Zones: [],
            CellCount: 1,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: 64,
            JudgeCost: 1L,
            Depth: 1,
            Best: "best",
            Shapes: [new SearchShapePlan(
                    Kind: SearchShapeKind.Relocate,
                    Displace: true,
                    Directions: [],
                    PairWithIndex: -1
                )],
            Chance: new SearchChancePlan(
                AtDepth: 0,
                CellCount: 1,
                Outcomes: outcomes,
                Row: "roll",
                Weights: weights
            )
        ) {
            Scored = true,
        };

    // A chance table that does not hold one run of cell values per weight, or whose weights sum past one word, is
    // refused by name when the job set is installed, never indexed past its end or averaged against a wrapped total.
    [Theory]
    [InlineData(new long[] { 10L }, new ulong[] { 1UL, 1UL }, "outcome table")]
    [InlineData(new long[] { 10L, 20L, 30L }, new ulong[] { 1UL, 1UL }, "outcome table")]
    [InlineData(new long[] { }, new ulong[] { }, "outcome table")]
    [InlineData(new long[] { 10L, 20L }, new ulong[] { (1UL << 63), (1UL << 63) }, "weights sum")]
    public void AChanceTableOfTheWrongShapeIsRefusedByName(long[] outcomes, ulong[] weights, string expected) {
        var position = new Position(rows: Roll());

        Assert.True(
            condition: ArenaSearchPlan.TryResolve(
                catalog: position.Catalog,
                drawSeed: 0UL,
                plan: RollPlan(
                    outcomes: outcomes,
                    weights: weights
                ),
                reason: out var reason,
                resolved: out var resolved
            ),
            userMessage: reason
        );
        Assert.False(condition: new ArenaSearch(arena: position.Arena).Rebuild(
            judges: [RuleJudge(
                position: position,
                rules: [],
                score: "roll[only]"
            )],
            plans: [resolved],
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: expected
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "'chance'"
        );
    }
    // A one-cell chance row with two equally weighted outcomes (10 and 20), a root chance node (depth one, so no
    // move follows the draw), and a score that reads the row directly: the value is exactly (10+20)/2.
    [Fact]
    public void ExpectiminimaxOverATwoOutcomeChanceEqualsTheHandComputedAverage() {
        var arena = RunRules(
            makePlan: static _ => RollPlan(
                outcomes: [10L, 20L],
                weights: [1UL, 1UL]
            ),
            rows: Roll(),
            rules: [],
            score: "roll[only]"
        );

        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "score", 15L))
        );
        // A chance node never chose a move — token and target land the no-move sentinel.
        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "token", -1L))
        );
    }
    // Weights need not be equal: three parts weight on 100 against one part on 0 rounds to 75 (round-half-up over
    // the exact rational (0*1 + 100*3) / 4 = 75).
    [Fact]
    public void ExpectiminimaxWeightsTheAverageByEachOutcomesShare() {
        var arena = RunRules(
            makePlan: static _ => RollPlan(
                outcomes: [0L, 100L],
                weights: [1UL, 3UL]
            ),
            rows: Roll(),
            rules: [],
            score: "roll[only]"
        );

        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "score", 75L))
        );
    }
    // A chance node one ply below the root: each root candidate folds to the average of the position it reached,
    // rather than to a move choice of the opponent's.
    [Fact]
    public void AChancePlyBelowTheRootFoldsTheAveragedValueOfThePositionItReached() {
        StateRow[] rows = [
            Keyed(
                name: "piece",
                ("t0", 0L)
            ),
            Keyed(
                name: "luck",
                ("only", 0L)
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
        var text = "(piece[t0] * 10) + luck[only]";

        var arena = RunRules(
            makePlan: _ => new SearchPlan(
                Name: "chance",
                Tokens: "piece",
                Topology: null,
                Zones: [],
                CellCount: 4,
                Turn: "turn",
                Verdict: "verdict",
                Off: -1L,
                Nodes: 256,
                JudgeCost: 1L,
                Depth: 2,
                Best: "best",
                Shapes: [new SearchShapePlan(
                        Kind: SearchShapeKind.Relocate,
                        Displace: false,
                        Directions: [],
                        PairWithIndex: -1
                    )],
                Chance: new SearchChancePlan(
                    AtDepth: 1,
                    CellCount: 1,
                    Outcomes: [-4L, 4L],
                    Row: "luck",
                    Weights: [1UL, 1UL]
                )
            ) {
                Scored = true,
            },
            rows: rows,
            rules: [SearchFixture.AcceptEveryCandidate()],
            score: text
        );

        // Cell 3 is the best move and the draw averages to zero around it.
        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "to", 3L))
        );
        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "score", 30L))
        );
    }
    private static StateRow[] PieceAndLuck() => [
        Keyed(
            name: "piece",
            ("t0", 0L)
        ),
        Keyed(
            name: "luck",
            ("only", 0L)
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
    private static SearchPlan PieceAndLuckPlan(int atDepth, int depth) =>
        new(
            Name: "chance",
            Tokens: "piece",
            Topology: null,
            Zones: [],
            CellCount: 4,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: 256,
            JudgeCost: 1L,
            Depth: depth,
            Best: "best",
            Shapes: [new SearchShapePlan(
                    Kind: SearchShapeKind.Relocate,
                    Displace: false,
                    Directions: [],
                    PairWithIndex: -1
                )],
            Chance: new SearchChancePlan(
                AtDepth: atDepth,
                CellCount: 1,
                Outcomes: [-4L, 4L],
                Row: "luck",
                Weights: [1UL, 1UL]
            )
        ) {
            Scored = true,
        };

    // A reply beneath a chance ply is the opponent's best, not its worst. The score is ten times the piece's cell
    // for whoever moved it there, so the opponent answers any root move with the highest free cell: 30, or 20 when
    // the root took cell 3 itself. The root therefore takes cell 3 and the draw averages out around -20.
    [Fact]
    public void AReplyBeneathAChancePlyIsTheOpponentsBestAndFoldsNegated() {
        var arena = RunRules(
            makePlan: static _ => PieceAndLuckPlan(
                atDepth: 1,
                depth: 3
            ),
            rows: PieceAndLuck(),
            rules: [SearchFixture.AcceptEveryCandidate()],
            score: "(piece[t0] * 10) + luck[only]"
        );

        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "to", 3L))
        );
        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "score", -20L))
        );
    }
    // A root chance node spends one ply of the plan's depth and searches the rest: at depth two the side to move
    // answers each draw with its best move, cell 3, so the value is 30 rather than the undrawn position's 0.
    [Fact]
    public void ARootChanceNodeSearchesThePlansRemainingDepth() {
        var arena = RunRules(
            makePlan: static _ => PieceAndLuckPlan(
                atDepth: 0,
                depth: 2
            ),
            rows: PieceAndLuck(),
            rules: [SearchFixture.AcceptEveryCandidate()],
            score: "(piece[t0] * 10) + luck[only]"
        );

        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "score", 30L))
        );
        Assert.Contains(
            collection: arena,
            filter: write => (write == ("best", "token", -1L))
        );
    }
    // A scored position is the one last judged, and a chance draw is no ply of its own: the score's view carries
    // the ply its judge saw, beneath a chance node as above one.
    [Fact]
    public void APositionIsScoredAtThePlyItWasJudgedAt() {
        var position = new Position(rows: PieceAndLuck());
        var slot = position.SlotKey;
        var turn = position.Ordinal(name: "turn");
        var verdict = position.Ordinal(name: "verdict");
        var judged = 0;
        var scored = new List<(int Judged, int Scored)>();
        var search = SearchFixture.Build(
            judge: new DelegateJudge(
                arena: position.Arena,
                judge: (in ArenaSearchView view) => {
                    judged = view.Ply;
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
                score: (in ArenaSearchView view) => {
                    scored.Add(item: (judged, view.Ply));

                    return 0L;
                }
            ),
            plan: PieceAndLuckPlan(
                atDepth: 1,
                depth: 3
            ),
            position: position
        );

        _ = RunArena(
            catalog: position.Catalog,
            search: search
        );

        Assert.Contains(
            collection: scored,
            filter: static pair => (pair.Scored == 2)
        );
        Assert.All(
            action: static pair => Assert.Equal(
                actual: pair.Scored,
                expected: pair.Judged
            ),
            collection: scored
        );
    }
    // A tree job's playout draws its chance ply from the stream the plan authors, so two jobs with the same seed
    // agree and a job with another seed walks a different stream.
    [Fact]
    public void ATreeJobDrawsItsChancePlyFromTheSeedItsPlanAuthors() {
        StateRow[] rows = [
            Keyed(
                name: "piece",
                ("a", 0L)
            ),
            Keyed(
                name: "luck",
                ("only", 0L)
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
                ("token", 0L),
                ("to", 0L),
                ("score", 0L)
            ),
        ];
        var text = "piece[a] == 3 ? 1000 : (piece[a] == 1 ? -1000 : luck[only])";

        ArenaSearch Make(ulong seed) {
            var position = new Position(rows: rows);

            return SearchFixture.Build(
                drawSeed: seed,
                judge: RuleJudge(
                    position: position,
                    rules: [SearchFixture.AcceptEveryCandidate()],
                    score: text
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
                    Iterations: 32,
                    Method: SearchMethod.Tree,
                    Chance: new SearchChancePlan(
                        AtDepth: 1,
                        CellCount: 1,
                        Outcomes: [-500L, 500L],
                        Row: "luck",
                        Weights: [1UL, 1UL]
                    )
                ) {
                    Scored = true,
                },
                position: position
            );
        }

        var first = Make(seed: 7UL);
        var second = Make(seed: 7UL);
        var other = Make(seed: 8UL);

        for (var step = 0; (step < 200); step++) {
            _ = first.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );
            _ = second.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );
            _ = other.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );
        }

        var a = first.Status(index: 0);
        var b = second.Status(index: 0);

        Assert.True(
            condition: a.Done,
            userMessage: a.ToString()
        );
        Assert.Equal(
            actual: b,
            expected: a
        );
        Assert.Equal(
            expected: first.Capture().Jobs[0].Tree!.Seed,
            actual: second.Capture().Jobs[0].Tree!.Seed
        );
        Assert.NotEqual(
            expected: first.Capture().Jobs[0].Tree!.Seed,
            actual: other.Capture().Jobs[0].Tree!.Seed
        );
    }
}
