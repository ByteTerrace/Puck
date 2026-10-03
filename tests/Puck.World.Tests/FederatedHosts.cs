using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using static Puck.World.Tests.FederationSigning;

namespace Puck.World.Tests;

/// <summary>
/// Two <see cref="WorldInstanceHost"/>s federated over real QUIC on IPv4 loopback, in one process. The source host
/// embodies local seats over a real <see cref="Client.WorldSeatAuthorityRouter"/> (<see cref="RoutedSeats"/>) and admits its
/// boot row under a signing identity and peer network of its own. The destination host embodies no seats and admits two
/// rows: the destination, served behind a <see cref="WorldPeerHost"/> door, and the onward row the destination hands a
/// traveler on to. Each side trusts the other's key, both networks and the door run on one
/// <see cref="VirtualClock"/> that never advances, and the destination's document is on disk so the source resolves it
/// as a remote transfer destination.
/// </summary>
/// <remarks>The source's drain blocks its calling thread on the door's socket workers, which answer under the
/// destination's authority gate, so a law never steps or drains the destination on another thread while the source
/// drains.</remarks>
internal sealed class FederatedHosts : IDisposable {
    /// <summary>The destination row's instance name, on the destination host.</summary>
    public const string DestinationName = "row-b";
    /// <summary>The onward row's instance name, on the destination host.</summary>
    public const string OnwardName = "row-c";

    private readonly TemporaryDirectory m_scratch;
    private readonly LocalKeySigningOracle m_sourceOracle;
    private readonly LocalKeySigningOracle m_destinationOracle;
    private readonly WorldPeerNetwork m_sourceNetwork;
    private readonly WorldPeerNetwork m_destinationNetwork;

    private FederatedHosts(Action<WorldRemoteAuthority>? travelerRouteStarted) {
        m_scratch = new TemporaryDirectory(prefix: "puck-federated-hosts-");
        Clock = new VirtualClock(start: AdmissionWireFixture.ClaimInstant);
        Seats = new RoutedSeats();
        Source = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: SourceMachineId,
            resolver: new WorldSessionResolver(),
            seats: Seats,
            stateRoot: new WorldStateRoot(path: Directory.CreateDirectory(path: m_scratch.PathOf(name: "source-state")).FullName),
            travelerRouteStarted: travelerRouteStarted
        );
        Destination = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: Directory.CreateDirectory(path: m_scratch.PathOf(name: "destination-state")).FullName)
        );
        SourceRow = HostRow.Build(name: WorldInstanceHost.BootInstanceName);
        DestinationRow = HostRow.Build(
            definition: Fixtures.PeerPopulationDocument(networkPlayers: 2),
            name: DestinationName
        );
        OnwardRow = HostRow.Build(
            definition: Fixtures.PeerPopulationDocument(networkPlayers: 2),
            name: OnwardName
        );
        m_sourceOracle = LocalOracle(subject: SourceRow.Server.AuthorityIdentity);
        m_destinationOracle = LocalOracle(subject: DestinationRow.Server.AuthorityIdentity);
        m_sourceNetwork = new WorldPeerNetwork(
            timeProvider: Clock,
            transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout
        );
        m_destinationNetwork = new WorldPeerNetwork(
            timeProvider: Clock,
            transportHandshakeTimeout: PeerTestClient.TransportHandshakeTimeout
        );

        var destinationSecurity = Authenticator(
            oracle: m_destinationOracle,
            trustEntries: () => [TrustEntryFor(oracle: m_sourceOracle)]
        );

        SourceRow.Instance.Federation = new WorldFederationIdentity(
            Authenticator: Authenticator(
                oracle: m_sourceOracle,
                trustEntries: () => [TrustEntryFor(oracle: m_destinationOracle)]
            ),
            Network: m_sourceNetwork,
            Subject: SourceRow.Server.AuthorityIdentity
        );
        DestinationRow.Instance.Federation = new WorldFederationIdentity(
            Authenticator: destinationSecurity,
            Network: m_destinationNetwork,
            Subject: DestinationRow.Server.AuthorityIdentity
        );
        Door = new WorldPeerHost(
            authenticator: destinationSecurity,
            network: m_destinationNetwork,
            server: DestinationRow.Server,
            timeProvider: Clock
        );
        // The row owns its door: disposing the row disposes the door.
        DestinationRow.Instance.Door = Door;
        Door.Start(listen: "127.0.0.1:0");
        Source.AdmitBoot(row: SourceRow.Instance);
        Destination.Admit(row: DestinationRow.Instance);
        Destination.Admit(row: OnwardRow.Instance);
        DestinationPath = m_scratch.WriteBytes(
            bytes: WorldDefinitionSerialization.Serialize(definition: DestinationRow.Server.Definition),
            name: $"{DestinationName}.world.json"
        );
    }

    /// <summary>The clock both networks and the door run on.</summary>
    public VirtualClock Clock { get; }
    /// <summary>The source host's embodied seats.</summary>
    public RoutedSeats Seats { get; }
    /// <summary>The source host.</summary>
    public WorldInstanceHost Source { get; }
    /// <summary>The source host's machine id, which names the route each followed local seat travels through.</summary>
    public Guid SourceMachineId { get; } = Guid.NewGuid();
    /// <summary>The source host's boot row.</summary>
    public HostRow SourceRow { get; }
    /// <summary>The destination host.</summary>
    public WorldInstanceHost Destination { get; }
    /// <summary>The destination row, served behind <see cref="Door"/>.</summary>
    public HostRow DestinationRow { get; }
    /// <summary>The onward row, colocated with the destination row on the destination host.</summary>
    public HostRow OnwardRow { get; }
    /// <summary>The destination row's door.</summary>
    public WorldPeerHost Door { get; }
    /// <summary>The destination row's document on disk.</summary>
    public string DestinationPath { get; }
    /// <summary>The remote transfer destination naming the destination row behind its door.</summary>
    public WorldInstanceHost.TransferDestination RemoteDestination => WorldInstanceHost.TransferDestination.Remote(
        authority: Door.ListenEndpoint!,
        documentPath: DestinationPath,
        name: DestinationName
    );

    /// <summary>Builds the two hosts.</summary>
    /// <param name="travelerRouteStarted">The source host's traveler-route callback, or <see langword="null"/>.</param>
    /// <returns>The hosts, which the caller disposes.</returns>
    public static FederatedHosts Build(Action<WorldRemoteAuthority>? travelerRouteStarted = null) => new(travelerRouteStarted: travelerRouteStarted);
    /// <summary>Hands a traveler the destination row holds on to the onward row, then asks the destination for the
    /// traveler's route through <paramref name="traveler"/>. The destination's occupant takes
    /// <paramref name="travelTurn"/> before it leaves, and a spawned arrival carries it, so the onward route describes
    /// it. Runs on the caller's thread, while nothing else steps or drains the destination.</summary>
    /// <param name="traveler">The source host's route wrapper for the traveler.</param>
    /// <param name="travelTurn">The occupant's accumulated arrival turn as it leaves the destination row.</param>
    /// <returns>The onward route.</returns>
    public WorldAuthorityRouteDescription HandOn(WorldRemoteAuthority traveler, FixedQ4816 travelTurn) {
        if (!traveler.TryRouteCredential(
            bodyIndex: -1,
            credential: out var credential
        )) {
            throw new InvalidOperationException(message: "the traveler's route wrapper holds no committed credential");
        }

        // The traveler leaves under its own admitted peer principal, as a crossing the destination's own scan mints does:
        // the arrival's exclusive Drive grant refuses any other principal's leave.
        var traveling = DestinationRow.Server.ExecuteAuthorityOperation(operation: () => {
            DestinationRow.Server.Population.SetTravelTurn(
                slot: credential.BodyIndex,
                travelTurn: travelTurn
            );

            return DestinationRow.Server.Population.PeerPrincipal(index: credential.BodyIndex);
        });

        _ = Destination.EnqueueTransfer(
            actingPrincipal: traveling,
            destination: WorldInstanceHost.TransferDestination.Existing(name: OnwardName),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: DestinationName,
            sourceSlot: credential.BodyIndex
        );
        Destination.DrainPendingTransfers();
        if (DestinationRow.Server.ExecuteAuthorityOperation(operation: () => DestinationRow.Server.Population.IsActive(index: credential.BodyIndex))) {
            throw new InvalidOperationException(message: $"'{DestinationName}' did not hand body:{credential.BodyIndex} on to '{OnwardName}'");
        }

        return (traveler.TryDescribeRoute(
            bodyIndex: credential.BodyIndex,
            reason: out var reason,
            route: out var route
        )
            ? route
            : throw new InvalidOperationException(message: $"the destination described no onward route ({reason})"));
    }
    /// <summary>Joins local seat <paramref name="slot"/> on the source's boot row, gives its occupant
    /// <paramref name="travelTurn"/>, and steps the source once.</summary>
    /// <param name="slot">The local seat.</param>
    /// <param name="travelTurn">The occupant's accumulated arrival turn.</param>
    public void JoinSeat(int slot, FixedQ4816 travelTurn) {
        var reply = SourceRow.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: Principal.Seat(slot: slot),
            Slot: slot,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        ));

        if (!reply.Accepted) {
            throw new InvalidOperationException(message: $"seat {slot} did not join the source ({reply})");
        }

        _ = Seats.OccupySeat(
            profile: null,
            slot: slot
        );
        SourceRow.Server.Population.SetTravelTurn(
            slot: slot,
            travelTurn: travelTurn
        );
        Source.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
    }
    /// <inheritdoc/>
    public void Dispose() {
        Source.Dispose();
        Destination.Dispose();
        SourceRow.Dispose();
        DestinationRow.Dispose();
        OnwardRow.Dispose();
        m_sourceNetwork.Dispose();
        m_destinationNetwork.Dispose();
        m_sourceOracle.Dispose();
        m_destinationOracle.Dispose();
        m_scratch.Dispose();
    }
}
