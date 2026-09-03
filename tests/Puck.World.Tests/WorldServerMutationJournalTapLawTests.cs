using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Proves <see cref="WorldServer.MutationJournalTap"/> fires with the exact tick/mutation
/// <see cref="WorldServer.TryApplyJournalTailMutation"/> reapplies — a hosted row's live-journaling seam
/// (<c>Puck.World.Silo</c>'s <c>WorldSiloHost.ScheduleJournalAppend</c>), proved at this layer without a store or a
/// silo: a fresh, uncheckpointed server fed only the tapped-and-reencoded entry reaches the tapped mutation's own
/// effect — the "mutate, kill before any checkpoint, restart, the mutation survives" claim.</summary>
public sealed class WorldServerMutationJournalTapLawTests {
    private static readonly WorldPrincipal Seller = WorldPrincipal.Seat(slot: 0);

    private static WorldOwnedWorlds FreshProfiles(WorldDefinition definition) => new(
        directory: Directory.CreateTempSubdirectory(prefix: "puck-journal-tap-tests-").FullName,
        machineId: Guid.NewGuid(),
        template: definition
    );

    [Fact]
    public void MutationJournalTap_DoesNotFireForARejectedMutation() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());
        var fired = 0;

        fixture.Server.MutationJournalTap = (_, _) => fired++;

        // A nonzero startPrice on a Buyout listing is refused by the compose-time gate before the mutation ever
        // reaches the journal (MarketBuyoutStartPriceLawTests' own control) — the tap must not fire for it.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: 50, CurrencyRow: MarketFixtures.GoldRow, DurationSeconds: MarketFixtures.MinDurationSeconds, Format: WorldMarketFormat.Buyout, ItemRow: MarketFixtures.AppleRow,
            Principal: Seller, Quantity: 1, Seller: Seller, StartPrice: 25
        ));
        fixture.Step();

        Assert.Equal(
            actual: fired,
            expected: 0
        );
    }
    [Fact]
    public void MutationJournalTap_FiresOnApply_AndItsReEncodedEntryReplaysToTheSameEffect() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());
        (ulong Tick, byte[] Encoded)? captured = null;

        fixture.Server.MutationJournalTap = (tick, mutation) => {
            Assert.Null(@object: captured);
            Assert.True(
                condition: WorldSubmissionCodec.TryEncodeCommittedMutation(
                bytes: out var encoded,
                failure: out var failure,
                mutation: mutation
            ),
                userMessage: failure.ToString()
            );
            captured = (tick, encoded);
        };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            BuyoutPrice: 50, CurrencyRow: MarketFixtures.GoldRow, DurationSeconds: MarketFixtures.MinDurationSeconds, Format: WorldMarketFormat.Buyout, ItemRow: MarketFixtures.AppleRow,
            Principal: Seller, Quantity: 1, Seller: Seller, StartPrice: 0
        ));
        fixture.Step();

        Assert.NotNull(@object: captured);

        var liveListing = MarketFixtures.FindListing(definition: fixture.Server.Definition, id: 1);

        Assert.NotNull(@object: liveListing);

        // A restart replays the tail against a fresh server built from the SAME (uncheckpointed) base document —
        // no checkpoint exists yet, so this is the whole recovered state.
        var definition = MarketFixtures.BuildDocument();
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );
        var restoredServer = new WorldServer(
            definition: definition,
            envelope: new WorldRenderEnvelope(),
            instanceIdentity: "restored",
            machines: restoredMachines,
            population: new WorldPopulation(definition: definition),
            profiles: FreshProfiles(definition: definition)
        );

        Assert.True(condition: WorldSubmissionCodec.TryDecodeCommittedMutation(
            bytes: captured!.Value.Encoded,
            failure: out var decodeFailure,
            mutation: out var decoded
        ), userMessage: decodeFailure.ToString());
        Assert.True(condition: restoredServer.TryApplyJournalTailMutation(
            mutation: decoded!,
            tick: captured.Value.Tick
        ));

        var restoredListing = MarketFixtures.FindListing(definition: restoredServer.Definition, id: 1);

        Assert.NotNull(@object: restoredListing);
        Assert.Equal(
            expected: liveListing!.StartPrice,
            actual: restoredListing!.StartPrice
        );
        Assert.Equal(
            expected: liveListing.BuyoutPrice,
            actual: restoredListing.BuyoutPrice
        );
    }
}
