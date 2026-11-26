using Puck.Networking;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldRemoteAuthority {
    // The projection codec hydrates synchronously on its network reader. Tick-side forwarding uses the async door.
    private byte[]? FetchPrototype(Puck.Assets.ContentPin pin, WorldTravelerObservation? traveler) =>
        FetchPrototypeAsync(pin, traveler).AsTask().GetAwaiter().GetResult();

    internal async ValueTask<byte[]?> FetchPrototypeAsync(Puck.Assets.ContentPin pin, WorldTravelerObservation? traveler) {
        // A fetch with no traveller is asked as this authority itself, the name its lanes are opened under.
        var answer = await AnswerAsync(sourceAuthority: (traveler?.SourceAuthority ?? m_observerAuthority),
            kind: WorldFederationRequest.Prototype,
            body: WorldFederationCodec.EncodePrototypeRequest(pin: pin, traveler: traveler)).ConfigureAwait(false);

        return ((answer.Kind == WorldFederationResponse.Prototype) ? answer.Body.ToArray() : null);
    }

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

    // Serves one segment of a traveler's stream from the authority at the far end of this route. That authority takes
    // the stream up from this process's lineage and stamps its records itself; each is relayed as it arrives, and the
    // lineage here follows its stamp, so the stream's next segment goes on after the last record relayed.
    internal async Task<WorldProjectionOutcome> RelayProjectionAsync(WorldTravelerObservation request, WorldProjectionSession session, CancellationToken ct) {
        var upstream = (m_submissionAuthority ?? this);
        var relay = new object();
        var output = session.Output;

        request = request with { Continuation = session.Lineage.Lend(owner: relay) };
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
        using var acknowledging = session.Receive(acknowledge: async (_, bytes, token) => {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        });

        while (true) {
            var frame = await WorldFederationCodec.ReadResponseAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!frame.Ok) { throw new IOException(message: $"traveler projection relay failed: {frame.Failure}"); }
            var kind = ((WorldFederationResponse)frame.Kind);

            switch (kind) {
                case WorldFederationResponse.ProjectionMoved:
                    return WorldProjectionOutcome.Moved;
                case WorldFederationResponse.ProjectionInvalidated:
                case WorldFederationResponse.Refusal:
                    break;
                case WorldFederationResponse.Route:
                case WorldFederationResponse.Definition:
                case WorldFederationResponse.PresentationRows:
                case WorldFederationResponse.ProjectionDelta:
                case WorldFederationResponse.Snapshot:
                case WorldFederationResponse.SnapshotDelta:
                case WorldFederationResponse.Cut:
                    // Every record of the segment opens with its stamp, a cut included.
                    if (
                        !WorldFederationCodec.TryReadStamped(record: frame.Body.Span, stream: out var stamp, body: out _, failure: out _) ||
                        !session.Lineage.Follow(owner: relay, relayed: in stamp)
                    ) {
                        throw new IOException(message: $"a relayed traveler projection {kind} record is out of its stream's order");
                    }
                    break;
                default:
                    throw new IOException(message: $"unexpected traveler projection response {kind}");
            }
            await WorldFederationCodec.WriteResponseAsync(
                output,
                kind,
                frame.Body,
                ct
            ).ConfigureAwait(continueOnCapturedContext: false);
            if (kind is WorldFederationResponse.ProjectionInvalidated or WorldFederationResponse.Refusal) {
                return WorldProjectionOutcome.Ended;
            }
        }
    }
}
