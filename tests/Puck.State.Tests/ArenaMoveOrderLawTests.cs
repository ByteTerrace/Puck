using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a reader of the arena's row version moves is handed every row that moved since the
/// place it kept, each once however many times it moved and however many moves fell between two reads, and is told
/// to take every row as moved only when it has read nothing or every version was reset at once.</summary>
public sealed class ArenaMoveOrderLawTests {
    private static void Commit(StateArena arena, CellKey slot, long value) {
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWrite(key: slot, operand: value, reason: out var reason, rowOrdinal: ArenaFixture.Score, write: StateWriteKind.Set), userMessage: reason);
        arena.Commit(mark: mark);
    }
    private static long WholeReads(IWorkCounterSource arena) {
        Assert.True(condition: arena.TryRead(kind: ArenaWork.MoveReadsWhole, value: out var whole));

        return whole;
    }

    [Fact]
    public void ARowMovedManyTimesBetweenTwoReadsIsHandedOverOnce() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var moved = new List<int>();
        var place = -1L;

        // A reader that has read nothing takes every row, and is given a place to resume from.
        Assert.False(condition: arena.TryReadMoves(into: moved, place: ref place));
        Assert.Empty(collection: moved);
        Assert.Equal(expected: arena.MoveSequence, actual: place);

        var version = arena.RowVersion(rowOrdinal: ArenaFixture.Score);
        var whole = WholeReads(arena: arena);

        // Far more moves than any fixed-size log of them would hold: the value alternates, so every commit after the
        // first changes the row.
        for (var write = 1; (write <= 5000); write++) {
            Commit(arena: arena, slot: slot, value: (write % 2));
        }

        Assert.True(condition: ((arena.RowVersion(rowOrdinal: ArenaFixture.Score) - version) >= 4999UL));
        Assert.True(condition: arena.TryReadMoves(into: moved, place: ref place));
        Assert.Equal(expected: [ArenaFixture.Score], actual: moved);
        Assert.Equal(expected: arena.MoveSequence, actual: place);
        Assert.Equal(expected: whole, actual: WholeReads(arena: arena));

        // Nothing moved since: the reader is handed nothing and keeps its place.
        moved.Clear();
        Assert.True(condition: arena.TryReadMoves(into: moved, place: ref place));
        Assert.Empty(collection: moved);
    }
    [Fact]
    public void EachReaderIsHandedWhatMovedSinceItsOwnPlace() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var early = arena.MoveSequence;
        var moved = new List<int>();

        Commit(arena: arena, slot: slot, value: 41L);

        var late = arena.MoveSequence;

        Assert.True(condition: arena.TryReadMoves(into: moved, place: ref late));
        Assert.Empty(collection: moved);
        Assert.True(condition: arena.TryReadMoves(into: moved, place: ref early));
        Assert.Equal(expected: [ArenaFixture.Score], actual: moved);
        Assert.Equal(expected: late, actual: early);
    }
    [Fact]
    public void ARelayoutTellsEveryReaderToTakeEveryRow() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = ArenaFixture.SlotKey(catalog: catalog);
        var place = arena.MoveSequence;
        var moved = new List<int>();
        var section = ArenaFixture.Section();

        Commit(arena: arena, slot: slot, value: 7L);
        Assert.True(condition: arena.TryRelayout(
            catalog: StateCatalog.Compile(section: section),
            reason: out var reason,
            section: section,
            time: ArenaTime.Origin
        ), userMessage: reason);

        var whole = WholeReads(arena: arena);

        Assert.False(condition: arena.TryReadMoves(into: moved, place: ref place));
        Assert.Empty(collection: moved);
        Assert.Equal(expected: (whole + 1L), actual: WholeReads(arena: arena));
        // The place it was given is past the reset: the next read names rows again.
        Assert.True(condition: arena.TryReadMoves(into: moved, place: ref place));
        Assert.Empty(collection: moved);
    }
}
