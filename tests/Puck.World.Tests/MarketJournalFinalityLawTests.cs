using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves market submissions are final economic commitments in the mutation journal: ordinary authoring
/// edits after one remain undoable, while an undo that would cross the market entry refuses without moving the
/// document. This is journal finality, not a claim that an operator cannot explicitly load a different world.</summary>
public sealed class MarketJournalFinalityLawTests {
    [Fact]
    public void UndoMayDropLaterAuthoringEdit_ButCannotCrossListingCommitment() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());
        var seller = WorldPrincipal.Seat(slot: 0);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: null,
            CurrencyRow: MarketFixtures.GoldRow,
            DurationSeconds: MarketFixtures.MinDurationSeconds,
            Format: WorldMarketFormat.English,
            ItemRow: MarketFixtures.AppleRow,
            Principal: seller,
            Quantity: 1,
            Seller: seller,
            StartPrice: 10
        ));
        fixture.Step();

        Assert.Equal(expected: 1, actual: fixture.Server.JournalLength);
        Assert.Equal(expected: 0, actual: fixture.Server.UndoableJournalLength);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: MarketFixtures.GoldRow,
            Key: "0",
            Value: 17,
            Kind: WorldDocumentWriteKind.Add
        ));
        fixture.Step();

        Assert.Equal(expected: 2, actual: fixture.Server.JournalLength);
        Assert.Equal(expected: 1, actual: fixture.Server.UndoableJournalLength);

        fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
        fixture.Step();

        Assert.Equal(expected: 1, actual: fixture.Server.JournalLength);
        Assert.Equal(expected: 0, actual: fixture.Server.UndoableJournalLength);
        Assert.Equal(
            expected: MarketFixtures.SellerStartingGold,
            actual: MarketFixtures.CellValueOf(fixture.Server.Definition, MarketFixtures.GoldRow, seller)
        );
        Assert.NotNull(@object: MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1));
        Assert.Equal(
            expected: (MarketFixtures.SellerStartingApples - 1),
            actual: MarketFixtures.CellValueOf(fixture.Server.Definition, MarketFixtures.AppleRow, seller)
        );

        var beforeRefusedUndo = fixture.DefinitionBytes();

        fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
        fixture.Step();

        Assert.Equal(expected: beforeRefusedUndo, actual: fixture.DefinitionBytes());
    }
    [Fact]
    public void UndoCannotReverseASettledBuyout() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());
        var seller = WorldPrincipal.Seat(slot: 0);
        var buyer = WorldPrincipal.Seat(slot: 1);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: 40,
            CurrencyRow: MarketFixtures.GoldRow,
            DurationSeconds: MarketFixtures.MinDurationSeconds,
            Format: WorldMarketFormat.Buyout,
            ItemRow: MarketFixtures.AppleRow,
            Principal: seller,
            Quantity: 2,
            Seller: seller,
            StartPrice: 0
        ));
        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.BuyoutMarketListing(
            Buyer: buyer,
            ListingId: 1,
            Principal: buyer
        ));
        fixture.Step();

        var settled = fixture.DefinitionBytes();

        fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
        fixture.Step();

        Assert.Equal(expected: settled, actual: fixture.DefinitionBytes());
        Assert.Equal(
            expected: 2L,
            actual: MarketFixtures.CellValueOf(fixture.Server.Definition, MarketFixtures.AppleRow, buyer)
        );
        Assert.Equal(
            expected: (MarketFixtures.BidderStartingGold - 40),
            actual: MarketFixtures.CellValueOf(fixture.Server.Definition, MarketFixtures.GoldRow, buyer)
        );
    }
}
