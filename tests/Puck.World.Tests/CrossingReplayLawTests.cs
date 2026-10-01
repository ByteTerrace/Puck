using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Destination tapes and multi-authority replay over one process's two rows. A recording armed on the source
/// row tapes the destination row beside it; the destination's tape carries the arrival the escrow landed, and the set
/// pairs the source's departure with the destination's arrival by handoff token. Red legs: a set with no destination
/// tape reports the crossing as not verified however exactly the source replays, and a destination tape with its
/// arrival removed no longer replays to the state its authority reached.</summary>
public sealed class CrossingReplayLawTests {
    private static readonly WorldChannelTable Channels = WorldChannelTable.Compile(channels: Fixtures.BuildDocument().Channels);

    private sealed class Scenario : IDisposable {
        private readonly TemporaryDirectory m_hostRoot;
        private readonly TemporaryDirectory m_tapeRoot;

        public Scenario() {
            m_hostRoot = new TemporaryDirectory(prefix: "puck-crossing-replay-host-");
            m_tapeRoot = new TemporaryDirectory(prefix: "puck-crossing-replay-tape-");
            Host = new WorldInstanceHost(
                applicationStopping: CancellationToken.None,
                admitsSpawn: true,
                machineHostFactory: Fixtures.MachineHostFactory,
                machineId: Guid.NewGuid(),
                resolver: new WorldSessionResolver(),
                seats: WorldEmbodiedSeats.None,
                stateRoot: new WorldStateRoot(path: m_hostRoot.RootPath)
            );
            Source = HostRow.Build(name: "row-a");
            Destination = HostRow.Build(name: "row-b");
            Host.Admit(row: Source.Instance);
            Host.Admit(row: Destination.Instance);
            Tape = new WorldReplayTape(
                addonHostFactory: static (_, _) => new NullAddonHost(),
                engines: [],
                liveServer: Source.Server,
                machineHostFactory: Fixtures.MachineHostFactory,
                profiles: Source.Server.Profiles,
                stateRoot: new WorldStateRoot(path: m_tapeRoot.RootPath),
                transport: ((LoopbackTransport)Source.Instance.Link)
            );
            Source.Instance.Tape = Tape;
        }

        public HostRow Destination { get; }
        public WorldInstanceHost Host { get; }
        public HostRow Source { get; }
        public WorldReplayTape Tape { get; }

        public void Dispose() {
            Host.Dispose();
            Source.Dispose();
            Destination.Dispose();
            m_tapeRoot.Dispose();
            m_hostRoot.Dispose();
        }
        // One master step: the host's transfer drain, each row's own seat input through its own loopback, the step.
        public void Step(HostRow drivenRow, FixedQ4816 forward) {
            Host.DrainPendingTransfers();
            ((LoopbackTransport)drivenRow.Instance.Link).SubmitIntent(submission: new IntentSubmission(
                Tick: drivenRow.Server.NextInputTick,
                EntityIndex: 0,
                Intent: Channels.RoleOrdinals.Intent(
                    moveAdvance: forward,
                    moveStrafe: FixedQ4816.Zero
                ),
                Principal: Principal.Seat(slot: 0)
            ));
            Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }
        public WorldReplaySnapshot Read(string name) {
            using var stream = File.OpenRead(path: Tape.PathFor(name: name));

            return WorldReplaySnapshot.Read(stream: stream);
        }
        // Records seat 0 walking on the source, crossing to the destination and walking on there.
        public WorldReplayStopResult RecordCrossing(string name, bool tapeDestination) {
            Assert.True(condition: Source.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: 0),
                Slot: 0,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
            // A second seat keeps the source running once the traveler leaves it.
            Assert.True(condition: Source.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: 1),
                Slot: 1,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
            Assert.True(
                condition: Tape.TryBeginRecording(
                    name: name,
                    refusal: out var refusal
                ),
                userMessage: refusal
            );
            if (tapeDestination) {
                Host.RecordCompanions(tape: Tape);
            }

            for (var tick = 0; (tick < 8); tick++) {
                Step(
                    drivenRow: Source,
                    forward: FixedQ4816.One
                );
            }

            _ = Host.EnqueueTransfer(
                actingPrincipal: Principal.Console,
                destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
                scope: WorldInstanceHost.TransferScope.Body,
                sourceInstance: "row-a",
                sourceSlot: 0
            );

            for (var tick = 0; (tick < 12); tick++) {
                Step(
                    drivenRow: Destination,
                    forward: FixedQ4816.One
                );
            }

            Assert.False(condition: Source.Server.Population.IsActive(index: 0));
            Assert.True(condition: Destination.Server.Population.IsActive(index: 0));

            return Tape.StopRecording();
        }
    }

    private static bool IsArrival(WorldReplayEntry entry, WorldChannelTable channels) => WorldReplayEntryDescriber.Describe(
        channels: channels,
        entry: entry
    ).StartsWith(
        comparisonType: StringComparison.Ordinal,
        value: "arrival #"
    );

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ALocalCrossingIsTapedAtBothAuthorities_AndTheSetVerifiesIt(bool destinationOwnsTape) {
        using var scenario = new Scenario();
        WorldReplayTape? destinationTape = null;

        if (destinationOwnsTape) {
            destinationTape = new WorldReplayTape(
                addonHostFactory: static (_, _) => new NullAddonHost(),
                engines: [],
                liveServer: scenario.Destination.Server,
                machineHostFactory: Fixtures.MachineHostFactory,
                profiles: scenario.Destination.Server.Profiles,
                stateRoot: new WorldStateRoot(path: scenario.Tape.Directory()),
                transport: ((LoopbackTransport)scenario.Destination.Instance.Link)
            );
            scenario.Destination.Instance.Tape = destinationTape;
        }
        var name = $"crossing-{Guid.NewGuid():N}";
        var stop = scenario.RecordCrossing(
            name: name,
            tapeDestination: true
        );

        Assert.Null(@object: stop.VerifyFault);
        var verdict = stop.Verdict!;

        Assert.True(
            condition: verdict.Passing,
            userMessage: verdict.Describe()
        );
        var companion = Assert.Single(collection: verdict.Companions);

        Assert.Equal(
            expected: scenario.Destination.Server.AuthorityIdentity,
            actual: companion.Authority
        );
        var crossing = Assert.Single(collection: verdict.Crossings);

        Assert.True(
            condition: crossing.Verified,
            userMessage: crossing.Reason
        );
        Assert.Equal(
            expected: scenario.Source.Server.AuthorityIdentity,
            actual: crossing.Source
        );
        Assert.Equal(
            expected: scenario.Destination.Server.AuthorityIdentity,
            actual: crossing.Target
        );

        // replay.verify reads the same set back from disk and reaches the same verdict.
        var reread = scenario.Tape.Verify(name: name);

        Assert.True(
            condition: reread.Passing,
            userMessage: reread.Describe()
        );
        Assert.Same(expected: destinationTape, actual: scenario.Destination.Instance.Tape);
        if (destinationTape is not null) {
            Assert.Equal(expected: WorldReplayMode.Idle, actual: destinationTape.Mode);
        }
    }
    [Fact]
    public void WorldExitCancelsTheRecordingAndItsCompanions() {
        using var scenario = new Scenario();

        Assert.True(condition: scenario.Tape.TryBeginRecording(name: "exit", refusal: out _));
        scenario.Host.RecordCompanions(tape: scenario.Tape);
        var companion = scenario.Destination.Instance.Tape;

        Assert.NotNull(@object: companion);
        scenario.Host.Dispose();
        Assert.Equal(expected: WorldReplayMode.Idle, actual: scenario.Tape.Mode);
        Assert.Equal(expected: WorldReplayMode.Idle, actual: companion.Mode);
        Assert.Null(@object: scenario.Destination.Instance.Tape);
        Assert.Null(@object: scenario.Source.Server.ArrivalTap);
        Assert.Null(@object: scenario.Destination.Server.ArrivalTap);
    }
    [Fact]
    public void WithoutTheDestinationTape_TheCrossingIsNotVerified_HoweverExactlyTheSourceReplays() {
        using var scenario = new Scenario();
        var stop = scenario.RecordCrossing(
            name: $"crossing-{Guid.NewGuid():N}",
            tapeDestination: false
        );
        var verdict = stop.Verdict!;

        Assert.True(
            condition: verdict.Match,
            userMessage: verdict.Describe()
        );
        Assert.False(condition: verdict.Passing);
        var crossing = Assert.Single(collection: verdict.Crossings);

        Assert.False(condition: crossing.Verified);
        Assert.Contains(
            expectedSubstring: "which no tape in this set recorded",
            actualString: crossing.Reason,
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: "NOT VERIFIED",
            actualString: verdict.Describe(),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void TheDestinationTapeReplaysItsArrivalToTheIdenticalState_AndWithoutItDoesNot() {
        using var scenario = new Scenario();
        var name = $"crossing-{Guid.NewGuid():N}";
        var stop = scenario.RecordCrossing(
            name: name,
            tapeDestination: true
        );

        Assert.True(
            condition: stop.Verdict!.Passing,
            userMessage: stop.Verdict.Describe()
        );

        var destination = Assert.Single(collection: scenario.Read(name: name).Companions);
        var channels = WorldChannelTable.Compile(channels: WorldDefinitionSerialization.Deserialize(utf8Json: destination.DefinitionJson).Channels);
        var arrivalTick = -1;

        for (var tick = 0; (tick < destination.Ticks.Count); tick++) {
            if (destination.Ticks[tick].Authority.Any(predicate: entry => IsArrival(
                channels: channels,
                entry: entry
            ))) {
                Assert.Equal(
                    actual: arrivalTick,
                    expected: -1
                );
                arrivalTick = tick;
            }
        }

        Assert.True(
            condition: (arrivalTick >= 0),
            userMessage: "the destination tape carries no arrival"
        );

        WorldReplayHashTraces Drive(WorldReplaySnapshot tape) => tape.DriveTraces(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: scenario.Destination.Server.Profiles
        );

        // The control: the destination's tape re-lands its arrival and reaches every recorded state exactly.
        Assert.Equal(
            expected: -1,
            actual: HashTrace.FirstDivergence(
                left: destination.RecordedAuthoritativeHashes,
                right: Drive(tape: destination).Authoritative
            )
        );

        // The red leg: the same tape with its arrival removed replays identically up to the arrival and diverges on it.
        var stripped = new WorldReplaySnapshot {
            Authority = destination.Authority,
            DefinitionJson = destination.DefinitionJson,
            DocumentDirectory = destination.DocumentDirectory,
            DocumentPath = destination.DocumentPath,
            Instance = destination.Instance,
            MountedAddons = destination.MountedAddons,
            PipelineSourceDirectory = destination.PipelineSourceDirectory,
            RecordedAuthoritativeHashes = destination.RecordedAuthoritativeHashes,
            RecordedHashes = destination.RecordedHashes,
            Seats = destination.Seats,
            SimulationRate = destination.SimulationRate,
            Ticks = [.. destination.Ticks.Select(selector: tick => new WorldReplayTickInput(
                Authority: [.. tick.Authority.Where(predicate: entry => !IsArrival(
                    channels: channels,
                    entry: entry
                ))],
                Intents: tick.Intents
            ))],
        };

        Assert.Equal(
            expected: arrivalTick,
            actual: HashTrace.FirstDivergence(
                left: destination.RecordedAuthoritativeHashes,
                right: Drive(tape: stripped).Authoritative
            )
        );
    }
    [Fact]
    public void AFederatedArrivalAndTheInputItsSourceForwardsReplayFromTheDestinationTapeAlone() {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-crossing-replay-federated-");
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 1));
        var server = fixture.Server;
        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: server.Profiles,
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            transport: new LoopbackTransport(server: server)
        );
        var name = $"federated-{Guid.NewGuid():N}";
        const int Slot = WorldBodiesLimits.LocalSeatCount;
        var request = new WorldTransferReservationRequest(
            TransferId: 3,
            SourceAuthority: "remote/source",
            SourceRateHz: WorldDefinition.UnauthoredSimulationRateHz,
            SourceTick: 0,
            DeadlineSourceTick: 60,
            Border: "door",
            BorderCapacity: null,
            PartyAllOrNothing: true,
            PeerAdmission: true,
            Members: [new WorldTransferReservationMember(
                Principal: Principal.Console,
                PreferredSlot: Slot,
                Identity: null,
                Source: IntentSource.Live,
                BodyColor: default,
                CatalogRig: 0,
                Mobility: new WorldMobilityIdentity(
                    DepartedFrom: new WorldEntityAddress(
                        Authority: "remote/source",
                        Generation: 1,
                        Index: 0
                    ),
                    Epoch: 0,
                    Incarnation: new WorldEntityAddress(
                        Authority: "remote/source",
                        Generation: 1,
                        Index: 0
                    )
                )
            )]
        );

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        for (var tick = 0; (tick < 3); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        // The arrival a remote source commits, then the device image its stream forwards every step after.
        Assert.True(condition: server.ReserveTransfer(request: request).Accepted);
        Assert.True(
            condition: server.CommitTransfer(
                members: [new WorldTransferCommitMember(
                    Profile: null,
                    HasMappedArrival: false,
                    BodyMotionProgramName: "grounded",
                    Position: default,
                    YawRadians: default,
                    PlanarVelocity: default,
                    VerticalVelocity: default
                )],
                reason: out var commitReason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            ),
            userMessage: commitReason
        );
        fixture.Step();
        tape.NoteTick();

        var principal = server.Population.PeerPrincipal(index: Slot);

        for (var tick = 0; (tick < 10); tick++) {
            server.PublishFederatedIntent(
                leaseId: 1,
                submission: new IntentSubmission(
                    Tick: server.NextInputTick,
                    EntityIndex: Slot,
                    Intent: Channels.RoleOrdinals.Intent(
                        moveAdvance: FixedQ4816.One,
                        moveStrafe: FixedQ4816.Zero
                    ),
                    Principal: principal
                )
            );
            fixture.Step();
            tape.NoteTick();
        }

        var stop = tape.StopRecording();
        var verdict = stop.Verdict!;

        // The destination's own trajectory replays, while the crossing's departure is on no tape here.
        Assert.True(
            condition: verdict.Match,
            userMessage: verdict.Describe()
        );
        var crossing = Assert.Single(collection: verdict.Crossings);

        Assert.False(condition: crossing.Verified);
        Assert.Contains(
            actualString: crossing.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "whose tape is not in this set"
        );

        // The red leg: without the taped device images, the replayed traveler stands still.
        WorldReplaySnapshot recording;

        using (var stream = File.OpenRead(path: tape.PathFor(name: name))) {
            recording = WorldReplaySnapshot.Read(stream: stream);
        }

        static bool IsFederated(WorldReplayEntry entry) => WorldReplayEntryDescriber.Describe(
            channels: Channels,
            entry: entry
        ).StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "federated ["
        );
        var firstFederated = recording.Ticks.ToList().FindIndex(match: tick => tick.Authority.Any(predicate: IsFederated));
        var stripped = new WorldReplaySnapshot {
            Authority = recording.Authority,
            DefinitionJson = recording.DefinitionJson,
            DocumentDirectory = recording.DocumentDirectory,
            DocumentPath = recording.DocumentPath,
            Instance = recording.Instance,
            MountedAddons = recording.MountedAddons,
            PipelineSourceDirectory = recording.PipelineSourceDirectory,
            RecordedAuthoritativeHashes = recording.RecordedAuthoritativeHashes,
            RecordedHashes = recording.RecordedHashes,
            Seats = recording.Seats,
            SimulationRate = recording.SimulationRate,
            Ticks = [.. recording.Ticks.Select(selector: tick => new WorldReplayTickInput(
                Authority: [.. tick.Authority.Where(predicate: entry => !IsFederated(entry: entry))],
                Intents: tick.Intents
            ))],
        };

        Assert.True(condition: (firstFederated >= 0));
        Assert.Equal(
            expected: firstFederated,
            actual: HashTrace.FirstDivergence(
                left: recording.RecordedAuthoritativeHashes,
                right: stripped.DriveTraces(
                    addonHostFactory: static (_, _) => new NullAddonHost(),
                    engines: [],
                    machineHostFactory: Fixtures.MachineHostFactory,
                    profiles: server.Profiles
                ).Authoritative
            )
        );
    }
}
