using System.Net;
using System.Threading.Channels;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>One identity-carrying process over one transport: it can dial another peer, accept a dial from
/// another peer, or both at once. There is no server role — <see cref="DialAsync"/> and an accepted connection run
/// the identical handshake in <see cref="PeerHandshake"/>, and the resulting <see cref="PeerLink"/> behaves the
/// same either way. The only asymmetry is who opens the control stream: the dialer opens it, the acceptor accepts
/// it, and neither side is told which it did.</summary>
public sealed class Peer : IAsyncDisposable {
    private const int HandshakeRefusalsRetained = 64;

    private readonly Channel<PeerHandshakeRefused> m_handshakeRefusals = Channel.CreateBounded<PeerHandshakeRefused>(options: new BoundedChannelOptions(capacity: HandshakeRefusalsRetained) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly Channel<PeerLink> m_incoming = Channel.CreateUnbounded<PeerLink>(options: new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock m_linksLock = new();
    private readonly List<PeerLink> m_links = [];
    private readonly PeerIdentity m_local;
    private readonly Func<DateTimeOffset>? m_now;
    private readonly IPeerTransport m_transport;

    private Task? m_acceptLoop;
    private CancellationTokenSource? m_acceptLifetime;
    private IPeerListener? m_listener;

    /// <summary>Initializes a peer over an identity and a transport it owns and disposes.</summary>
    /// <param name="identity">This peer's identity.</param>
    /// <param name="transport">The transport every dial and listen goes through.</param>
    /// <param name="now">The verification-boundary clock read, overridable for tests.</param>
    public Peer(PeerIdentity identity, IPeerTransport transport, Func<DateTimeOffset>? now = null) {
        ArgumentNullException.ThrowIfNull(argument: identity);
        ArgumentNullException.ThrowIfNull(argument: transport);

        m_local = identity;
        m_transport = transport;
        m_now = now;
    }

    /// <summary>Gets a channel of inbound connections that passed the transport but were refused at the handshake,
    /// each with its named refusal. Bounded; the oldest is dropped once nobody reads.</summary>
    public ChannelReader<PeerHandshakeRefused> HandshakeRefusals => m_handshakeRefusals.Reader;
    /// <summary>Gets this peer's own identity.</summary>
    public KeyId Id => m_local.Id;
    /// <summary>Gets a channel of links accepted from a dialing peer. A caller pumping <see cref="DialAsync"/>'s
    /// own result never sees its link here — only inbound connections arrive on this channel.</summary>
    public ChannelReader<PeerLink> IncomingLinks => m_incoming.Reader;
    /// <summary>Gets a snapshot of every currently open link, dialed or accepted.</summary>
    public IReadOnlyList<PeerLink> Links {
        get {
            lock (m_linksLock) {
                return [.. m_links];
            }
        }
    }
    /// <summary>Gets the bound listen endpoint, or <see langword="null"/> when not listening.</summary>
    public IPEndPoint? ListenEndpoint => m_listener?.LocalEndpoint;

    private static CancellationTokenSource HandshakeDeadline(CancellationToken ct) {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: ct);

        deadline.CancelAfter(delay: PeerWireProtocol.HandshakeTimeout);

        return deadline;
    }

    private void Register(PeerLink link) {
        lock (m_linksLock) {
            m_links.Add(item: link);
        }

        link.Start();
    }
    private void Unregister(PeerLink link) {
        lock (m_linksLock) {
            m_links.Remove(item: link);
        }
    }
    private async Task AcceptLoopAsync(CancellationToken ct) {
        var listener = m_listener!;

        while (!ct.IsCancellationRequested) {
            IPeerConnection? connection;

            try {
                connection = await listener.AcceptAsync(ct: ct).ConfigureAwait(continueOnCapturedContext: false);
            } catch (OperationCanceledException) {
                break;
            }

            if (connection is null) {
                break;
            }

            _ = Task.Run(function: () => AcceptOneAsync(
                connection: connection,
                ct: ct
            ));
        }
    }
    private async Task AcceptOneAsync(IPeerConnection connection, CancellationToken ct) {
        using var deadline = HandshakeDeadline(ct: ct);

        PeerLink? link = null;
        PeerFailure failure;

        try {
            var stream = await connection.AcceptStreamAsync(ct: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);

            if (stream is null) {
                failure = new PeerFailure(
                    Detail: "the connection closed before a control stream was opened",
                    Refusal: PeerRefusal.ConnectionClosed
                );
            } else {
                (link, failure) = await PeerHandshake.RunAsync(
                    connection: connection,
                    ct: deadline.Token,
                    local: m_local,
                    now: m_now,
                    onClosed: Unregister,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException or OperationCanceledException)) {
            failure = new PeerFailure(
                Detail: $"{exception.GetType().Name}: {exception.Message}",
                Refusal: PeerRefusal.ConnectionClosed
            );
        }

        if (link is null) {
            m_handshakeRefusals.Writer.TryWrite(item: new PeerHandshakeRefused(
                Failure: failure,
                RemoteEndpoint: connection.RemoteEndpoint
            ));
            await connection.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);

            return;
        }

        Register(link: link);

        if (!m_incoming.Writer.TryWrite(item: link)) {
            await link.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    /// <summary>Dials another peer and runs the same symmetric handshake an acceptor runs.</summary>
    /// <param name="endpoint">The address to dial.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The established link.</returns>
    /// <exception cref="PeerRefusedException">The handshake was refused; <see cref="PeerRefusedException.Failure"/>
    /// names the refusal.</exception>
    public async Task<PeerLink> DialAsync(EndPoint endpoint, CancellationToken ct = default) {
        using var deadline = HandshakeDeadline(ct: ct);

        var connection = await m_transport.DialAsync(
            ct: deadline.Token,
            endpoint: endpoint
        ).ConfigureAwait(continueOnCapturedContext: false);

        PeerLink? link;
        PeerFailure failure;

        try {
            var stream = await connection.OpenStreamAsync(ct: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);

            (link, failure) = await PeerHandshake.RunAsync(
                connection: connection,
                ct: deadline.Token,
                local: m_local,
                now: m_now,
                onClosed: Unregister,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException) || ((exception is OperationCanceledException) && !ct.IsCancellationRequested)) {
            link = null;
            failure = new PeerFailure(
                Detail: $"{exception.GetType().Name}: {exception.Message}",
                Refusal: PeerRefusal.ConnectionClosed
            );
        }

        if (link is null) {
            await connection.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);

            throw new PeerRefusedException(failure: failure);
        }

        Register(link: link);

        return link;
    }
    /// <summary>Binds a listener and starts accepting connections in the background.</summary>
    /// <param name="endpoint">The endpoint to bind; port 0 picks a free port.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bound endpoint.</returns>
    public async Task<IPEndPoint> ListenAsync(IPEndPoint endpoint, CancellationToken ct = default) {
        m_listener = await m_transport.ListenAsync(
            ct: ct,
            endpoint: endpoint
        ).ConfigureAwait(continueOnCapturedContext: false);
        m_acceptLifetime = new CancellationTokenSource();
        m_acceptLoop = Task.Run(function: () => AcceptLoopAsync(ct: m_acceptLifetime.Token));

        return m_listener.LocalEndpoint;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() {
        m_acceptLifetime?.Cancel();

        if (m_listener is { } listener) {
            await listener.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        }

        if (m_acceptLoop is { } loop) {
            await loop.ConfigureAwait(continueOnCapturedContext: false);
        }

        m_incoming.Writer.TryComplete();
        m_handshakeRefusals.Writer.TryComplete();

        foreach (var link in Links) {
            await link.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        }

        m_acceptLifetime?.Dispose();
        await m_transport.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        m_local.Dispose();
    }
}
