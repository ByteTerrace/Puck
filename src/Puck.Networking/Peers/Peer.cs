using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;
using Puck.Attestation;

namespace Puck.Networking.Peers;

/// <summary>One identity-carrying process over one transport: it can dial another peer, accept a dial from
/// another peer, or both at once. There is no server role — <see cref="DialAsync"/> and an accepted connection run
/// the identical handshake in <see cref="PeerHandshake"/>, and the resulting <see cref="PeerLink"/> behaves the
/// same either way. The only asymmetry is who opens the control stream: the dialer opens it, the acceptor accepts
/// it, and neither side is told which it did.
/// <para>Accepting is bounded on every axis a remote side controls: at most
/// <see cref="PeerWireProtocol.MaxConcurrentHandshakes"/> inbound handshakes run at once (the accept loop stops
/// pulling connections off the transport until one finishes), an accepted connection must open its control stream
/// inside <see cref="PeerWireProtocol.ControlStreamTimeout"/> and finish its handshake inside
/// <see cref="PeerWireProtocol.HandshakeTimeout"/>, and <see cref="IncomingLinks"/> holds a bounded number of
/// established links a listener has not yet taken. Every inbound failure — a refusal, a timeout, or an exception the
/// handshake did not expect — is named on <see cref="HandshakeRefusals"/> and disposes its connection; nothing a
/// remote side does faults the accept loop, and if the transport itself faults the loop, <see cref="ListenerFault"/>
/// records why.</para>
/// <para>Every dial and every accepted handshake runs inside the peer's lifetime: a dial after disposal is refused
/// at entry, and disposal runs one fixed sequence — stop accepting, cancel the lifetime, wait for the accept loop and
/// every in-flight handshake, dialed or accepted (their deadlines are linked to the cancelled lifetime, so they
/// unwind promptly, a dial as <see cref="PeerRefusal.Disposed"/>), complete both channels, dispose every link
/// concurrently, then the transport, then the identity. The transport and the identity are disposed only once
/// nothing can still be using them.</para></summary>
public sealed class Peer : IAsyncDisposable {
    private const int HandshakeRefusalsRetained = 64;
    private const int IncomingLinksRetained = 64;

    private readonly SemaphoreSlim m_handshakeGate = new(
        initialCount: PeerWireProtocol.MaxConcurrentHandshakes,
        maxCount: PeerWireProtocol.MaxConcurrentHandshakes
    );
    private readonly Channel<PeerHandshakeRefused> m_handshakeRefusals = Channel.CreateBounded<PeerHandshakeRefused>(options: new BoundedChannelOptions(capacity: HandshakeRefusalsRetained) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly TaskCompletionSource m_handshakesDrained = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<PeerLink> m_incoming = Channel.CreateBounded<PeerLink>(options: new BoundedChannelOptions(capacity: IncomingLinksRetained) {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    // Cancelled by DisposeAsync once m_disposed is set, and disposed only after every handshake has drained: every
    // dial and every accepted handshake links its deadline to this token, so disposal unwinds them promptly.
    private readonly CancellationTokenSource m_lifetime = new();
    private readonly Lock m_linksLock = new();
    private readonly List<PeerLink> m_links = [];

    private readonly PeerIdentity m_local;
    private readonly Func<DateTimeOffset>? m_now;
    private readonly IPeerTransport m_transport;

    private Task? m_acceptLoop;
    private int m_disposed;
    private int m_handshakesInFlight;
    private IPeerListener? m_listener;
    private Exception? m_listenerFault;
    private bool m_listening;

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
    /// each with its named refusal — a refusal by either side, a deadline (<see cref="PeerRefusal.HandshakeTimedOut"/>),
    /// or an exception the handshake did not expect (<see cref="PeerRefusal.HandshakeFaulted"/>). Bounded; the oldest
    /// is dropped once nobody reads.</summary>
    public ChannelReader<PeerHandshakeRefused> HandshakeRefusals => m_handshakeRefusals.Reader;
    /// <summary>Gets this peer's own identity.</summary>
    public KeyId Id => m_local.Id;
    /// <summary>Gets a channel of links accepted from a dialing peer. A caller pumping <see cref="DialAsync"/>'s
    /// own result never sees its link here — only inbound connections arrive on this channel. It is bounded: a
    /// listener must pump it or dispose the peer, because once it is full each further accepted handshake waits to
    /// publish its link (and that wait is what stops the accept loop — accept backpressure is bounded by
    /// <see cref="PeerWireProtocol.MaxConcurrentHandshakes"/>, the number of handshakes that can be parked here at
    /// once). Disposing the peer releases every waiting handshake and disposes its link.</summary>
    public ChannelReader<PeerLink> IncomingLinks => m_incoming.Reader;
    /// <summary>Gets a snapshot of every currently open link, dialed or accepted.</summary>
    public IReadOnlyList<PeerLink> Links {
        get {
            lock (m_linksLock) {
                return [.. m_links];
            }
        }
    }
    /// <summary>Gets the bound listen endpoint, or <see langword="null"/> when not listening. Stale once
    /// <see cref="ListenerFault"/> is set: the endpoint is still bound but nothing accepts on it any more.</summary>
    public IPEndPoint? ListenEndpoint => m_listener?.LocalEndpoint;
    /// <summary>Gets the exception that ended the accept loop, or <see langword="null"/> while it runs or after it
    /// ended by disposal. A remote side cannot set this — every per-connection failure is named on
    /// <see cref="HandshakeRefusals"/> instead — so a value here means the transport's own accept faulted, and the peer
    /// accepts nothing further until it is disposed.</summary>
    public Exception? ListenerFault => Volatile.Read(location: ref m_listenerFault);

    private static CancellationTokenSource Deadline(CancellationToken ct, TimeSpan timeout) {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: ct);

        deadline.CancelAfter(delay: timeout);

        return deadline;
    }
    private static async ValueTask DisposeQuietlyAsync(IAsyncDisposable disposable) {
        try {
            await disposable.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException)) {
        }
    }
    /// <summary>Counts one handshake, dialed or accepted, as finished; the last one to finish after disposal began
    /// releases <see cref="DisposeAsync"/>'s drain wait. The accept loop holds one count of its own for its whole
    /// life, so the drain can never be released while the loop can still spawn a handshake: an accept that had
    /// already returned a connection when disposal began still gets counted before the loop's own count is
    /// released.</summary>
    private void HandshakeFinished() {
        if (
            (Interlocked.Decrement(location: ref m_handshakesInFlight) == 0) &&
            IsDisposed
        ) {
            m_handshakesDrained.TrySetResult();
        }
    }

    private bool IsDisposed => (Volatile.Read(location: ref m_disposed) != 0);

    private async ValueTask<bool> RegisterAsync(PeerLink link) {
        lock (m_linksLock) {
            if (m_disposed == 0) {
                m_links.Add(item: link);
                link.Start();

                return true;
            }
        }

        await link.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);

        return false;
    }
    private void Unregister(PeerLink link) {
        lock (m_linksLock) {
            m_links.Remove(item: link);
        }
    }
    private async Task AcceptLoopAsync(IPeerListener listener, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                try {
                    await m_handshakeGate.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
                } catch (OperationCanceledException) {
                    break;
                }

                IPeerConnection? connection;

                try {
                    connection = await listener.AcceptAsync(ct: ct).ConfigureAwait(continueOnCapturedContext: false);
                } catch (OperationCanceledException) {
                    m_handshakeGate.Release();

                    break;
                }

                if (connection is null) {
                    m_handshakeGate.Release();

                    break;
                }

                Interlocked.Increment(location: ref m_handshakesInFlight);

                _ = Task.Run(function: () => AcceptOneAsync(
                    connection: connection,
                    ct: ct
                ));
            }
        } catch (Exception exception) {
            Volatile.Write(
                location: ref m_listenerFault,
                value: exception
            );
        } finally {
            // The loop's own count, taken by ListenAsync before the loop started: released only here, once no
            // further AcceptOneAsync can be spawned, so every accepted handshake is counted before the drain can
            // complete.
            HandshakeFinished();
        }
    }
    private async Task AcceptOneAsync(IPeerConnection connection, CancellationToken ct) {
        try {
            PeerLink? link = null;
            PeerFailure failure;
            var deadlineName = nameof(PeerWireProtocol.ControlStreamTimeout);

            try {
                Stream? stream;

                using (var controlDeadline = Deadline(
                    ct: ct,
                    timeout: PeerWireProtocol.ControlStreamTimeout
                )) {
                    stream = await connection.AcceptStreamAsync(ct: controlDeadline.Token).ConfigureAwait(continueOnCapturedContext: false);
                }

                if (stream is null) {
                    failure = new PeerFailure(
                        Detail: "the connection closed before a control stream was opened",
                        Refusal: PeerRefusal.ConnectionClosed
                    );
                } else {
                    deadlineName = nameof(PeerWireProtocol.HandshakeTimeout);

                    using var handshakeDeadline = Deadline(
                        ct: ct,
                        timeout: PeerWireProtocol.HandshakeTimeout
                    );

                    (link, failure) = await PeerHandshake.RunAsync(
                        connection: connection,
                        ct: handshakeDeadline.Token,
                        local: m_local,
                        now: m_now,
                        onClosed: Unregister,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                failure = new PeerFailure(
                    Detail: $"{deadlineName} expired before the handshake completed",
                    Refusal: PeerRefusal.HandshakeTimedOut
                );
            } catch (Exception exception) when ((exception is IOException or ObjectDisposedException or OperationCanceledException)) {
                failure = new PeerFailure(
                    Detail: $"{exception.GetType().Name}: {exception.Message}",
                    Refusal: PeerRefusal.ConnectionClosed
                );
            } catch (Exception exception) {
                failure = new PeerFailure(
                    Detail: $"{exception.GetType().Name}: {exception.Message}",
                    Refusal: PeerRefusal.HandshakeFaulted
                );
            }

            if (link is null) {
                m_handshakeRefusals.Writer.TryWrite(item: new PeerHandshakeRefused(
                    Failure: failure,
                    RemoteEndpoint: connection.RemoteEndpoint
                ));
                await DisposeQuietlyAsync(disposable: connection).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            if (!await RegisterAsync(link: link).ConfigureAwait(continueOnCapturedContext: false)) {
                return;
            }

            try {
                await m_incoming.Writer.WriteAsync(
                    cancellationToken: ct,
                    item: link
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception exception) when ((exception is OperationCanceledException or ChannelClosedException)) {
                await link.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            }
        } finally {
            m_handshakeGate.Release();
            HandshakeFinished();
        }
    }
    /// <summary>The body of <see cref="DialAsync"/>, run while the dial is counted in flight: every wait in it is
    /// linked to the peer's lifetime, so disposal unwinds it as <see cref="PeerRefusal.Disposed"/>.</summary>
    private async Task<PeerLink> DialCountedAsync(EndPoint endpoint, CancellationToken ct) {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            token1: ct,
            token2: m_lifetime.Token
        );

        IPeerConnection connection;

        try {
            connection = await m_transport.DialAsync(
                ct: lifetime.Token,
                endpoint: endpoint
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            throw new PeerRefusedException(failure: new PeerFailure(
                Detail: "the peer was disposed while the transport connected",
                Refusal: PeerRefusal.Disposed
            ));
        } catch (Exception exception) when ((exception is IOException or SocketException or AuthenticationException)) {
            throw new PeerRefusedException(failure: new PeerFailure(
                Detail: $"{exception.GetType().Name}: {exception.Message}",
                Refusal: PeerRefusal.TransportFailed
            ));
        }

        PeerLink? link = null;
        PeerFailure failure;

        try {
            using var deadline = Deadline(
                ct: lifetime.Token,
                timeout: PeerWireProtocol.HandshakeTimeout
            );

            var stream = await connection.OpenStreamAsync(ct: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);

            (link, failure) = await PeerHandshake.RunAsync(
                connection: connection,
                ct: deadline.Token,
                local: m_local,
                now: m_now,
                onClosed: Unregister,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when ((!ct.IsCancellationRequested && m_lifetime.IsCancellationRequested)) {
            failure = new PeerFailure(
                Detail: "the peer was disposed while the handshake ran",
                Refusal: PeerRefusal.Disposed
            );
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            failure = new PeerFailure(
                Detail: $"{nameof(PeerWireProtocol.HandshakeTimeout)} expired before the handshake completed",
                Refusal: PeerRefusal.HandshakeTimedOut
            );
        } catch (Exception exception) when ((exception is IOException or ObjectDisposedException)) {
            failure = new PeerFailure(
                Detail: $"{exception.GetType().Name}: {exception.Message}",
                Refusal: PeerRefusal.ConnectionClosed
            );
        } catch (Exception exception) when ((exception is not OperationCanceledException)) {
            failure = new PeerFailure(
                Detail: $"{exception.GetType().Name}: {exception.Message}",
                Refusal: PeerRefusal.HandshakeFaulted
            );
        } finally {
            if (link is null) {
                await DisposeQuietlyAsync(disposable: connection).ConfigureAwait(continueOnCapturedContext: false);
            }
        }

        if (link is null) {
            throw new PeerRefusedException(failure: failure);
        }

        if (!await RegisterAsync(link: link).ConfigureAwait(continueOnCapturedContext: false)) {
            throw new PeerRefusedException(failure: new PeerFailure(
                Detail: "the peer was disposed while the handshake ran",
                Refusal: PeerRefusal.Disposed
            ));
        }

        return link;
    }

    /// <summary>Dials another peer and runs the same symmetric handshake an acceptor runs. The handshake deadline
    /// (<see cref="PeerWireProtocol.HandshakeTimeout"/>) starts once the transport has connected, so a slow transport
    /// handshake never eats into it. The dial is counted as an in-flight handshake for the peer's whole lifetime
    /// protocol: <see cref="DisposeAsync"/> cancels it and waits for it to unwind before the transport and the
    /// identity are disposed, so a dial never runs against a disposed key. The connection is disposed on every
    /// failure path.</summary>
    /// <param name="endpoint">The address to dial.</param>
    /// <param name="ct">Cancellation. The caller's own cancellation propagates as
    /// <see cref="OperationCanceledException"/>; every other failure is a <see cref="PeerRefusedException"/>.</param>
    /// <returns>The established link.</returns>
    /// <exception cref="PeerRefusedException">The transport could not connect or authenticate
    /// (<see cref="PeerRefusal.TransportFailed"/>), the deadline expired (<see cref="PeerRefusal.HandshakeTimedOut"/>),
    /// the handshake raised outside the wire vocabulary (<see cref="PeerRefusal.HandshakeFaulted"/>), the peer was
    /// disposed while the transport connected or the handshake ran (<see cref="PeerRefusal.Disposed"/>), or the
    /// handshake was refused; <see cref="PeerRefusedException.Failure"/> names which.</exception>
    /// <exception cref="ObjectDisposedException">The peer had already been disposed when the dial began.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<PeerLink> DialAsync(EndPoint endpoint, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);

        lock (m_linksLock) {
            ObjectDisposedException.ThrowIf(
                condition: (m_disposed != 0),
                instance: this
            );

            Interlocked.Increment(location: ref m_handshakesInFlight);
        }

        try {
            return await DialCountedAsync(
                ct: ct,
                endpoint: endpoint
            ).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            HandshakeFinished();
        }
    }
    /// <summary>Binds a listener and starts accepting connections in the background. A peer listens at most once.</summary>
    /// <param name="endpoint">The endpoint to bind; port 0 picks a free port.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bound endpoint.</returns>
    /// <exception cref="InvalidOperationException">The peer is already listening.</exception>
    /// <exception cref="ObjectDisposedException">The peer has been disposed.</exception>
    public async Task<IPEndPoint> ListenAsync(IPEndPoint endpoint, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);

        CancellationToken lifetime;

        lock (m_linksLock) {
            ObjectDisposedException.ThrowIf(
                condition: (m_disposed != 0),
                instance: this
            );

            if (m_listening) {
                throw new InvalidOperationException(message: "the peer is already listening");
            }

            m_listening = true;
            lifetime = m_lifetime.Token;
        }

        IPeerListener listener;

        try {
            listener = await m_transport.ListenAsync(
                ct: ct,
                endpoint: endpoint
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception) {
            lock (m_linksLock) {
                m_listening = false;
            }

            throw;
        }

        lock (m_linksLock) {
            if (m_disposed == 0) {
                m_listener = listener;
                // The accept loop's own in-flight count (see HandshakeFinished), taken under the same lock that
                // refuses a dispose from missing it.
                Interlocked.Increment(location: ref m_handshakesInFlight);
                m_acceptLoop = Task.Run(function: () => AcceptLoopAsync(
                    ct: lifetime,
                    listener: listener
                ));

                return listener.LocalEndpoint;
            }
        }

        await DisposeQuietlyAsync(disposable: listener).ConfigureAwait(continueOnCapturedContext: false);

        throw new ObjectDisposedException(objectName: nameof(Peer));
    }
    /// <inheritdoc/>
    public async ValueTask DisposeAsync() {
        IPeerListener? listener;
        Task? loop;

        lock (m_linksLock) {
            if (Interlocked.Exchange(
                location1: ref m_disposed,
                value: 1
            ) != 0) {
                return;
            }

            // Snapshotted under the same lock ListenAsync publishes them under, so a listen racing this dispose is
            // either fully visible here or refused there; nothing below reads the fields again.
            listener = m_listener;
            loop = m_acceptLoop;
        }

        m_lifetime.Cancel();

        if (listener is not null) {
            await DisposeQuietlyAsync(disposable: listener).ConfigureAwait(continueOnCapturedContext: false);
        }

        if (loop is not null) {
            try {
                await loop.ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception) {
            }
        }

        if (Volatile.Read(location: ref m_handshakesInFlight) == 0) {
            m_handshakesDrained.TrySetResult();
        }

        await m_handshakesDrained.Task.ConfigureAwait(continueOnCapturedContext: false);

        m_incoming.Writer.TryComplete();
        m_handshakeRefusals.Writer.TryComplete();

        await Task.WhenAll(tasks: Links.Select(selector: static link => link.DisposeAsync().AsTask())).ConfigureAwait(continueOnCapturedContext: false);

        m_lifetime.Dispose();
        await m_transport.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        m_local.Dispose();
    }
}
