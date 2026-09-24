using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="StateArena.TryWriteOrMint"/> and <see cref="StateArena.TryWriteLiveOrMint"/>
/// mint only when a keyed or ordered row holds no cell under the key, so a refused write of a held cell reports the
/// write's own reason; the live door rebases a traited cell where the stored door would not; and
/// <see cref="StateArena.TryMintEvicting"/> names the cell a full evicting row dropped.</summary>
public sealed class ArenaWriteOrMintLawTests {
    [Fact]
    public void AMissingCellMintsAndAHeldOneIsWrittenInPlace() {
        var (catalog, arena) = ArenaFixture.Build();
        var key = ArenaFixture.Key(
            catalog: catalog,
            value: "coin"
        );

        Assert.True(
            condition: arena.TryWriteOrMint(
                evicted: out var evicted,
                key: key,
                reason: out var reason,
                rowOrdinal: ArenaFixture.Purse,
                value: CellValue.Int(value: 3L)
            ),
            userMessage: reason
        );
        Assert.Null(@object: evicted);
        Assert.Equal(
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Purse),
            expected: 1
        );
        Assert.True(
            condition: arena.TryWriteOrMint(
                evicted: out _,
                key: key,
                reason: out reason,
                rowOrdinal: ArenaFixture.Purse,
                value: CellValue.Int(value: 4L)
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Purse),
            expected: 1
        );
        Assert.Equal(
            actual: arena.Read(
                key: key,
                rowOrdinal: ArenaFixture.Purse
            )!.Value.AsInt,
            expected: 4L
        );
    }
    [Fact]
    public void AHeldCellsRefusalReportsTheWritesOwnReasonNeverAMints() {
        var (catalog, arena) = ArenaFixture.Build();
        var key = ArenaFixture.Key(
            catalog: catalog,
            value: "coin"
        );

        Assert.True(
            condition: arena.TryWriteOrMint(
                evicted: out _,
                key: key,
                reason: out var reason,
                rowOrdinal: ArenaFixture.Purse,
                value: CellValue.Int(value: 3L)
            ),
            userMessage: reason
        );

        // The purse's envelope is 0..10: the write door refuses 11 by the envelope, and no mint is attempted
        // beside a cell the row already holds.
        foreach (var live in ((bool[])[false, true])) {
            var written = (live
                ? arena.TryWriteLiveOrMint(
                    evicted: out _,
                    key: key,
                    operand: 11L,
                    reason: out reason,
                    rowOrdinal: ArenaFixture.Purse,
                    time: ArenaTime.Origin,
                    write: StateWriteKind.Set
                )
                : arena.TryWriteOrMint(
                    evicted: out _,
                    key: key,
                    reason: out reason,
                    rowOrdinal: ArenaFixture.Purse,
                    value: CellValue.Int(value: 11L)
                )
            );

            Assert.False(condition: written);
            Assert.Contains(
                actualString: reason,
                expectedSubstring: "envelope"
            );
            Assert.DoesNotContain(
                actualString: reason,
                expectedSubstring: "already holds"
            );
        }

        Assert.Equal(
            actual: arena.Read(
                key: key,
                rowOrdinal: ArenaFixture.Purse
            )!.Value.AsInt,
            expected: 3L
        );
    }
    [Fact]
    public void ARingAddressIsRefusedByTheWriteDoorNeverMinted() {
        var (catalog, arena) = ArenaFixture.Build();
        var before = arena.CellCount(rowOrdinal: ArenaFixture.History);

        Assert.False(condition: arena.TryWriteOrMint(
            evicted: out _,
            key: ArenaFixture.Key(
                catalog: catalog,
                value: "coin"
            ),
            reason: out var reason,
            rowOrdinal: ArenaFixture.History,
            value: CellValue.Int(value: 1L)
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "ring"
        );
        Assert.Equal(
            actual: arena.CellCount(rowOrdinal: ArenaFixture.History),
            expected: before
        );
    }
    [Fact]
    public void TheLiveDoorRebasesAHeldTraitedCellAndAMintCarriesTheOperand() {
        var (catalog, arena) = ArenaFixture.Build();
        var second = FixedTickConversion.TicksPerSecond;
        var time = ArenaTime.At(
            engineTick: second,
            tick: 0UL
        );

        // The accumulating clock reads 70 one second in; an add of three lands on what a reader sees.
        Assert.True(
            condition: arena.TryWriteLiveOrMint(
                evicted: out _,
                key: ArenaFixture.SlotKey(catalog: catalog),
                operand: 3L,
                reason: out var reason,
                rowOrdinal: ArenaFixture.Clock,
                time: in time,
                write: StateWriteKind.Add
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: arena.Read(
                key: ArenaFixture.SlotKey(catalog: catalog),
                rowOrdinal: ArenaFixture.Clock
            )!.Value.AsInt,
            expected: 73L
        );

        // An add to a cell the row does not hold adds to nothing: the mint carries the operand.
        var key = ArenaFixture.Key(
            catalog: catalog,
            value: "coin"
        );

        Assert.True(
            condition: arena.TryWriteLiveOrMint(
                evicted: out _,
                key: key,
                operand: 3L,
                reason: out reason,
                rowOrdinal: ArenaFixture.Purse,
                time: in time,
                write: StateWriteKind.Add
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: arena.Read(
                key: key,
                rowOrdinal: ArenaFixture.Purse
            )!.Value.AsInt,
            expected: 3L
        );
    }
    [Fact]
    public void AnEvictingMintNamesTheCellItDropped() {
        var (_, arena) = ArenaFixture.Build();

        foreach (var name in ((string[])["p", "q"])) {
            Assert.True(
                condition: arena.TryMintEvicting(
                    evicted: out var evicted,
                    key: out _,
                    name: ArenaFixture.Name(value: name),
                    reason: out var reason,
                    rowOrdinal: ArenaFixture.Bag,
                    value: CellValue.Int(value: 1L)
                ),
                userMessage: reason
            );
            Assert.Null(@object: evicted);
        }

        Assert.True(
            condition: arena.TryMintEvicting(
                evicted: out var dropped,
                key: out _,
                name: ArenaFixture.Name(value: "r"),
                reason: out var dropReason,
                rowOrdinal: ArenaFixture.Bag,
                value: CellValue.Int(value: 1L)
            ),
            userMessage: dropReason
        );
        Assert.Equal(
            actual: dropped,
            expected: ArenaFixture.Name(value: "p")
        );

        // A refused mint drops nothing and names nothing.
        Assert.False(condition: arena.TryMintEvicting(
            evicted: out var none,
            key: out _,
            name: ArenaFixture.Name(value: "s"),
            reason: out _,
            rowOrdinal: ArenaFixture.Bag,
            value: CellValue.Text(value: "wrong kind")
        ));
        Assert.Null(@object: none);
    }
}
