using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves the trade-party authority split every market mutation checks beneath its coarse
/// <c>Mutate/section:market</c> hold: a seat's own boot-seeded section hold is authority over its own inventory,
/// never another seat's — naming a different seat as the trade party while acting as a third seat is impersonation
/// and refuses by name, leaving the named party's own balance untouched. Console is the one narrow exception (the
/// split stdin's own Console-stamped, seat-naming submissions rely on): it may name any seat/peer, and a seat acting
/// for itself is always admitted. Every law pairs the denial with both controls — actor==party succeeds, and
/// Console-on-behalf succeeds — so a law that could never fail proves nothing (the discriminating-case rule).</summary>
public sealed class MarketPartyAuthorityLawTests {
    // The attacker never owns anything the victim's listing/bid/buyout/cancel could move — actor != target
    // discriminates the impersonation refusal from a coincidental self-action.
    private static readonly WorldPrincipal Attacker = WorldPrincipal.Seat(slot: 1);
    private static readonly WorldPrincipal Victim = WorldPrincipal.Seat(slot: 0);
    private static readonly WorldPrincipal ThirdParty = WorldPrincipal.Seat(slot: 2);

    [Fact]
    public void CreateMarketListing_ImpersonationRefused_SelfAndConsoleOnBehalfSucceed() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        var applesBefore = MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Victim);

        // The attack: seat1 (Attacker) lists seat0's (Victim's) apples, naming itself as Principal and Victim as Seller.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Attacker,
            Seller: Victim,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 3,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        Assert.Null(@object: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1));
        Assert.Equal(expected: applesBefore, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Victim));

        // Control 1: the victim acting for themselves succeeds.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Victim,
            Seller: Victim,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 3,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        var selfListing = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1);

        Assert.NotNull(@object: selfListing);
        Assert.Equal(expected: (applesBefore - 3), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Victim));

        // Control 2: Console naming the victim on behalf succeeds — the one narrow exception the split exists for.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: WorldPrincipal.Console,
            Seller: Victim,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 2,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        var consoleListing = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 2);

        Assert.NotNull(@object: consoleListing);
        Assert.Equal(expected: (applesBefore - 5), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Victim));
    }
    [Fact]
    public void PlaceMarketBid_ImpersonationRefused_SelfAndConsoleOnBehalfSucceed() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Victim,
            Seller: Victim,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 3,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        const long listingId = 1;
        var goldBefore = MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty);

        // The attack: seat1 (Attacker) bids as seat2 (ThirdParty) against the listing.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: Attacker, Bidder: ThirdParty, ListingId: listingId, Amount: 10));
        fixture.Step();

        Assert.Equal(expected: 0L, actual: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: listingId)!.CurrentBid);
        Assert.Equal(expected: goldBefore, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty));

        // Control 1: the third party bidding for themselves succeeds.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: ThirdParty, Bidder: ThirdParty, ListingId: listingId, Amount: 10));
        fixture.Step();

        Assert.Equal(expected: 10L, actual: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: listingId)!.CurrentBid);
        Assert.Equal(expected: (goldBefore - 10), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty));

        // Control 2: Console bidding on the third party's behalf succeeds.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: WorldPrincipal.Console, Bidder: ThirdParty, ListingId: listingId, Amount: 20));
        fixture.Step();

        Assert.Equal(expected: 20L, actual: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: listingId)!.CurrentBid);
        Assert.Equal(expected: (goldBefore - 20), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty));
    }
    [Fact]
    public void BuyoutMarketListing_ImpersonationRefused_SelfAndConsoleOnBehalfSucceed() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Victim,
            Seller: Victim,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 3,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.Buyout,
            StartPrice: 0,
            BuyoutPrice: 30,
            DurationSeconds: MarketFixtures.MaxDurationSeconds
        ));
        fixture.Step();

        var goldBefore = MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty);

        // The attack: seat1 (Attacker) buys out as seat2 (ThirdParty).
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.BuyoutMarketListing(Principal: Attacker, Buyer: ThirdParty, ListingId: 1));
        fixture.Step();

        Assert.Equal(expected: WorldMarketListingStatus.Active, actual: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!.Status);
        Assert.Equal(expected: goldBefore, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty));

        // Control: Console buying out on the third party's behalf succeeds.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.BuyoutMarketListing(Principal: WorldPrincipal.Console, Buyer: ThirdParty, ListingId: 1));
        fixture.Step();

        Assert.Equal(expected: WorldMarketListingStatus.Settled, actual: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!.Status);
        Assert.Equal(expected: (goldBefore - 30), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty));
    }
    [Fact]
    public void CancelMarketListing_ImpersonationRefused_SelfSucceeds() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Victim,
            Seller: Victim,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 4,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MaxDurationSeconds
        ));
        fixture.Step();

        var applesEscrowed = MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Victim);

        // The attack: seat1 (Attacker) cancels naming seat0 (Victim, the real seller) as Canceler.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CancelMarketListing(Principal: Attacker, Canceler: Victim, ListingId: 1));
        fixture.Step();

        Assert.Equal(expected: WorldMarketListingStatus.Active, actual: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!.Status);
        Assert.Equal(expected: applesEscrowed, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Victim));

        // Control: the victim (the real seller) cancelling for themselves succeeds.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CancelMarketListing(Principal: Victim, Canceler: Victim, ListingId: 1));
        fixture.Step();

        Assert.Equal(expected: WorldMarketListingStatus.Cancelled, actual: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!.Status);
        Assert.Equal(expected: (applesEscrowed + 4), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.AppleRow, principal: Victim));
    }
}
