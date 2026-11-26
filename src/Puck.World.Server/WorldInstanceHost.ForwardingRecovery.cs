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
    public Task<string?> StreamForwardedProjectionAsync(WorldServer source, WorldTravelerObservation request, Stream output, CancellationToken ct) {
        if (request.RemainingHops <= 1) { return Task.FromResult<string?>(result: "traveler projection exceeded its forwarding hop limit"); }
        return (m_forwardedBodies.TryGetValue(
            left: source,
            right: request.Mobility.Incarnation,
            value: out var route
        )
            ? route.Authority.StreamProjectionAsync(
                output,
                request.Ceiling,
                ((byte)(request.RemainingHops - 1)),
                ct
            )
            : Task.FromResult<string?>(result: "traveler has no committed onward projection route")
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

                deferred.TryBind(current: new WorldRemoteForwardedAuthority(
                    authority: remote,
                    credential: new(
                        pair.Value.BodyIndex,
                        description.SourceAuthority,
                        description.Mobility
                    )
                ));
            }
        }
    }
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

    // The routes a recovery bound to a colocated destination since the last drain, each checked against its
    // destination on the drain that follows (RetireUnheldRecoveries).
    private readonly List<(WorldServer Source, WorldEntityAddress Incarnation)> m_recoveredRoutes = [];

    // A recovered route stands only while its destination holds a credential for its traveler. A destination that
    // retires a traveler tells this host under its own gate, and the host drops the routes on its next loop
    // (RetireTraveler); a process that stopped between the two recovers the forwarding row's route and a destination
    // that holds no credential for it. Such a route is retired here as the destination's word would have retired it.
    // The check waits for the drain because a destination is bound as it is admitted, before its own checkpoint and
    // crossing log are redone, and every row has recovered by the first drain after its admission (RecoverCrossings).
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
                destination.Server.TryTransferredPrincipal(mobility: description.Mobility, principal: out _, sourceAuthority: description.SourceAuthority)
            ) {
                continue;
            }
            if (m_narration.HasNarrationSink) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: recovered route of traveler {incarnation} from '{description.SourceAuthority}' retired: '{description.DestinationAuthority}' holds no credential for it]"
                );
            }
            RetireForwardedTraveler(incarnation: incarnation);
        }
        m_recoveredRoutes.Clear();
    }
    // Applies, in the order they were queued, what the authorities of this host learned since the last drain about
    // travelers they forward: whether each one's source still attends to it, passed to the authority it stands at, and
    // each one whose body was retired where it stood. Runs on the host loop.
    private void RetireEndedTravelers() {
        RetireUnheldRecoveries();
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
    // route the last request did not traverse included.
    private void RetireForwardedTraveler(WorldEntityAddress incarnation) {
        foreach (var source in m_forwardedBodies.LeftsOf(right: incarnation)) {
            if (m_forwardedBodies.TryRemove(left: source, right: incarnation, removed: out var retired)) {
                (retired.Authority as IDisposable)?.Dispose();
                source.RetireTransferredIncarnation(incarnation: incarnation);
            }
        }
    }
}
