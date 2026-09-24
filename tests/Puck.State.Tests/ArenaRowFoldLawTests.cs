using Puck.Maths;

using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="StateArena.AddRowTo"/> folds everything the arena stores for one row,
/// so two rows fold the same exactly when every read of them answers the same. A row is more than the values its
/// positions hold: which key stands where, where a ring's cursor is, which lattice cell holds the value, what a
/// live read answers from, and where a draw site's stream stands all move the fold; and a change to one row moves
/// no other row's fold.</summary>
public sealed class ArenaRowFoldLawTests {
    private static ulong Fold(StateArena arena, int rowOrdinal) {
        var hash = Fnv1aHash.Create();

        arena.AddRowTo(
            hash: ref hash,
            rowOrdinal: rowOrdinal
        );

        return hash.Value;
    }
    private static ulong[] Folds(StateArena arena) {
        var folds = new ulong[arena.Layout.RowCount];

        for (var ordinal = 0; (ordinal < folds.Length); ordinal++) {
            folds[ordinal] = Fold(
                arena: arena,
                rowOrdinal: ordinal
            );
        }

        return folds;
    }
    // The mutation moved the arena's own hash, moved the fold of every row named, and moved no other row's.
    private static void MovesOnly(StateArena arena, Action mutate, params int[] rows) {
        var before = Folds(arena: arena);
        var hash = arena.ComputeHash();

        mutate();

        var after = Folds(arena: arena);

        Assert.NotEqual(
            actual: arena.ComputeHash(),
            expected: hash
        );

        for (var ordinal = 0; (ordinal < before.Length); ordinal++) {
            if (rows.Contains(value: ordinal)) {
                Assert.NotEqual(
                    actual: after[ordinal],
                    expected: before[ordinal]
                );
            } else {
                Assert.Equal(
                    actual: after[ordinal],
                    expected: before[ordinal]
                );
            }
        }
    }

    [Fact]
    public void ALatticeCellPastTheRowsLiveCountMovesTheFold() {
        var (_, arena) = TopologyArenaFixture.Build();
        var last = (TopologyArenaFixture.BoardCells - 1);

        // One cell of a sparse board, far past how many cells the row holds.
        MovesOnly(
            arena,
            () => Assert.True(condition: arena.TryWriteBoardCell(
                cell: last,
                reason: out _,
                rowOrdinal: TopologyArenaFixture.Board,
                value: 7L,
                write: StateWriteKind.Set
            )),
            TopologyArenaFixture.Board
        );
        MovesOnly(
            arena,
            () => Assert.True(condition: arena.TryWriteBoardCell(
                cell: last,
                reason: out _,
                rowOrdinal: TopologyArenaFixture.Board,
                value: 9L,
                write: StateWriteKind.Set
            )),
            TopologyArenaFixture.Board
        );
    }
    // Two piles each holding one card of the same value are a different position when the cards are swapped: the
    // values at every position agree and the keys standing there do not.
    [Fact]
    public void TheKeyStandingAtAPositionMovesTheFold() {
        static StateArena Dealt(string toHand) {
            var (catalog, arena) = ArenaFixture.Build();

            Assert.True(condition: arena.TryWrite(
                key: catalog.Keys.Intern(name: ArenaFixture.Name(value: "b")),
                operand: 11L,
                reason: out _,
                rowOrdinal: ArenaFixture.Deck,
                write: StateWriteKind.Set
            ));
            Assert.True(condition: arena.TryTransfer(
                fromOrdinal: ArenaFixture.Deck,
                insertFirst: false,
                key: catalog.Keys.Intern(name: ArenaFixture.Name(value: toHand)),
                reason: out _,
                toOrdinal: ArenaFixture.Hand
            ));

            return arena;
        }

        var first = Dealt(toHand: "a");
        var second = Dealt(toHand: "b");

        Assert.NotEqual(
            actual: Fold(
                arena: second,
                rowOrdinal: ArenaFixture.Deck
            ),
            expected: Fold(
                arena: first,
                rowOrdinal: ArenaFixture.Deck
            )
        );
        Assert.NotEqual(
            actual: Fold(
                arena: second,
                rowOrdinal: ArenaFixture.Hand
            ),
            expected: Fold(
                arena: first,
                rowOrdinal: ArenaFixture.Hand
            )
        );
    }
    // A full ring pushed the value its oldest slot already holds rewrites that slot with what it held: only the
    // cursor moves, and the oldest and newest reads now answer different slots.
    [Fact]
    public void ARingsCursorMovesTheFold() {
        var (_, arena) = ArenaFixture.Build();

        foreach (var value in ((long[])[1L, 2L, 3L])) {
            Assert.True(condition: arena.TryPush(
                reason: out _,
                rowOrdinal: ArenaFixture.History,
                value: value
            ));
        }

        MovesOnly(
            arena,
            () => Assert.True(condition: arena.TryPush(
                reason: out _,
                rowOrdinal: ArenaFixture.History,
                value: 1L
            )),
            ArenaFixture.History
        );
    }
    // A clock rebased to the same value at another tick stores the same number and answers a live read differently.
    [Fact]
    public void AClocksEpochMovesTheFold() {
        var (catalog, arena) = ArenaFixture.Build();
        var slot = catalog.Keys.Intern(name: StateRow.SlotKey);

        void Rebase(long engineTick) => Assert.True(condition: arena.TryWriteClock(
            epochEngineTick: engineTick,
            epochTick: engineTick,
            key: slot,
            reason: out _,
            rowOrdinal: ArenaFixture.Clock,
            substepTicks: 0L,
            v0: 0L,
            y0: 10L
        ));

        Rebase(engineTick: 0L);
        MovesOnly(
            arena,
            () => Rebase(engineTick: 6000L),
            ArenaFixture.Clock
        );
    }
    [Fact]
    public void ADrawSitesCursorAndMasksMoveTheFold() {
        var (_, arena) = ArenaFixture.Build();

        MovesOnly(
            arena,
            () => Assert.True(condition: arena.TryWriteDrawCursor(
                cursor: 3L,
                rowOrdinal: ArenaFixture.Deal
            )),
            ArenaFixture.Deal
        );
    }
    [Fact]
    public void ARemovedCellAndAPhaseSequenceMoveTheFold() {
        var (catalog, arena) = ArenaFixture.Build();

        MovesOnly(
            arena,
            () => Assert.True(condition: arena.TryRemove(
                key: catalog.Keys.Intern(name: ArenaFixture.Name(value: "b")),
                reason: out _,
                rowOrdinal: ArenaFixture.Codes
            )),
            // The board is derived from the codes row, so it moves with it.
            ArenaFixture.Board,
            ArenaFixture.Codes
        );
        MovesOnly(
            arena,
            () => Assert.True(condition: arena.TryWritePhaseSequence(
                rowOrdinal: ArenaFixture.Turn,
                sequence: 4L
            )),
            ArenaFixture.Turn
        );
    }
    [Fact]
    public void ARowTheArenaDoesNotCarryIsRefused() {
        var (_, arena) = ArenaFixture.Build();

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Fold(
            arena: arena,
            rowOrdinal: arena.Layout.RowCount
        ));
    }
}
