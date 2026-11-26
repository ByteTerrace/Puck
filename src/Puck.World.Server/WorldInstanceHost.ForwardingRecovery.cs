using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    /// <inheritdoc/>
    public ValueTask<byte[]?> FetchForwardedPrototypeAsync(WorldServer source, WorldTravelerObservation request, Puck.Assets.ContentPin pin) =>
        (((request.RemainingHops > 1) && m_forwardedBodies.TryGetValue(left: source, right: request.Mobility.Incarnation, value: out var route))
            ? route.Authority.FetchPrototypeAsync(pin: pin, ceiling: request.Ceiling, remainingHops: ((byte)(request.RemainingHops - 1)))
            : default);
    /// <inheritdoc/>
    public Task<WorldProjectionOutcome> StreamForwardedProjectionAsync(WorldServer source, WorldTravelerObservation request, WorldProjectionSession session, CancellationToken ct) {
        if (request.RemainingHops <= 1) { return Task.FromResult(result: WorldProjectionOutcome.Refused(reason: "traveler projection exceeded its forwarding hop limit")); }
        return (m_forwardedBodies.TryGetValue(
            left: source,
            right: request.Mobility.Incarnation,
            value: out var route
        )
            ? route.Authority.StreamProjectionAsync(
                session,
                request.Ceiling,
                ((byte)(request.RemainingHops - 1)),
                ct
            )
            // The route went between the caller's check and this read: the traveler landed back at the source.
            : Task.FromResult(result: WorldProjectionOutcome.InFlight(reason: "traveler has no committed onward projection route"))
        );
    }

    private bool HasForwardingFrom(WorldServer source) => m_forwardedBodies.ContainsLeft(left: source);
    private List<(WorldEntityAddress Incarnation, ForwardedBody Body)> PrepareForwardedBodies(WorldInstance row,
        IReadOnlyList<WorldForwardedBodyCheckpoint> records) {
        var result = new List<(WorldEntityAddress, ForwardedBody)>(capacity: records.Count);
        var identities = new HashSet<WorldEntityAddress>();

        foreach (var record in records) {
            void Refuse(string reason) => throw new ArgumentException(
                message: $"forwarded route for '{row.Name}': {reason}",
                paramName: nameof(records)
            );
            if (
                !identities.Add(item: record.SourceIncarnation) ||
                (record.SourceIncarnation != record.Mobility.Incarnation) ||
                string.IsNullOrWhiteSpace(value: record.SourceIncarnation.Authority) ||
                (record.SourceIncarnation.Generation < 0) ||
                (((uint)record.SourceIncarnation.Index) >= WorldBodiesLimits.IndexLimit) ||
                (record.Mobility.Epoch == 0) ||
                (record.SourceAuthority != row.Server.AuthorityIdentity) ||
                string.IsNullOrWhiteSpace(value: record.DestinationAddress.Authority) ||
                (record.DestinationAddress.Index != record.DestinationBodyIndex) ||
                (record.DestinationAddress.Generation != 0) ||
                (((uint)record.DestinationBodyIndex) >= WorldBodiesLimits.IndexLimit)
            ) {
                Refuse(reason: "invalid or duplicated incarnation, source namespace, destination, or ownership epoch");
            }
            WorldDefinition? definition = null;

            if (record.DestinationEndpoint is { } endpoint) {
                if (
                    !Puck.Networking.PeerEndpoint.TryParse(
                    endpoint: out _,
                    value: endpoint
                ) ||
                    (record.DestinationDefinitionJson is null)
                ) {
                    Refuse(reason: "remote destination needs a valid endpoint and definition");
                }
                try { definition = WorldDefinitionSerialization.Deserialize(utf8Json: record.DestinationDefinitionJson!); } catch (InvalidDataException exception) {
                    throw new ArgumentException(
                    "invalid forwarding destination definition",
                    nameof(records),
                    exception
                );
                }
            } else if (record.DestinationDefinitionJson is not null) { Refuse(reason: "local destination cannot carry a remote definition"); }
            var destination = new WorldForwardingDestination(
                record.DestinationAddress.Authority,
                record.SourceAuthority,
                record.Mobility,
                record.DestinationEndpoint,
                definition
            );

            result.Add(item: (record.SourceIncarnation, new(
                Authority: new WorldDeferredForwardedAuthority(destination),
                BodyIndex: record.DestinationBodyIndex
            )));
        }
        return result;
    }
    // Null when the source row is local-only: it holds no peer network to dial the recovered destination through, so
    // the binding stays deferred exactly as it does for a row that is not admitted yet.
    private WorldRemoteAuthority? RecoveryRemoteAuthority(WorldInstance source, string sourceAuthority,
        string authority, string endpoint, WorldDefinition definition) {
        var key = (sourceAuthority, authority, endpoint);

        if (source.Federation.Network is null) { return null; }
        if (!m_recoveredRemoteAuthorities.TryGetValue(
            key: key,
            value: out var remote
        )) {
            remote = new(
                endpoint,
                definition,
                source.Federation.Authenticator,
                source.Federation.Subject,
                applicationStopping: m_applicationStopping,
                expectedAuthority: authority,
                network: source.Federation.Network,
                boundary: m_linkBoundary
            );
            m_recoveredRemoteAuthorities.Add(
                key: key,
                value: remote
            );
        }
        return remote;
    }
    // Host-thread only: never inspect the mutable instance registry from a socket reader. A deferred arm remains
    // in the concurrent route table, answering by name and remaining capturable while its target is absent.
    private void ResolveForwardedRecoveries() {
        foreach (var pair in m_forwardedBodies.Entries) {
            if (pair.Value.Authority is not WorldDeferredForwardedAuthority { IsBound: false } deferred) { continue; }
            var description = deferred.DescribeForCheckpoint();

            if (FindRecoveryDestination(authority: description.DestinationAuthority) is { } destination) {
                if (deferred.TryBind(current: new WorldLocalForwardedAuthority(
                    destination.Server,
                    (destination.Server.Definition.Host.Authority ?? EndpointFor(instance: destination).Identity),
                    description.SourceAuthority,
                    description.Mobility
                ))) {
                    m_recoveredRoutes.Add(item: pair.Key);
                }
            } else if (
                (description.Endpoint is { } endpoint) &&
                (description.Definition is { } definition)
            ) {
                var source = m_instances.Values.FirstOrDefault(predicate: candidate => ReferenceEquals(
                    objA: candidate.Server,
                    objB: pair.Key.Left
                ));

                if (source is null) { continue; }
                if (RecoveryRemoteAuthority(
                    source,
                    description.SourceAuthority,
                    description.DestinationAuthority,
                    endpoint,
                    definition
                ) is not { } remote) { continue; }

                deferred.TryBind(current: RemoteArm(
                    authority: remote,
                    credential: new(
                        pair.Value.BodyIndex,
                        description.SourceAuthority,
                        description.Mobility
                    ),
                    source: pair.Key.Left
                ));
            }
        }
    }
    // The socket arm of a route `source` keeps. The remote authority's answer that it holds no credential for the
    // traveler is queued for the host loop, which retires the route (RetireUnheldRoutes).
    private WorldRemoteForwardedAuthority RemoteArm(WorldServer source, WorldRemoteAuthority authority, WorldRemoteRouteCredential credential) => new(
        authority: authority,
        credential: credential,
        unheld: destination => m_unheldRoutes.Enqueue(item: (source, destination))
    );
    private void RemoveSourceForwarding(WorldServer source) {
        foreach (var incarnation in m_forwardedBodies.RightsOf(left: source)) {
            if (m_forwardedBodies.TryRemove(left: source, right: incarnation, removed: out var removed)) {
                (removed.Authority as IDisposable)?.Dispose();
            }
        }
    }
    /// <inheritdoc/>
    /// <remarks>Queued for the host loop (<see cref="DrainPendingTransfers"/>): the authority that retired the body
    /// calls this under its own gate, and no authority's gate is held while another authority is entered.</remarks>
    public void RetireTraveler(WorldEntityAddress incarnation) => m_endedTravelers.Enqueue(item: incarnation);
    /// <inheritdoc/>
    /// <remarks>Queued for the host loop, as <see cref="RetireTraveler"/> is.</remarks>
    public void SetTravelerPresence(WorldServer source, in WorldMobilityIdentity mobility, bool present) =>
        m_travelerPresence.Enqueue(item: (source, mobility.Incarnation, present));
    /// <inheritdoc/>
    /// <remarks>Queued for the host loop, as <see cref="RetireTraveler"/> is.</remarks>
    public void TravelerLanded(WorldServer destination, in WorldMobilityIdentity mobility) =>
        m_landedTravelers.Enqueue(item: (destination, mobility));

    // The travelers that landed at an authority of this host since the last drain, each with the ownership epoch it
    // landed with. Written under the landing authority's gate and drained on the host loop (RetireReturnedRoutes).
    private readonly System.Collections.Concurrent.ConcurrentQueue<(WorldServer Destination, WorldMobilityIdentity Mobility)> m_landedTravelers = new();

    // A route an authority kept for a traveler stands until the traveler lands at that authority again: from then on
    // the traveler is served where it stands, and when it next leaves, the route it leaves by replaces this one. Left
    // in place, the old route would be followed in the interval between that departure's detach and its publication,
    // around a loop that ends at this authority. The epoch a route's credential carries is one past the epoch the
    // traveler held when it left, so a route at or below the landing's epoch is from before the landing, and one
    // above it was published since and stays.
    private void RetireReturnedRoutes() {
        while (m_landedTravelers.TryDequeue(result: out var landed)) {
            if (
                m_forwardedBodies.TryGetValue(left: landed.Destination, right: landed.Mobility.Incarnation, value: out var route) &&
                (route.Authority.DescribeForCheckpoint().Mobility.Epoch <= landed.Mobility.Epoch) &&
                m_forwardedBodies.TryRemove(left: landed.Destination, right: landed.Mobility.Incarnation, removed: out var returned)
            ) {
                (returned.Authority as IDisposable)?.Dispose();
            }
        }
    }

    // The routes a recovery bound to a colocated destination since the last drain, each checked against its
    // destination on the drain that follows (RetireUnheldRecoveries).
    private readonly List<(WorldServer Source, WorldEntityAddress Incarnation)> m_recoveredRoutes = [];

    // The routes whose next hop, in another process, answered that it holds no credential for the traveler
    // (WorldFederationRefusal.TravelerUnknown), with the destination the answering arm forwarded to. Written from
    // whichever thread reads the answer and drained on the host loop (RetireUnheldRoutes).
    private readonly System.Collections.Concurrent.ConcurrentQueue<(WorldServer Source, WorldForwardingDestination Destination)> m_unheldRoutes = new();

    // A route stands only while its next hop holds a credential for its traveler: a next hop that holds none retired
    // the traveler, and nothing reaches it from here. Two things break that without this host being told. A colocated
    // destination tells the host under its own gate and the host drops the routes on its next loop (RetireTraveler), so
    // a process that stopped between the two recovers the forwarding row's route and a destination that holds no
    // credential for it. And a destination in another process tells no one upstream. Each is caught where the next
    // hop is asked: a recovered colocated route on the first drain after its recovery (RetireUnheldRecoveries), and a
    // socket route when its next hop answers TravelerUnknown to anything this host sends it for the traveler
    // (RetireUnheldRoutes). Either retires the traveler through RetireForwardedTraveler, as the word would have.
    //
    // A recovered route is not checked where it is bound, because a destination is bound as it is admitted, before its
    // own checkpoint and crossing log are redone; every row has recovered by the first drain after its admission
    // (RecoverCrossings).
    private void RetireUnheldRecoveries() {
        if (m_recoveredRoutes.Count == 0) {
            return;
        }
        foreach (var (source, incarnation) in m_recoveredRoutes) {
            if (
                !m_forwardedBodies.TryGetValue(left: source, right: incarnation, value: out var route) ||
                (route.Authority is not WorldDeferredForwardedAuthority { IsBound: true } bound)
            ) {
                continue;
            }
            var description = bound.DescribeForCheckpoint();

            if (
                (FindRecoveryDestination(authority: description.DestinationAuthority) is not { } destination) ||
                destination.Server.HoldsTransferredTraveler(incarnation: incarnation, sourceAuthority: description.SourceAuthority)
            ) {
                continue;
            }
            RetireUnheld(description: description, told: null);
        }
        m_recoveredRoutes.Clear();
    }
    // A late answer about a route this host has since replaced (the traveler came back and left again, at a later
    // epoch or for another destination) retires nothing: only the route the answering arm stood for is retired.
    private void RetireUnheldRoutes() {
        while (m_unheldRoutes.TryDequeue(result: out var unheld)) {
            if (!m_forwardedBodies.TryGetValue(left: unheld.Source, right: unheld.Destination.Mobility.Incarnation, value: out var route)) {
                continue;
            }
            var current = route.Authority.DescribeForCheckpoint();

            if (
                (current.DestinationAuthority == unheld.Destination.DestinationAuthority) &&
                (current.SourceAuthority == unheld.Destination.SourceAuthority) &&
                (current.Mobility == unheld.Destination.Mobility)
            ) {
                // The hop that answered has nothing left to be told.
                RetireUnheld(description: current, told: route.Authority);
            }
        }
    }
    private void RetireUnheld(WorldForwardingDestination description, IWorldForwardedAuthority? told) {
        if (m_narration.HasNarrationSink) {
            m_narration.Narrate(
                channel: "world.transfer",
                text: $"[world.transfer: route of traveler {description.Mobility.Incarnation} from '{description.SourceAuthority}' retired: '{description.DestinationAuthority}' holds no credential for it]"
            );
        }
        RetireForwardedTraveler(incarnation: description.Mobility.Incarnation, told: told);
    }
    // Applies, in the order they were queued, what the authorities of this host learned since the last drain about
    // travelers they forward: whether each one's source still attends to it, passed to the authority it stands at, and
    // each one whose body was retired where it stood. Runs on the host loop.
    private void RetireEndedTravelers() {
        RetireReturnedRoutes();
        RetireUnheldRecoveries();
        RetireUnheldRoutes();
        while (m_travelerPresence.TryDequeue(result: out var presence)) {
            if (m_forwardedBodies.TryGetValue(left: presence.Source, right: presence.Incarnation, value: out var route)) {
                route.Authority.SetPresence(present: presence.Present);
            }
        }
        while (m_endedTravelers.TryDequeue(result: out var incarnation)) {
            RetireForwardedTraveler(incarnation: incarnation);
        }
    }
    // The one retirement of a traveler that is gone for good: every authority of this host that still forwards to it
    // drops its route and its credential. An accepted leave reaches it when the leave's answer arrives, and a body
    // retired where it stood (its reconnect grace ran out, or it disconnected with none) reaches it from the authority
    // that retired it. A traveler can revisit an authority, so every retained branch for the incarnation goes, a stale
    // route the last request did not traverse included. Each dropped route's next hop is told, so an authority in
    // another process drops what it keeps for the traveler too (IWorldForwardedAuthority.TellRetired). `told` is the
    // hop the retirement came from, which already knows: the one that answered the leave, or that it holds nothing.
    private void RetireForwardedTraveler(WorldEntityAddress incarnation, IWorldForwardedAuthority? told = null) {
        foreach (var source in m_forwardedBodies.LeftsOf(right: incarnation)) {
            if (m_forwardedBodies.TryRemove(left: source, right: incarnation, removed: out var retired)) {
                if (!ReferenceEquals(objA: retired.Authority, objB: told)) {
                    retired.Authority.TellRetired();
                }
                (retired.Authority as IDisposable)?.Dispose();
                source.RetireTransferredIncarnation(incarnation: incarnation);
            }
        }
    }
}
