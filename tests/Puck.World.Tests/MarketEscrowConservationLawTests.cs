using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves item and currency totals are conserved across list/bid/settle — "physics with monies": every
/// debit has exactly one credit, including the house fee (routed to <see cref="WorldMarketSection.FeeReserve"/>,
/// never destroyed).</summary>
public sealed class MarketEscrowConservationLawTests {
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);
    private static readonly WorldPrincipal OutbidBidder = WorldPrincipal.Seat(slot: 1);
    private static readonly WorldPrincipal Winner = WorldPrincipal.Seat(slot: 2);

    [Fact]
    public void ListBidOutbidSettle_ConservesGoldAndApples() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        var goldBefore = TotalGold(fixture: fixture);
        var applesBefore = TotalApples(fixture: fixture);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Seller,
            Seller: Seller,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 3,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 10,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: OutbidBidder, Bidder: OutbidBidder, ListingId: 1, Amount: 20));
        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: Winner, Bidder: Winner, ListingId: 1, Amount: 30));
        fixture.Step();

        for (var index = 0; (index < 300); index++) {
            fixture.Step();
        }

        var settled = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!;

        Assert.Equal(expected: WorldMarketListingStatus.Settled, actual: settled.Status);

        // The exact ledger: a 10% fee on the winning bid of 30 is 3 — seller nets 27, the house keeps 3, the
        // outbid bidder is refunded in full, and the winner pays the full 30.
        Assert.Equal(expected: (MarketFixtures.SellerStartingGold + 27), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Seller));
        Assert.Equal(expected: MarketFixtures.BidderStartingGold, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: OutbidBidder));
        Assert.Equal(expected: (MarketFixtures.BidderStartingGold - 30), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Winner));
        Assert.Equal(expected: 3L, actual: (fixture.Server.Definition.Market?.FeeReserve ?? 0L));

        Assert.Equal(expected: (MarketFixtures.SellerStartingApples - 3), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Seller));
        Assert.Equal(expected: 3L, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Winner));

        // Conservation, stated generally: total gold (every holder's cell PLUS the house's fee reserve) and total
        // apples (every holder's cell — nothing is ever in flight once every listing is resolved) are unchanged.
        Assert.Equal(expected: goldBefore, actual: TotalGold(fixture: fixture));
        Assert.Equal(expected: applesBefore, actual: TotalApples(fixture: fixture));
    }

    private static long TotalGold(WorldFixture fixture) {
        var definition = fixture.Server.Definition;
        var holders = MarketFixtures.CellValueOf(definition: definition, row: MarketFixtures.GoldRow, principal: Seller)
            + MarketFixtures.CellValueOf(definition: definition, row: MarketFixtures.GoldRow, principal: OutbidBidder)
            + MarketFixtures.CellValueOf(definition: definition, row: MarketFixtures.GoldRow, principal: Winner);
        var feeReserve = (definition.Market?.FeeReserve ?? 0L);
        var escrowedInActiveListings = SumActiveListingBids(definition: definition);

        return (holders + feeReserve + escrowedInActiveListings);
    }

    private static long TotalApples(WorldFixture fixture) {
        var definition = fixture.Server.Definition;
        var holders = MarketFixtures.CellValueOf(definition: definition, row: MarketFixtures.AppleRow, principal: Seller)
            + MarketFixtures.CellValueOf(definition: definition, row: MarketFixtures.AppleRow, principal: OutbidBidder)
            + MarketFixtures.CellValueOf(definition: definition, row: MarketFixtures.AppleRow, principal: Winner);
        var escrowedInActiveListings = SumActiveListingQuantities(definition: definition);

        return (holders + escrowedInActiveListings);
    }

    private static long SumActiveListingBids(WorldDefinition definition) {
        var total = 0L;

        foreach (var listing in (definition.Market?.Listings ?? [])) {
            if (listing.Status == WorldMarketListingStatus.Active) {
                total += listing.CurrentBid;
            }
        }

        return total;
    }

    private static long SumActiveListingQuantities(WorldDefinition definition) {
        var total = 0L;

        foreach (var listing in (definition.Market?.Listings ?? [])) {
            if (listing.Status == WorldMarketListingStatus.Active) {
                total += listing.Quantity;
            }
        }

        return total;
    }
}
