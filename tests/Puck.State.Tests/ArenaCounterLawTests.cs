using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: the arena's two counters answer two different questions.
/// <see cref="StateArena.RowVersion"/> moves only when a commit leaves the row's bytes different from what they
/// held when the outermost scope opened; <see cref="StateArena.RowGeneration"/> moves on every mutation including
/// a no-op write and a rewind.</summary>
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
}
