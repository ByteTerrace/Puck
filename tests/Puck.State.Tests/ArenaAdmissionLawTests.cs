using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: one admission door decides every value entering a row. A mint, a ring push, and
/// the landing of a transfer each carry their operand through the row's declared envelope, overflow policy, and
/// symbolic domain exactly as a write to the same cell does, a carrier holding no value is refused by name, an
/// admitted transfer carries the value and nothing else, and a refused membership change leaves every row it
/// touched as it was.</summary>
public sealed class ArenaAdmissionLawTests {
    [Fact]
    public void AMintOutsideTheRowsEnvelopeRefusesLikeAWrite() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.False(condition: arena.TryMint(
            key: out _,
            name: ArenaFixture.Name(value: "x"),
            reason: out var reason,
            rowOrdinal: ArenaFixture.Purse,
            value: CellValue.Int(value: 9999L)
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "purse"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "envelope"
        );
        Assert.Equal(
            expected: 0,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Purse)
        );
        Assert.True(
            condition: arena.TryMint(
                key: out _,
                name: ArenaFixture.Name(value: "x"),
                reason: out reason,
                rowOrdinal: ArenaFixture.Purse,
                value: CellValue.Int(value: 7L)
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: CellValue.Int(value: 7L),
            actual: arena.Read(
                key: ArenaFixture.Key(
                    catalog: catalog,
                    value: "x"
                ),
                rowOrdinal: ArenaFixture.Purse
            )
        );

        // The same value through the write door refuses for the same reason, so neither door can reach a state the
        // other forbids.
        Assert.False(condition: arena.TryWrite(
            key: ArenaFixture.Key(
                catalog: catalog,
                value: "x"
            ),
            operand: 9999L,
            reason: out _,
            rowOrdinal: ArenaFixture.Purse,
            write: StateWriteKind.Set
        ));
    }
    [Fact]
    public void AMintOutsideTheRowsEnumRefusesByName() {
        var (catalog, arena) = ArenaFixture.Build();

        Assert.False(condition: arena.TryMint(
            key: out _,
            name: ArenaFixture.Name(value: "top"),
            reason: out var reason,
            rowOrdinal: ArenaFixture.Suits,
            value: CellValue.Int(value: 9L)
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "suit"
        );
        Assert.Equal(
            expected: 0,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Suits)
        );
        Assert.True(
            condition: arena.TryMint(
                key: out _,
                name: ArenaFixture.Name(value: "top"),
                reason: out reason,
                rowOrdinal: ArenaFixture.Suits,
                value: CellValue.Int(value: 2L)
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: CellValue.Int(value: 2L),
            actual: arena.Read(
                key: ArenaFixture.Key(
                    catalog: catalog,
                    value: "top"
                ),
                rowOrdinal: ArenaFixture.Suits
            )
        );
    }
    [Fact]
    public void APushOutsideTheRingsEnumRefusesByName() {
        var (_, arena) = ArenaFixture.Build();

        Assert.False(condition: arena.TryPush(
            reason: out var reason,
            rowOrdinal: ArenaFixture.Phases,
            value: 9L
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "suit"
        );
        Assert.Equal(
            expected: 0,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Phases)
        );
        Assert.True(
            condition: arena.TryPush(
                reason: out reason,
                rowOrdinal: ArenaFixture.Phases,
                value: 2L
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: 1,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Phases)
        );
    }
    [Fact]
    public void ATransferTheDestinationRefusesLeavesBothRowsUntouched() {
        var (catalog, arena) = ArenaFixture.Build();
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );

        Assert.True(
            condition: arena.TryMint(
                key: out _,
                name: ArenaFixture.Name(value: "a"),
                reason: out var reason,
                rowOrdinal: ArenaFixture.Hand,
                value: CellValue.Int(value: 33L)
            ),
            userMessage: reason
        );
        Assert.False(condition: arena.TryTransfer(
            fromOrdinal: ArenaFixture.Deck,
            insertFirst: false,
            key: a,
            reason: out reason,
            toOrdinal: ArenaFixture.Hand
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "already holds"
        );
        Assert.Equal(
            expected: 2,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Deck)
        );
        Assert.Equal(
            expected: 1,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Hand)
        );
        Assert.Equal(
            expected: CellValue.Int(value: 11L),
            actual: arena.Read(
                key: a,
                rowOrdinal: ArenaFixture.Deck
            )
        );
        Assert.Equal(
            expected: CellValue.Int(value: 33L),
            actual: arena.Read(
                key: a,
                rowOrdinal: ArenaFixture.Hand
            )
        );
    }
    [Fact]
    public void ATransferOutsideTheDestinationsEnvelopeLeavesTheMemberWhereItWas() {
        var (catalog, arena) = ArenaFixture.Build();
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );

        Assert.False(condition: arena.TryTransfer(
            fromOrdinal: ArenaFixture.Deck,
            insertFirst: false,
            key: a,
            reason: out var reason,
            toOrdinal: ArenaFixture.Vault
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "vault"
        );
        Assert.Equal(
            expected: 2,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Deck)
        );
        Assert.Equal(
            expected: 0,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Vault)
        );
        Assert.Equal(
            expected: CellValue.Int(value: 11L),
            actual: arena.Read(
                key: a,
                rowOrdinal: ArenaFixture.Deck
            )
        );
    }
    [Fact]
    public void AnAdmittedTransferCarriesItsValueAcross() {
        var (catalog, arena) = ArenaFixture.Build();
        var b = ArenaFixture.Key(
            catalog: catalog,
            value: "b"
        );

        Assert.True(
            condition: arena.TryWrite(
                key: b,
                operand: 4L,
                reason: out var reason,
                rowOrdinal: ArenaFixture.Deck,
                write: StateWriteKind.Set
            ),
            userMessage: reason
        );
        Assert.True(
            condition: arena.TryWriteClock(
                epochEngineTick: 4L,
                epochTick: 3L,
                key: b,
                reason: out reason,
                rowOrdinal: ArenaFixture.Deck,
                substepTicks: 7L,
                v0: 6L,
                y0: 5L
            ),
            userMessage: reason
        );
        Assert.True(condition: arena.TryWriteBehavior(
            behavior: StateCellBehavior.None,
            key: b,
            rowOrdinal: ArenaFixture.Deck
        ));
        Assert.True(condition: arena.TryWriteObservation(
            key: b,
            observation: new StateObservation(
                Tick: 12L,
                Visible: true
            ),
            rowOrdinal: ArenaFixture.Deck
        ));
        Assert.True(condition: arena.TryWriteProvenance(
            key: b,
            provenance: "issuer",
            rowOrdinal: ArenaFixture.Deck
        ));
        Assert.True(condition: arena.TryWriteVisibility(
            key: b,
            rowOrdinal: ArenaFixture.Deck,
            visibility: new StateVisibility(Readers: ["p1"])
        ));
        Assert.True(
            condition: arena.TryTransfer(
                fromOrdinal: ArenaFixture.Deck,
                insertFirst: false,
                key: b,
                reason: out reason,
                toOrdinal: ArenaFixture.Vault
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: 1,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Deck)
        );
        Assert.Equal(
            expected: CellValue.Int(value: 4L),
            actual: arena.Read(
                key: b,
                rowOrdinal: ArenaFixture.Vault
            )
        );

        // The value crosses; every runtime-state field stays with the slot the member left.
        Assert.True(condition: arena.TryReadClock(
            epochEngineTick: out var epochEngineTick,
            epochTick: out var epochTick,
            key: b,
            rowOrdinal: ArenaFixture.Vault,
            set: out _,
            substepTicks: out var substepTicks,
            v0: out var v0,
            y0: out var y0
        ));
        Assert.Equal(
            actual: ((((epochEngineTick + epochTick) + substepTicks) + v0) + y0),
            expected: 0L
        );
        Assert.Equal(
            actual: arena.Behavior(
                key: b,
                rowOrdinal: ArenaFixture.Vault
            ),
            expected: StateCellBehavior.Inherit
        );
        Assert.Null(@object: arena.Observation(
            key: b,
            rowOrdinal: ArenaFixture.Vault
        ));
        Assert.Null(@object: arena.Provenance(
            key: b,
            rowOrdinal: ArenaFixture.Vault
        ));
        Assert.Null(@object: arena.Visibility(
            key: b,
            rowOrdinal: ArenaFixture.Vault
        ));
    }
    [Fact]
    public void AMintOfACarrierHoldingNoValueRefusesByName() {
        var (_, arena) = ArenaFixture.Build();

        Assert.False(condition: arena.TryMint(
            key: out _,
            name: ArenaFixture.Name(value: "empty"),
            reason: out var reason,
            rowOrdinal: ArenaFixture.Hand,
            value: default
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "hand"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "empty"
        );
        Assert.Equal(
            expected: 0,
            actual: arena.CellCount(rowOrdinal: ArenaFixture.Hand)
        );
    }
}
