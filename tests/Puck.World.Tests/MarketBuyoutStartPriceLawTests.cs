using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves <see cref="WorldMarketListing.StartPrice"/> is validated PER-ARM, not only for the format that
/// reads it. English's minimum-opening-bid reading of the field was already refused when non-positive; a
/// <see cref="WorldMarketFormat.Buyout"/> listing never reads <see cref="WorldMarketListing.StartPrice"/> at all
/// (see <c>Server.WorldServer.TryComposeBuyoutMarketListing</c>) but the field is still a carried, representable
/// <see langword="long"/> — inert today, but a door, not a type, if left unchecked. Both refusal doors are proved:
/// <see cref="WorldDefinitionValidator"/>'s whole-document invariant (a hand-built document) and
/// <see cref="WorldMutation.CreateMarketListing"/>'s own compose-time gate (the live authoring path), each paired
/// with the control that <c>startPrice: 0</c> — the value <c>market.list</c>'s own help text documents as the
/// canonical inert one — still succeeds.</summary>
public sealed class MarketBuyoutStartPriceLawTests {
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);

    [Fact]
    public void BuyoutListingWithNonzeroStartPrice_RefusesWholeDocumentValidation() {
        var market = (MarketFixtures.BuildDocument().Market!) with {
            Listings = [
                new WorldMarketListing(
                    Id: 1,
                    Seller: Seller,
                    ItemRow: MarketFixtures.AppleRow,
                    Quantity: 1,
                    CurrencyRow: MarketFixtures.GoldRow,
                    Format: WorldMarketFormat.Buyout,
                    StartPrice: 25, // unread by buyout, but not sane data
                    BuyoutPrice: 50,
                    DeadlineTick: 100
                ),
            ],
            NextListingId = 2,
        };
        var document = (MarketFixtures.BuildDocument() with { Market = market });

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: System.StringComparison.Ordinal, expectedSubstring: "startPrice");
    }
    [Fact]
    public void BuyoutListingWithZeroStartPrice_ValidatesWholeDocument() {
        var market = (MarketFixtures.BuildDocument().Market!) with {
            Listings = [
                new WorldMarketListing(
                    Id: 1,
                    Seller: Seller,
                    ItemRow: MarketFixtures.AppleRow,
                    Quantity: 1,
                    CurrencyRow: MarketFixtures.GoldRow,
                    Format: WorldMarketFormat.Buyout,
                    StartPrice: 0,
                    BuyoutPrice: 50,
                    DeadlineTick: 100
                ),
            ],
            NextListingId = 2,
        };
        var document = (MarketFixtures.BuildDocument() with { Market = market });

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: reason);
    }
    [Fact]
    public void CreateBuyoutListing_NonzeroStartPrice_RefusedByName_ZeroSucceeds() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: 50, CurrencyRow: MarketFixtures.GoldRow, DurationSeconds: MarketFixtures.MinDurationSeconds, Format: WorldMarketFormat.Buyout, ItemRow: MarketFixtures.AppleRow,
            Principal: Seller, Quantity: 1, Seller: Seller, StartPrice: 25
        ));
        fixture.Step();

        Assert.Null(@object: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1));

        // Control: the documented canonical inert value (0) succeeds.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: 50, CurrencyRow: MarketFixtures.GoldRow, DurationSeconds: MarketFixtures.MinDurationSeconds, Format: WorldMarketFormat.Buyout, ItemRow: MarketFixtures.AppleRow,
            Principal: Seller, Quantity: 1, Seller: Seller, StartPrice: 0
        ));
        fixture.Step();

        var listing = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1);

        Assert.NotNull(@object: listing);
        Assert.Equal(expected: 0L, actual: listing!.StartPrice);
    }
}
