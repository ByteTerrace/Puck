using Puck.Networking;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldRemoteAuthority {
    private sealed class ObservationLease : IDisposable {
        private CancellationTokenSource? m_source;

        public ObservationLease(CancellationToken parent) {
            m_source = CancellationTokenSource.CreateLinkedTokenSource(token: parent);
            Token = m_source.Token;
        }

        public CancellationToken Token { get; }

        public void Dispose() {
            var source = Interlocked.Exchange(
                location1: ref m_source,
                value: null
            );

            if (source is null) { return; }
            source.Cancel();
            source.Dispose();
        }
    }

    internal async Task<string?> RelayProjectionAsync(WorldTravelerObservation request, Stream output, CancellationToken ct) {
        var upstream = (m_submissionAuthority ?? this);
        await using var stream = await upstream.m_network.ConnectAsync(
            upstream.m_route.Endpoint,
            ct
        ).ConfigureAwait(continueOnCapturedContext: false);

        await HandshakeWireFormat.WriteHelloAsync(
            ct: ct,
            key: WorldFederationCodec.WireKey,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
        await upstream.AuthenticateAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
        await WorldFederationCodec.WriteRequestAsync(
            stream,
            WorldFederationRequest.ObserveTraveler,
            WorldFederationCodec.EncodeTravelerObservation(request: in request),
            ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        await WorldProjectionStream.RunAsync(
            output,
            async token => {
                while (!token.IsCancellationRequested) {
                    var frame = await WorldFederationCodec.ReadResponseAsync(
                        ct: token,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    if (!frame.Ok) { throw new IOException(message: $"traveler projection relay failed: {frame.Failure}"); }
                    var kind = ((WorldFederationResponse)frame.Kind);

                    if (kind is not (WorldFederationResponse.Route or WorldFederationResponse.Definition or WorldFederationResponse.Snapshot
                        or WorldFederationResponse.ProjectionInvalidated or WorldFederationResponse.Refusal)) {
                        throw new IOException(message: $"unexpected traveler projection response {kind}");
                    }
                    await WorldFederationCodec.WriteResponseAsync(
                        output,
                        kind,
                        frame.Body,
                        token
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    if (kind is WorldFederationResponse.ProjectionInvalidated or WorldFederationResponse.Refusal) { return; }
                }
            },
            ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        return null;
    }
}
