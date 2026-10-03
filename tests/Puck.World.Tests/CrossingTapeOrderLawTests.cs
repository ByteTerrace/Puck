using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A source's tape records each departure, and each rollback of one, where the authority decided it: inside
/// the authority operation that detached or restored the body, never at the settlement that follows. Two crossings
/// prove it on the source's own tape. A traveler handed back into the source while the source's outbound
/// acknowledgement is still running arrives at the seat it left, so its arrival must follow its departure. A departure
/// whose commit stays in doubt across ticks and is then rolled back leaves the seat empty for those ticks, so the
/// replay must leave it empty too. Red legs: with departures applied at settlement, the return refuses its arrival and
/// the in-doubt rollback diverges at the departure tick.</summary>
public sealed partial class CrossingTapeOrderLawTests {
    private static readonly WorldChannelTable Channels = WorldChannelTable.Compile(channels: Fixtures.BuildDocument().Channels);

    // Forwards every call to a co-hosted destination's own server, with three seams: a commit that never reaches it,
    // answered as lost or as uncertain, an action run once the destination has heard the acknowledgement, and a
    // reservation answered by a seam that may forward it to the destination.
    private sealed class SeamPeerCall(WorldServer destination, string commitFault, Action? afterAcknowledge, Func<WorldTransferReservationRequest, Func<WorldTransferReservationReply>, WorldTransferReservationReply>? reserve = null) : IWorldPeerCall {
        public void Abort(string sourceAuthority, ulong transferId) => destination.AbortTransfer(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
        public void Acknowledge(string sourceAuthority, ulong transferId) {
            destination.AcknowledgeTransfer(
                sourceAuthority: sourceAuthority,
                transferId: transferId
            );
            afterAcknowledge?.Invoke();
        }
        public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out WorldTransferStatus status, out string reason) {
            reason = string.Empty;

            switch (commitFault) {
                case "unreachable":
                    status = WorldTransferStatus.Missing;
                    return WorldTransferStep.Unreachable;
                case "uncertain":
                    status = WorldTransferStatus.Uncertain;
                    return WorldTransferStep.Answered;
            }

            status = destination.CommitTransfer(
                members: members,
                reason: out reason,
                sourceAuthority: sourceAuthority,
                transferId: transferId
            );
            return WorldTransferStep.Answered;
        }
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => ((reserve is null)
            ? destination.ReserveTransfer(request: request)
            : reserve(arg1: request, arg2: () => destination.ReserveTransfer(request: request)));
        public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
            status = destination.TransferStatus(
                sourceAuthority: sourceAuthority,
                transferId: transferId
            );
            return true;
        }
    }
    private sealed class Scenario : IDisposable {
        private readonly TemporaryDirectory m_hostRoot;
        private readonly TemporaryDirectory m_tapeRoot;

        public Scenario() {
            m_hostRoot = new TemporaryDirectory(prefix: "puck-tape-order-host-");
            m_tapeRoot = new TemporaryDirectory(prefix: "puck-tape-order-tape-");
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
        public void Step() {
            Host.DrainPendingTransfers();
            ((LoopbackTransport)Source.Instance.Link).SubmitIntent(submission: new IntentSubmission(
                Tick: Source.Server.NextInputTick,
                EntityIndex: 1,
                Intent: Channels.RoleOrdinals.Intent(
                    moveAdvance: FixedQ4816.One,
                    moveStrafe: FixedQ4816.Zero
                ),
                Principal: Principal.Seat(slot: 1)
            ));
            Host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }
        // Seats 0 (the traveler) and 1 (which keeps the source running) on the source, then starts the recording.
        public string Begin() {
            for (var slot = 0; (slot < 2); slot++) {
                Assert.True(condition: Source.Server.ApplySession(request: new SessionRequest.Join(
                    IdentityName: null,
                    Principal: Principal.Seat(slot: slot),
                    Slot: slot,
                    WireProtocolKey: WorldProtocol.WireProtocolKey
                )).Accepted);
            }

            var name = $"order-{Guid.NewGuid():N}";

            Assert.True(
                condition: Tape.TryBeginRecording(
                    name: name,
                    refusal: out var refusal
                ),
                userMessage: refusal
            );
            for (var tick = 0; (tick < 4); tick++) {
                Step();
            }
            return name;
        }
        public void EnqueueCrossing() => _ = Host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: 0
        );
        // The authority entries of the recording, described, in tape order.
        public string[] Described(string name) {
            using var stream = File.OpenRead(path: Tape.PathFor(name: name));

            return [.. WorldReplaySnapshot.Read(stream: stream).Ticks.SelectMany(selector: static tick => tick.Authority).Select(selector: static entry => WorldReplayEntryDescriber.Describe(
                channels: Channels,
                entry: entry
            ))];
        }
    }

    // The destination hands the traveler straight back into the seat it left on the source, through the source's own
    // escrow, while the source is still inside its acknowledgement call.
    private static void HandBack(Scenario scenario) {
        var destination = scenario.Destination.Server;
        var returning = destination.Population.EnsureMobility(
            authority: destination.AuthorityIdentity,
            index: 0
        );

        Assert.NotNull(@object: destination.ExecuteAuthorityOperation(operation: () => destination.DetachForTransfer(
            slot: 0,
            transferId: 900
        )));

        var request = new WorldTransferReservationRequest(
            TransferId: 900,
            SourceAuthority: destination.AuthorityIdentity,
            SourceRateHz: 240,
            SourceTick: 0,
            DeadlineSourceTick: 60,
            Border: "door",
            BorderCapacity: null,
            PartyAllOrNothing: true,
            PeerAdmission: false,
            Members: [new WorldTransferReservationMember(
                Principal: Principal.Seat(slot: 0),
                PreferredSlot: 0,
                Identity: null,
                Source: default,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Mobility: returning
            )]
        );
        var reservation = scenario.Source.Server.ReserveTransfer(request: request);

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );
        Assert.Equal([0], reservation.BodyIndices);
        Assert.Equal(
            actual: scenario.Source.Server.CommitTransfer(
                members: [new WorldTransferCommitMember(
                    Profile: null,
                    HasMappedArrival: false,
                    BodyMotionProgramName: "grounded",
                    Position: default,
                    YawRadians: default,
                    PlanarVelocity: default,
                    VerticalVelocity: default
                )],
                reason: out var reason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            ),
            expected: WorldTransferStatus.Committed
        );
    }

    // THE LAW: a traveler handed back while the source is still acknowledging its commit replays in authority order.
    // The source tapes the departure where it detached the traveler, then the return's arrival into the same seat,
    // then the settlement, and the source's tape replays tick for tick.
    [Fact]
    public void AReturnDuringTheAcknowledgementReplaysInAuthorityOrder() {
        using var scenario = new Scenario();
        var name = scenario.Begin();
        var handedBack = false;

        scenario.Host.SetPeerCallFault(
            fault: new SeamPeerCall(
                afterAcknowledge: () => {
                    if (!handedBack) {
                        handedBack = true;
                        HandBack(scenario: scenario);
                    }
                },
                commitFault: string.Empty,
                destination: scenario.Destination.Server
            ),
            instanceName: "row-b"
        );
        scenario.EnqueueCrossing();
        for (var tick = 0; (tick < 4); tick++) {
            scenario.Step();
        }

        Assert.True(condition: handedBack);
        Assert.True(condition: scenario.Source.Server.Population.IsActive(index: 0));
        _ = scenario.Tape.StopRecording();
        Assert.Equal(
            actual: scenario.Tape.Verify(name: name).Primary.DivergedAt,
            expected: -1
        );

        var described = scenario.Described(name: name);
        var departure = Array.FindIndex(array: described, match: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "departure #"));
        var arrival = Array.FindIndex(array: described, match: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "arrival #900"));
        var settlement = Array.FindIndex(array: described, match: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "transfer #"));

        Assert.True(condition: ((departure >= 0) && (departure < arrival) && (arrival < settlement)), userMessage: string.Join(separator: " | ", value: described));
    }
    // THE LAW: a departure whose commit stays in doubt across ticks and is then rolled back replays in authority
    // order. The commit is lost or answered uncertain, so the traveler is gone from the source for as long as the
    // destination's lease lives; once the destination answers that the transfer is missing, the source restores it.
    // The tape holds the departure and the restore at their own ticks, and replays tick for tick.
    [InlineData("unreachable")]
    [InlineData("uncertain")]
    [Theory]
    public void AnInDoubtDepartureThatRollsBackReplaysInAuthorityOrder(string commitFault) {
        using var scenario = new Scenario();
        var name = scenario.Begin();

        scenario.Host.SetPeerCallFault(
            fault: new SeamPeerCall(
                afterAcknowledge: null,
                commitFault: commitFault,
                destination: scenario.Destination.Server
            ),
            instanceName: "row-b"
        );
        scenario.EnqueueCrossing();
        scenario.Step();

        var awayTicks = 0;

        for (var tick = 0; ((tick < 2000) && !scenario.Source.Server.Population.IsActive(index: 0)); tick++) {
            awayTicks++;
            scenario.Step();
        }

        Assert.True(condition: (awayTicks > 1));
        Assert.True(condition: scenario.Source.Server.Population.IsActive(index: 0));
        for (var tick = 0; (tick < 4); tick++) {
            scenario.Step();
        }
        _ = scenario.Tape.StopRecording();
        Assert.Equal(
            actual: scenario.Tape.Verify(name: name).Primary.DivergedAt,
            expected: -1
        );

        var described = scenario.Described(name: name);

        Assert.Single(collection: described, predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "departure #"));
        Assert.Single(collection: described, predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "restore #"));
    }

    // The fields a rollback restores that tell a returning body's authority apart: whether it was transferred in or is a
    // remote human, the mobility credential and its generation, and whether it is parked and until when.
    private static (bool AuthorityTransferred, bool RemoteHuman, WorldMobilityIdentity? Mobility, int MobilityGeneration, bool Parked, long? ParkedUntilTick)? Restored(WorldServer server) {
        foreach (var entry in server.Population.Capture().Entries) {
            if (entry.Index == 0) {
                return (entry.IsAuthorityTransferred, entry.IsRemoteHuman, entry.Mobility, entry.MobilityGeneration, entry.Parked, entry.ParkedUntilTick);
            }
        }

        return null;
    }

    // THE LAW: a re-driven rollback of an in-doubt departure restores the body exactly as the live rollback did, field
    // by field: transfer and remote-human flags, mobility credential and generation, parking and its deadline, on
    // every tick from the rollback on. The red leg restores on the re-drive without the departing credential.
    [InlineData("unreachable")]
    [InlineData("uncertain")]
    [Theory]
    public void ARedrivenRollbackRestoresTheFieldsTheLiveRollbackRestored(string commitFault) {
        using var scenario = new Scenario();
        var name = scenario.Begin();
        var live = new List<(bool, bool, WorldMobilityIdentity?, int, bool, long?)?>();

        scenario.Host.SetPeerCallFault(
            fault: new SeamPeerCall(
                afterAcknowledge: null,
                commitFault: commitFault,
                destination: scenario.Destination.Server
            ),
            instanceName: "row-b"
        );
        scenario.EnqueueCrossing();
        // The recording's first four ticks are Begin's own; the live state is sampled after each tick from here.
        for (var tick = 0; (tick < 4); tick++) {
            live.Add(item: null);
        }

        int? rollback = null;

        for (var tick = 0; ((tick < 2000) && ((rollback is null) || (tick < (rollback + 4)))); tick++) {
            scenario.Step();
            live.Add(item: Restored(server: scenario.Source.Server));
            if ((rollback is null) && (tick > 0) && scenario.Source.Server.Population.IsActive(index: 0)) {
                rollback = tick;
            }
        }

        Assert.NotNull(@object: rollback);
        _ = scenario.Tape.StopRecording();

        WorldReplaySnapshot snapshot;

        using (var stream = File.OpenRead(path: scenario.Tape.PathFor(name: name))) {
            snapshot = WorldReplaySnapshot.Read(stream: stream);
        }

        var replayed = new List<(bool, bool, WorldMobilityIdentity?, int, bool, long?)?>();

        _ = snapshot.DriveTraces(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            observeTick: (_, server) => replayed.Add(item: Restored(server: server)),
            profiles: scenario.Source.Server.Profiles
        );

        Assert.Equal(expected: live.Count, actual: replayed.Count);
        for (var tick = (4 + rollback!.Value); (tick < live.Count); tick++) {
            Assert.True(condition: live[tick].HasValue, userMessage: $"tick {tick}: the live rollback left no body at index 0");
            Assert.Equal(expected: live[tick], actual: replayed[tick]);
        }
    }
}
