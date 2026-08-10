using System.Text;

using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves an English listing's absent buyout is optional on both sides of the canonical persistence door.</summary>
public sealed class MarketOptionalBuyoutPersistenceLawTests {
    [Fact]
    public void EnglishListingWithoutBuyout_OmitsAndRehydratesTheOptionalMember() {
        var seller = WorldPrincipal.Seat(slot: 0);
        var market = (MarketFixtures.BuildDocument().Market!) with {
            Listings = [
                new WorldMarketListing(
                    Id: 1,
                    Seller: seller,
                    ItemRow: MarketFixtures.AppleRow,
                    Quantity: 1,
                    CurrencyRow: MarketFixtures.GoldRow,
                    Format: WorldMarketFormat.English,
                    StartPrice: 25,
                    DeadlineTick: 100,
                    BuyoutPrice: null
                ),
            ],
            NextListingId = 2,
        };
        var document = (MarketFixtures.BuildDocument() with { Market = market });
        var bytes = WorldDefinitionSerialization.Serialize(definition: document);
        var json = Encoding.UTF8.GetString(bytes: bytes);

        Assert.DoesNotContain(expectedSubstring: "\"buyoutPrice\"", actualString: json, comparisonType: StringComparison.Ordinal);

        var roundTripped = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);
        var listing = Assert.Single(collection: roundTripped.Market!.Listings!);

        Assert.Null(@object: listing.BuyoutPrice);
    }
}
