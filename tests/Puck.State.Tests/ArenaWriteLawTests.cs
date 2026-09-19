using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: the arena refuses a mint past a row's capacity by name, recomputes a derived
/// board through every door that can change its tokens or codes, refuses a direct write to the board itself,
/// clears the ring slot a push reuses, and admits and releases participant ordinals.</summary>
public sealed class ArenaWriteLawTests {
    [Fact]
    public void MintingPastCapacityRefusesByName() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.True(
            condition: arena.TryMint(
                key: out _,
                name: ArenaFixture.Name(value: "c"),
                reason: out var reason,
                rowOrdinal: ArenaFixture.Tokens,
                value: CellValue.Int(value: 1L)
            ),
            userMessage: reason
        );
        Assert.True(
            condition: arena.TryMint(
                key: out _,
                name: ArenaFixture.Name(value: "d"),
                reason: out reason,
                rowOrdinal: ArenaFixture.Tokens,
                value: CellValue.Int(value: 1L)
            ),
            userMessage: reason
        );
        Assert.False(condition: arena.TryMint(
            key: out _,
            name: ArenaFixture.Name(value: "e"),
            reason: out reason,
            rowOrdinal: ArenaFixture.Tokens,
            value: CellValue.Int(value: 1L)
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "tokens"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "capacity"
        );
        Assert.Equal(
            expected: 4,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Tokens)
        );
    }
    [Fact]
    public void AnEvictingRowDropsItsOldestCellRatherThanRefusing() {
        var (catalog, arena) = ArenaFixture.Build();

        foreach (var name in ((string[])["p", "q", "r"])) {
            Assert.True(
                condition: arena.TryMint(
                    key: out _,
                    name: ArenaFixture.Name(value: name),
                    reason: out var reason,
                    rowOrdinal: ArenaFixture.Bag,
                    value: CellValue.Int(value: 1L)
                ),
                userMessage: reason
            );
        }

        Assert.Equal(
            expected: 2,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Bag)
        );
        Assert.False(condition: arena.TryRead(
            key: ArenaFixture.Key(
                catalog: catalog,
                value: "p"
            ),
            rowOrdinal: ArenaFixture.Bag,
            value: out _
        ));
    }
    [Fact]
    public void ADerivedBoardRecomputesThroughEveryDoor() {
        var (catalog, arena) = ArenaFixture.Build();
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );
        var b = ArenaFixture.Key(
            catalog: catalog,
            value: "b"
        );

        Assert.Equal(
            expected: 7L,
            actual: Board(
                arena: arena,
                cell: 0
            )
        );
        Assert.Equal(
            expected: 9L,
            actual: Board(
                arena: arena,
                cell: 2
            )
        );
        Assert.Equal(
            expected: -1L,
            actual: Board(
                arena: arena,
                cell: 1
            )
        );

        Assert.True(
            condition: arena.TryWrite(
                key: a,
                operand: 1L,
                reason: out var reason,
                rowOrdinal: ArenaFixture.Tokens,
                write: StateWriteKind.Set
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: -1L,
            actual: Board(
                arena: arena,
                cell: 0
            )
        );
        Assert.Equal(
            expected: 7L,
            actual: Board(
                arena: arena,
                cell: 1
            )
        );

        Assert.True(
            condition: arena.TryWrite(
                key: a,
                operand: 70L,
                reason: out reason,
                rowOrdinal: ArenaFixture.Codes,
                write: StateWriteKind.Set
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: 70L,
            actual: Board(
                arena: arena,
                cell: 1
            )
        );

        Assert.True(
            condition: arena.TryMint(
                key: out var c,
                name: ArenaFixture.Name(value: "c"),
                reason: out reason,
                rowOrdinal: ArenaFixture.Tokens,
                value: CellValue.Int(value: 2L)
            ),
            userMessage: reason
        );
        Assert.True(
            condition: arena.TryMint(
                key: out _,
                name: ArenaFixture.Name(value: "c"),
                reason: out reason,
                rowOrdinal: ArenaFixture.Codes,
                value: CellValue.Int(value: 55L)
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: 55L,
            actual: Board(
                arena: arena,
                cell: 2
            )
        );

        Assert.True(
            condition: arena.TryRemove(
                key: c,
                reason: out reason,
                rowOrdinal: ArenaFixture.Tokens
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: 9L,
            actual: Board(
                arena: arena,
                cell: 2
            )
        );

        var mark = arena.BeginScope();

        Assert.True(
            condition: arena.TryWrite(
                key: b,
                operand: 3L,
                reason: out reason,
                rowOrdinal: ArenaFixture.Tokens,
                write: StateWriteKind.Set
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: 9L,
            actual: Board(
                arena: arena,
                cell: 3
            )
        );

        arena.Rewind(mark: mark);

        Assert.Equal(
            expected: -1L,
            actual: Board(
                arena: arena,
                cell: 3
            )
        );
        Assert.Equal(
            expected: 9L,
            actual: Board(
                arena: arena,
                cell: 2
            )
        );
    }
    [Fact]
    public void ADerivedBoardRefusesADirectWrite() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.False(condition: arena.TryWriteBoardCell(
            cell: 0,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Board,
            value: 4L,
            write: StateWriteKind.Set
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "derived board"
        );
    }
    [Fact]
    public void AParticipantJoinsAtTheOrdinalItsHostNamesAndLeavesCleanly() {
        var (catalog, arena) = ArenaFixture.Build();
        var first = 3;

        Assert.True(
            condition: arena.TryJoin(
                lane: StateLane.Participant,
                ordinal: first,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.False(
            condition: arena.TryJoin(
                lane: StateLane.Participant,
                ordinal: first,
                reason: out reason
            )
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "has already joined"
        );
        Assert.False(
            condition: arena.TryJoin(
                lane: StateLane.Participant,
                ordinal: ArenaCapacity.DefaultParticipants,
                reason: out reason
            )
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "lies outside it"
        );
        Assert.False(condition: arena.IsJoined(
            lane: StateLane.Participant,
            ordinal: 0
        ));
        Assert.True(
            condition: arena.TryWriteSlot(
                operand: 12L,
                ordinal: first,
                reason: out reason,
                rowOrdinal: ArenaFixture.Coins,
                write: StateWriteKind.Set
            ),
            userMessage: reason
        );
        Assert.True(condition: arena.TryReadSlot(
            ordinal: first,
            rowOrdinal: ArenaFixture.Coins,
            value: out var coins
        ));
        Assert.Equal(
            expected: CellValue.Fixed(rawBits: 12L),
            actual: coins
        );

        Assert.True(
            condition: arena.TryLeave(
                lane: StateLane.Participant,
                ordinal: first,
                reason: out reason
            ),
            userMessage: reason
        );
        Assert.False(condition: arena.IsJoined(
            lane: StateLane.Participant,
            ordinal: first
        ));
        Assert.False(condition: arena.TryReadSlot(
            ordinal: first,
            rowOrdinal: ArenaFixture.Coins,
            value: out _
        ));
        Assert.False(condition: arena.TryLeave(
            lane: StateLane.Participant,
            ordinal: first,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "has not joined"
        );

        Assert.True(
            condition: arena.TryJoin(
                lane: StateLane.Participant,
                ordinal: first,
                reason: out reason
            ),
            userMessage: reason
        );
        Assert.True(condition: arena.TryReadSlot(
            ordinal: first,
            rowOrdinal: ArenaFixture.Coins,
            value: out var cleared
        ));
        Assert.Equal(
            expected: CellValue.Fixed(rawBits: 0L),
            actual: cleared
        );
    }
    [Fact]
    public void AJoinAndAWriteRewindWithTheirScope() {
        var (catalog, arena) = ArenaFixture.Build();
        var mark = arena.BeginScope();
        var ordinal = 0;

        Assert.True(
            condition: arena.TryJoin(
                lane: StateLane.Participant,
                ordinal: ordinal,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: arena.TryWriteSlot(
                operand: 3L,
                ordinal: ordinal,
                reason: out reason,
                rowOrdinal: ArenaFixture.Timer,
                write: StateWriteKind.Set
            ),
            userMessage: reason
        );

        arena.Rewind(mark: mark);

        Assert.False(condition: arena.IsJoined(
            lane: StateLane.Participant,
            ordinal: ordinal
        ));
    }
    [Fact]
    public void AHostOwnedRowAnswersNothingAndRefusesEveryWrite() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.False(condition: arena.TryRead(
            key: ArenaFixture.SlotKey(catalog: catalog),
            rowOrdinal: ArenaFixture.Field,
            value: out _
        ));
        Assert.False(condition: arena.TryWrite(
            key: ArenaFixture.SlotKey(catalog: catalog),
            operand: 1L,
            reason: out var reason,
            rowOrdinal: ArenaFixture.Field,
            write: StateWriteKind.Set
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "host-owned"
        );
    }
    [Fact]
    public void ARingPushAdvancesItsCursorAndOverwritesTheOldestSlot() {
        var (catalog, arena) = ArenaFixture.Build();

        foreach (var value in ((long[])[1L, 2L, 3L, 4L])) {
            Assert.True(
                condition: arena.TryPush(
                    reason: out var reason,
                    rowOrdinal: ArenaFixture.History,
                    value: value
                ),
                userMessage: reason
            );
        }

        Assert.Equal(
            expected: 4L,
            actual: arena.HistoryCursor(rowOrdinal: ArenaFixture.History)
        );
        Assert.True(condition: arena.TryReadAt(
            position: 0,
            rowOrdinal: ArenaFixture.History,
            value: out var newest
        ));
        Assert.Equal(
            expected: CellValue.Int(value: 4L),
            actual: newest
        );
    }
    [Fact]
    public void ARingPushClearsTheSlotItReuses() {
        var haunted = PushedOverALoadedRing(provenance: "ghost");
        var clean = PushedOverALoadedRing(provenance: null);

        Assert.Equal(
            actual: clean.ComputeHash(),
            expected: haunted.ComputeHash()
        );

        var reused = haunted.ToRows()[ArenaFixture.History].Cells![0];

        Assert.Equal(
            actual: reused.Key.Value,
            expected: "0"
        );
        Assert.Equal(
            actual: reused.Value.AsInt,
            expected: 1L
        );
        Assert.Null(@object: reused.Provenance);
        Assert.Null(@object: reused.Clock);
    }

    private static long Board(StateArena arena, int cell) {
        Assert.True(condition: arena.TryReadBoardCell(
            cell: cell,
            rowOrdinal: ArenaFixture.Board,
            value: out var value
        ));

        return value;
    }
    // A ring whose oldest slot carries runtime state, pushed over once so the cursor lands back on that slot.
    private static StateArena PushedOverALoadedRing(string? provenance) {
        var (_, arena) = ArenaFixture.Build();

        Assert.True(
            condition: arena.TryLoad(
                reason: out var reason,
                rows: [new StateRow(
                        Name: ArenaFixture.Name(value: "history"),
                        Kind: CellKind.Int,
                        Cells: [new StateCell(
                                Key: ArenaFixture.Name(value: "0"),
                                Value: CellValue.Int(value: 7L),
                                Provenance: provenance,
                                Clock: ((provenance is null)
                                    ? null
                                    : new StateCellClock(EpochTick: 5L)
                                )
                            )],
                        Domain: new StateDomain.Ring(
                            Capacity: 3,
                            Empty: -1L
                        ),
                        HistoryCursor: 3L
                    )],
                time: ArenaTime.Origin
            ),
            userMessage: reason
        );
        Assert.True(
            condition: arena.TryPush(
                reason: out reason,
                rowOrdinal: ArenaFixture.History,
                value: 1L
            ),
            userMessage: reason
        );

        return arena;
    }
}
