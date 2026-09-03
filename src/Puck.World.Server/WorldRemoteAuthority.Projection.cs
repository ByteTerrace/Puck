using Puck.Networking;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldRemoteAuthority {
    private sealed class ObservationLease : IDisposable {
        private CancellationTokenSource? m_source;
        public ObservationLease(CancellationToken parent) {
            m_source = CancellationTokenSource.CreateLinkedTokenSource(parent);
            Token = m_source.Token;
        }
        public CancellationToken Token { get; }
        public void Dispose() {
            var source = Interlocked.Exchange(ref m_source, null);
            if (source is null) { return; }
            source.Cancel();
            source.Dispose();
        }
    }
    internal async Task<string?> RelayProjectionAsync(WorldTravelerObservation request, Stream output, CancellationToken ct) {
        var upstream = m_submissionAuthority ?? this;
        await using var stream = await upstream.m_network.ConnectAsync(upstream.m_route.Endpoint, ct).ConfigureAwait(false);
        await HandshakeWireFormat.WriteHelloAsync(stream, WorldFederationCodec.WireKey, ct).ConfigureAwait(false);
        await upstream.AuthenticateAsync(stream, ct).ConfigureAwait(false);
        await WorldFederationCodec.WriteRequestAsync(stream, WorldFederationRequest.ObserveTraveler,
            WorldFederationCodec.EncodeTravelerObservation(in request), ct).ConfigureAwait(false);
        await WorldProjectionStream.RunAsync(output, async token => {
            while (!token.IsCancellationRequested) {
                var frame = await WorldFederationCodec.ReadResponseAsync(stream, token).ConfigureAwait(false);
                if (!frame.Ok) { throw new IOException($"traveler projection relay failed: {frame.Failure}"); }
                var kind = (WorldFederationResponse)frame.Kind;
                if (kind is not (WorldFederationResponse.Route or WorldFederationResponse.Definition or WorldFederationResponse.Snapshot
                    or WorldFederationResponse.ProjectionInvalidated or WorldFederationResponse.Refusal)) {
                    throw new IOException($"unexpected traveler projection response {kind}");
                }
                await WorldFederationCodec.WriteResponseAsync(output, kind, frame.Body, token).ConfigureAwait(false);
                if (kind is WorldFederationResponse.ProjectionInvalidated or WorldFederationResponse.Refusal) { return; }
            }
        }, ct).ConfigureAwait(false);
        return null;
    }
}
