using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    /// <inheritdoc/>
    public Task<string?> StreamForwardedProjectionAsync(WorldServer source, WorldTravelerObservation request, Stream output, CancellationToken ct) {
        if (request.RemainingHops <= 1) { return Task.FromResult<string?>("traveler projection exceeded its forwarding hop limit"); }
        return m_forwardedBodies.TryGetValue((source, request.Mobility.Incarnation), out var route)
            ? route.Authority.StreamProjectionAsync(output, request.Ceiling, (byte)(request.RemainingHops - 1), ct)
            : Task.FromResult<string?>("traveler has no committed onward projection route");
    }
    private bool HasForwardingFrom(WorldServer source) {
        foreach (var pair in m_forwardedBodies) {
            if (ReferenceEquals(pair.Key.Server, source)) { return true; }
        }
        return false;
    }
    private List<(WorldEntityAddress Incarnation, ForwardedBody Body)> PrepareForwardedBodies(WorldInstance row,
        IReadOnlyList<WorldForwardedBodyCheckpoint> records) {
        var result = new List<(WorldEntityAddress, ForwardedBody)>(records.Count);
        var identities = new HashSet<WorldEntityAddress>();
        foreach (var record in records) {
            void Refuse(string reason) => throw new ArgumentException($"forwarded route for '{row.Name}': {reason}", nameof(records));
            if (!identities.Add(record.SourceIncarnation) || record.SourceIncarnation != record.Mobility.Incarnation ||
                string.IsNullOrWhiteSpace(record.SourceIncarnation.Authority) || record.SourceIncarnation.Generation < 0 ||
                (uint)record.SourceIncarnation.Index >= WorldBodiesLimits.CapacityCeiling || record.Mobility.Epoch == 0 ||
                record.SourceAuthority != row.Server.AuthorityIdentity || string.IsNullOrWhiteSpace(record.DestinationAddress.Authority) ||
                record.DestinationAddress.Index != record.DestinationBodyIndex || record.DestinationAddress.Generation != 0 ||
                (uint)record.DestinationBodyIndex >= WorldBodiesLimits.CapacityCeiling) {
                Refuse("invalid or duplicated incarnation, source namespace, destination, or ownership epoch");
            }
            WorldDefinition? definition = null;
            if (record.DestinationEndpoint is { } endpoint) {
                if (!System.Net.IPEndPoint.TryParse(endpoint, out _) || record.DestinationDefinitionJson is null) {
                    Refuse("remote destination needs a valid endpoint and definition");
                }
                try { definition = WorldDefinitionSerialization.Deserialize(record.DestinationDefinitionJson!); }
                catch (InvalidDataException exception) { throw new ArgumentException("invalid forwarding destination definition", nameof(records), exception); }
            } else if (record.DestinationDefinitionJson is not null) { Refuse("local destination cannot carry a remote definition"); }
            var destination = new WorldForwardingDestination(record.DestinationAddress.Authority, record.SourceAuthority,
                record.Mobility, record.DestinationEndpoint, definition);
            result.Add((record.SourceIncarnation, new(new WorldDeferredForwardedAuthority(destination), record.DestinationBodyIndex)));
        }
        return result;
    }

    private WorldRemoteAuthority RecoveryRemoteAuthority(WorldInstance source, string sourceAuthority,
        string authority, string endpoint, WorldDefinition definition) {
        var key = (sourceAuthority, authority, endpoint);
        if (!m_recoveredRemoteAuthorities.TryGetValue(key, out var remote)) {
            remote = new(endpoint, definition, source.Federation.Authenticator, source.Federation.Subject,
                applicationStopping: m_applicationStopping, expectedAuthority: authority, network: source.Federation.Network);
            m_recoveredRemoteAuthorities.Add(key, remote);
        }
        return remote;
    }

    // Host-thread only: never inspect the mutable instance registry from a socket reader. A deferred arm remains
    // in the concurrent route table, answering by name and remaining capturable while its target is absent.
    private void ResolveForwardedRecoveries() {
        foreach (var pair in m_forwardedBodies) {
            if (pair.Value.Authority is not WorldDeferredForwardedAuthority { IsBound: false } deferred) { continue; }
            var description = deferred.DescribeForCheckpoint();
            if (FindRecoveryDestination(description.DestinationAuthority) is { } destination) {
                deferred.TryBind(new WorldLocalForwardedAuthority(destination.Server,
                    destination.Server.Definition.Host.Authority ?? EndpointFor(destination).Identity,
                    description.SourceAuthority, description.Mobility));
            } else if (description.Endpoint is { } endpoint && description.Definition is { } definition) {
                var source = m_instances.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Server, pair.Key.Server));
                if (source is null) { continue; }
                var remote = RecoveryRemoteAuthority(source, description.SourceAuthority, description.DestinationAuthority, endpoint, definition);
                deferred.TryBind(new WorldRemoteForwardedAuthority(remote,
                    new(pair.Value.BodyIndex, description.SourceAuthority, description.Mobility)));
            }
        }
    }

    private void RemoveSourceForwarding(WorldServer source) {
        foreach (var pair in m_forwardedBodies) {
            if (ReferenceEquals(pair.Key.Server, source) && m_forwardedBodies.TryRemove(pair.Key, out var removed)) {
                (removed.Authority as IDisposable)?.Dispose();
            }
        }
    }

    private void RetireForwardedTraveler(in WorldMobilityIdentity mobility) {
        // A traveler can revisit an authority. Terminal leave retires every retained local branch for this
        // incarnation, including a stale route owned by the final authority that this request did not traverse.
        foreach (var pair in m_forwardedBodies) {
            if (pair.Key.Incarnation == mobility.Incarnation && m_forwardedBodies.TryRemove(pair.Key, out var retired)) {
                (retired.Authority as IDisposable)?.Dispose();
                pair.Key.Server.RetireTransferredMobility(in mobility);
            }
        }
    }
}
