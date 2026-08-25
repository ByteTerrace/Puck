using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Proves the reusable economic candidate's atomicity, exact per-asset conservation, and typed candidate receipt.</summary>
public sealed class WorldEconomicSettlementLawTests {
    private static readonly WorldEconomicCell SellerApples = Cell(row: MarketFixtures.AppleRow, key: "0");
    private static readonly WorldEconomicCell BuyerApples = Cell(row: MarketFixtures.AppleRow, key: "1");
    private static readonly WorldEconomicCell SellerGold = Cell(row: MarketFixtures.GoldRow, key: "0");
    private static readonly WorldEconomicCell BuyerGold = Cell(row: MarketFixtures.GoldRow, key: "1");

    private static WorldEconomicCell Cell(WorldCellName row, string key) => new(
        Row: row,
        Key: WorldCellName.Parse(candidate: key)
    );

    [Fact]
    public void AcceptedCandidate_ReportsTouchedAccountsAndBalancedReserveDeltas() {
        var source = MarketFixtures.BuildDocument();
        var settlement = new WorldEconomicSettlement(source: source, tick: 0UL);

        Assert.True(condition: settlement.Require(condition: true, reason: "the market is closed"));
        Assert.True(condition: settlement.Debit(cell: SellerApples, amount: 4L, insufficientReason: "not enough apples"));
        Assert.True(condition: settlement.Reserve(row: MarketFixtures.AppleRow, amount: 4L));
        Assert.True(condition: settlement.Release(row: MarketFixtures.AppleRow, amount: 1L));
        Assert.True(condition: settlement.Credit(cell: BuyerApples, amount: 1L));
        Assert.True(condition: settlement.Transfer(source: SellerGold, destination: BuyerGold, amount: 25L, insufficientReason: "not enough gold"));

        Assert.True(condition: settlement.TryApply(
            complete: static candidate => candidate,
            candidate: out var candidate,
            receipt: out var receipt,
            reason: out var reason
        ), userMessage: reason);

        Assert.NotSame(expected: source, actual: candidate);
        Assert.NotNull(@object: receipt);
        Assert.True(condition: receipt.Conserved);
        Assert.Collection(
            receipt.Cells,
            delta => Assert.Equal(
                expected: new WorldEconomicCellDelta(Cell: SellerApples, Before: 10L, After: 6L, Delta: -4),
                actual: delta
            ),
            delta => Assert.Equal(
                expected: new WorldEconomicCellDelta(Cell: BuyerApples, Before: 0L, After: 1L, Delta: 1),
                actual: delta
            ),
            delta => Assert.Equal(
                expected: new WorldEconomicCellDelta(Cell: SellerGold, Before: 500L, After: 475L, Delta: -25),
                actual: delta
            ),
            delta => Assert.Equal(
                expected: new WorldEconomicCellDelta(Cell: BuyerGold, Before: 500L, After: 525L, Delta: 25),
                actual: delta
            )
        );
        Assert.Collection(
            receipt.Conservation,
            delta => Assert.Equal(
                expected: new WorldEconomicConservationDelta(Row: MarketFixtures.AppleRow, CellDelta: -3, ReserveDelta: 3, NetDelta: 0),
                actual: delta
            ),
            delta => Assert.Equal(
                expected: new WorldEconomicConservationDelta(Row: MarketFixtures.GoldRow, CellDelta: 0, ReserveDelta: 0, NetDelta: 0),
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

        Assert.False(condition: settlement.Debit(cell: SellerApples, amount: 11L, insufficientReason: "not enough apples"));
        Assert.False(condition: settlement.TryApply(
            complete: candidate => {
                completed = true;

                return candidate;
            },
            candidate: out var candidate,
            receipt: out var receipt,
            reason: out var reason
        ));

        Assert.Equal(expected: "not enough apples", actual: reason);
        Assert.Same(expected: source, actual: candidate);
        Assert.Null(@object: receipt);
        Assert.False(condition: completed);
        Assert.Equal(expected: 10L, actual: MarketFixtures.CellValueOf(definition: source, row: MarketFixtures.AppleRow, principal: WorldPrincipal.Seat(slot: 0)));
    }

    [Fact]
    public void UnbalancedCandidate_IsRefusedAtomically() {
        var source = MarketFixtures.BuildDocument();
        var settlement = new WorldEconomicSettlement(source: source, tick: 0UL);

        Assert.True(condition: settlement.Credit(cell: BuyerGold, amount: 1L));
        Assert.False(condition: settlement.TryApply(
            complete: static candidate => candidate,
            candidate: out var candidate,
            receipt: out var receipt,
            reason: out var reason
        ));

        Assert.Equal(expected: "economic settlement does not conserve 'gold' (net 1)", actual: reason);
        Assert.Same(expected: source, actual: candidate);
        Assert.Null(@object: receipt);
    }
}
