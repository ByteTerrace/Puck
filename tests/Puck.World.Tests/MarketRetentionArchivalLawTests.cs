using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves the market's lifetime listing count stays bounded: a terminal (settled/cancelled/expired) row
/// ages out of <see cref="WorldMarketCapacity.MaxListings"/> once it has stood past
/// <see cref="WorldMarketSection.RetentionSeconds"/> (the engine's own per-tick retention sweep,
/// <c>Server.WorldServer.PruneExpiredMarketListings</c>/<c>PruneMarketListings</c>), so a market that only ever
/// resolves listings never permanently exhausts. Paired with the control every retention law owes: a cap filled with
/// active rows (never terminal) still refuses past the same wait, proving the sweep prunes archival rows only, never
/// live ones.</summary>
public sealed class MarketRetentionArchivalLawTests {
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);

    private const int FillCount = WorldMarketCapacity.MaxListings;

    // A bespoke document, not MarketFixtures.BuildDocument(): filling 256 listings needs a seller apple balance far
    // past the fixture's own (10), and this suite needs its own retentionSeconds to force an elapse inside a test's
    // tick budget. Capacity (the row's own distinct-key ceiling, WorldStateCapacity.MaxCellsPerRow) stays at its max
    // — this test seeds only one holder key ("0"), so it is the cell value, not the key count, that must be large.
    private static WorldDefinition BuildDocument(float retentionSeconds) {
        var gold = new WorldStateRow(Name: MarketFixtures.GoldRow, Kind: CellKind.Int, Capacity: 128, NonNegative: true, Cells: [
            new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: 100_000),
        ]);
        var apple = new WorldStateRow(Name: MarketFixtures.AppleRow, Kind: CellKind.Int, Capacity: 128, NonNegative: true, Cells: [
            new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: (FillCount + 8)),
        ]);
        var market = new WorldMarketSection(
            Formats: [WorldMarketFormat.English, WorldMarketFormat.Buyout],
            FeeBasisPoints: 0,
            MinDurationSeconds: 1f,
            MaxDurationSeconds: 3_600f,
            RetentionSeconds: retentionSeconds
        );

        return (Fixtures.BuildDocument() with { State = [gold, apple], Market = market });
    }

    [Fact]
    public void TerminalRowsAgeOutPastRetention_AndTheNextListingSucceeds() {
        // 3 seconds (720 ticks) of retention — long enough that nothing ages out mid-fill (256 create+cancel pairs
        // span ~512 ticks, so even the first row cancelled is still short of 720 elapsed ticks by the time the fill
        // loop finishes), so the "still full, still refuses" assertion right after the loop is honest, not a race.
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(retentionSeconds: 3f));

        for (var id = 1; (id <= FillCount); id++) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
                Principal: Seller,
                Seller: Seller,
                ItemRow: MarketFixtures.AppleRow,
                Quantity: 1,
                CurrencyRow: MarketFixtures.GoldRow,
                Format: WorldMarketFormat.Buyout,
                StartPrice: 0,
                BuyoutPrice: 5,
                DurationSeconds: 60f
            ));
            fixture.Step();
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.CancelMarketListing(Principal: Seller, Canceler: Seller, ListingId: id));
            fixture.Step();
        }

        Assert.Equal(expected: FillCount, actual: (fixture.Server.Definition.Market?.Listings?.Count ?? 0));

        // Before retention elapses, the cap still refuses — the sweep has had no chance to archive anything yet.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Seller,
            Seller: Seller,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 1,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.Buyout,
            StartPrice: 0,
            BuyoutPrice: 5,
            DurationSeconds: 60f
        ));
        fixture.Step();

        Assert.Null(@object: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: (FillCount + 1)));

        // Step well past every row's own 3-second (720-tick) retention window — the last cancel landed around tick
        // ~2*FillCount (~512); 800 further ticks clears even that row's ResolvedTick + 720 comfortably.
        for (var index = 0; (index < 800); index++) {
            fixture.Step();
        }

        Assert.True(condition: ((fixture.Server.Definition.Market?.Listings?.Count ?? int.MaxValue) < FillCount), userMessage: "the retention sweep was expected to have archived every terminal row by now");

        // The next listing now fits — proving the archived rows actually freed capacity.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Seller,
            Seller: Seller,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 1,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.Buyout,
            StartPrice: 0,
            BuyoutPrice: 5,
            DurationSeconds: 60f
        ));
        fixture.Step();

        var next = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: (FillCount + 1));

        Assert.NotNull(@object: next);
        // Listing ids stay monotonic — archival never reissues a pruned id.
        Assert.Equal(expected: ((long)(FillCount + 1)), actual: next!.Id);
    }
    [Fact]
    public void ActiveRowsNeverPrune_TheCapStillRefusesEvenPastRetention() {
        // A tiny retention window — if the sweep were (wrongly) pruning active rows, this would expose it fastest.
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(retentionSeconds: 1f));

        for (var id = 1; (id <= FillCount); id++) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
                Principal: Seller,
                Seller: Seller,
                ItemRow: MarketFixtures.AppleRow,
                Quantity: 1,
                CurrencyRow: MarketFixtures.GoldRow,
                Format: WorldMarketFormat.English,
                StartPrice: 5,
                BuyoutPrice: null,
                // The market's own maxDurationSeconds ceiling — never reaches its deadline within this law's window.
                DurationSeconds: 3_600f
            ));
            fixture.Step();
        }

        Assert.Equal(expected: FillCount, actual: (fixture.Server.Definition.Market?.Listings?.Count ?? 0));

        for (var index = 0; (index < 400); index++) {
            fixture.Step();
        }

        // Every row is still active — the sweep must never have touched any of them.
        Assert.Equal(expected: FillCount, actual: (fixture.Server.Definition.Market?.Listings?.Count ?? 0));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Seller,
            Seller: Seller,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 1,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: 60f
        ));
        fixture.Step();

        Assert.Null(@object: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: (FillCount + 1)));
    }
}
