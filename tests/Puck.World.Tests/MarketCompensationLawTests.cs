using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves cancel and expiry compensate EXACTLY — the escrowed item returns to the seller, and any escrowed
/// bid returns to its bidder, leaving every holder exactly where they started.</summary>
public sealed class MarketCompensationLawTests {
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);
    private static readonly WorldPrincipal Bidder = WorldPrincipal.Seat(slot: 1);

    [Fact]
    public void Cancel_ReturnsTheEscrowedItemAndRefundsTheStandingBid() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: null,
            CurrencyRow: MarketFixtures.GoldRow,
            DurationSeconds: MarketFixtures.MinDurationSeconds,
            Format: WorldMarketFormat.English,
            ItemRow: MarketFixtures.AppleRow,
            Principal: Seller,
            Quantity: 4,
            Seller: Seller,
            StartPrice: 5
        ));
        fixture.Step();

        // Escrowed mid-flight: the seller's apples are already down, the listing carries them.
        Assert.Equal(expected: (MarketFixtures.SellerStartingApples - 4), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Seller));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Amount: 15, Bidder: Bidder, ListingId: 1, Principal: Bidder));
        fixture.Step();

        Assert.Equal(expected: (MarketFixtures.BidderStartingGold - 15), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Bidder));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CancelMarketListing(Canceler: Seller, ListingId: 1, Principal: Seller));
        fixture.Step();

        var cancelled = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!;

        Assert.Equal(expected: WorldMarketListingStatus.Cancelled, actual: cancelled.Status);
        Assert.Equal(expected: MarketFixtures.SellerStartingApples, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Seller));
        Assert.Equal(expected: MarketFixtures.SellerStartingGold, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Seller));
        Assert.Equal(expected: MarketFixtures.BidderStartingGold, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Bidder));
    }
    [Fact]
    public void UnbidListing_ExpiresAndReturnsTheEscrowedItem() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: null,
            CurrencyRow: MarketFixtures.GoldRow,
            DurationSeconds: MarketFixtures.MinDurationSeconds,
            Format: WorldMarketFormat.English,
            ItemRow: MarketFixtures.AppleRow,
            Principal: Seller,
            Quantity: 6,
            Seller: Seller,
            StartPrice: 5
        ));
        fixture.Step();

        Assert.Equal(expected: (MarketFixtures.SellerStartingApples - 6), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Seller));

        // No bid ever lands — step past the deadline and let the engine's own sweep expire it.
        for (var index = 0; (index < 300); index++) {
            fixture.Step();
        }

        var expired = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!;

        Assert.Equal(expected: WorldMarketListingStatus.Expired, actual: expired.Status);
        Assert.Equal(expected: MarketFixtures.SellerStartingApples, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Seller));
        Assert.Equal(expected: MarketFixtures.SellerStartingGold, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Seller));
    }
}
