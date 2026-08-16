using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>Proves a market fact cell keys a <see cref="PrincipalKind.Peer"/> holder on its full (index, generation)
/// pair, never the index alone — <see cref="WorldPopulationLimits"/> recycles a vacated population slot for a later,
/// unrelated connection, so keying on index alone would let generation 2 read (and spend) generation 1's balance.
/// Every mutating verb here is submitted under <see cref="WorldPrincipal.Console"/> — the party-authority law
/// (<see cref="MarketPartyAuthorityLawTests"/>) is proved elsewhere; this suite isolates the key-derivation
/// question alone.</summary>
public sealed class MarketPeerGenerationIsolationLawTests {
    private static readonly WorldPrincipal PeerGeneration1 = WorldPrincipal.Peer(index: 4, generation: 1);
    private static readonly WorldPrincipal PeerGeneration2 = WorldPrincipal.Peer(index: 4, generation: 2);

    // Seeds the same 0-based population index (4) with two independent apple balances, keyed the way the fixed
    // TryPlayerCellKey addresses a peer ("<index>_<generation>") — the fixture asserts the isolation the production
    // code is responsible for producing, so it must seed data a broken index-only key derivation could still
    // corrupt (both cells share index 4) rather than data that happens to differ only by chance.
    private static WorldDefinition BuildDocument() {
        var baseDocument = MarketFixtures.BuildDocument();
        var appleRow = baseDocument.State.First(predicate: row => (row.Name == MarketFixtures.AppleRow));
        var seededCells = new List<WorldStateCell>(collection: appleRow.Cells!) {
            new(Key: WorldCellName.Parse(candidate: "4_1"), Value: 10),
            new(Key: WorldCellName.Parse(candidate: "4_2"), Value: 0),
        };
        var seededAppleRow = (appleRow with { Cells = seededCells });
        var otherRows = baseDocument.State.Where(predicate: row => (row.Name != MarketFixtures.AppleRow)).ToList();

        return (baseDocument with { State = [seededAppleRow, .. otherRows] });
    }
    private static long AppleCellOf(WorldDefinition definition, WorldPrincipal peer) {
        var row = WorldDefinitionRows.FindStateRow(rows: definition.State, name: MarketFixtures.AppleRow)!;
        var key = $"{peer.Index}_{peer.Generation}";

        foreach (var cell in row.Cells!) {
            if (string.Equals(a: cell.Key.Value, b: key, comparisonType: StringComparison.Ordinal)) {
                return cell.Value;
            }
        }

        return 0L;
    }

    [Fact]
    public void RecycledSlot_NewGenerationSeesZeroBalance_NotThePriorOccupants() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());

        // Generation 1 lists 4 of its 10 apples — escrows out of the "4_1" cell alone.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: WorldPrincipal.Console,
            Seller: PeerGeneration1,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 4,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        Assert.Equal(expected: 6L, actual: AppleCellOf(definition: fixture.Server.Definition, peer: PeerGeneration1));

        // Generation 2 — a later, unrelated occupant of the same population slot — tries to list 1 apple. It must be
        // refused: its own cell ("4_2") is still zero, never generation 1's remaining 6.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: WorldPrincipal.Console,
            Seller: PeerGeneration2,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 1,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        Assert.Null(@object: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 2));
        Assert.Equal(expected: 0L, actual: AppleCellOf(definition: fixture.Server.Definition, peer: PeerGeneration2));
        // Generation 1's remaining balance is untouched by generation 2's refused attempt.
        Assert.Equal(expected: 6L, actual: AppleCellOf(definition: fixture.Server.Definition, peer: PeerGeneration1));
    }
    [Fact]
    public void SameGeneration_ContinuesItsOwnBalanceAcrossMutations() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: WorldPrincipal.Console,
            Seller: PeerGeneration1,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 4,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        Assert.Equal(expected: 6L, actual: AppleCellOf(definition: fixture.Server.Definition, peer: PeerGeneration1));

        // The same generation lists again — continuity, not a reset: it draws from its own already-debited balance.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: WorldPrincipal.Console,
            Seller: PeerGeneration1,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 2,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 5,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();

        Assert.NotNull(@object: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 2));
        Assert.Equal(expected: 4L, actual: AppleCellOf(definition: fixture.Server.Definition, peer: PeerGeneration1));
    }
}
