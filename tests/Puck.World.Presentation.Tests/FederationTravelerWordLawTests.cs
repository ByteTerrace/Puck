using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>Laws for the two words that follow a forwarding route across a socket. A source host hands a remote
/// player's traveler to a destination behind its door, over real federation, and keeps a route to it. The player's
/// absence and return reach the destination as a presence word, a traveler the destination retires is retired at the
/// source when the destination next answers for it, and a retirement at the source reaches the destination as a retire
/// word that never retires a body standing there.</summary>
public sealed class FederationTravelerWordLawTests {
    private const int FirstPeer = WorldBodiesLimits.LocalSeatCount;

    private static void Until(Func<bool> done, string what, Action? each = null) {
        using var deadline = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 30));

        while (true) {
            each?.Invoke();
            if (done()) {
                return;
            }
            Assert.False(condition: deadline.IsCancellationRequested, userMessage: $"{what}: this did not happen in time");
            Thread.Sleep(millisecondsTimeout: 1);
        }
    }
    // Admits a remote player at the source's boot row and hands its traveler to the destination behind its door. The
    // source then keeps the route this returns: the destination's body index and the credential the route travels under.
    private static WorldForwardedBodyCheckpoint Walk(FederatedHosts hosts, int slot) {
        Assert.True(condition: hosts.SourceRow.Server.ExecuteAuthorityOperation(operation: () => hosts.SourceRow.Server.Population.TryAdmitRemotePeerAt(
            slot: slot,
            source: IntentSource.Live,
            grantTemplates: [],
            identityDomain: string.Empty,
            identitySubject: string.Empty,
            admitted: out _,
            refusal: out _
        )));
        hosts.Source.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        var incarnation = hosts.SourceRow.Server.Population.ReadMobility(authority: hosts.SourceRow.Server.AuthorityIdentity, index: slot).Incarnation;

        _ = hosts.Source.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: hosts.RemoteDestination,
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: WorldInstanceHost.BootInstanceName,
            sourceSlot: slot
        );
        WorldForwardedBodyCheckpoint? route = null;

        Until(
            done: () => ((route = hosts.Source.CaptureRow(row: hosts.SourceRow.Instance).ForwardedBodies.FirstOrDefault(predicate: body => (body.SourceIncarnation == incarnation))) is not null),
            each: hosts.Source.DrainPendingTransfers,
            what: "the source handed its remote player's traveler to the destination"
        );
        return route!;
    }
    private static bool Parked(FederatedHosts hosts, int body) =>
        hosts.DestinationRow.Server.ExecuteAuthorityOperation(operation: () => hosts.DestinationRow.Server.Population.IsParked(index: body));
    private static WorldDefinition SourceDocument() => Fixtures.PeerPopulationDocument(networkPlayers: 2);

    [Fact]
    public async Task APlayersAbsenceReachesTheNextHopAndATravelerRetiredThereIsRetiredUpstream() {
        using var hosts = FederatedHosts.Build(sourceDefinition: SourceDocument());
        var route = Walk(hosts: hosts, slot: FirstPeer);
        var mobility = route.Mobility;
        var source = hosts.SourceRow.Server;
        var destination = hosts.DestinationRow.Server;

        Assert.False(condition: Parked(body: route.DestinationBodyIndex, hosts: hosts));

        // The source stops attending to the traveler. It holds the route and no body, so the word travels the route:
        // the destination parks the body, and resumes it when the source attends again.
        hosts.Source.SetTravelerPresence(mobility: in mobility, present: false, source: source);
        Until(done: () => Parked(body: route.DestinationBodyIndex, hosts: hosts), each: hosts.Source.DrainPendingTransfers, what: "the destination parked the traveler its source left");
        hosts.Source.SetTravelerPresence(mobility: in mobility, present: true, source: source);
        Until(done: () => !Parked(body: route.DestinationBodyIndex, hosts: hosts), each: hosts.Source.DrainPendingTransfers, what: "the destination resumed the traveler its source returned to");

        // The source leaves for good. The body's reconnect grace runs out at the destination, which retires it and
        // tells no one upstream; the source's route still stands.
        hosts.Source.SetTravelerPresence(mobility: in mobility, present: false, source: source);
        Until(done: () => Parked(body: route.DestinationBodyIndex, hosts: hosts), each: hosts.Source.DrainPendingTransfers, what: "the destination parked the traveler again");
        Until(
            done: () => !destination.HoldsTransferredTraveler(incarnation: mobility.Incarnation, sourceAuthority: route.SourceAuthority),
            each: () => hosts.Destination.StepInstances(masterDeltaTicks: Fixtures.StepTicks),
            what: "the destination retired the traveler when its grace ran out"
        );
        hosts.Source.DrainPendingTransfers();
        Assert.True(condition: hosts.Source.HasOnwardRoute(mobility: in mobility, source: source));

        // The next thing the source asks the destination for the traveler is answered by name, and the source retires
        // the route on that answer.
        var (described, reason) = await hosts.Source.DescribeForwardingAsync(mobility: mobility, source: source);

        Assert.Null(@object: described);
        Assert.StartsWith(expectedStartString: nameof(WorldFederationRefusal.TravelerUnknown), actualString: reason);
        hosts.Source.DrainPendingTransfers();
        Assert.False(condition: hosts.Source.HasOnwardRoute(mobility: in mobility, source: source));
        Assert.Empty(collection: hosts.Source.CaptureRow(row: hosts.SourceRow.Instance).ForwardedBodies);
    }

    [Fact]
    public void ARetirementReachesTheNextHopAndNeverRetiresABodyThatStandsThere() {
        using var hosts = FederatedHosts.Build(sourceDefinition: SourceDocument());
        var standing = Walk(hosts: hosts, slot: FirstPeer);
        var walkedOn = Walk(hosts: hosts, slot: (FirstPeer + 1));
        var destination = hosts.DestinationRow.Server;

        // The second traveler walks on to the destination's colocated neighbour: the destination keeps its credential
        // and a route, and no body.
        _ = hosts.Destination.EnqueueTransfer(
            actingPrincipal: destination.ExecuteAuthorityOperation(operation: () => destination.Population.PeerPrincipal(index: walkedOn.DestinationBodyIndex)),
            destination: WorldInstanceHost.TransferDestination.Existing(name: FederatedHosts.OnwardName),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: FederatedHosts.DestinationName,
            sourceSlot: walkedOn.DestinationBodyIndex
        );
        hosts.Destination.DrainPendingTransfers();
        Assert.Contains(collection: hosts.Destination.CaptureRow(row: hosts.DestinationRow.Instance).ForwardedBodies, filter: body => (body.SourceIncarnation == walkedOn.SourceIncarnation));

        // The source retires both travelers. Each dropped route's next hop is told over the socket.
        hosts.Source.RetireTraveler(incarnation: standing.SourceIncarnation);
        hosts.Source.RetireTraveler(incarnation: walkedOn.SourceIncarnation);
        hosts.Source.DrainPendingTransfers();
        Assert.Empty(collection: hosts.Source.CaptureRow(row: hosts.SourceRow.Instance).ForwardedBodies);

        // The traveler that walked on is retired at the destination, which then drops its own onward route. The one
        // whose body stands at the destination is refused by name and keeps its body and its credential.
        Until(
            done: () => !destination.HoldsTransferredTraveler(incarnation: walkedOn.SourceIncarnation, sourceAuthority: walkedOn.SourceAuthority) &&
                hosts.Door.FederationRefusals.Any(predicate: static row => (row.Refusal == WorldFederationRefusal.SubmissionRefused)),
            what: "the destination answered both retire words"
        );
        hosts.Destination.DrainPendingTransfers();
        Assert.Empty(collection: hosts.Destination.CaptureRow(row: hosts.DestinationRow.Instance).ForwardedBodies);
        Assert.True(condition: destination.HoldsTransferredTraveler(incarnation: standing.SourceIncarnation, sourceAuthority: standing.SourceAuthority));
        Assert.True(condition: destination.ExecuteAuthorityOperation(operation: () => destination.Population.IsActive(index: standing.DestinationBodyIndex)));
    }
}
