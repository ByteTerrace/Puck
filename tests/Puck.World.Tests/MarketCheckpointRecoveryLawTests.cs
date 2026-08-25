using Puck.Hosting;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves an in-flight auction survives the full authority checkpoint codec and resumes to the same
/// settlement as the uninterrupted authority. The final undo probe also proves the market journal-finality marker
/// is reconstructed from the restored mutation journal rather than held in uncaptured side bookkeeping.</summary>
public sealed class MarketCheckpointRecoveryLawTests {
    [Fact]
    public void InFlightAuction_CodecRestore_ContinuesAndSettlesBitIdentically() {
        using var fixture = Fixtures.FreshServer(definition: MarketFixtures.BuildDocument());
        var seller = WorldPrincipal.Seat(slot: 0);
        var bidder = WorldPrincipal.Seat(slot: 1);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.CreateMarketListing(
            Principal: seller,
            Seller: seller,
            ItemRow: MarketFixtures.AppleRow,
            Quantity: 3,
            CurrencyRow: MarketFixtures.GoldRow,
            Format: WorldMarketFormat.English,
            StartPrice: 10,
            BuyoutPrice: null,
            DurationSeconds: MarketFixtures.MinDurationSeconds
        ));
        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.PlaceMarketBid(
            Principal: bidder,
            Bidder: bidder,
            ListingId: 1,
            Amount: 30
        ));
        fixture.Step();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var captured,
            hostRow: EmptyHostRow(),
            reason: out var captureReason
        ), userMessage: captureReason);

        var checkpoint = captured!;
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var decodeReason
        ), userMessage: decodeReason);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-market-checkpoint-").FullName;
        var profiles = new WorldOwnedWorlds(template: definition, directory: stateDirectory, machineId: Guid.NewGuid());
        using var machines = new WorldMachineHost(screens: definition.Screens, engines: []);
        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: decoded,
            instanceIdentity: "market-checkpoint",
            machines: machines,
            profiles: profiles
        );

        try {
            var uninterruptedTick = fixture.Server.NextInputTick;
            var restoredTick = restored.NextInputTick;
            var uninterruptedElapsed = checkpoint.Server.LastCompletedEngineTicks;
            var restoredElapsed = decoded.Server.LastCompletedEngineTicks;

            for (var step = 0; (step < 300); step++) {
                uninterruptedElapsed = checked((uninterruptedElapsed + Fixtures.StepTicks));
                restoredElapsed = checked((restoredElapsed + Fixtures.StepTicks));
                fixture.Server.Step(context: new FixedStepContext(
                    ElapsedTicks: uninterruptedElapsed,
                    StepTicks: Fixtures.StepTicks,
                    Tick: uninterruptedTick
                ));
                restored.Step(context: new FixedStepContext(
                    ElapsedTicks: restoredElapsed,
                    StepTicks: Fixtures.StepTicks,
                    Tick: restoredTick
                ));
                uninterruptedTick++;
                restoredTick++;

                Assert.Equal(
                    expected: WorldDefinitionSerialization.Serialize(definition: fixture.Server.Definition),
                    actual: WorldDefinitionSerialization.Serialize(definition: restored.Definition)
                );
            }

            var settled = MarketFixtures.FindListing(definition: restored.Definition, id: 1)!;

            Assert.Equal(expected: WorldMarketListingStatus.Settled, actual: settled.Status);
            Assert.Equal(expected: bidder, actual: settled.CurrentBidder);

            var beforeUndo = WorldDefinitionSerialization.Serialize(definition: restored.Definition);

            restored.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
            restoredElapsed = checked((restoredElapsed + Fixtures.StepTicks));
            restored.Step(context: new FixedStepContext(
                ElapsedTicks: restoredElapsed,
                StepTicks: Fixtures.StepTicks,
                Tick: restoredTick
            ));

            Assert.Equal(
                expected: beforeUndo,
                actual: WorldDefinitionSerialization.Serialize(definition: restored.Definition)
            );
        } finally {
            try {
                Directory.Delete(path: stateDirectory, recursive: true);
            } catch (IOException) {
                // Best-effort scratch cleanup; a slow CI disk may still have a transient handle open.
            }
        }
    }

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );
}
