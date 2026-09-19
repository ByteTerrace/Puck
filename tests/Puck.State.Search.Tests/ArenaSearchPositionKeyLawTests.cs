using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a job's transposition key folds the rows its plan addresses and the rows its judge
/// reads or writes, not the whole arena, so two positions differing only outside that reach are the same position
/// to the job; and a tree job's draw stream comes from the seed its plan authors rather than from the store.</summary>
public sealed class ArenaSearchPositionKeyLawTests {
    private static StateRow[] Rows() => [
        .. Board(
            cells: 4,
            tokens: 2
        ),
        Slot(
            name: "weather",
            value: 0L
        ),
        Slot(
            name: "mood",
            value: 0L
        ),
        Slot(
            name: "noise",
            value: 0L
        ),
    ];
    // Beside accepting a candidate, the judge reads one row the plan never names, so the key must carry it.
    private static Rule ReadWeather() =>
        new(
            Name: Name(value: "weather"),
            Effects: [
                new ActionEffect.SetState(
                    Expression: ExpressionProgram.Parse(text: "weather"),
                    State: "mood"
                ),
            ]
        );
    private static SearchPlan Plan(int depth = 2, SearchMethod method = SearchMethod.Negamax, int iterations = 0, bool scored = false) =>
        new(
            Name: "moves",
            Tokens: "piece",
            Topology: null,
            Zones: [],
            CellCount: 4,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: 64,
            Work: SearchWork.NodeBounded(judge: 1L),
            Depth: depth,
            Best: "best",
            Shapes: [new SearchShapePlan(
                    Kind: SearchShapeKind.Relocate,
                    Displace: false,
                    Directions: [],
                    PairWithIndex: -1
                )],
            Counts: "counts",
            Iterations: iterations,
            Legal: "legal",
            Method: method
        ) {
            Scored = scored,
        };
    private static void Write(Position position, string row, long value) => Assert.True(condition: position.Arena.TryWrite(
        key: position.SlotKey,
        operand: value,
        reason: out var reason,
        rowOrdinal: position.Ordinal(name: row),
        write: StateWriteKind.Set
    ), userMessage: reason);

    [Fact]
    public void ARowOutsideThePlansReachLeavesThePositionKeyStandingAndARowInsideItDoesNot() {
        var position = new Position(rows: Rows());
        var search = SearchFixture.Build(
            judge: RuleJudge(
                position: position,
                rules: [SearchFixture.AcceptEveryCandidate(), ReadWeather()]
            ),
            plan: Plan(),
            position: position
        );
        var key = search.PositionKey(index: 0);
        var hash = position.Arena.ComputeHash();

        Write(
            position: position,
            row: "noise",
            value: 5L
        );
        Assert.Equal(
            expected: key,
            actual: search.PositionKey(index: 0)
        );
        // The arena's own fold does move for that row: the key is narrower than the store by construction.
        Assert.NotEqual(
            expected: hash,
            actual: position.Arena.ComputeHash()
        );

        // A row the plan names.
        Assert.True(condition: position.Arena.TryWrite(
            key: position.Key(name: "t0"),
            operand: 3L,
            reason: out var reason,
            rowOrdinal: position.Ordinal(name: "piece"),
            write: StateWriteKind.Set
        ), userMessage: reason);
        Assert.NotEqual(
            expected: key,
            actual: search.PositionKey(index: 0)
        );
    }
    // The key folds a row as the arena stores it, not the values read back by position: a full ring pushed the
    // value its oldest slot already holds changes no slot and is still another position, and at a clocked row the
    // same stored epoch is another position at another tick.
    [Fact]
    public void ThePositionKeyFoldsARowsCursorsAndItsClockAsWellAsItsValues() {
        var position = new Position(rows: [
            .. Rows(),
            new StateRow(
                Name: Name(value: "log"),
                Kind: CellKind.Int,
                Domain: new StateDomain.Ring(
                    Capacity: 3,
                    Empty: -1L
                )
            ),
            new StateRow(
                Name: Name(value: "meter"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Int(value: 10L)
                    )],
                Advance: new StateAdvance(
                    PerSecondDenominator: 1L,
                    PerSecondNumerator: 60L
                )
            ),
        ]);
        var log = position.Ordinal(name: "log");

        ArenaSearch Make(params int[] keyRows) => SearchFixture.Build(
            judge: new DelegateJudge(
                arena: position.Arena,
                judge: static (in ArenaSearchView _) => true,
                keyRows: keyRows
            ),
            plan: Plan(),
            position: position
        );

        foreach (var value in (long[])[1L, 2L, 3L]) {
            Assert.True(condition: position.Arena.TryPush(
                reason: out _,
                rowOrdinal: log,
                value: value
            ));
        }

        var ring = Make(log);
        var key = ring.PositionKey(index: 0);

        Assert.True(condition: position.Arena.TryPush(
            reason: out _,
            rowOrdinal: log,
            value: 1L
        ));
        Assert.NotEqual(
            actual: ring.PositionKey(index: 0),
            expected: key
        );

        var clocked = Make(position.Ordinal(name: "meter"));
        var untimed = Make(log);

        ulong KeyAt(ArenaSearch search, ulong tick) {
            _ = search.Step(
                apply: static _ => true,
                engineTick: tick,
                tick: tick
            );

            return search.PositionKey(index: 0);
        }

        Assert.NotEqual(
            actual: KeyAt(search: clocked, tick: 9UL),
            expected: KeyAt(search: clocked, tick: 1UL)
        );
        Assert.Equal(
            actual: KeyAt(search: untimed, tick: 9UL),
            expected: KeyAt(search: untimed, tick: 1UL)
        );
    }
    [Fact]
    public void ARowOnlyTheJudgeReadsIsInsideThePlansReach() {
        var position = new Position(rows: Rows());
        var search = SearchFixture.Build(
            judge: RuleJudge(
                position: position,
                rules: [SearchFixture.AcceptEveryCandidate(), ReadWeather()]
            ),
            plan: Plan(),
            position: position
        );
        var key = search.PositionKey(index: 0);

        Write(
            position: position,
            row: "weather",
            value: 3L
        );
        Assert.NotEqual(
            expected: key,
            actual: search.PositionKey(index: 0)
        );
    }
    [Fact]
    public void ARowOnlyTheScoreReadsIsInsideThePlansReach() {
        var position = new Position(rows: Rows());
        var search = SearchFixture.Build(
            judge: RuleJudge(
                position: position,
                rules: [SearchFixture.AcceptEveryCandidate()],
                score: "piece[t0] + weather"
            ),
            plan: Plan(scored: true),
            position: position
        );
        var key = search.PositionKey(index: 0);

        Write(
            position: position,
            row: "weather",
            value: 3L
        );
        Assert.NotEqual(
            expected: key,
            actual: search.PositionKey(index: 0)
        );

        var settled = search.PositionKey(index: 0);

        Write(
            position: position,
            row: "noise",
            value: 9L
        );
        Assert.Equal(
            expected: settled,
            actual: search.PositionKey(index: 0)
        );
    }
    // The draw stream is authored, so a row outside the job's reach cannot move it.
    [Fact]
    public void ATreeJobsDrawStreamDoesNotMoveWhenARowOutsideThePlansReachDoes() {
        var rows = Rows();

        ArenaSearch Make(long noise) {
            var position = new Position(rows: rows);

            if (noise != 0L) {
                Write(
                    position: position,
                    row: "noise",
                    value: noise
                );
            }

            return SearchFixture.Build(
                drawSeed: 11UL,
                judge: RuleJudge(
                    position: position,
                    rules: [SearchFixture.AcceptEveryCandidate()],
                    score: "piece[t0]"
                ),
                plan: Plan(
                    depth: 2,
                    iterations: 24,
                    method: SearchMethod.Tree,
                    scored: true
                ),
                position: position
            );
        }

        var quiet = Make(noise: 0L);
        var noisy = Make(noise: 42L);

        for (var step = 0; (step < 64); step++) {
            _ = quiet.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );
            _ = noisy.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );
        }

        Assert.Equal(
            expected: quiet.Capture().Jobs[0].Tree!.Seed,
            actual: noisy.Capture().Jobs[0].Tree!.Seed
        );
        Assert.Equal(
            expected: quiet.Status(index: 0),
            actual: noisy.Status(index: 0)
        );
    }
}
