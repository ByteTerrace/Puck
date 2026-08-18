using Xunit;
using Puck.Hosting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

public sealed class WorldAuthorityCheckpointLawTests {
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
    private static WorldOwnedWorlds FreshProfiles(WorldDefinition definition) => new(
        directory: Directory.CreateTempSubdirectory(prefix: "puck-checkpoint-tests-").FullName,
        machineId: Guid.NewGuid(),
        template: definition
    );

    // activation-roundtrip-identity (§3.5): a single-authority, no-adjacency, code-built scenario — a producer-driven
    // census plus one scripted (untouched-live-input) seat. Runs 5000 ticks, captures, restores into a fresh server,
    // then runs both the restored and the uninterrupted server 5000 more ticks with the identical (empty) input
    // stream. PASS = (a) the pose hash agrees every tick and (b) the checkpoint captured from each at the final tick
    // is structurally identical.
    [Fact]
    public void Activation_roundtrip_identity() {
        using var fixture = Fixtures.FreshServer();

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));
        _ = fixture.Server.Population.SetSimulatedCount(count: 3);

        for (var tick = 0; (tick < 5000); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var refusal
        ), userMessage: refusal);
        Assert.NotNull(@object: checkpoint);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: FreshProfiles(definition: definition)
        );

        var uninterruptedElapsed = 0UL;
        var restoredElapsed = 0UL;
        var uninterruptedTick = fixture.Server.NextInputTick;
        var restoredTick = restoredServer.NextInputTick;

        for (var step = 0; (step < 5000); step++) {
            uninterruptedElapsed = checked((uninterruptedElapsed + Fixtures.StepTicks));
            restoredElapsed = checked((restoredElapsed + Fixtures.StepTicks));

            fixture.Server.Step(context: new FixedStepContext(
                ElapsedTicks: uninterruptedElapsed,
                StepTicks: Fixtures.StepTicks,
                Tick: uninterruptedTick
            ));
            restoredServer.Step(context: new FixedStepContext(
                ElapsedTicks: restoredElapsed,
                StepTicks: Fixtures.StepTicks,
                Tick: restoredTick
            ));
            uninterruptedTick++;
            restoredTick++;

            Assert.Equal(
                expected: WorldReplaySnapshot.HashState(population: fixture.Server.Population),
                actual: WorldReplaySnapshot.HashState(population: restoredServer.Population)
            );
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var uninterruptedFinal,
            hostRow: EmptyHostRow(),
            reason: out var uninterruptedRefusal
        ), userMessage: uninterruptedRefusal);
        Assert.True(condition: restoredServer.TryCaptureCheckpoint(
            checkpoint: out var restoredFinal,
            hostRow: EmptyHostRow(),
            reason: out var restoredRefusal
        ), userMessage: restoredRefusal);

        Assert.True(condition: DeepEqual.Compare(
            a: uninterruptedFinal,
            b: restoredFinal
        ));
    }
    // Discriminating control: corrupting one entry's Generation before restore must diverge the restored trajectory
    // from the uninterrupted one — the restore path must actually consult the captured value, not silently ignore it.
    [Fact]
    public void Activation_roundtrip_identity_control_corrupted_generation_reads_red() {
        using var fixture = Fixtures.FreshServer();

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));

        for (var tick = 0; (tick < 100); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out _
        ));

        var entries = checkpoint!.Population.Entries;
        var corruptedEntry = entries[0] with { Generation = (entries[0].Generation + 1000) };
        var corrupted = checkpoint with {
            Population = checkpoint.Population with {
                Entries = [corruptedEntry, .. entries.Skip(count: 1)],
            },
        };

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.DefinitionJson);
        using var machines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );

        var (restoredServer, restoredPopulation) = WorldServer.FromCheckpoint(
            checkpoint: corrupted,
            instanceIdentity: "boot",
            machines: machines,
            profiles: FreshProfiles(definition: definition)
        );

        Assert.NotEqual(
            expected: entries[0].Generation,
            actual: restoredPopulation.Generation(index: entries[0].Index)
        );
    }
}
