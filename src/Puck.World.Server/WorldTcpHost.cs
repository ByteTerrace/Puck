using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One admitted remote connection's read-back row — <c>world.peers</c>' one source of truth.</summary>
/// <param name="ConnectionId">The connection id this door assigned at admission.</param>
/// <param name="PeerIndex">The admitted population body index.</param>
/// <param name="Generation">The admission generation.</param>
/// <param name="RemoteEndpoint">The remote socket endpoint, as text.</param>
public readonly record struct WorldPeerConnectionInfo(int ConnectionId, int PeerIndex, int Generation, string RemoteEndpoint);

/// <summary>
/// The P7 socket door: a TCP listener admitting remote peers onto the SAME ordered domain a local script drives.
/// Per connection: the raw Hello handshake (<see cref="WorldHelloDoor"/>) runs off the tick thread (it touches no
/// server state), admission (<see cref="WorldServer.TryAdmitPeerConnection"/>) and every subsequent submission run
/// ON the tick thread — marshaled there through <see cref="m_pending"/>, drained by <see cref="DrainPending"/> at the
/// top of every fixed step (<c>WorldServerStepShell.Step</c>), exactly where the design's §1.5 "deterministic fair
/// merge" window sits. v1 keeps that merge to its simplest correct shape: ONE global FIFO (no per-connection quotas,
/// no bounded-queue backpressure) — a trusted-LAN connection count small enough that fairness never needs more.
/// </summary>
/// <remarks>
/// v1 is strictly request-then-response PER CONNECTION: a connection's dedicated read loop decodes one upstream
/// frame, awaits its tick-thread completion, writes the one downstream reply, then reads the next — so no
/// correlation id needs to travel on the wire (see <see cref="WorldTcpWireFormat"/>). This is a deliberate
/// simplification the design's own admission-budget/pipelining machinery is not part of; nothing here queues or
/// retries a connection.
/// </remarks>
public sealed class WorldTcpHost : IDisposable {
    private readonly WorldServer m_server;
    private readonly ConcurrentQueue<Action> m_pending = new();
    private readonly List<Connection> m_connections = [];
    private readonly Lock m_connectionsLock = new();
    private TcpListener? m_listener;
    private CancellationTokenSource? m_cts;
    private Task? m_acceptLoop;
    private int m_nextConnectionId;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="WorldTcpHost"/> class over the server it admits into.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public WorldTcpHost(WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        m_server = server;
    }

    /// <summary>Gets a value indicating whether the listener is currently bound.</summary>
    public bool IsListening => (m_listener is not null);

    /// <summary>Gets the bound endpoint, or <see langword="null"/> while not listening.</summary>
    public string? ListenEndpoint { get; private set; }

    /// <summary>Gets a read-back snapshot of every currently admitted connection, oldest first.</summary>
    public IReadOnlyList<WorldPeerConnectionInfo> Connections {
        get {
            lock (m_connectionsLock) {
                return [.. m_connections.Select(selector: static c => new WorldPeerConnectionInfo(ConnectionId: c.Id, PeerIndex: c.PeerIndex, Generation: c.Generation, RemoteEndpoint: c.RemoteEndpoint))];
            }
        }
    }

    /// <summary>Binds the listener and starts accepting connections in the background. The composition root calls this
    /// only when <c>host.listen</c>/<c>--listen</c> names an endpoint; when it is absent the call is skipped entirely,
    /// so no listener ever binds.</summary>
    /// <param name="listen">The <c>host:port</c> endpoint to bind.</param>
    /// <exception cref="FormatException"><paramref name="listen"/> is not a parseable IP endpoint.</exception>
    public void Start(string listen) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: listen);

        if (!IPEndPoint.TryParse(s: listen, result: out var endpoint)) {
            throw new FormatException(message: $"host.listen '{listen}' is not a parseable \"ip:port\" endpoint (a hostname is not accepted).");
        }

        m_cts = new CancellationTokenSource();
        m_listener = new TcpListener(localEP: endpoint);
        m_listener.Start();
        ListenEndpoint = m_listener.LocalEndpoint.ToString();
        m_acceptLoop = Task.Run(function: () => AcceptLoopAsync(ct: m_cts.Token));
        Console.Error.WriteLine(value: $"[world.listen: bound {ListenEndpoint}]");
    }

    /// <summary>Drains every tick-thread work item enqueued by a connection since the last drain — admissions,
    /// submissions, and disconnects alike. MUST run on the tick thread, before <c>WorldServer.Step</c>, so it never
    /// races the single-threaded server/population/grant state.</summary>
    public void DrainPending() {
        while (m_pending.TryDequeue(result: out var action)) {
            action();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct) {
        var listener = m_listener!;

        while (!ct.IsCancellationRequested) {
            TcpClient client;

            try {
                client = await listener.AcceptTcpClientAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) {
                break;
            }

            _ = Task.Run(function: () => HandleConnectionAsync(client: client, ct: ct));
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct) {
        client.NoDelay = true;

        var remoteEndpoint = (client.Client.RemoteEndPoint?.ToString() ?? "unknown");
        var stream = client.GetStream();

        try {
            var helloBuffer = new byte[WorldTcpWireFormat.HelloBytes];

            if (!await WorldTcpWireFormat.TryReadExactAsync(stream: stream, buffer: helloBuffer, ct: ct).ConfigureAwait(continueOnCapturedContext: false)) {
                return;
            }

            var offeredKey = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(source: helloBuffer);

            if (!WorldHelloDoor.TryAccept(offeredKey: offeredKey, refusal: out var helloRefusal)) {
                await WorldTcpWireFormat.WriteHelloRefusedAsync(stream: stream, reason: $"{helloRefusal}: wire key 0x{offeredKey:x16} != server 0x{WorldProtocol.WireProtocolKey:x16}", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            var (admitted, admissionRefusal) = await RunOnTickThreadAsync(work: () =>
                m_server.TryAdmitPeerConnection(admitted: out var entry, refusal: out var reason) ? (entry, (string?)null) : (default(WorldPeerEventEntry), reason)
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (admissionRefusal is { } refusalReason) {
                await WorldTcpWireFormat.WriteHelloRefusedAsync(stream: stream, reason: refusalReason, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            var connectionId = Interlocked.Increment(location: ref m_nextConnectionId);
            var connection = new Connection(id: connectionId, peerIndex: admitted.BodyIndex, generation: admitted.Generation, client: client, stream: stream, remoteEndpoint: remoteEndpoint);

            lock (m_connectionsLock) {
                m_connections.Add(item: connection);
            }

            try {
                Console.Error.WriteLine(value: $"[world.listen: admitted connection {connectionId} as {connection.Principal.Describe()} from {remoteEndpoint}]");
                await WorldTcpWireFormat.WriteHelloAcceptedAsync(stream: stream, peerIndex: connection.PeerIndex, generation: connection.Generation, connectionId: connectionId, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

                await FrameLoopAsync(connection: connection, ct: ct).ConfigureAwait(continueOnCapturedContext: false);
            } finally {
                lock (m_connectionsLock) {
                    m_connections.Remove(item: connection);
                }

                await RunOnTickThreadAsync(work: () => {
                    m_server.DisconnectPeerConnection(peer: admitted);

                    return true;
                }).ConfigureAwait(continueOnCapturedContext: false);
                Console.Error.WriteLine(value: $"[world.listen: disconnected connection {connectionId} ({connection.Principal.Describe()})]");
            }
        } catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) {
            // Pre-admission socket death (during the hello exchange, before m_connections.Add above) admitted
            // nothing, so there is nothing to revoke here. A post-admission death is already revoked by the
            // connection's own teardown finally above, which runs on every exit from that block — including
            // this exception unwinding through it.
        } finally {
            client.Dispose();
        }
    }

    private async Task FrameLoopAsync(Connection connection, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            byte[]? frame;

            try {
                frame = await WorldTcpWireFormat.TryReadLengthPrefixedFrameAsync(stream: connection.Stream, maxTotalBytes: WorldTcpWireFormat.MaxUpstreamFrameBytes, ct: ct).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception ex) when (ex is IOException or SocketException) {
                return;
            }

            if (frame is null) {
                return;
            }

            if (!WorldFrameCodec.TryDecode(frame: frame, payload: out var payload, failure: out var failure) || (payload is null)) {
                await WorldTcpWireFormat.WriteRefusalAsync(stream: connection.Stream, reason: $"{failure.Refusal}: {failure.Detail}", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

                continue;
            }

            // The door stamps the connection's OWN admitted identity onto every kind that carries an embedded
            // Principal (Command/Session/Mutation each read it directly rather than the envelope's copy — see
            // ApplyEnvelope's own remarks) — a handler reads the identity the door resolved, never the one the
            // client's bytes claimed.
            var stamped = StampPrincipal(payload: payload, principal: connection.Principal);
            var envelope = new SubmissionEnvelope(
                ConnectionId: connection.Id,
                SessionGeneration: connection.Generation,
                Sequence: connection.NextSequence(),
                CorrelationId: connection.NextCorrelation(),
                Principal: connection.Principal,
                Payload: stamped
            );

            var result = await RunOnTickThreadAsync(work: () => {
                WorldSubmissionResult? captured = null;

                m_server.Submit(envelope: envelope, completion: r => captured = r);

                return captured;
            }).ConfigureAwait(continueOnCapturedContext: false);

            if (result is null) {
                await WorldTcpWireFormat.WriteRefusalAsync(stream: connection.Stream, reason: "the envelope drained with no completion", ct: ct).ConfigureAwait(continueOnCapturedContext: false);
            } else {
                await WorldTcpWireFormat.WriteResultAsync(stream: connection.Stream, result: result, ct: ct).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
    }

    private static WorldSubmissionPayload StampPrincipal(WorldSubmissionPayload payload, WorldPrincipal principal) => payload switch {
        WorldSubmissionPayload.Command command => new WorldSubmissionPayload.Command(Value: (command.Value with { Principal = principal })),
        WorldSubmissionPayload.Session session => new WorldSubmissionPayload.Session(Value: (session.Value with { Principal = principal })),
        WorldSubmissionPayload.Mutation mutation => new WorldSubmissionPayload.Mutation(Value: (mutation.Value with { Principal = principal })),
        _ => payload,
    };

    // Marshals one unit of work onto the tick thread via DrainPending and awaits its result — the ONE hand-off point
    // between a connection's background read loop and the single-threaded server.
    private Task<T> RunOnTickThreadAsync<T>(Func<T> work) {
        var tcs = new TaskCompletionSource<T>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        m_pending.Enqueue(item: () => {
            try {
                tcs.SetResult(result: work());
            } catch (Exception ex) {
                tcs.SetException(exception: ex);
            }
        });

        return tcs.Task;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_cts?.Cancel();
        m_listener?.Stop();

        lock (m_connectionsLock) {
            foreach (var connection in m_connections) {
                connection.Client.Dispose();
            }

            m_connections.Clear();
        }

        m_cts?.Dispose();
    }

    private sealed class Connection(int id, int peerIndex, int generation, TcpClient client, NetworkStream stream, string remoteEndpoint) {
        private long m_sequence;
        private long m_correlationId;

        public int Id { get; } = id;
        public int PeerIndex { get; } = peerIndex;
        public int Generation { get; } = generation;
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = stream;
        public string RemoteEndpoint { get; } = remoteEndpoint;
        public WorldPrincipal Principal => WorldPrincipal.Peer(index: PeerIndex, generation: Generation);

        public long NextSequence() => Interlocked.Increment(location: ref m_sequence);

        public long NextCorrelation() => Interlocked.Increment(location: ref m_correlationId);
    }
}
