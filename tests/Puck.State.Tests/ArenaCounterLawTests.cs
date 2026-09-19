using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: the arena's three counters answer three different questions.
/// <see cref="StateArena.RowVersion"/> moves only when a commit leaves the row's bytes different from what they
/// held when the outermost scope opened; <see cref="StateArena.RowGeneration"/> moves on every mutation including
/// a no-op write and a rewind; and <see cref="StateArena.AppendGeneration"/> moves on every mutation of an ordered
/// row except a push at its tail.</summary>
public sealed class ArenaCounterLawTests {
    [Fact]
    public void RowVersionMovesOnACommittedByteChange() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 9L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            expected: 0UL,
            actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score)
        );

        arena.Commit(mark: mark);

        Assert.Equal(
            expected: 1UL,
            actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score)
        );
    }
    [Fact]
    public void RowVersionNeverMovesOnANoOpWriteARewoundWriteOrAWriteThenRewind() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 5L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            expected: 0UL,
            actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score)
        );

        var noop = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 5L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        arena.Commit(mark: noop);

        Assert.Equal(
            expected: 0UL,
            actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score)
        );

        var rewound = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 11L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        arena.Rewind(mark: rewound);

        Assert.Equal(
            expected: 0UL,
            actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score)
        );

        var returned = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 11L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));
        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 5L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        arena.Commit(mark: returned);

        Assert.Equal(
            expected: 0UL,
            actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score)
        );
    }
    [Fact]
    public void RowGenerationMovesOnEveryMutationIncludingARewind() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 5L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        var afterNoOp = arena.RowGeneration(rowOrdinal: ArenaFixture.Score);

        Assert.True(condition: (afterNoOp > 0UL));

        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(
            key: slot,
            operand: 11L,
            reason: out _,
            rowOrdinal: ArenaFixture.Score,
            write: StateWriteKind.Set
        ));

        var afterWrite = arena.RowGeneration(rowOrdinal: ArenaFixture.Score);

        Assert.True(condition: (afterWrite > afterNoOp));

        arena.Rewind(mark: mark);

        Assert.True(condition: (arena.RowGeneration(rowOrdinal: ArenaFixture.Score) > afterWrite));
        Assert.Equal(
            expected: 0UL,
            actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score)
        );
    }
    [Fact]
    public void TheAppendGenerationStandsStillOnATailPush() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.True(
            condition: arena.TryMint(
                key: out _,
                name: ArenaFixture.Name(value: "c"),
                reason: out var reason,
                rowOrdinal: ArenaFixture.Deck,
                value: CellValue.Int(value: 33L)
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: 0UL,
            actual: arena.AppendGeneration(rowOrdinal: ArenaFixture.Deck)
        );
        Assert.True(condition: (arena.RowGeneration(rowOrdinal: ArenaFixture.Deck) > 0UL));
    }
    [Fact]
    public void TheAppendGenerationMovesOnARemovalANonTailInsertAndAReorder() {
        var (catalog, arena) = ArenaFixture.Build();
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );

        Assert.True(
            condition: arena.TryInsert(
                key: out _,
                name: ArenaFixture.Name(value: "c"),
                position: 0,
                reason: out var reason,
                rowOrdinal: ArenaFixture.Deck,
                value: CellValue.Int(value: 33L)
            ),
            userMessage: reason
        );

        var afterInsert = arena.AppendGeneration(rowOrdinal: ArenaFixture.Deck);

        Assert.True(condition: (afterInsert > 0UL));
        Assert.True(
            condition: arena.TryRemove(
                key: a,
                reason: out reason,
                rowOrdinal: ArenaFixture.Deck
            ),
            userMessage: reason
        );

        var afterRemove = arena.AppendGeneration(rowOrdinal: ArenaFixture.Deck);

        Assert.True(condition: (afterRemove > afterInsert));
        Assert.True(
            condition: arena.TryTransferEnd(
                fromOrdinal: ArenaFixture.Deck,
                insertFirst: true,
                key: out _,
                reason: out reason,
                takeFirst: false,
                toOrdinal: ArenaFixture.Hand
            ),
            userMessage: reason
        );
        Assert.True(condition: (arena.AppendGeneration(rowOrdinal: ArenaFixture.Deck) > afterRemove));
        Assert.Equal(
            expected: 0UL,
            actual: arena.AppendGeneration(rowOrdinal: ArenaFixture.Hand)
        );
        Assert.True(
            condition: arena.TryTransferEnd(
                fromOrdinal: ArenaFixture.Deck,
                insertFirst: true,
                key: out _,
                reason: out reason,
                takeFirst: false,
                toOrdinal: ArenaFixture.Hand
            ),
            userMessage: reason
        );
        Assert.True(condition: (arena.AppendGeneration(rowOrdinal: ArenaFixture.Hand) > 0UL));
    }
    [Fact]
    public void AValueWriteToAnOrderedRowMovesItsAppendGeneration() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.True(
            condition: arena.TryWrite(
                key: ArenaFixture.Key(
                    catalog: catalog,
                    value: "a"
                ),
                operand: 99L,
                reason: out var reason,
                rowOrdinal: ArenaFixture.Deck,
                write: StateWriteKind.Set
            ),
            userMessage: reason
        );
        Assert.True(condition: (arena.AppendGeneration(rowOrdinal: ArenaFixture.Deck) > 0UL));
    }
    [Fact]
    public void AKeyedRowsAppendGenerationNeverMoves() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.True(
            condition: arena.TryRemove(
                key: ArenaFixture.Key(
                    catalog: catalog,
                    value: "a"
                ),
                reason: out var reason,
                rowOrdinal: ArenaFixture.Tokens
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: 0UL,
            actual: arena.AppendGeneration(rowOrdinal: ArenaFixture.Tokens)
        );
    }
}
