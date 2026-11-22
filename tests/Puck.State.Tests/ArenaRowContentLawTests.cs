using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: the number that names a row's content (<see cref="StateArena.RowContent"/>) is equal
/// at two reads only when the row held the same content at both. It follows the version, and returns to an earlier
/// number exactly when a commit that changed nothing but the row's numbers left them as they stood then; a write
/// outside a scope forgets what the row held, so a content it held before is named afresh.</summary>
public sealed class ArenaRowContentLawTests {
    private static void Commit(StateArena arena, CellKey slot, long value) {
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(key: slot, operand: value, reason: out var reason, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set), userMessage: reason);
        arena.Commit(mark: mark);
    }

    [Fact]
    public void ARowTakenThroughACycleAnswersTheSameNumberAtEachStation() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        // The first pass through the cycle is remembered; from then on each station answers what it answered before,
        // while the version moves on every commit.
        Commit(arena: arena, slot: slot, value: 7L);
        Commit(arena: arena, slot: slot, value: 3L);

        var cleared = arena.RowContent(rowOrdinal: ArenaFixture.Score);

        Commit(arena: arena, slot: slot, value: 7L);

        var rebuilt = arena.RowContent(rowOrdinal: ArenaFixture.Score);

        Assert.NotEqual(expected: cleared, actual: rebuilt);
        for (var pass = 0; (pass < 3); pass++) {
            var version = arena.RowVersion(rowOrdinal: ArenaFixture.Score);

            Commit(arena: arena, slot: slot, value: 3L);
            Assert.Equal(expected: cleared, actual: arena.RowContent(rowOrdinal: ArenaFixture.Score));
            Commit(arena: arena, slot: slot, value: 7L);
            Assert.Equal(expected: rebuilt, actual: arena.RowContent(rowOrdinal: ArenaFixture.Score));
            Assert.Equal(expected: (version + 2UL), actual: arena.RowVersion(rowOrdinal: ArenaFixture.Score));
        }

        // A content the row never held is named by its own version.
        Commit(arena: arena, slot: slot, value: 9L);
        Assert.Equal(expected: arena.RowVersion(rowOrdinal: ArenaFixture.Score), actual: arena.RowContent(rowOrdinal: ArenaFixture.Score));
        Assert.NotEqual(expected: cleared, actual: arena.RowContent(rowOrdinal: ArenaFixture.Score));
        Assert.NotEqual(expected: rebuilt, actual: arena.RowContent(rowOrdinal: ArenaFixture.Score));
    }
    [Fact]
    public void AWriteOutsideAScopeForgetsWhatTheRowHeld() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);

        Commit(arena: arena, slot: slot, value: 7L);
        Commit(arena: arena, slot: slot, value: 3L);
        Commit(arena: arena, slot: slot, value: 7L);

        var rebuilt = arena.RowContent(rowOrdinal: ArenaFixture.Score);

        // No commit settles a write made outside every scope, so the arena cannot tell what else about the row it
        // changed: the row's memory is emptied, and the same numbers are named by a new version.
        Assert.True(condition: arena.TryWrite(key: slot, operand: 3L, reason: out var reason, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set), userMessage: reason);
        Commit(arena: arena, slot: slot, value: 7L);
        Assert.NotEqual(expected: rebuilt, actual: arena.RowContent(rowOrdinal: ArenaFixture.Score));
        Assert.Equal(expected: arena.RowVersion(rowOrdinal: ArenaFixture.Score), actual: arena.RowContent(rowOrdinal: ArenaFixture.Score));
    }
}
