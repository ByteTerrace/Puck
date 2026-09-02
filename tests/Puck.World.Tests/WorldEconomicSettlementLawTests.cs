using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Proves the reusable economic candidate's atomicity, exact per-asset conservation, and typed candidate receipt.</summary>
public sealed class WorldEconomicSettlementLawTests {
    private static readonly WorldEconomicCell SellerApples = Cell(key: "0", row: MarketFixtures.AppleRow);
    private static readonly WorldEconomicCell BuyerApples = Cell(key: "1", row: MarketFixtures.AppleRow);
    private static readonly WorldEconomicCell SellerGold = Cell(key: "0", row: MarketFixtures.GoldRow);
    private static readonly WorldEconomicCell BuyerGold = Cell(key: "1", row: MarketFixtures.GoldRow);

    private static WorldEconomicCell Cell(WorldCellName row, string key) => new(
        Row: row,
        Key: WorldCellName.Parse(candidate: key)
    );

    [Fact]
    public void AcceptedCandidate_ReportsTouchedAccountsAndBalancedReserveDeltas() {
        var source = MarketFixtures.BuildDocument();
        var settlement = new WorldEconomicSettlement(source: source, tick: 0UL);

        Assert.True(condition: settlement.Require(condition: true, reason: "the market is closed"));
        Assert.True(condition: settlement.Debit(amount: 4L, cell: SellerApples, insufficientReason: "not enough apples"));
        Assert.True(condition: settlement.Reserve(amount: 4L, row: MarketFixtures.AppleRow));
        Assert.True(condition: settlement.Release(amount: 1L, row: MarketFixtures.AppleRow));
        Assert.True(condition: settlement.Credit(amount: 1L, cell: BuyerApples));
        Assert.True(condition: settlement.Transfer(amount: 25L, destination: BuyerGold, insufficientReason: "not enough gold", source: SellerGold));

        Assert.True(condition: settlement.TryApply(
            candidate: out var candidate,
            complete: static candidate => candidate,
            reason: out var reason,
            receipt: out var receipt
        ), userMessage: reason);

        Assert.NotSame(actual: candidate, expected: source);
        Assert.NotNull(@object: receipt);
        Assert.True(condition: receipt.Conserved);
        Assert.Collection(
            receipt.Cells,
            delta => Assert.Equal(
                expected: new WorldEconomicCellDelta(After: 6L, Before: 10L, Cell: SellerApples, Delta: -4),
                actual: delta
            ),
            delta => Assert.Equal(
                expected: new WorldEconomicCellDelta(After: 1L, Before: 0L, Cell: BuyerApples, Delta: 1),
                actual: delta
            ),
            delta => Assert.Equal(
                expected: new WorldEconomicCellDelta(After: 475L, Before: 500L, Cell: SellerGold, Delta: -25),
                actual: delta
            ),
            delta => Assert.Equal(
                expected: new WorldEconomicCellDelta(After: 525L, Before: 500L, Cell: BuyerGold, Delta: 25),
                actual: delta
            )
        );
        Assert.Collection(
            receipt.Conservation,
            delta => Assert.Equal(
                expected: new WorldEconomicConservationDelta(CellDelta: -3, NetDelta: 0, ReserveDelta: 3, Row: MarketFixtures.AppleRow),
                actual: delta
            ),
            delta => Assert.Equal(
                expected: new WorldEconomicConservationDelta(CellDelta: 0, NetDelta: 0, ReserveDelta: 0, Row: MarketFixtures.GoldRow),
                actual: delta
            )
        );
        Assert.Equal(expected: 6L, actual: MarketFixtures.CellValueOf(definition: candidate, row: MarketFixtures.AppleRow, principal: WorldPrincipal.Seat(slot: 0)));
        Assert.Equal(expected: 1L, actual: MarketFixtures.CellValueOf(definition: candidate, row: MarketFixtures.AppleRow, principal: WorldPrincipal.Seat(slot: 1)));
        Assert.Equal(expected: 475L, actual: MarketFixtures.CellValueOf(definition: candidate, row: MarketFixtures.GoldRow, principal: WorldPrincipal.Seat(slot: 0)));
        Assert.Equal(expected: 525L, actual: MarketFixtures.CellValueOf(definition: candidate, row: MarketFixtures.GoldRow, principal: WorldPrincipal.Seat(slot: 1)));
    }
    [Fact]
    public void RefusedCandidate_PublishesNoDocumentOrReceiptAndNeverRunsCompletion() {
        var source = MarketFixtures.BuildDocument();
        var settlement = new WorldEconomicSettlement(source: source, tick: 0UL);
        var completed = false;

        Assert.False(condition: settlement.Debit(amount: 11L, cell: SellerApples, insufficientReason: "not enough apples"));
        Assert.False(condition: settlement.TryApply(
            complete: candidate => {
                completed = true;

                return candidate;
            },
            candidate: out var candidate,
            receipt: out var receipt,
            reason: out var reason
        ));

        Assert.Equal(actual: reason, expected: "not enough apples");
        Assert.Same(actual: candidate, expected: source);
        Assert.Null(@object: receipt);
        Assert.False(condition: completed);
        Assert.Equal(expected: 10L, actual: MarketFixtures.CellValueOf(definition: source, row: MarketFixtures.AppleRow, principal: WorldPrincipal.Seat(slot: 0)));
    }
    [Fact]
    public void UnbalancedCandidate_IsRefusedAtomically() {
        var source = MarketFixtures.BuildDocument();
        var settlement = new WorldEconomicSettlement(source: source, tick: 0UL);

        Assert.True(condition: settlement.Credit(amount: 1L, cell: BuyerGold));
        Assert.False(condition: settlement.TryApply(
            candidate: out var candidate,
            complete: static candidate => candidate,
            reason: out var reason,
            receipt: out var receipt
        ));

        Assert.Equal(actual: reason, expected: "economic settlement does not conserve 'gold' (net 1)");
        Assert.Same(actual: candidate, expected: source);
        Assert.Null(@object: receipt);
    }
}
