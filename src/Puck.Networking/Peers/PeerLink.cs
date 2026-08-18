using System.Net;
using System.Threading.Channels;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>One symmetric, authenticated connection between two peers. Either side may have dialed; after the
/// handshake both sides send and receive identically over the control stream. Every outbound message is signed by
/// this side's own identity, and every inbound message is verified against <see cref="RemoteId"/> — the identity
/// the peer proved at handshake, never a value a later message merely claims. A refused inbound message does not
/// close the link: it is reported through <see cref="Events"/> and the link keeps carrying honest traffic.</summary>
public sealed class PeerLink : IAsyncDisposable {
    private readonly IPeerConnection m_connection;
    private readonly Channel<PeerEvent> m_events = Channel.CreateUnbounded<PeerEvent>(options: new UnboundedChannelOptions { SingleReader = true });
    private readonly PeerIdentity m_local;
    private readonly Func<DateTimeOffset> m_now;
    private readonly Action<PeerLink>? m_onClosed;
    private readonly byte[] m_remoteSubjectPublicKeyInfo;
    private readonly Stream m_stream;
    private readonly SemaphoreSlim m_writeGate = new(initialCount: 1, maxCount: 1);

    private int m_closed;
    private Task? m_readLoop;

    internal PeerLink(IPeerConnection connection, Stream stream, PeerIdentity local, KeyId remoteId, byte[] remoteSubjectPublicKeyInfo, Action<PeerLink>? onClosed, Func<DateTimeOffset>? now = null) {
        m_connection = connection;
        m_stream = stream;
        m_local = local;
        m_remoteSubjectPublicKeyInfo = remoteSubjectPublicKeyInfo;
        m_onClosed = onClosed;
        m_now = (now ?? (static () => DateTimeOffset.UtcNow));

        RemoteId = remoteId;
    }

    /// <summary>Gets a channel of every message, refusal, and closure this link produces.</summary>
    public ChannelReader<PeerEvent> Events => m_events.Reader;
    /// <summary>Gets a value indicating whether the link is still open.</summary>
    public bool IsOpen => (Volatile.Read(location: ref m_closed) == 0);
    /// <summary>Gets the remote transport address.</summary>
    public EndPoint RemoteEndpoint => m_connection.RemoteEndpoint;
    /// <summary>Gets the identity the remote side proved at handshake.</summary>
    public KeyId RemoteId { get; }

    private void Publish(PeerEvent @event) => m_events.Writer.TryWrite(item: @event);
    private async Task ReadLoopAsync() {
        var reason = "the connection closed";

        try {
            while (IsOpen) {
                var frame = await WireFrame.ReadAsync(
                    ct: CancellationToken.None,
                    maxFrameBytes: PeerWireProtocol.MaxFrameBytes,
                    stream: m_stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!frame.Ok) {
                    reason = frame.Failure.ToString();

                    break;
                }

                if (frame.Kind != ((byte)PeerFrameKind.Message)) {
                    reason = $"the peer sent an unexpected frame kind ({frame.Kind}) on an established link";

                    break;
                }

                HandleMessageFrame(body: frame.Body);
            }
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException)) {
            reason = $"{exception.GetType().Name}: {exception.Message}";
        }

        await CloseAsync(reason: reason).ConfigureAwait(continueOnCapturedContext: false);
    }
    private void HandleMessageFrame(byte[] body) {
        var reader = new WireReader(bytes: body);
        var attestationBytes = reader.ReadBlock(
            field: "attestation",
            maxBytes: PeerWireProtocol.MaxFrameBytes
        );

        if (!reader.TryFinish(out var wireFailure)) {
            Publish(@event: new PeerEvent.Refused(Failure: new PeerFailure(
                Detail: wireFailure.ToString(),
                Refusal: PeerRefusal.HandshakeMalformed
            )));

            return;
        }

        if (TryVerifyMessage(
            failure: out var failure,
            payload: out var payload,
            wire: attestationBytes
        )) {
            Publish(@event: new PeerEvent.Received(Payload: payload));
        } else {
            Publish(@event: new PeerEvent.Refused(Failure: failure));
        }
    }
    private bool TryVerifyMessage(byte[] wire, out ReadOnlyMemory<byte> payload, out PeerFailure failure) {
        payload = default;

        SignedAttestation claim;

        try {
            claim = PeerWireProtocol.Profile.DecodeAttestation(
                codec: PeerWireProtocol.Codec,
                wire: wire
            );
        } catch (FormatException exception) {
            failure = new PeerFailure(
                Detail: exception.Message,
                Refusal: PeerRefusal.MessageUnsigned
            );

            return false;
        }

        if (
            !string.Equals(
            a: claim.Header.Domain,
            b: RemoteId.Domain,
            comparisonType: StringComparison.Ordinal
        ) ||
            !string.Equals(
            a: claim.Header.Subject,
            b: RemoteId.Subject,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            failure = new PeerFailure(
                Detail: $"the message names identity '{claim.Header.Domain}', not the '{RemoteId.Domain}' this link established at handshake",
                Refusal: PeerRefusal.MessageWrongSigner
            );

            return false;
        }

        var trustList = PeerWireProtocol.SingleEntryTrust(
            id: RemoteId,
            maximumAge: PeerWireProtocol.MaximumMessageClaimAge,
            reach: "message",
            subjectPublicKeyInfo: m_remoteSubjectPublicKeyInfo
        );
        var result = PeerWireProtocol.Profile.VerifyChain(
            chain: [],
            claim: claim,
            codec: PeerWireProtocol.Codec,
            expectedAudience: m_local.Id.Domain,
            expectedPurpose: PeerWireProtocol.MessagePurpose,
            now: m_now(),
            trustList: trustList
        );

        if (!result.Admits(slot: "message")) {
            failure = new PeerFailure(
                Detail: (result.RefusalReason ?? "the claim did not reach the message slot"),
                Refusal: PeerRefusal.MessageUnverified
            );

            return false;
        }

        payload = claim.PayloadBytes;
        failure = default;

        return true;
    }
    private async ValueTask CloseAsync(string reason) {
        if (Interlocked.Exchange(
            location1: ref m_closed,
            value: 1
        ) != 0) {
            return;
        }

        Publish(@event: new PeerEvent.Closed(Reason: reason));
        m_events.Writer.TryComplete();

        try {
            await m_stream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            await m_connection.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException)) {
        }

        m_onClosed?.Invoke(obj: this);
    }

    /// <summary>Starts the link's background read loop. Called once, after the caller has finished wiring up
    /// whatever consumes <see cref="Events"/>.</summary>
    internal void Start() => m_readLoop = Task.Run(function: ReadLoopAsync);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() {
        await CloseAsync(reason: "disposed").ConfigureAwait(continueOnCapturedContext: false);

        if (m_readLoop is { } loop) {
            try {
                await loop.ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception exception) when ((exception is IOException or ObjectDisposedException)) {
            }
        }

        m_writeGate.Dispose();
    }
    /// <summary>Signs <paramref name="payload"/> under this side's identity, directed at <see cref="RemoteId"/>,
    /// and sends it as one message frame.</summary>
    /// <param name="payload">The opaque message bytes.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) {
        var claim = m_local.SignClaim(
            audience: RemoteId.Domain,
            now: m_now(),
            payload: payload,
            purpose: PeerWireProtocol.MessagePurpose,
            validity: PeerWireProtocol.MaximumMessageClaimAge
        );
        var writer = new WireWriter();

        writer.WriteBlock(value: PeerWireProtocol.Codec.EncodeAttestation(attestation: claim));

        await m_writeGate.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        try {
            await WireFrame.WriteAsync(
                body: writer.ToArray(),
                ct: ct,
                kind: (byte)PeerFrameKind.Message,
                stream: m_stream
            ).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            m_writeGate.Release();
        }
    }
}
