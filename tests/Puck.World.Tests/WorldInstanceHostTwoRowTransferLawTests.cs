using Puck.Commands;
using Xunit;

using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// The harness the two-row host-roundtrip-identity law builds on: ONE <see cref="WorldInstanceHost"/>
/// with TWO admitted rows, driving an in-process <c>world.transfer</c> between them the way the desktop console
/// verb does (<c>EnqueueTransfer</c> + <c>DrainPendingTransfers</c>). Proves the harness alone, before any
/// checkpoint enters the picture: a transfer commits, the body appears in the destination, and the source keeps a
/// forwarded arm for it (the P2c debt <c>CaptureRow</c>'s own remarks name as untested by any law that reaches a
/// live arm).
/// </summary>
public sealed class WorldInstanceHostTwoRowTransferLawTests {
    // The production host never deletes its own storage root on dispose (a real deployment's directory persists on
    // purpose), so the scratch root comes back as its own TemporaryDirectory for the caller to dispose alongside it.
    private static (WorldInstanceHost Host, TemporaryDirectory StateRoot) BuildHost(Guid machineId, bool admitsSpawn = true) {
        var stateRoot = new TemporaryDirectory(prefix: "puck-two-row-host-tests-");
        var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: admitsSpawn,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: stateRoot.RootPath)
        );

        return (host, stateRoot);
    }

    [Fact]
    public void LocalTransfer_Commits_LandsInDestination_AndForwardsAtTheSource() {
        // A local seat never carries a forwarded arm — WorldPopulation.TryCaptureTransferredEntity only reads the PEER
        // range, since a local seat's onward routing is the desktop's own seat router, never a forwarding lease.
        // Exercising ForwardedBodies needs a body already active at a peer index, so the document adds a peer slot.
        var document = Fixtures.PeerPopulationDocument(networkPlayers: 1);
        const int PeerSlot = WorldBodiesLimits.LocalSeatCount;

        var (host, hostStateRoot) = BuildHost(machineId: Guid.NewGuid());
        using var disposeHost = host;
        using var disposeHostStateRoot = hostStateRoot;
        using var rowA = HostRow.Build(
            definition: document,
            name: "row-a"
        );
        using var rowB = HostRow.Build(
            definition: document,
            name: "row-b"
        );

        host.Admit(row: rowA.Instance);
        host.Admit(row: rowB.Instance);

        Assert.True(condition: rowA.Server.ExecuteAuthorityOperation(operation: () => rowA.Server.Population.TryAdmitRemotePeerAt(
            slot: PeerSlot,
            source: IntentSource.Live,
            grantTemplates: [],
            identityDomain: string.Empty,
            identitySubject: string.Empty,
            admitted: out _,
            refusal: out _
        )));

        for (var tick = 0; (tick < 10); tick++) {
            host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        var transferId = host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: PeerSlot
        );

        host.DrainPendingTransfers();

        Assert.True(condition: rowB.Server.Population.IsActive(index: PeerSlot));
        Assert.False(condition: rowA.Server.Population.IsActive(index: PeerSlot));

        var sourceRow = host.CaptureRow(row: rowA.Instance);
        var forwarded = Assert.Single(collection: sourceRow.ForwardedBodies);

        Assert.Equal(
            expected: PeerSlot,
            actual: forwarded.DestinationBodyIndex
        );
        Assert.Contains(
            expected: transferId,
            collection: sourceRow.AppliedTransferIds
        );

        var destinationRow = host.CaptureRow(row: rowB.Instance);

        Assert.Empty(collection: destinationRow.ForwardedBodies);
    }
    // The peer call admits a replacement after reservation. Without the gated credential comparison both the
    // commit and forced-rollback paths detach it, so the peer index appears in departures under the wrong offer.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APeerReplacedAfterReservationIsNeverDetachedOrRestoredAsTheReservedTraveler(bool forceRollback) {
        const int PeerSlot = WorldBodiesLimits.LocalSeatCount;
        const string Upstream = "origin";
        var document = Fixtures.PeerPopulationDocument(networkPlayers: 2);

        document = document with { PopulationRaw = document.Population with { ReconnectGraceSeconds = 0f } };
        var (host, root) = BuildHost(machineId: Guid.NewGuid());
        using var disposeHost = host;
        using var disposeRoot = root;
        using var source = HostRow.Build(definition: document, name: "row-a");
        using var destination = HostRow.Build(definition: document, name: "row-b");

        host.Admit(row: source.Instance);
        host.Admit(row: destination.Instance);
        Assert.True(condition: source.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null, Principal: Principal.Seat(slot: 0), Slot: 0, WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        var seatMobility = source.Server.Population.ReadMobility(authority: source.Server.AuthorityIdentity, index: 0);
        var original = Arrive(transferId: 1);
        var reserved = source.Server.Population.ReadMobility(authority: source.Server.AuthorityIdentity, index: PeerSlot);
        WorldMobilityIdentity replacement = default;
        var departures = new List<int>();
        var narration = new RecordingNarrationSink();
        using var narrationLease = host.AttachNarrationSink(sink: narration);
        using var sourceNarrationLease = source.Server.AttachNarrationSink(sink: narration);

        source.Server.DepartureTap = (_, slot, returned) => { if (!returned) { departures.Add(item: slot); } };
        var fault = new FaultingPeerCall(destination: destination.Server) {
            LoseFirstCommit = false,
            AfterReserve = () => {
                WorldSubmissionResult? answer = null;

                Assert.True(condition: WorldLocalForwardedAuthority.TryApplySubmission(
                    completion: result => answer = result,
                    mobility: in original,
                    operationId: Guid.NewGuid(),
                    payload: new WorldSubmissionPayload.Session(Value: new SessionRequest.Leave(Principal: Principal.Console, Slot: PeerSlot)),
                    reason: out var reason,
                    server: source.Server,
                    sourceAuthority: Upstream
                ), userMessage: reason);
                Assert.True(condition: Assert.IsType<WorldSubmissionResult.Session>(@object: answer).Reply.Accepted);
                Assert.False(condition: source.Server.Population.IsActive(index: PeerSlot));
                _ = Arrive(transferId: 2);
                replacement = source.Server.Population.ReadMobility(authority: source.Server.AuthorityIdentity, index: PeerSlot);
                Assert.NotEqual(expected: reserved.DepartedFrom.Generation, actual: replacement.DepartedFrom.Generation);
            },
        };

        host.SetPeerCallFault(fault: fault, instanceName: "row-b");
        var transferId = host.EnqueueTransfer(
            actingPrincipal: Principal.Seat(slot: 0),
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            frozenCohortSlots: [0, PeerSlot],
            scope: WorldInstanceHost.TransferScope.Party,
            sourceInstance: "row-a",
            sourceSlot: 0,
            testForceJoinRefusalOrdinal: (forceRollback ? 1 : null)
        );

        host.DrainPendingTransfers();

        Assert.DoesNotContain(collection: departures, expected: PeerSlot);
        Assert.Equal(actual: departures, expected: new[] { 0 });
        Assert.True(condition: source.Server.Population.IsActive(index: PeerSlot));
        Assert.Equal(expected: replacement, actual: source.Server.Population.ReadMobility(authority: source.Server.AuthorityIdentity, index: PeerSlot));
        Assert.Equal(expected: seatMobility, actual: source.Server.Population.ReadMobility(authority: source.Server.AuthorityIdentity, index: 0));
        Assert.Equal(expected: 0, actual: fault.CommitCalls);
        Assert.Equal(expected: WorldTransferStatus.Missing, actual: destination.Server.TransferStatus(sourceAuthority: source.Server.AuthorityIdentity, transferId: transferId));
        Assert.False(condition: destination.Server.Population.IsActive(index: PeerSlot));
        Assert.False(condition: destination.Server.Population.IsActive(index: (PeerSlot + 1)));
        Assert.Empty(collection: host.CaptureRow(row: source.Instance).ForwardedBodies);
        Assert.Contains(collection: narration.Narrations, filter: static line => line.Text.Contains(
            comparisonType: StringComparison.Ordinal,
            value: "the reserved traveler is no longer live at this authority"
        ));
        Assert.Contains(collection: narration.Narrations, filter: static line => line.Text.Contains(
            comparisonType: StringComparison.Ordinal,
            value: "could not detach after reservation"
        ));

        WorldMobilityIdentity Arrive(ulong transferId) {
            var address = new WorldEntityAddress(Authority: Upstream, Generation: ((int)transferId), Index: 0);
            var mobility = new WorldMobilityIdentity(DepartedFrom: address, Epoch: 0, Incarnation: address);
            var reservation = source.Server.ReserveTransfer(request: new WorldTransferReservationRequest(
                Border: "seam", BorderCapacity: null, DeadlineSourceTick: 60,
                Members: [new WorldTransferReservationMember(
                    BodyColor: default, CatalogRig: 0, Identity: null, Mobility: mobility,
                    PreferredSlot: PeerSlot, Principal: Principal.Console, Source: IntentSource.Live
                )],
                PartyAllOrNothing: true, PeerAdmission: true, SourceAuthority: Upstream,
                SourceRateHz: 240, SourceTick: 0, TransferId: transferId
            ));

            Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
            Assert.Equal(expected: PeerSlot, actual: Assert.Single(collection: reservation.BodyIndices));
            Assert.Equal(expected: WorldTransferStatus.Committed, actual: source.Server.CommitTransfer(
                members: [new WorldTransferCommitMember(
                    BodyMotionProgramName: "grounded", HasMappedArrival: false, PlanarVelocity: default,
                    Position: default, Profile: null, VerticalVelocity: default, YawRadians: default
                )],
                reason: out _, sourceAuthority: Upstream, transferId: transferId
            ));
            return mobility;
        }
    }
    [Fact]
    public void LocalTransfer_ReachesReservedUncommitted_ThenInDoubtOnce_ThroughAFaultingPeerCall() {
        var (host, hostStateRoot) = BuildHost(machineId: Guid.NewGuid());
        using var disposeHost = host;
        using var disposeHostStateRoot = hostStateRoot;
        using var rowA = HostRow.Build(name: "row-a");
        using var rowB = HostRow.Build(name: "row-b");

        host.Admit(row: rowA.Instance);
        host.Admit(row: rowB.Instance);

        var fault = new FaultingPeerCall(destination: rowB.Server);

        host.SetPeerCallFault(
            fault: fault,
            instanceName: "row-b"
        );

        Assert.True(condition: rowA.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        _ = host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: 0
        );
        host.DrainPendingTransfers();

        // Faulted: the destination holds a reservation (a lease) but no body is active there yet — genuinely
        // reserved-uncommitted, not merely a refused commit.
        Assert.Equal(
            expected: 1,
            actual: fault.CommitCalls
        );
        Assert.False(condition: rowB.Server.Population.IsActive(index: 0));
        Assert.False(condition: rowA.Server.Population.IsActive(index: 0));

        // The next drain reconciles: TryStatus reads Reserved, Commit is retried for real, and this time it lands.
        host.DrainPendingTransfers();

        Assert.Equal(
            expected: 2,
            actual: fault.CommitCalls
        );
        Assert.True(condition: rowB.Server.Population.IsActive(index: 0));
    }
}
