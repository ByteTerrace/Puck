using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Resolves and streams one authenticated traveler view, never holding an authority gate across network I/O.</summary>
internal static class WorldTravelerProjection {
    public static async Task<string?> StreamAsync(WorldServer server, WorldTravelerObservation request, string endpoint, Stream output, CancellationToken ct) {
        if (request.RemainingHops is 0 or > 64) { return "traveler projection exceeded its forwarding hop limit"; }
        WorldFederationProjectionSink? sink = null;
        IDisposable? lease = null;
        var refusal = server.ExecuteAuthorityOperation(operation: () => {
            if (!server.TryTransferredPrincipal(
                request.SourceAuthority,
                request.Mobility,
                out var principal
            )) {
                return "the projection credential names no committed traveler";
            }
            if (WorldAdmissionDoor.TryAdmitArrival(
                entries: server.Definition.Admission,
                sourceAuthority: request.SourceAuthority,
                verdict: out var admission
            ) is not null) {
                return "the source authority is no longer admitted for traveler projection";
            }
            request = request with {
                Ceiling = ((WorldDisclosureTier)Math.Min(
                val1: ((byte)request.Ceiling),
                val2: ((byte)admission!.Tier)
            )),
            };
            if (request.Ceiling == WorldDisclosureTier.Frames) { return "the projection disclosure tier permits no world document"; }
            if (!WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(
                principal: principal,
                server: server
            )) { return null; }
            var definition = server.Definition;
            var subject = GrantSubject.Body(index: principal.Index);

            if (!server.Grants.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: subject
            ).IsAllowed) {
                return "the traveler holds no Observe grant for its body";
            }
            bool Current() => (ReferenceEquals(
                objA: server.Definition,
                objB: definition
            ) &&
                server.TryTransferredPrincipal(
                request.SourceAuthority,
                request.Mobility,
                out var current
            ) && (current == principal) &&
                WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(
                principal: principal,
                server: server
            ) &&
                server.Grants.Allows(
                capability: WorldCapability.Observe,
                principal: principal,
                subject: subject
            ).IsAllowed);
            var disclosure = new WorldSinkDisclosure(
                definition.Population.ObserverDisclosure,
                principal.Index
            );

            sink = new(
                request.Ceiling,
                server.AuthorityIdentity,
                () => server.Population.Revision,
                () => disclosure,
                Current,
                principal
            );
            sink.PrimeRoute(route: WorldLocalForwardedAuthority.DescribeRoute(
                endpoint: endpoint,
                principal: principal,
                server: server
            ));
            lease = server.AttachSink(sink: sink);
            return ((string?)null);
        });

        if (refusal is not null) { return refusal; }
        if (
            (sink is null) ||
            (lease is null)
        ) {
            return ((server.TransferForwarder is { } forwarder)
                ? await forwarder.StreamForwardedProjectionAsync(
                    ct: ct,
                    output: output,
                    request: request,
                    source: server
                ).ConfigureAwait(continueOnCapturedContext: false)
                : "the traveler has no committed onward projection route"
            );
        }
        try {
            await sink.StreamAsync(
            ct: ct,
            output: output
        ).ConfigureAwait(continueOnCapturedContext: false);
        } finally { server.ExecuteAuthorityOperation(operation: lease.Dispose); }
        return null;
    }
}
