using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves a standing bidder RAISING THEIR OWN BID nets against their own standing escrow — one balance
/// read, one write, delta-charged — rather than being charged the FULL new amount and then "refunded" the old bid
/// through a second read of the very cell this compose pass just wrote (which would see the cell's pre-rebase
/// Advance/EpochTick — <c>Server.WorldServer.RebaseAdvanceEpoch</c> runs AFTER <c>TryCompose</c> — re-applying
/// elapsed accrual <c>WorldStateReader.TryRead</c> already folded into the first read). Two red-first cases: the
/// delta-affordability gap (a bidder who can afford the RAISE but not the full new amount was wrongly refused) and
/// the advancing-cell double-count (an advancing currency cell's elapsed accrual applied twice, minting money out of
/// a self-raise).</summary>
public sealed class MarketSelfRaiseBidLawTests {
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);
    private static readonly WorldPrincipal Bidder = WorldPrincipal.Seat(slot: 1);
    private static readonly WorldPrincipal ThirdParty = WorldPrincipal.Seat(slot: 2);

    [Fact]
    public void RaiseOwnBid_DeltaAffordable_SucceedsAndChargesOnlyTheDifference() {
        // The bidder's liquid balance (15, after the first bid escrows 100 out of a starting 115) cannot cover the
        // new bid's FULL amount (110) — only the ADDITIONAL 10 over their own standing escrow. The pre-fix full-
        // amount check wrongly refused this exact shape.
        var document = BuildDocument(bidderStartingGold: 115);
        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Seller, Seller: Seller, ItemRow: MarketFixtures.AppleRow, Quantity: 1, CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English, StartPrice: 50, BuyoutPrice: null, DurationSeconds: MarketFixtures.MaxDurationSeconds
        ));
        fixture.Step();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: Bidder, Bidder: Bidder, ListingId: 1, Amount: 100));
        fixture.Step();

        Assert.Equal(expected: 15L, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Bidder));

        // The self-raise: pre-fix, this required the bidder to hold the FULL 110 liquid and refused at 15.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: Bidder, Bidder: Bidder, ListingId: 1, Amount: 110));
        fixture.Step();

        var listing = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!;

        Assert.Equal(expected: 110L, actual: listing.CurrentBid);
        Assert.Equal(expected: Bidder, actual: listing.CurrentBidder);
        Assert.Equal(expected: 5L, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Bidder));

        // Control: a genuine outbid from a DIFFERENT party still refunds the self-raised bidder in FULL (their
        // whole 110 escrow, not just the delta) — proving the netting is scoped to a true self-raise, never leaking
        // into the ordinary cross-bidder refund path.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: ThirdParty, Bidder: ThirdParty, ListingId: 1, Amount: 200));
        fixture.Step();

        Assert.Equal(expected: 115L, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Bidder));
        Assert.Equal(expected: (MarketFixtures.BidderStartingGold - 200), actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty));
    }
    // Gold's holder-"1" cell (the Bidder fixture) carries an advancing base of 1000 at rate 1/tick from epoch 0 —
    // the same shape MarketAdvanceCellRebaseLawTests uses, so a self-raise against an advancing cell exercises the
    // SAME rebase machinery a first bid already proves, but through the compose-time double-read this law targets.
    [Fact]
    public void RaiseOwnBid_AgainstAnAdvancingCell_DoesNotDoubleCountElapsedAccrual() {
        var baseDocument = MarketFixtures.BuildDocument();
        var goldRow = baseDocument.State.First(predicate: row => (row.Name == MarketFixtures.GoldRow));
        var advancingCells = goldRow.Cells!.Select(selector: cell => (string.Equals(a: cell.Key.Value, b: "1", comparisonType: StringComparison.Ordinal)
            ? (cell with { Value = 1000, Advance = new WorldStateAdvance(RateNumerator: 1, RateDenominator: 1, EpochTick: 0) })
            : cell)).ToList();
        var advancingGoldRow = (goldRow with { Cells = advancingCells });
        var otherRows = baseDocument.State.Where(predicate: row => (row.Name != MarketFixtures.GoldRow)).ToList();
        var document = (baseDocument with { State = [advancingGoldRow, .. otherRows] });

        using var fixture = Fixtures.FreshServer(definition: document);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: Seller, Seller: Seller, ItemRow: MarketFixtures.AppleRow, Quantity: 1, CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English, StartPrice: 100, BuyoutPrice: null, DurationSeconds: MarketFixtures.MaxDurationSeconds
        ));
        fixture.Step(); // tick 0

        // 49 filler steps land the next call (the bid) at tick 50 — the same "Nth call applies at tick N-1" cadence
        // MarketAdvanceCellRebaseLawTests establishes.
        for (var index = 0; (index < 49); index++) {
            fixture.Step();
        }

        // Bid1 applies at tick 50: reads 1000 + 50 = 1050, escrows 300, installs base 750 (rebased to epoch 50).
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: Bidder, Bidder: Bidder, ListingId: 1, Amount: 300));
        fixture.Step(); // tick 50

        for (var index = 0; (index < 39); index++) {
            fixture.Step();
        }

        // Bid2 (self-raise) applies at tick 90: reads 750 + 1*(90-50) = 790. The correct net charge is (450-300) =
        // 150, installing base 640 (rebased to epoch 90). The bug instead re-reads the just-written cell through its
        // STALE epoch-50 advance, re-applying the same 40-tick span, landing at 680 — a free 40 gold conjured by a
        // bidder raising their own bid.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Principal: Bidder, Bidder: Bidder, ListingId: 1, Amount: 450));
        fixture.Step(); // tick 90

        Assert.True(condition: WorldStateReader.TryRead(definition: fixture.Server.Definition, rowName: MarketFixtures.GoldRow.Value, key: "1", tick: 90UL, row: out _, rawValue: out var atTick90, text: out _));
        Assert.Equal(expected: 640L, actual: atTick90);

        // One further tick's worth of accrual applies correctly on top of the rebased base — rules out "the fix
        // just clamped the value" rather than genuinely rebasing the epoch to 90.
        Assert.True(condition: WorldStateReader.TryRead(definition: fixture.Server.Definition, rowName: MarketFixtures.GoldRow.Value, key: "1", tick: 91UL, row: out _, rawValue: out var oneTickLater, text: out _));
        Assert.Equal(expected: 641L, actual: oneTickLater);
    }
    [Fact]
    public void AMaximumStandingBid_HasNoWrappedSuccessor() {
        // Market prices are long-shaped independently of state-cell balances, so an authored/resumed ledger may
        // legally carry the carrier maximum even though no one can accumulate that amount through an int state row.
        var baseDocument = BuildDocument(bidderStartingGold: MarketFixtures.BidderStartingGold);
        var maximumListing = new WorldMarketListing(
            Id: 1,
            Seller: Seller,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 1,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 50,
            BuyoutPrice: null,
            DeadlineTick: 1_000,
            CurrentBid: long.MaxValue,
            CurrentBidder: Bidder
        );
        var document = (baseDocument with {
            Market = (baseDocument.Market! with { Listings = [maximumListing], NextListingId = 2 }),
        });
        using var fixture = Fixtures.FreshServer(definition: document);

        var standingBidderBalance = MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Bidder);
        var challengerBalance = MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty);

        // Before the guard, CurrentBid + 1 wrapped to long.MinValue. This bid of one then replaced the standing
        // maximum and refunded its escrow, violating the English auction's monotonicity.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(
            Principal: ThirdParty, Bidder: ThirdParty, ListingId: 1, Amount: 1
        ));
        fixture.Step();

        var listing = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1)!;

        Assert.Equal(expected: long.MaxValue, actual: listing.CurrentBid);
        Assert.Equal(expected: Bidder, actual: listing.CurrentBidder);
        Assert.Equal(expected: standingBidderBalance, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: Bidder));
        Assert.Equal(expected: challengerBalance, actual: MarketFixtures.CellValueOf(definition: fixture.Server.Definition, row: MarketFixtures.GoldRow, principal: ThirdParty));
    }

    private static WorldDefinition BuildDocument(long bidderStartingGold) {
        var gold = new WorldStateRow(Name: MarketFixtures.GoldRow, Kind: CellKind.Int, Capacity: 128, NonNegative: true, Cells: [
            new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: MarketFixtures.SellerStartingGold),
            new WorldStateCell(Key: WorldCellName.Parse(candidate: "1"), Value: bidderStartingGold),
            new WorldStateCell(Key: WorldCellName.Parse(candidate: "2"), Value: MarketFixtures.BidderStartingGold),
        ]);
        var apple = new WorldStateRow(Name: MarketFixtures.AppleRow, Kind: CellKind.Int, Capacity: 128, NonNegative: true, Cells: [
            new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: MarketFixtures.SellerStartingApples),
        ]);
        var market = new WorldMarketSection(
            Formats: [WorldMarketFormat.English, WorldMarketFormat.Buyout],
            FeeBasisPoints: MarketFixtures.FeeBasisPoints,
            MinDurationSeconds: MarketFixtures.MinDurationSeconds,
            MaxDurationSeconds: MarketFixtures.MaxDurationSeconds
        );

        return (Fixtures.BuildDocument() with { State = [gold, apple], Market = market });
    }
}
