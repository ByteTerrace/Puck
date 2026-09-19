using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a finished job searches again exactly when the bytes of a row it reads have
/// changed — never for an idle step, a write that stores what the cell already held, or a scope that was opened,
/// written through, and rewound.</summary>
public sealed class ArenaSearchStampLawTests {
    private static SearchPlan Plan() =>
        new(
            Name: "moves",
            Tokens: "piece",
            Topology: null,
            Zones: [],
            CellCount: 4,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: 256,
            JudgeCost: 1L,
            Depth: 1,
            Best: "best",
            Shapes: [new SearchShapePlan(
                    Kind: SearchShapeKind.Relocate,
                    Displace: false,
                    Directions: [],
                    PairWithIndex: -1
                )]
        ) {
            Scored = true,
        };
    private static void Step(ArenaSearch search) => _ = search.Step(
        apply: static _ => true,
        engineTick: 0UL,
        tick: 1UL
    );
    private static ArenaSearchStatus Finish(ArenaSearch search) {
        for (var step = 0; ((step < 64) && !search.Status(index: 0).Done); step++) {
            Step(search: search);
        }

        var status = search.Status(index: 0);

        Assert.True(
            condition: status.Done,
            userMessage: status.ToString()
        );

        return status;
    }

    [Fact]
    public void AFinishedJobSearchesAgainOnlyWhenARowItReadsChanged() {
        var position = new Position(rows: Board(
            cells: 4,
            tokens: 1
        ));
        var piece = position.Ordinal(name: "piece");
        var token = position.Key(name: "t0");
        var search = SearchFixture.Build(
            judge: RuleJudge(
                position: position,
                rules: [AcceptEveryCandidate()],
                score: "piece[t0] * 10"
            ),
            plan: Plan(),
            position: position
        );
        var finished = Finish(search: search);

        // Idle steps.
        Step(search: search);
        Step(search: search);
        Assert.Equal(
            actual: search.Status(index: 0),
            expected: finished
        );

        // A write that stores what the cell already held.
        Assert.True(condition: position.Arena.TryWrite(
            key: token,
            operand: 0L,
            reason: out _,
            rowOrdinal: piece,
            write: StateWriteKind.Set
        ));
        Step(search: search);
        Assert.Equal(
            actual: search.Status(index: 0),
            expected: finished
        );

        // A scope that wrote the row and was rewound.
        var mark = position.Arena.BeginScope();

        Assert.True(condition: position.Arena.TryWrite(
            key: token,
            operand: 3L,
            reason: out _,
            rowOrdinal: piece,
            write: StateWriteKind.Set
        ));
        position.Arena.Rewind(mark: mark);
        Step(search: search);
        Assert.Equal(
            actual: search.Status(index: 0),
            expected: finished
        );

        // The same write committed moves the row's bytes, so the job searches the new position: from cell 3 the
        // best cell left is 2.
        mark = position.Arena.BeginScope();
        Assert.True(condition: position.Arena.TryWrite(
            key: token,
            operand: 3L,
            reason: out _,
            rowOrdinal: piece,
            write: StateWriteKind.Set
        ));
        position.Arena.Commit(mark: mark);
        Step(search: search);

        var again = Finish(search: search);

        Assert.Equal(
            actual: again.BestTarget,
            expected: 2
        );

        // And a write outside any scope does the same.
        Assert.True(condition: position.Arena.TryWrite(
            key: token,
            operand: 1L,
            reason: out _,
            rowOrdinal: piece,
            write: StateWriteKind.Set
        ));
        Step(search: search);
        Assert.Equal(
            actual: Finish(search: search).BestTarget,
            expected: 3
        );
    }
}
