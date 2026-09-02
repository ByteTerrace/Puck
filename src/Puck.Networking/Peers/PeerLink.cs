using System.Net;
using System.Threading.Channels;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>One symmetric, authenticated connection between two peers. Either side may have dialed; after the
/// handshake both sides send and receive identically over the control stream. Every outbound message is signed by
/// this side's own identity, and every inbound message is verified against <see cref="RemoteId"/> — the identity
/// the peer proved at handshake, never a value a later message merely claims.</summary>
/// <remarks>
/// <para>A message payload is at most <see cref="PeerWireProtocol.MaxMessagePayloadBytes"/> bytes; <see cref="SendAsync"/>
/// refuses a longer one as a caller bug before signing it, so an honest sender never produces a frame the receiver
/// cannot admit.</para>
/// <para>A refused inbound message (<see cref="PeerEvent.Refused"/>) does not close the link: the refusal is
/// reported by name and the link keeps carrying honest traffic. A frame-grammar violation is different — a frame
/// the stream cannot be resynchronized after (an impossible length, an unknown kind) closes the link as
/// <see cref="PeerRefusal.FrameMalformed"/>, because nothing that follows it can be trusted to start a frame.</para>
/// <para><see cref="Events"/> holds at most <see cref="EventsCapacity"/> pending events. A consumer that stops
/// reading stalls the read loop at the next publish rather than growing memory without bound; the link still
/// closes promptly, because closing cancels the stalled publish. Closing never waits for the remote side either:
/// the connection is closed before the stream, so a remote that has vanished bounds a close by the transport's
/// own connection-close handshake, never by a stream shutdown it will never acknowledge. Once the link closes,
/// <see cref="Events"/> is completed (its reader's completion settles once the pending events are drained) and
/// <see cref="CloseFailure"/> names why.</para>
/// <para>Once closed, <see cref="SendAsync"/> throws <see cref="PeerRefusedException"/> naming
/// <see cref="PeerRefusal.ConnectionClosed"/>, and a send that was already writing fails the same way. A caller's
/// cancellation token is honored only while a send waits for its turn at the stream; the write itself is bound to
/// the link's lifetime, so cancelling a send never leaves a partial frame on the wire. The write is bound in time
/// as well: a peer that withholds stream flow-control credit stalls the write rather than failing it, so one that
/// has not completed inside <see cref="PeerWireProtocol.SendTimeout"/> closes the link as
/// <see cref="PeerRefusal.ConnectionClosed"/>, which refuses the stalled send and every send queued behind it.</para>
/// </remarks>
public sealed class PeerLink : IAsyncDisposable {
    /// <summary>The number of events <see cref="Events"/> holds before the read loop waits for the consumer.</summary>
    public const int EventsCapacity = 32;

    // Never disposed: SendAsync reads its token outside any lock, and a disposed source throws on that read.
    private readonly CancellationTokenSource m_closeSource = new();

    private readonly IPeerConnection m_connection;

    private readonly Channel<PeerEvent> m_events = Channel.CreateBounded<PeerEvent>(options: new BoundedChannelOptions(capacity: EventsCapacity) {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly PeerIdentity m_local;
    private readonly Func<DateTimeOffset> m_now;
    private readonly Action<PeerLink>? m_onClosed;
    private readonly TrustList m_remoteTrust;
    private readonly Stream m_stream;

    // Never disposed: a SemaphoreSlim holds no unmanaged resource until its wait handle is touched, and disposing
    // it under a pending WaitAsync leaves that waiter pending forever.
    private readonly SemaphoreSlim m_writeGate = new(initialCount: 1, maxCount: 1);

    private int m_closed;
    private Task? m_readLoop;

    internal PeerLink(IPeerConnection connection, Stream stream, PeerIdentity local, KeyId remoteId, byte[] remoteSubjectPublicKeyInfo, Action<PeerLink>? onClosed, Func<DateTimeOffset>? now = null) {
        m_connection = connection;
        m_stream = stream;
        m_local = local;
        m_remoteTrust = PeerWireProtocol.SingleEntryTrust(
            id: remoteId,
            maximumAge: (PeerWireProtocol.MaximumMessageClaimAge + PeerWireProtocol.ClockSkewTolerance),
            reach: "message",
            subjectPublicKeyInfo: remoteSubjectPublicKeyInfo
        );
        m_onClosed = onClosed;
        m_now = (now ?? (static () => DateTimeOffset.UtcNow));

        RemoteId = remoteId;
    }

    /// <summary>Gets why the link closed, or a failure naming no refusal while it is open. A consumer that stopped
    /// reading <see cref="Events"/> observes its completion (which settles once the pending events are drained)
    /// and reads this instead of the <see cref="PeerEvent.Closed"/> event, which is dropped when the channel is
    /// full at close.</summary>
    public PeerFailure CloseFailure { get; private set; }
    /// <summary>Gets a channel of every message, refusal, and closure this link produces. Bounded to
    /// <see cref="EventsCapacity"/> pending events: the read loop waits on a full channel rather than dropping or
    /// growing, so a consumer that stops reading applies backpressure to the peer. The channel completes once the
    /// link closes; the final <see cref="PeerEvent.Closed"/> is dropped if the channel is full at that moment, and
    /// <see cref="CloseFailure"/> always carries the same failure.</summary>
    public ChannelReader<PeerEvent> Events => m_events.Reader;
    /// <summary>Gets a value indicating whether the link is still open.</summary>
    public bool IsOpen => (Volatile.Read(location: ref m_closed) == 0);
    /// <summary>Gets the remote transport address.</summary>
    public EndPoint RemoteEndpoint => m_connection.RemoteEndpoint;
    /// <summary>Gets the identity the remote side proved at handshake.</summary>
    public KeyId RemoteId { get; }

    private ValueTask PublishAsync(PeerEvent @event) => m_events.Writer.WriteAsync(
        cancellationToken: m_closeSource.Token,
        item: @event
    );
    private async Task ReadLoopAsync() {
        PeerFailure failure;

        try {
            while (true) {
                var frame = await WireFrame.ReadAsync(
                    ct: m_closeSource.Token,
                    maxFrameBytes: PeerWireProtocol.MaxFrameBytes,
                    stream: m_stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!frame.Ok) {
                    failure = ((frame.Failure.Refusal == WireRefusal.ConnectionClosed)
                        ? new PeerFailure(
                            Detail: frame.Failure.Detail,
                            Refusal: PeerRefusal.ConnectionClosed
                        )
                        : new PeerFailure(
                            Detail: frame.Failure.ToString(),
                            Refusal: PeerRefusal.FrameMalformed
                        )
                    );

                    break;
                }

                if (frame.Kind == ((byte)PeerFrameKind.HelloRefused)) {
                    // Only a refusal the peer actually named keeps its name here; a refused frame whose body does not
                    // decode is this link's own grammar violation, and the Closed contract names that FrameMalformed.
                    var refusal = PeerHandshake.ReadRefusal(frame: frame);

                    failure = ((refusal.Refusal == PeerRefusal.RefusedByPeer)
                        ? refusal
                        : new PeerFailure(
                            Detail: refusal.Detail,
                            Refusal: PeerRefusal.FrameMalformed
                        )
                    );

                    break;
                }

                if (frame.Kind != ((byte)PeerFrameKind.Message)) {
                    failure = new PeerFailure(
                        Detail: $"the peer sent an unexpected frame kind ({frame.Kind}) on an established link",
                        Refusal: PeerRefusal.FrameMalformed
                    );

                    break;
                }

                await PublishAsync(@event: DecodeMessageFrame(body: frame.Body.Span)).ConfigureAwait(continueOnCapturedContext: false);
            }
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException)) {
            failure = new PeerFailure(
                Detail: $"{exception.GetType().Name}: {exception.Message}",
                Refusal: PeerRefusal.ConnectionClosed
            );
        } catch (Exception exception) when ((exception is OperationCanceledException or ChannelClosedException)) {
            // The link closed under this loop: its close token cancelled the pending read or publish, or the
            // completed channel refused the publish. CloseAsync already ran.
            return;
        } catch (Exception exception) {
            failure = new PeerFailure(
                Detail: $"{exception.GetType().Name}: {exception.Message}",
                Refusal: PeerRefusal.LinkFaulted
            );
        }

        await CloseAsync(failure: failure).ConfigureAwait(continueOnCapturedContext: false);
    }
    private PeerEvent DecodeMessageFrame(ReadOnlySpan<byte> body) {
        var reader = new WireReader(bytes: body);
        var attestationBytes = reader.ReadBlock(
            field: "attestation",
            maxBytes: PeerWireProtocol.MaxFrameBytes
        );

        if (!reader.TryFinish(failure: out var wireFailure)) {
            return new PeerEvent.Refused(Failure: new PeerFailure(
                Detail: wireFailure.ToString(),
                Refusal: PeerRefusal.MessageMalformed
            ));
        }

        return (TryVerifyMessage(
            failure: out var failure,
            payload: out var payload,
            wire: attestationBytes
        )
            ? new PeerEvent.Received(Payload: payload)
            : new PeerEvent.Refused(Failure: failure)
        );
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

        var result = PeerWireProtocol.Profile.VerifyChain(
            chain: [],
            claim: claim,
            codec: PeerWireProtocol.Codec,
            expectedAudience: m_local.Id.Domain,
            expectedPurpose: PeerWireProtocol.MessagePurpose,
            now: m_now(),
            trustList: m_remoteTrust
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
    private async ValueTask CloseAsync(PeerFailure failure) {
        if (Interlocked.Exchange(
            location1: ref m_closed,
            value: 1
        ) != 0) {
            return;
        }

        CloseFailure = failure;
        m_closeSource.Cancel();
        m_events.Writer.TryWrite(item: new PeerEvent.Closed(Failure: failure));
        m_events.Writer.TryComplete();

        // The connection goes first: a stream's graceful shutdown completes only once the peer acknowledges it or
        // the connection beneath it dies, so disposing the stream first would park every close — and every peer
        // dispose above it — on a vanished remote until the transport's own disconnect timeout. Closing the
        // connection is bounded by the transport's close handshake alone, after which the stream's dispose is
        // immediate.
        try {
            await m_connection.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            await m_stream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException)) {
        }

        m_onClosed?.Invoke(obj: this);
    }

    /// <summary>Starts the link's background read loop. Called once, after the caller has finished wiring up
    /// whatever consumes <see cref="Events"/>.</summary>
    internal void Start() => m_readLoop = Task.Run(function: ReadLoopAsync);

    /// <summary>Closes the link as <see cref="PeerRefusal.Disposed"/> — cancelling any pending send or publish,
    /// completing <see cref="Events"/>, and disposing the connection and then the stream — then waits for the read
    /// loop to unwind. Idempotent, and a no-op after the link closed for any other reason.</summary>
    /// <returns>The dispose task.</returns>
    public async ValueTask DisposeAsync() {
        await CloseAsync(failure: new PeerFailure(
            Detail: "the link was disposed locally",
            Refusal: PeerRefusal.Disposed
        )).ConfigureAwait(continueOnCapturedContext: false);

        if (m_readLoop is { } loop) {
            await loop.ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    /// <summary>Signs <paramref name="payload"/> under this side's identity, directed at <see cref="RemoteId"/>,
    /// and sends it as one message frame. Sends are serialized: <paramref name="ct"/> is honored while waiting for
    /// an earlier send to finish, and the write itself is bound to the link's lifetime instead, so a cancelled send
    /// never aborts the stream mid-frame while closing the link aborts a pending write. The write is also bound by
    /// <see cref="PeerWireProtocol.SendTimeout"/>, the link's own ceiling rather than the caller's: a peer that
    /// withholds stream credit that long has the link closed under it as <see cref="PeerRefusal.ConnectionClosed"/>,
    /// and this send and every send queued behind it are refused by that name.</summary>
    /// <param name="payload">The opaque message bytes, at most <see cref="PeerWireProtocol.MaxMessagePayloadBytes"/> long.</param>
    /// <param name="ct">Cancellation, honored only while waiting for the stream.</param>
    /// <returns>The send task.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payload"/> is longer than
    /// <see cref="PeerWireProtocol.MaxMessagePayloadBytes"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before the write began.</exception>
    /// <exception cref="PeerRefusedException">The link is closed, closed while this send was signing or writing
    /// (a <see cref="Peer"/> disposed under it disposes the identity it signs with), or the write outlived
    /// <see cref="PeerWireProtocol.SendTimeout"/>; the failure names <see cref="PeerRefusal.ConnectionClosed"/>.</exception>
    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) {
        if (!IsOpen) {
            throw new PeerRefusedException(failure: new PeerFailure(
                Detail: "the link is closed",
                Refusal: PeerRefusal.ConnectionClosed
            ));
        }

        if (payload.Length > PeerWireProtocol.MaxMessagePayloadBytes) {
            throw new ArgumentOutOfRangeException(
                actualValue: payload.Length,
                message: $"the payload is {payload.Length} bytes; {nameof(PeerWireProtocol)}.{nameof(PeerWireProtocol.MaxMessagePayloadBytes)} admits at most {PeerWireProtocol.MaxMessagePayloadBytes}",
                paramName: nameof(payload)
            );
        }

        SignedAttestation claim;

        // The identity belongs to the peer, which disposes it only after disposing every link; a send that passed
        // the open check before that sequence began can still reach the disposed key, and that is the link closing
        // under the send, not a caller bug.
        try {
            claim = m_local.SignClaim(
                audience: RemoteId.Domain,
                now: m_now(),
                payload: payload,
                purpose: PeerWireProtocol.MessagePurpose,
                validity: PeerWireProtocol.MaximumMessageClaimAge
            );
        } catch (ObjectDisposedException exception) {
            throw new PeerRefusedException(failure: new PeerFailure(
                Detail: $"{exception.GetType().Name}: {exception.Message}",
                Refusal: PeerRefusal.ConnectionClosed
            ));
        }

        var writer = new WireWriter();

        writer.WriteBlock(value: PeerWireProtocol.Codec.EncodeAttestation(attestation: claim));

        await m_writeGate.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

        try {
            // The deadline is the link's, not the caller's: its expiry closes the link, so the stream is only ever
            // aborted mid-frame by a close, never by a send that merely gave up waiting.
            using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(token: m_closeSource.Token);

            sendDeadline.CancelAfter(delay: PeerWireProtocol.SendTimeout);

            await WireFrame.WriteAsync(
                body: writer.WrittenMemory,
                ct: sendDeadline.Token,
                kind: ((byte)PeerFrameKind.Message),
                stream: m_stream
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (!m_closeSource.IsCancellationRequested) {
            var failure = new PeerFailure(
                Detail: $"{nameof(PeerWireProtocol.SendTimeout)} expired while the peer withheld stream credit",
                Refusal: PeerRefusal.ConnectionClosed
            );

            await CloseAsync(failure: failure).ConfigureAwait(continueOnCapturedContext: false);

            throw new PeerRefusedException(failure: failure);
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException or OperationCanceledException)) {
            throw new PeerRefusedException(failure: new PeerFailure(
                Detail: $"{exception.GetType().Name}: {exception.Message}",
                Refusal: PeerRefusal.ConnectionClosed
            ));
        } finally {
            m_writeGate.Release();
        }
    }
}
