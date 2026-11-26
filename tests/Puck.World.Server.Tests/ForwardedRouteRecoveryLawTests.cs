using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A forwarding route a host recovers stands only while its colocated destination still holds a credential
/// for the traveler. The destination retires a traveler under its own gate and the host drops the routes to it on its
/// next loop, so a process that stops between the two leaves the forwarding row's checkpoint a route the destination's
/// checkpoint no longer answers for. The first drain after the recovery retires such a route, and keeps one whose
/// destination still holds the traveler.</summary>
public sealed class ForwardedRouteRecoveryLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ARecoveredRouteStandsOnlyWhileItsDestinationHoldsTheTraveler(bool retiredAtTheDestination) {
        const int PeerSlot = WorldBodiesLimits.LocalSeatCount;
        var machineId = Guid.NewGuid();
        var document = Fixtures.PeerPopulationDocument(networkPlayers: 1);

        // No reconnect grace: a disconnected traveler is retired on the tick that takes its disconnect.
        document = document with { PopulationRaw = document.Population with { ReconnectGraceSeconds = 0f } };
        using var stateRoot = new TemporaryDirectory(prefix: "puck-forwarded-route-recovery-tests-");
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: stateRoot.RootPath)
        );
        using var rowA = HostRow.Build(definition: document, name: "row-a");
        using var rowB = HostRow.Build(definition: document, name: "row-b");

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
        host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        _ = host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: PeerSlot
        );
        host.DrainPendingTransfers();
        var forwarded = Assert.Single(collection: host.CaptureRow(row: rowA.Instance).ForwardedBodies);
        var mobility = forwarded.Mobility;

        // The forwarding row's image is the one a checkpoint took while the traveler stood at the destination.
        Assert.True(condition: rowA.Server.TryCaptureCheckpoint(checkpoint: out var checkpointA, hostRow: host.CaptureRow(row: rowA.Instance), reason: out var reasonA), userMessage: reasonA);
        Assert.True(condition: rowB.Server.TryTransferredPrincipal(mobility: in mobility, principal: out var peer, sourceAuthority: forwarded.SourceAuthority));
        if (retiredAtTheDestination) {
            // The destination retires the traveler and tells the host, and the host stops before its next drain: the
            // word is queued and the route still stands.
            Assert.True(condition: rowB.Server.DisconnectPeer(actor: Principal.Console, peer: peer));
            for (var tick = 0; (tick < 3); tick++) {
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            }
            Assert.False(condition: rowB.Server.TryTransferredPrincipal(mobility: in mobility, principal: out _, sourceAuthority: forwarded.SourceAuthority));
            Assert.Single(collection: host.CaptureRow(row: rowA.Instance).ForwardedBodies);
        }
        Assert.True(condition: rowB.Server.TryCaptureCheckpoint(checkpoint: out var checkpointB, hostRow: host.CaptureRow(row: rowB.Instance), reason: out var reasonB), userMessage: reasonB);

        var (restoredHost, restoredA, restoredB, restoredRoot) = HostRoundtripFixture.RestoreBoth(
            checkpointA: HostRoundtripFixture.EncodeDecode(checkpoint: checkpointA!),
            checkpointB: HostRoundtripFixture.EncodeDecode(checkpoint: checkpointB!),
            machineId: machineId
        );
        using var disposeHost = restoredHost;
        using var disposeA = restoredA;
        using var disposeB = restoredB;
        using var disposeRoot = restoredRoot;

        // The route is recovered as data either way; what the destination holds decides it on the first drain.
        Assert.Single(collection: restoredHost.CaptureRow(row: restoredA.Instance).ForwardedBodies);
        restoredHost.DrainPendingTransfers();
        Assert.Equal(
            expected: !retiredAtTheDestination,
            actual: restoredHost.HasOnwardRoute(mobility: in mobility, source: restoredA.Server)
        );
        Assert.Equal(
            expected: (retiredAtTheDestination ? 0 : 1),
            actual: restoredHost.CaptureRow(row: restoredA.Instance).ForwardedBodies.Count
        );
    }
}
