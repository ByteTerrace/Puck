using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves a market write against an advancing keyed cell (<see cref="WorldStateCell.Advance"/>) rebases the
/// cell's <see cref="WorldStateAdvance.EpochTick"/> to the applying tick — exactly like an explicit
/// <see cref="WorldMutation.UpsertStateCell"/> write already does (<c>Server.WorldServer.RebaseCellTraits</c>).
/// Without the rebase, <c>WriteMarketCell</c> preserves the pre-write epoch verbatim while installing a base that
/// already has the elapsed accrual baked in, so the very next read applies the same elapsed span a second time.
/// The exact scenario: base 10, rate 1/tick, epoch 0 — reads 110 at tick 100; a bid spends 10 out of it (installing
/// base 100); the bug reads 200 immediately afterward (100 + 1*(100-0)); the fix reads 100 (100 + 1*(100-100)).</summary>
public sealed class MarketAdvanceCellRebaseLawTests {
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);
    private static readonly WorldPrincipal Bidder = WorldPrincipal.Seat(slot: 1);

    // Gold's holder-"1" cell (the Bidder fixture) carries an advancing base of 10 at rate 1/tick from epoch 0 — at
    // tick 100 it reads 110 (10 + 1*100), matching the review's own 10/1/0 scenario exactly.
    private static WorldDefinition BuildDocument() {
        var baseDocument = MarketFixtures.BuildDocument();
        var goldRow = baseDocument.State.First(predicate: row => (row.Name == MarketFixtures.GoldRow));
        var advancingCells = goldRow.Cells!.Select(selector: cell => (string.Equals(a: cell.Key.Value, b: "1", comparisonType: StringComparison.Ordinal)
            ? (cell with { Value = 10, Advance = new WorldStateAdvance(EpochTick: 0, RateDenominator: 1, RateNumerator: 1) })
            : cell)).ToList();
        var advancingGoldRow = (goldRow with { Cells = advancingCells });
        var otherRows = baseDocument.State.Where(predicate: row => (row.Name != MarketFixtures.GoldRow)).ToList();

        return baseDocument.WithWorldState(rows: [advancingGoldRow, .. otherRows]);
    }

    [Fact]
    public void PlaceMarketBid_RebasesTheSpentCellsEpoch_SoTheSameTickReadIsNotDoubled() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: null,
            CurrencyRow: MarketFixtures.GoldRow,
            DurationSeconds: MarketFixtures.MinDurationSeconds,
            Format: WorldMarketFormat.English,
            ItemRow: MarketFixtures.AppleRow,
            Principal: Seller,
            Quantity: 1,
            Seller: Seller,
            StartPrice: 5
        ));
        fixture.Step();

        // Advance to tick 100 (the listing's own creation already consumed tick 0, so 99 further Step() calls land
        // the next one at tick 100 — a Step() call applies against, then completes, the current tick counter, so
        // the Nth call applies at tick N-1), then bid at tick 100: the read that composes the bid sees 10 + 1*100 = 110.
        for (var index = 0; (index < 99); index++) {
            fixture.Step();
        }

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Amount: 10, Bidder: Bidder, ListingId: 1, Principal: Bidder));
        fixture.Step();

        // The bid applied at tick 100: base 110 - 10 = 100. Reading the same cell back at tick 100 (no further
        // elapsed ticks) must show exactly 100 — the bug shows 200 (100 + 1*(100-0), the un-rebased epoch's elapsed
        // span applying a second time on top of a base that already reflects it).
        Assert.True(condition: WorldStateReader.TryRead(definition: fixture.Server.Definition, rowName: MarketFixtures.GoldRow.Value, key: "1", tick: 100UL, row: out _, rawValue: out var computed, text: out _));
        Assert.Equal(actual: computed, expected: 100L);

        // A further tick's worth of elapsed accrual (tick 101) still applies correctly on top of the rebased base —
        // this control is what rules out "the fix just clamped the value" rather than genuinely rebasing the epoch.
        Assert.True(condition: WorldStateReader.TryRead(definition: fixture.Server.Definition, rowName: MarketFixtures.GoldRow.Value, key: "1", tick: 101UL, row: out _, rawValue: out var oneTickLater, text: out _));
        Assert.Equal(actual: oneTickLater, expected: 101L);
    }
}
