using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves <see cref="WorldMarketListing.ResolvedTick"/> — added to the market shape after listings had
/// already shipped — is a load-time MIGRATION, never a validator-side tolerance for the old shape. A persisted world
/// file whose terminal listing predates the field (the field simply absent — exactly
/// <see cref="WorldMarketListing.ResolvedTick"/>'s own <c>JsonIgnore(WhenWritingNull)</c> wire shape) is stamped a
/// deterministic fallback age basis — tick 0, the earliest possible tick, since a document carries no absolute-tick
/// field of its own — on load, by <see cref="WorldDefinitionMigrations.Apply"/>, run from every seam a document's raw
/// bytes become a live <see cref="WorldDefinition"/> (<see cref="WorldDefinitionFileSource.TryLoad"/>,
/// <see cref="WorldDefinitionSerialization.Deserialize"/>) BEFORE <see cref="WorldDefinitionValidator"/> ever sees
/// it. The validator's own invariant — a terminal listing carries a resolvedTick, an active one carries none — stays
/// strict throughout: the paired control proves a document whose ACTIVE listing carries a resolvedTick (a genuinely
/// malformed shape the migration does not, and must not, touch) still refuses on load.</summary>
public sealed class MarketTerminalListingMigrationLawTests {
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);

    [Fact]
    public void PreFieldTerminalListing_MigratesOnLoad_AndPrunesOnRetention() {
        var document = BuildDocumentWithListing(status: WorldMarketListingStatus.Cancelled, resolvedTick: null, retentionSeconds: 1f);

        // On the RAW (un-migrated) document, TryValidate refuses — proves this fixture genuinely reproduces the
        // pre-field shape the validator's invariant rejects, not an already-well-formed document in disguise.
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, reason: out var preMigrationReason, neighbours: null));
        Assert.Contains(expectedSubstring: "resolvedTick", actualString: preMigrationReason, comparisonType: StringComparison.Ordinal);

        // ResolvedTick carries JsonIgnore(WhenWritingNull), so serializing this document omits the field entirely —
        // the SAME bytes shape a save written before the field existed would carry on disk.
        var path = WriteTempWorldFile(document: document, suffix: "migrate");

        try {
            Assert.True(condition: WorldDefinitionFileSource.TryLoad(path: path, definition: out var loaded, contentHash: out _, reason: out var loadReason), userMessage: loadReason);

            var stamped = MarketFixtures.FindListing(definition: loaded!, id: 1);

            Assert.NotNull(@object: stamped);
            Assert.Equal(expected: 0L, actual: stamped!.ResolvedTick);
            Assert.Equal(expected: WorldMarketListingStatus.Cancelled, actual: stamped.Status);

            using var fixture = Fixtures.FreshServer(definition: loaded);

            // retentionSeconds 1f is 240 ticks at the fixture's fixed 240 Hz rate; the stamped tick-0 basis makes
            // the row eligible the moment the sweep next runs past that — 260 steps clears it comfortably.
            for (var index = 0; (index < 260); index++) {
                fixture.Step();
            }

            Assert.Null(@object: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1));
        } finally {
            TryDeleteFile(path: path);
        }
    }
    [Fact]
    public void MalformedActiveListingCarryingResolvedTick_StillRefusesOnLoad() {
        // Active + a resolvedTick present is the OTHER half of the invariant — a shape the migration never produces
        // and must never paper over, discriminating "the field was simply never written" from "this document is
        // wrong".
        var document = BuildDocumentWithListing(status: WorldMarketListingStatus.Active, resolvedTick: 5L, retentionSeconds: 1f);
        var path = WriteTempWorldFile(document: document, suffix: "control");

        try {
            Assert.False(condition: WorldDefinitionFileSource.TryLoad(path: path, definition: out _, contentHash: out _, reason: out var reason));
            Assert.Contains(expectedSubstring: "resolvedTick", actualString: reason, comparisonType: StringComparison.Ordinal);
        } finally {
            TryDeleteFile(path: path);
        }
    }

    private static WorldDefinition BuildDocumentWithListing(WorldMarketListingStatus status, long? resolvedTick, float retentionSeconds) {
        var market = new WorldMarketSection(
            Formats: [WorldMarketFormat.Buyout],
            FeeBasisPoints: 0,
            MinDurationSeconds: 1f,
            MaxDurationSeconds: 3_600f,
            RetentionSeconds: retentionSeconds,
            Listings: [
                new WorldMarketListing(
                    Id: 1,
                    Seller: Seller,
                    ItemRow: MarketFixtures.AppleRow,
                    Quantity: 1,
                    CurrencyRow: MarketFixtures.GoldRow,
                    Format: WorldMarketFormat.Buyout,
                    StartPrice: 0,
                    BuyoutPrice: 10,
                    DeadlineTick: 60,
                    Status: status,
                    CurrentBid: 0,
                    CurrentBidder: null,
                    ResolvedTick: resolvedTick
                ),
            ],
            NextListingId: 2
        );

        return (MarketFixtures.BuildDocument() with { Market = market });
    }
    private static string WriteTempWorldFile(WorldDefinition document, string suffix) {
        var bytes = WorldDefinitionSerialization.Serialize(definition: document);
        var path = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-world-tests-market-{suffix}-{Guid.NewGuid():N}.json");

        File.WriteAllBytes(path: path, bytes: bytes);

        return path;
    }
    private static void TryDeleteFile(string path) {
        try {
            File.Delete(path: path);
        } catch (IOException) {
        }
    }
}
