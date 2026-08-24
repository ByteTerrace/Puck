using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves an explicit write against a cell carrying a <see cref="WorldStateDynamics"/> easing trait
/// rebases the trait to the applying tick (<c>Server.WorldServer.RebaseCellTraits</c>) rather than replacing it
/// wholesale: the follower keeps chasing from wherever it actually was, receives a velocity kick signed by the
/// referenced <c>dynamics</c> row's own <c>r</c>, and <c>world.undo</c>/a market touch rebase the same way.</summary>
public sealed class StateDynamicsRebaseLawTests {
    private static readonly WorldPrincipal Actor = WorldPrincipal.Seat(slot: 0);
    private static readonly WorldDynamicsRow KickPositive = new(Name: "kickPos", Frequency: 1f, Damping: 1f, Response: 1f);
    private static readonly WorldDynamicsRow KickZero = new(Name: "kickZero", Frequency: 1f, Damping: 1f, Response: 0f);
    private static readonly WorldDynamicsRow KickNegative = new(Name: "kickNeg", Frequency: 1f, Damping: 1f, Response: -1f);

    private static WorldDefinition BuildDocument(string dynamicsRow) {
        var row = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "gauge"),
            Kind: CellKind.Int,
            Capacity: 8,
            Cells: [
                new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: 0, Dynamics: new WorldStateDynamics(Row: dynamicsRow, Y0: 0, V0: 0, EpochTick: 0)),
            ]
        );

        return (Fixtures.BuildDocument().WithWorldState(rows: [row]) with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, KickPositive, KickZero, KickNegative],
        });
    }
    private static WorldStateDynamics ReadTrait(WorldDefinition definition) {
        var row = WorldDefinitionRows.FindStateRow(rows: definition.State, name: "gauge")!;

        foreach (var cell in row.Cells!) {
            if (string.Equals(a: cell.Key.Value, b: "0", comparisonType: System.StringComparison.Ordinal)) {
                return cell.Dynamics!;
            }
        }

        throw new System.InvalidOperationException(message: "cell '0' not found");
    }

    [InlineData("kickPos", 1)]
    [InlineData("kickZero", 0)]
    [InlineData("kickNeg", -1)]
    [Theory]
    public void UpsertStateCell_FromRest_KicksTheVelocitySignedByTheDynamicsRowsResponse(string dynamicsRow, int expectedSign) {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(dynamicsRow: dynamicsRow));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 300, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        // A mutation submitted before a Step() call composes against the tick counter AS IT STOOD before that
        // call, then the call completes and advances it — so the tick a just-applied write rebased at trails
        // NextInputTick by two, not one (one Step call: composes at 0, NextInputTick reads 2 afterward).
        var appliedTick = (fixture.Server.NextInputTick - 2UL);
        var trait = ReadTrait(definition: fixture.Server.Definition);

        // The cell was already at rest at its OLD target (0), so the eased sample the rebase captures is exactly
        // (0, 0) before the kick — the whole velocity is the retarget impulse.
        Assert.Equal(actual: trait.Y0, expected: 0L);
        Assert.Equal(actual: trait.EpochTick, expected: unchecked((long)appliedTick));
        Assert.Equal(actual: System.Math.Sign(value: trait.V0), expected: expectedSign);

        // Truth moved to exactly what was written — the trait rebases, it never overrides the write.
        Assert.True(condition: WorldStateReader.TryRead(definition: fixture.Server.Definition, key: "0", rawValue: out var truth, row: out _, rowName: "gauge", text: out _, tick: appliedTick));
        Assert.Equal(actual: truth, expected: 300L);
    }
    [Fact]
    public void MidFlightRewrite_RebasesFromTheLiveEasedPositionRatherThanEitherEndpoint_WithNoJumpAtTheRebaseTick() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(dynamicsRow: "kickZero"));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 300, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        // 24 further ticks (0.1s at 240 Hz) — well inside the ~1.9s settle horizon for f=1 Hz, ζ=1 — so the second
        // write below rebases from a GENUINELY mid-flight position, never a value already pinned to an endpoint.
        for (var index = 0; (index < 24); index++) {
            fixture.Step();
        }

        var beforeRewriteTick = (fixture.Server.NextInputTick - 1UL);

        Assert.True(condition: WorldStateReader.TryReadEased(definition: fixture.Server.Definition, key: "0", rawValue: out var midFlight, row: out _, rowName: "gauge", text: out _, tick: beforeRewriteTick));
        Assert.InRange(actual: midFlight!.Value, low: 1L, high: 299L);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 600, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var rewriteTick = (fixture.Server.NextInputTick - 2UL);
        var trait = ReadTrait(definition: fixture.Server.Definition);

        // The rebased Y0 is the SAME live eased value just sampled above (bit-exact, since no tick elapsed between
        // the sample and the write applying) — a genuine capture of where the follower actually was, never the old
        // truth (300) nor the new one (600).
        Assert.Equal(actual: trait.Y0, expected: midFlight);
        Assert.Equal(actual: trait.EpochTick, expected: unchecked((long)rewriteTick));

        // Continuity: reading right back at the rebase tick reports EXACTLY Y0 — the write never jumps the follower.
        Assert.True(condition: WorldStateReader.TryReadEased(definition: fixture.Server.Definition, key: "0", rawValue: out var atRebase, row: out _, rowName: "gauge", text: out _, tick: rewriteTick));
        Assert.Equal(actual: atRebase, expected: trait.Y0);

        // Chasing the NEW target: read far in the future (the closed form needs no further Step() calls) and
        // confirm convergence to 600, never back to the old truth of 300.
        Assert.True(condition: WorldStateReader.TryReadEased(definition: fixture.Server.Definition, key: "0", rawValue: out var settled, row: out _, rowName: "gauge", text: out _, tick: (rewriteTick + 10_000UL)));
        Assert.Equal(actual: settled, expected: 600L);
    }
    [Fact]
    public void Undo_RestoresTheRebasedTraitBitExactly() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument(dynamicsRow: "kickPos"));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 300, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var afterFirstWrite = ReadTrait(definition: fixture.Server.Definition);

        for (var index = 0; (index < 24); index++) {
            fixture.Step();
        }

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Actor, Row: "gauge", Key: "0", Value: 900, Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();

        var afterSecondWrite = ReadTrait(definition: fixture.Server.Definition);

        Assert.NotEqual(actual: afterSecondWrite, expected: afterFirstWrite); // the control: the second write genuinely rebased.

        fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
        fixture.Step();

        var afterUndo = ReadTrait(definition: fixture.Server.Definition);

        Assert.Equal(actual: afterUndo, expected: afterFirstWrite);
    }
    [Fact]
    public void PlaceMarketBid_RebasesTheSpentCellsDynamicsTrait() {
        var gold = new WorldStateRow(
            Name: MarketFixtures.GoldRow,
            Kind: CellKind.Int,
            Capacity: 128,
            NonNegative: true,
            Cells: [
                new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: MarketFixtures.SellerStartingGold),
                new WorldStateCell(
                    Key: WorldCellName.Parse(candidate: "1"),
                    Value: MarketFixtures.BidderStartingGold,
                    Dynamics: new WorldStateDynamics(Row: "kickZero", Y0: MarketFixtures.BidderStartingGold, V0: 0, EpochTick: 0)
                ),
                new WorldStateCell(Key: WorldCellName.Parse(candidate: "2"), Value: MarketFixtures.BidderStartingGold),
            ]
        );
        var apple = new WorldStateRow(
            Name: MarketFixtures.AppleRow,
            Kind: CellKind.Int,
            Capacity: 128,
            NonNegative: true,
            Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "0"), Value: MarketFixtures.SellerStartingApples)]
        );
        var market = new WorldMarketSection(
            Formats: [WorldMarketFormat.English, WorldMarketFormat.Buyout],
            FeeBasisPoints: MarketFixtures.FeeBasisPoints,
            MinDurationSeconds: MarketFixtures.MinDurationSeconds,
            MaxDurationSeconds: MarketFixtures.MaxDurationSeconds
        );
        var definition = (Fixtures.BuildDocument().WithWorldState(rows: [gold, apple]) with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, KickZero],
            Market = market,
        });
        using var fixture = Fixtures.FreshServer(definition: definition);
        var seller = WorldPrincipal.Seat(slot: 0);
        var bidder = WorldPrincipal.Seat(slot: 1);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: null, CurrencyRow: MarketFixtures.GoldRow, DurationSeconds: MarketFixtures.MinDurationSeconds,
            Format: WorldMarketFormat.English, ItemRow: MarketFixtures.AppleRow, Principal: seller, Quantity: 1,
            Seller: seller, StartPrice: 5
        ));
        fixture.Step();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(Amount: 10, Bidder: bidder, ListingId: 1, Principal: bidder));
        fixture.Step();

        var appliedTick = (fixture.Server.NextInputTick - 2UL);
        var goldRow = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: MarketFixtures.GoldRow)!;
        WorldStateDynamics? trait = null;

        foreach (var cell in goldRow.Cells!) {
            if (string.Equals(a: cell.Key.Value, b: "1", comparisonType: System.StringComparison.Ordinal)) {
                trait = cell.Dynamics;
            }
        }

        Assert.NotNull(@object: trait);
        Assert.Equal(actual: trait!.EpochTick, expected: unchecked((long)appliedTick));
        // The trait's own Y0/V0 rebase through the SAME closed form the read side uses — since the cell was already
        // AT REST at its old truth (500) with a zero-response (r=0) row, the captured sample is unchanged by the
        // write and the retarget kick is zero, so the follower keeps sitting at 500 for this one instant even
        // though truth just moved to 490 — it is truth, never the trait, that reflects the spend immediately.
        Assert.Equal(actual: trait.Y0, expected: MarketFixtures.BidderStartingGold);
        Assert.Equal(actual: trait.V0, expected: 0L);
        Assert.True(condition: WorldStateReader.TryRead(definition: fixture.Server.Definition, key: "1", rawValue: out var truth, row: out _, rowName: MarketFixtures.GoldRow.Value, text: out _, tick: appliedTick));
        Assert.Equal(actual: truth, expected: (MarketFixtures.BidderStartingGold - 10));

        // The follower then eases from 500 toward the NEW target 490 — read far in the future (the closed form
        // needs no further Step() calls) to prove it actually chases the truth rather than sitting at 500 forever.
        Assert.True(condition: WorldStateReader.TryReadEased(definition: fixture.Server.Definition, key: "1", rawValue: out var settled, row: out _, rowName: MarketFixtures.GoldRow.Value, text: out _, tick: (appliedTick + 10_000UL)));
        Assert.Equal(actual: settled, expected: (MarketFixtures.BidderStartingGold - 10));
    }
}
