using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Resolves and streams one authenticated traveler view, never holding an authority gate across network I/O.</summary>
internal static class WorldTravelerProjection {
    public static async Task<string?> StreamAsync(WorldServer server, WorldTravelerObservation request, string endpoint, Stream output, CancellationToken ct) {
        if (request.RemainingHops is 0 or > 64) { return "traveler projection exceeded its forwarding hop limit"; }
        WorldFederationProjectionSink? sink = null;
        IDisposable? lease = null;
        var refusal = server.ExecuteAuthorityOperation(() => {
            if (!server.TryTransferredPrincipal(request.SourceAuthority, request.Mobility, out var principal)) {
                return "the projection credential names no committed traveler";
            }
            if (WorldAdmissionDoor.TryAdmitArrival(server.Definition.Admission, request.SourceAuthority, out var admission) is not null) {
                return "the source authority is no longer admitted for traveler projection";
            }
            request = request with { Ceiling = (WorldDisclosureTier)Math.Min((byte)request.Ceiling, (byte)admission!.Tier) };
            if (request.Ceiling == WorldDisclosureTier.Frames) { return "the projection disclosure tier permits no world document"; }
            if (!WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(server, principal)) { return null; }
            var definition = server.Definition;
            var subject = GrantSubject.Body(principal.Index);
            if (!server.Grants.Allows(principal, WorldCapability.Observe, subject).IsAllowed) {
                return "the traveler holds no Observe grant for its body";
            }
            bool Current() => ReferenceEquals(server.Definition, definition) &&
                server.TryTransferredPrincipal(request.SourceAuthority, request.Mobility, out var current) && current == principal &&
                WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(server, principal) &&
                server.Grants.Allows(principal, WorldCapability.Observe, subject).IsAllowed;
            var disclosure = new WorldSinkDisclosure(definition.Population.ObserverDisclosure, principal.Index);
            sink = new(request.Ceiling, server.AuthorityIdentity, () => server.Population.Revision,
                () => disclosure, Current, principal);
            sink.PrimeRoute(WorldLocalForwardedAuthority.DescribeRoute(server, endpoint, principal));
            lease = server.AttachSink(sink: sink);
            return (string?)null;
        });
        if (refusal is not null) { return refusal; }
        if (sink is null || lease is null) {
            return server.TransferForwarder is { } forwarder
                ? await forwarder.StreamForwardedProjectionAsync(server, request, output, ct).ConfigureAwait(false)
                : "the traveler has no committed onward projection route";
        }
        try { await sink.StreamAsync(output, ct).ConfigureAwait(false); }
        finally { server.ExecuteAuthorityOperation(lease.Dispose); }
        return null;
    }
}
