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
            stateRoot: stateRoot.RootPath
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
