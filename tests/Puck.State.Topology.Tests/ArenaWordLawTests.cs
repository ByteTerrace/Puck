using Puck.Testing;
using Xunit;

namespace Puck.State.Topology.Tests;

/// <summary>CONTRACT UNDER TEST: the arena-facing board reads and pattern words answer off the arena's own columns
/// exactly what the span kernels answer off a caller's scratch — an ordered row's cells in pile order, the same
/// cells read through an attribute row, a ring's slots oldest push first, a lattice's cells in topology order, and
/// a ray walked from one cell. A board query addressing another topology refuses by name.</summary>
public sealed class ArenaWordLawTests {
    private static StateArena Seeded() {
        var (_, arena) = TopologyArenaFixture.Build();

        for (var cell = 0; (cell < TopologyArenaFixture.BoardCells); cell++) {
            Assert.True(
                condition: arena.TryWriteBoardCell(
                    cell: cell,
                    reason: out var reason,
                    rowOrdinal: TopologyArenaFixture.Board,
                    value: (cell % 3),
                    write: StateWriteKind.Set
                ),
                userMessage: reason
            );
        }

        return arena;
    }

    [Fact]
    public void AnOrderedRowsWordIsItsCellsInPileOrder() {
        var (catalog, arena) = TopologyArenaFixture.Build();
        long[] values = [2L, 0L, 1L, 3L];

        for (var index = 0; (index < values.Length); index++) {
            Assert.True(condition: arena.TryMint(
                key: out _,
                name: TopologyArenaFixture.Name(value: $"t{index}"),
                reason: out var reason,
                rowOrdinal: TopologyArenaFixture.Pile,
                value: CellValue.Int(value: values[index])
            ), userMessage: reason);
        }

        var buffer = new long[16];

        Assert.Equal(
            actual: arena.ReadWord(
                time: ArenaTime.Origin,
                rowOrdinal: TopologyArenaFixture.Pile,
                word: buffer
            ).ToArray(),
            expected: values
        );

        var tail = arena.ReadWord(
            time: ArenaTime.Origin,
            rowOrdinal: TopologyArenaFixture.Pile,
            start: 2,
            word: buffer
        );

        Assert.Equal(
            actual: tail.Length,
            expected: 2
        );
        Assert.Equal(
            actual: tail.ToArray(),
            expected: [1L, 3L]
        );

        // The same pile read through the attribute row is that row's value for each of the pile's keys.
        var through = arena.ReadWord(
            time: ArenaTime.Origin,
            attributeOrdinal: TopologyArenaFixture.Attribute,
            rowOrdinal: TopologyArenaFixture.Pile,
            word: buffer
        );

        Assert.Equal(
            actual: through.ToArray(),
            expected: [0L, 1L, 2L, 3L]
        );
        Assert.True(condition: arena.TryWrite(
            key: catalog.Keys.Intern(name: TopologyArenaFixture.Name(value: "t0")),
            operand: 9L,
            reason: out _,
            rowOrdinal: TopologyArenaFixture.Attribute,
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            actual: arena.ReadWord(
                time: ArenaTime.Origin,
                attributeOrdinal: TopologyArenaFixture.Attribute,
                rowOrdinal: TopologyArenaFixture.Pile,
                word: buffer
            )[0],
            expected: 9L
        );
    }
    [Fact]
    public void ARingsWordRunsFromTheOldestLivePushToTheNewest() {
        var (_, arena) = TopologyArenaFixture.Build();
        var buffer = new long[16];

        Assert.Equal(
            actual: arena.ReadWord(
                time: ArenaTime.Origin,
                rowOrdinal: TopologyArenaFixture.History,
                word: buffer
            ).Length,
            expected: 0
        );

        for (var value = 1L; (value <= 6L); value++) {
            Assert.True(condition: arena.TryPush(
                reason: out var reason,
                rowOrdinal: TopologyArenaFixture.History,
                value: value
            ), userMessage: reason);
        }

        var ring = arena.ReadWord(
            time: ArenaTime.Origin,
            rowOrdinal: TopologyArenaFixture.History,
            start: 2,
            word: buffer
        );

        // A ring reads its whole live span whatever start it is handed.
        Assert.Equal(
            actual: ring.ToArray(),
            expected: [3L, 4L, 5L, 6L]
        );
    }
    [Fact]
    public void ALatticesWordAndItsRayReadTheSameCellsTheSpanKernelDoes() {
        var arena = Seeded();
        var topology = arena.Layout[TopologyArenaFixture.Board].Topology;

        Assert.NotNull(@object: topology);

        var scratch = new long[TopologyArenaFixture.BoardCells];

        Assert.True(condition: arena.TryReadBoard(
            rowOrdinal: TopologyArenaFixture.Board,
            values: scratch
        ));

        var buffer = new long[TopologyArenaFixture.BoardCells];

        Assert.Equal(
            actual: arena.ReadWord(
                time: ArenaTime.Origin,
                rowOrdinal: TopologyArenaFixture.Board,
                word: buffer
            ).ToArray(),
            expected: scratch
        );

        var direction = 0;
        var expected = new long[TopologyArenaFixture.BoardCells];
        var expectedLength = BoardQueries.ReadRay(
            direction: direction,
            origin: 0,
            topology: topology!,
            values: scratch,
            word: expected
        );

        Assert.True(condition: ArenaBoards.TryReadRay(
            arena: arena,
            direction: direction,
            origin: 0,
            ray: out var ray,
            reason: out var reason,
            rowOrdinal: TopologyArenaFixture.Board,
            values: new long[TopologyArenaFixture.BoardCells],
            word: buffer
        ), userMessage: reason);
        Assert.Equal(
            actual: ray.ToArray(),
            expected: expected[..expectedLength]
        );

        // A ray steps in one of the topology's directions, so no ray reads the word a row read answers.
        Assert.False(condition: ArenaBoards.TryReadRay(
            arena: arena,
            direction: -1,
            origin: 0,
            ray: out _,
            reason: out var refusal,
            rowOrdinal: TopologyArenaFixture.Board,
            values: new long[TopologyArenaFixture.BoardCells],
            word: buffer
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "direction ordinal"
        );
    }
    [Fact]
    public void ABoardQueryOverAnArenaRowAnswersWhatTheSpanKernelAnswers() {
        var arena = Seeded();
        var topology = arena.Layout[TopologyArenaFixture.Board].Topology!;
        var query = new BoardMaskQuery(
            lower: 0L,
            topology: topology,
            upper: 0L
        );
        var scratch = new long[TopologyArenaFixture.BoardCells];

        Assert.True(condition: arena.TryReadBoard(
            rowOrdinal: TopologyArenaFixture.Board,
            values: scratch
        ));
        Assert.True(condition: ArenaBoards.TryEvaluate(
            arena: arena,
            query: query,
            reason: out var reason,
            result: out var result,
            rowOrdinal: TopologyArenaFixture.Board,
            source: 0,
            values: new long[TopologyArenaFixture.BoardCells]
        ), userMessage: reason);
        Assert.Equal(
            actual: result,
            expected: BoardQueries.Evaluate(
                empty: arena.Layout[TopologyArenaFixture.Board].Empty,
                query: query,
                source: 0,
                values: scratch
            )
        );
        Assert.False(condition: ArenaBoards.TryEvaluate(
            arena: arena,
            query: query,
            reason: out var refusal,
            result: out _,
            rowOrdinal: TopologyArenaFixture.Pile,
            source: 0,
            values: new long[TopologyArenaFixture.BoardCells]
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "lattice"
        );
    }
    [Fact]
    public void ANumberReadAnswersOnlyForANumericCellThatIsPresent() {
        var (catalog, arena) = TopologyArenaFixture.Build();
        var time = ArenaTime.Origin;

        Assert.False(condition: arena.TryReadRawAt(position: 0, raw: out var absent, rowOrdinal: TopologyArenaFixture.Pile));
        Assert.False(condition: arena.TryReadLiveNumberAt(position: 0, rowOrdinal: TopologyArenaFixture.Pile, time: time, value: out var absentLive));
        Assert.Equal(actual: absent, expected: 0L);
        Assert.Equal(actual: absentLive, expected: 0L);
        Assert.True(condition: arena.TryMint(
            key: out var key,
            name: TopologyArenaFixture.Name(value: "t7"),
            reason: out _,
            rowOrdinal: TopologyArenaFixture.Pile,
            value: CellValue.Int(value: 5L)
        ));
        Assert.True(condition: arena.TryReadLiveNumber(key: key, rowOrdinal: TopologyArenaFixture.Pile, time: time, value: out var keyed));
        Assert.True(condition: arena.TryReadLiveNumberAt(position: 0, rowOrdinal: TopologyArenaFixture.Pile, time: time, value: out var positioned));
        Assert.True(condition: arena.TryReadRawAt(position: 0, raw: out var stored, rowOrdinal: TopologyArenaFixture.Pile));
        Assert.Equal(actual: new long[] { keyed, positioned, stored }, expected: new long[] { 5L, 5L, 5L });
        Assert.False(condition: arena.TryReadLiveNumber(
            key: catalog.Keys.Intern(name: TopologyArenaFixture.Name(value: "t8")),
            rowOrdinal: TopologyArenaFixture.Pile,
            time: time,
            value: out _
        ));
    }
}
