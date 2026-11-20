using Puck.Commands;
using Puck.Abstractions.Waiting;
using Xunit;
using Puck.Hosting;
using Puck.Testing;
using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

public sealed class WorldAuthorityCheckpointLawTests {
    [Fact]
    public void PendingMachineTickRefusesAuthorityOperationsAndResumesWithoutRepeatingTheTick() {
        using var fixture = Fixtures.FreshServer();
        var baseline = fixture.Server;
        var machines = System.Reflection.DispatchProxy.Create<IWorldMachineHost, NonDurableMachines>();
        var proxy = (NonDurableMachines)machines;
        proxy.Inner = baseline.Machines;
        proxy.Hold = true;
        var server = new WorldServer(baseline.Definition, new WorldPopulation(baseline.Definition),
            baseline.Profiles, new WorldRenderEnvelope(), machines);
        var context = new FixedStepContext(0, Fixtures.StepTicks, Fixtures.StepTicks);
        var published = 0UL;
        Action<ulong> publish = tick => published = tick;

        var first = WorldServerStepShell.TryStep(server, null, publish, in context, HostPacing.OneTickPerFrame,
            default, out var pending);
        Assert.Equal(0UL, first);
        Assert.Equal(0UL, published);
        Assert.Equal(proxy.Wait, pending);
        Assert.Equal(0UL, server.CompletedTick);
        Assert.Contains(proxy.Wait.ToString(), Assert.Throws<InvalidOperationException>(() => server.ReadView(default(CommandContext))).Message);
        Assert.Contains(proxy.Wait.ToString(), Assert.Throws<InvalidOperationException>(() => server.AttachSink(null!)).Message);
        Assert.Contains(proxy.Wait.ToString(), Assert.Throws<InvalidOperationException>(() => server.RestoreCheckpoint(null!)).Message);
        Assert.False(server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out _, out var refusal));
        Assert.Contains(proxy.Wait.ToString(), refusal);

        proxy.Hold = false;
        var final = WorldServerStepShell.TryStep(server, null, publish, in context, HostPacing.OneTickPerFrame,
            WaitDeadline.In(TimeSpan.FromSeconds(10)), out pending);
        Assert.Null(pending);
        Assert.Equal(1UL, final);
        Assert.Equal(1UL, published);
        Assert.Equal(2, proxy.Attempts);
        baseline.Advance(Fixtures.StepTicks);
        Assert.Equal(WorldStateHashComposition.HashAuthoritative(baseline, 1UL), WorldStateHashComposition.HashAuthoritative(server, 1UL));
    }

    [Fact]
    public void AHostWithoutDurableCheckpointsNamesOnlyItsSteppedMachine() {
        using var fixture = Fixtures.FreshServer();
        var machines = System.Reflection.DispatchProxy.Create<IWorldMachineHost, NonDurableMachines>();
        var proxy = (NonDurableMachines)machines;
        proxy.Inner = fixture.Server.Machines;
        var server = new WorldServer(fixture.Server.Definition, new WorldPopulation(fixture.Server.Definition),
            fixture.Server.Profiles, new WorldRenderEnvelope(), machines);
        Assert.True(server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out _, out var reason), reason);
        machines.Advance(Fixtures.StepTicks, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.False(server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out _, out reason));
        Assert.Equal("a checkpoint cannot capture a stepped machine without durable checkpoint support", reason);
    }

    // Exposes the real fixture host through its non-durable interface; Advance latches the unsupported step.
    public class NonDurableMachines : System.Reflection.DispatchProxy {
        public IWorldMachineHost Inner { get; set; } = null!;
        public WaitReason Wait { get; } = new("machines", "held fixture segment");
        public bool Hold { get; set; }
        public int Attempts { get; private set; }
        private bool m_stepped;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? arguments) {
            if (method!.Name == "get_AnyEverPumped") { return m_stepped; }
            if (method.Name == nameof(IWorldMachineHost.Advance)) { m_stepped = true; }
            if (method.Name == nameof(IWorldMachineHost.TryAdvance)) {
                Attempts++;
                if (Hold) { return Wait; }
            }
            return method.Invoke(Inner, arguments);
        }
    }

    [Fact]
    public void MalformedJournalBaseRefusesBeforeReplacingTheLiveDefinition() {
        using var fixture = Fixtures.FreshServer();

        fixture.Step();
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            checkpoint: out var checkpoint, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: checkpoint);
        var before = fixture.Server.Definition;
        var hash = WorldStateHashComposition.HashAuthoritative(fixture.Server, tick: 0UL);
        var malformed = checkpoint with { Server = checkpoint.Server with { BaseDefinitionJson = "{"u8.ToArray() } };

        Assert.Throws<InvalidDataException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: malformed));
        Assert.Same(expected: before, actual: fixture.Server.Definition);
        Assert.Equal(expected: hash, actual: WorldStateHashComposition.HashAuthoritative(fixture.Server, tick: 0UL));
    }
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
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));
        _ = fixture.Server.Population.SetSimulatedCount(count: 3);

        for (var tick = 0; (tick < 625); tick++) {
            fixture.Step();
        }

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var refusal
            ),
            userMessage: refusal
        );
        Assert.NotNull(@object: checkpoint);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );
        using var restoredProfilesDirectory = new TemporaryDirectory(prefix: "puck-checkpoint-tests-");

        var (restoredServer, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: restoredMachines,
            profiles: new WorldOwnedWorlds(directory: restoredProfilesDirectory.RootPath, machineId: Guid.NewGuid(), template: definition)
        );

        var uninterruptedElapsed = 0UL;
        var restoredElapsed = 0UL;
        var uninterruptedTick = fixture.Server.NextInputTick;
        var restoredTick = restoredServer.NextInputTick;

        for (var step = 0; (step < 625); step++) {
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

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var uninterruptedFinal,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var uninterruptedRefusal
            ),
            userMessage: uninterruptedRefusal
        );
        Assert.True(
            condition: restoredServer.TryCaptureCheckpoint(
                checkpoint: out var restoredFinal,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var restoredRefusal
            ),
            userMessage: restoredRefusal
        );

        Assert.True(condition: DeepEqual.Compare(
            a: uninterruptedFinal,
            b: restoredFinal
        ));
    }
    // Discriminating control: an entry generation must agree with the full slot-generation image. Reject a
    // corrupted entry before mutating the authority rather than silently accepting contradictory identities.
    [Fact]
    public void Activation_roundtrip_identity_control_corrupted_generation_reads_red() {
        using var fixture = Fixtures.FreshServer();

        _ = fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));

        for (var tick = 0; (tick < 12); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            reason: out _
        ));

        var entries = checkpoint!.Population.Entries;
        var corruptedEntry = entries[0] with { Admission = entries[0].Admission with { Generation = (entries[0].Admission.Generation + 1000) } };
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
        using var profilesDirectory = new TemporaryDirectory(prefix: "puck-checkpoint-tests-");

        Assert.Throws<InvalidOperationException>(testCode: () => WorldServer.FromCheckpoint(
            checkpoint: corrupted,
            instanceIdentity: "boot",
            machines: machines,
            profiles: new WorldOwnedWorlds(directory: profilesDirectory.RootPath, machineId: Guid.NewGuid(), template: definition)
        ));
    }
    [Fact]
    public void AutonomousCadenceCheckpointRefusesBeforeMutatingAuthority() {
        var source = Fixtures.BuildDocument();
        var definition = source with {
            PopulationRaw = source.Population with {
                CapacityRaw = 5,
                NetworkPlayers = 1,
                DefaultPeerSourceRaw = IntentSource.Producer(name: "roam"),
            },
            KitRowsRaw = [source.Kits[0] with {
                AutonomyRaw = new WorldAutonomyCadence(
                MotionSeconds: 0.1f,
                SteeringSeconds: 0.1f
            ),
            }],
        };
        using var fixture = Fixtures.FreshServer(definition);

        Assert.Equal(
            1,
            fixture.Server.Population.SetSimulatedCount(count: 1)
        );
        fixture.Step();
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                checkpoint: out var captured,
                reason: out var reason
            ),
            userMessage: reason
        );
        var entries = captured!.Population.Entries.ToArray();
        var peer = Array.FindIndex(
            array: entries,
            match: entry => (entry.Index == 4)
        );

        Assert.InRange(
            peer,
            0,
            (entries.Length - 1)
        );
        entries[peer] = entries[peer] with {
            Cadence = new EntryCadenceSection(
                MotionElapsedTicks: 1UL,
                MotionPeriodTicks: 1UL,
                MotionRemainingTicks: 1UL,
                SteeringElapsedTicks: 0UL,
                SteeringPeriodTicks: 0UL,
                SteeringRemainingTicks: 0UL
            ),
            Steering = default,
        };
        var malformed = captured with { Population = captured.Population with { Entries = entries } };
        var beforeHash = WorldStateHashComposition.HashAuthoritative(
            fixture.Server,
            tick: 0UL
        );
        var beforeDefinition = fixture.DefinitionBytes();

        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: malformed));
        Assert.Equal(
            beforeHash,
            WorldStateHashComposition.HashAuthoritative(
                fixture.Server,
                tick: 0UL
            )
        );
        Assert.Equal(
            beforeDefinition,
            fixture.DefinitionBytes()
        );

        entries[peer] = entries[peer] with {
            Cadence = entries[peer].Cadence with {
                MotionPeriodTicks = 2UL,
                MotionElapsedTicks = 0UL,
                MotionRemainingTicks = 1UL,
            },
        };
        malformed = captured with { Population = captured.Population with { Entries = entries } };
        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: malformed));
        Assert.Equal(
            beforeHash,
            WorldStateHashComposition.HashAuthoritative(
                fixture.Server,
                tick: 0UL
            )
        );

        entries = captured.Population.Entries.ToArray();
        entries[peer] = entries[peer] with { Admission = entries[peer].Admission with { KitIndex = byte.MaxValue } };
        malformed = captured with { Population = captured.Population with { Entries = entries } };
        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: malformed));
        Assert.Equal(
            beforeHash,
            WorldStateHashComposition.HashAuthoritative(
                fixture.Server,
                tick: 0UL
            )
        );

        malformed = captured with {
            Population = captured.Population with { SimulatedCount = (fixture.Server.Population.PeerCapacity + 1) },
        };
        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: malformed));
        Assert.Equal(
            beforeHash,
            WorldStateHashComposition.HashAuthoritative(
                fixture.Server,
                tick: 0UL
            )
        );
    }
    [Fact]
    public void ACheckpointCarriesTheCodecsVersionAndThePreviousVersionIsRefused() {
        using var fixture = Fixtures.FreshServer();

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                reason: out var reason,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty
            ),
            userMessage: reason
        );
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!);

        Assert.Equal(
            WorldAuthorityCheckpointCodec.SupportedVersion,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(source: bytes.AsSpan(start: 4))
        );
        // The law never pins a token's literal: whatever the current key is, the Hello door accepts it and refuses any
        // other, and the federation key stays a distinct identity.
        Assert.True(condition: WorldHelloDoor.TryAccept(
            offeredKey: WorldProtocol.WireProtocolKey,
            refusal: out _
        ));
        Assert.False(condition: WorldHelloDoor.TryAccept(
            offeredKey: WorldProtocol.WireProtocolKey ^ 1UL,
            refusal: out var helloRefusal
        ));
        Assert.Equal(
            actual: helloRefusal,
            expected: WorldHelloRefusal.WireProtocolKeyMismatch
        );
        Assert.NotEqual(
            actual: WorldFederationCodec.WireKey,
            expected: WorldProtocol.WireProtocolKey
        );
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: bytes,
                checkpoint: out _,
                reason: out reason
            ),
            userMessage: reason
        );
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
            destination: bytes.AsSpan(start: 4),
            value: ((ushort)(WorldAuthorityCheckpointCodec.SupportedVersion - 1))
        );
        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: bytes,
            checkpoint: out _,
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"version {(WorldAuthorityCheckpointCodec.SupportedVersion - 1)}"
        );
    }
    [Fact]
    public void EventFeedCheckpointRefusesBeforeMutatingAuthority() {
        using var fixture = Fixtures.FreshServer();

        fixture.Step();
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                checkpoint: out var captured,
                reason: out var reason
            ),
            userMessage: reason
        );
        var beforeHash = WorldStateHashComposition.HashAuthoritative(
            fixture.Server,
            tick: 0UL
        );
        var beforeDefinition = fixture.DefinitionBytes();
        var malformed = captured! with {
            EventFeed = captured.EventFeed with { SeatOccupied = [false] },
        };

        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: malformed));
        Assert.Equal(
            beforeHash,
            WorldStateHashComposition.HashAuthoritative(
                fixture.Server,
                tick: 0UL
            )
        );
        Assert.Equal(
            beforeDefinition,
            fixture.DefinitionBytes()
        );

    }
}
