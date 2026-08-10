using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves the market's resolution is a deterministic function of (document, submission order): driving the
/// identical list/bid/deadline sequence against two independent fresh servers produces byte-identical documents.</summary>
public sealed class MarketDeterministicResolutionLawTests {
    [Fact]
    public void SameBidsSameOrder_ProduceTheSameWinner_Twice() {
        var first = RunScenario();
        var second = RunScenario();

        Assert.Equal(expected: first, actual: second);
    }

    private static byte[] RunScenario() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: WorldPrincipal.Seat(slot: 0),
            Seller: WorldPrincipal.Seat(slot: 0),
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 3,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 10,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: WorldPrincipal.Seat(slot: 1), Bidder: WorldPrincipal.Seat(slot: 1), ListingId: 1, Amount: 20));
        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: WorldPrincipal.Seat(slot: 2), Bidder: WorldPrincipal.Seat(slot: 2), ListingId: 1, Amount: 30));
        fixture.Step();

        // Step past the deadline — the engine's own sweep settles the listing to the standing bidder with no
        // further submission.
        for (var index = 0; (index < 300); index++) {
            fixture.Step();
        }

        var settled = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!;

        Assert.Equal(expected: WorldMarketListingStatus.Settled, actual: settled.Status);
        Assert.Equal(expected: WorldPrincipal.Seat(slot: 2), actual: (WorldPrincipal)settled.CurrentBidder!);

        return fixture.DefinitionBytes();
    }
}
