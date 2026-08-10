using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves ties resolve by submission order: two buyouts submitted for the same listing before one tick
/// drains resolve to whichever landed FIRST in the ordered queue — the second finds the listing already settled and
/// refuses, never a race or a double-sale.</summary>
public sealed class MarketTieBreakSubmissionOrderLawTests {
    [Fact]
    public void CompetingBuyouts_ResolveToTheFirstSubmission() {
        Laws.RefusalWithControl(
            lawId: "market.buyout-tie-break-by-submission-order",
            deniedOutcome: () => SecondBuyerSpent(competing: true),
            controlOutcome: () => SecondBuyerSpent(competing: false));
    }

    // competing: true enqueues seat1's buyout, then seat2's, before ONE Step() — both drain FIFO in that tick, so
    // seat1's lands first and seat2's refuses against the now-settled listing. competing: false enqueues ONLY
    // seat2's buyout, against an otherwise-untouched listing, which succeeds.
    private static bool SecondBuyerSpent(bool competing) {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: WorldPrincipal.Seat(slot: 0),
            Seller: WorldPrincipal.Seat(slot: 0),
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 2,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.Buyout,
            StartPrice: 0,
            BuyoutPrice: 50,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        if (competing) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.BuyoutMarketListing(Principal: WorldPrincipal.Seat(slot: 1), Buyer: WorldPrincipal.Seat(slot: 1), ListingId: 1));
        }

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.BuyoutMarketListing(Principal: WorldPrincipal.Seat(slot: 2), Buyer: WorldPrincipal.Seat(slot: 2), ListingId: 1));
        fixture.Step();

        var seat2GoldAfter = MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: WorldPrincipal.Seat(slot: 2));

        return (seat2GoldAfter < MarketFixtures.BidderStartingGold);
    }
}
