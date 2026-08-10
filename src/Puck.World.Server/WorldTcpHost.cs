using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Puck.Carriage;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One admitted remote connection's read-back row — <c>world.peers</c>' one source of truth.</summary>
/// <param name="ConnectionId">The connection id this door assigned at admission.</param>
/// <param name="PeerIndex">The admitted population body index.</param>
/// <param name="Generation">The admission generation.</param>
/// <param name="RemoteEndpoint">The remote socket endpoint, as text.</param>
/// <param name="IdentityDomain">The verified admission identity's domain (see <see cref="WorldAdmissionDoor"/>).</param>
/// <param name="IdentitySubject">The verified admission identity's subject (empty for a Vouches root's chain-resolved subject).</param>
public readonly record struct WorldPeerConnectionInfo(int ConnectionId, int PeerIndex, int Generation, string RemoteEndpoint, string IdentityDomain, string IdentitySubject);

/// <summary>
/// The P7 socket door: a TCP listener admitting remote peers onto the same ordered domain a local script drives.
/// Per connection: the raw Hello handshake (<see cref="WorldHelloDoor"/>'s protocol-version check, then
/// <see cref="WorldAdmissionDoor"/>'s challenge-response identity check) runs off the tick thread — neither touches
/// server state beyond a read-only snapshot of the current document's admission entries — while admission
/// (<see cref="WorldServer.TryAdmitPeerConnection"/>) and every subsequent submission run on the tick thread —
/// marshaled there through <see cref="m_pending"/>, drained by <see cref="DrainPending"/> at the top of every fixed
/// step (<c>WorldServerStepShell.Step</c>), exactly where the design's §1.5 "deterministic fair merge" window sits.
/// v1 keeps that merge to its simplest correct shape: one global FIFO (no per-connection quotas, no bounded-queue
/// backpressure) — a trusted-LAN connection count small enough that fairness never needs more.
/// </summary>
/// <remarks>
/// v1 is strictly request-then-response per connection: a connection's dedicated read loop decodes one upstream
/// frame, awaits its tick-thread completion, writes the one downstream reply, then reads the next — so no
/// correlation id needs to travel on the wire (see <see cref="WorldTcpWireFormat"/>). This is a deliberate
/// simplification the design's own admission-budget/pipelining machinery is not part of; nothing here queues or
/// retries a connection.
/// </remarks>
public sealed class WorldTcpHost : IDisposable {
    /// <summary>The wall-clock deadline for the entire pre-admission handshake (Hello's version check through the
    /// identity door's verify and the tick-thread population admit). This bounds connection lifecycle, never
    /// simulation state — the wall-clock ban in CLAUDE.md's determinism rule governs the tick, and a socket that
    /// never finishes proving who it is has not entered the tick at all
    /// (<see cref="Protocol.WorldAdmissionDoor.TryAdmit"/>'s own <c>now: DateTimeOffset</c> parameter already reads
    /// the wall clock for the identical reason). Without a deadline, a peer that completes Hello but then stalls
    /// (or never sends) the identity frame pins a socket, a read buffer, and a slot under
    /// <see cref="MaxConcurrentHandshakes"/> forever — the slowloris shape. 10 seconds is generous for any
    /// legitimate LAN or WAN client (the identity frame is at most
    /// <see cref="WorldTcpWireFormat.MaxHelloIdentityBytes"/>, ~64 KiB of already-small P-256 envelopes) while still
    /// bounding the worst case to a small, fixed number of seconds rather than never.</summary>
    private static readonly TimeSpan HandshakeDeadline = TimeSpan.FromSeconds(value: 10);

    /// <summary>The ceiling on concurrent in-flight unauthenticated handshakes (a socket accepted but not yet
    /// admitted or refused). A safety representation constant, never a document knob (CLAUDE.md core rule 8's
    /// "legitimate constants" carve-out names capacity bounds that size memory or the wire — this sizes the
    /// pre-admission connection table, not a per-world tunable Play/Dive/Kart/Jump would ever want different).
    /// Sized independently of <see cref="WorldPopulationLimits.CapacityCeiling"/> (128, the admitted population
    /// bound) — a stalled handshake never reaches the population table at all, so it needs its own,
    /// smaller ceiling. 64 is chosen against this class's own documented design target ("a trusted-LAN connection
    /// count small enough that fairness never needs more" — this type's remarks above): generous headroom for that
    /// target's legitimate traffic, while still bounding the socket/thread/buffer cost a flood of half-open
    /// connections can pin before any of them ever reaches a capacity check.</summary>
    private const int MaxConcurrentHandshakes = 64;

    private readonly WorldServer m_server;
    private readonly ConcurrentQueue<Action> m_pending = new();
    private readonly List<Connection> m_connections = [];
    private readonly Lock m_connectionsLock = new();
    private TcpListener? m_listener;
    private CancellationTokenSource? m_cts;
    private Task? m_acceptLoop;
    private int m_nextConnectionId;
    private int m_pendingHandshakes;
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
                return [.. m_connections.Select(selector: static c => new WorldPeerConnectionInfo(ConnectionId: c.Id, PeerIndex: c.PeerIndex, Generation: c.Generation, RemoteEndpoint: c.RemoteEndpoint, IdentityDomain: c.IdentityDomain, IdentitySubject: c.IdentitySubject))];
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

        // The concurrent-handshake ceiling is checked before any byte crosses the wire for this connection, so a
        // flood of sockets that never speak still cannot itself grow past MaxConcurrentHandshakes. Refused cheaply
        // (no crypto, no tick-thread hop) and observably (stderr line, never a silent drop).
        if (Interlocked.Increment(location: ref m_pendingHandshakes) > MaxConcurrentHandshakes) {
            Interlocked.Decrement(location: ref m_pendingHandshakes);
            Console.Error.WriteLine(value: $"[world.listen: refused connection from {remoteEndpoint} — {MaxConcurrentHandshakes} concurrent unauthenticated handshakes already in flight]");

            try {
                await WorldTcpWireFormat.WriteHelloRefusedAsync(stream: stream, reason: $"handshake-ceiling: {MaxConcurrentHandshakes} concurrent unauthenticated handshakes already in flight", ct: ct).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) {
                // The caller could not even receive the refusal — nothing more to do.
            }

            client.Dispose();

            return;
        }

        // Held only for the pre-admission portion below — released the instant this connection is either admitted
        // (it becomes an ordinary authenticated session, no longer subject to the handshake ceiling or deadline) or
        // refused/times out/dies (the outer finally releases it).
        var handshakeSlotHeld = true;

        // The wall-clock deadline, linked to the accept loop's own cancellation so shutdown still wins.
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadlineCts.CancelAfter(delay: HandshakeDeadline);

        var handshakeCt = deadlineCts.Token;

        try {
            // Door 1 of 2 — protocol-version compatibility, checked first and refused with its own spelling
            // ("version-mismatch: …"), never sharing a reason with the identity door below.
            var helloBuffer = new byte[WorldTcpWireFormat.HelloBytes];

            if (!await WorldTcpWireFormat.TryReadExactAsync(stream: stream, buffer: helloBuffer, ct: handshakeCt).ConfigureAwait(continueOnCapturedContext: false)) {
                return;
            }

            var offeredKey = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(source: helloBuffer);

            if (!WorldHelloDoor.TryAccept(offeredKey: offeredKey, refusal: out var helloRefusal)) {
                await WorldTcpWireFormat.WriteHelloRefusedAsync(stream: stream, reason: $"version-mismatch: {helloRefusal}: wire key 0x{offeredKey:x16} != server 0x{WorldProtocol.WireProtocolKey:x16}", ct: handshakeCt).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            // Door 2 of 2 — the identity door: challenge, read back the peer's claim (and chain, for a Vouches
            // identity), verify off the tick thread against a snapshot of the current document's admission entries.
            // Every refusal path here is spelled "identity-refused: …", distinct from door 1's "version-mismatch: ".
            // A disconnect before any length prefix declared a frame is not a refusal — the socket closes with no
            // reply; once a prefix has declared a frame, an incomplete body is a named refusal instead.
            var challenge = WorldAdmissionDoor.NewChallenge();

            await WorldTcpWireFormat.WriteHelloChallengeAsync(stream: stream, challenge: challenge, ct: handshakeCt).ConfigureAwait(continueOnCapturedContext: false);

            var identityRead = await WorldTcpWireFormat.TryReadHelloIdentityAsync(stream: stream, ct: handshakeCt).ConfigureAwait(continueOnCapturedContext: false);

            if (identityRead is WorldTcpWireFormat.HelloIdentityReadResult.Eof) {
                return;
            }

            if (identityRead is not WorldTcpWireFormat.HelloIdentityReadResult.Ok identity) {
                var malformed = (WorldTcpWireFormat.HelloIdentityReadResult.Malformed)identityRead;

                await WorldTcpWireFormat.WriteHelloRefusedAsync(stream: stream, reason: $"identity-refused: {malformed.Reason}", ct: handshakeCt).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            var codec = new FixedLayoutCarriageCodec();
            WorldAdmissionDoor.AdmissionOutcome outcome;
            // Captured alongside the exact entries TryAdmit consults below, so the tick-thread commit can prove the
            // policy has not moved since (WorldDefinition's sections are immutable records: an unrelated
            // mutation/rebuild that never touches Admission leaves this reference unchanged; one that does mints a
            // new list — see WorldServer.TryAdmitPeerConnection's own remarks on this parameter).
            var admissionEntriesAtVerify = m_server.Definition.Admission;

            try {
                var claimEnvelope = codec.DecodeEnvelope(wire: identity.Claim);
                var chainEnvelopes = identity.Chain.Select(selector: bytes => codec.DecodeEnvelope(wire: bytes)).ToArray();
                // Definition is read fresh, off the tick thread — an admission decision consulting a document that
                // moves mid-handshake (a concurrent world.reset/load/reload) is eventual-consistency the same way
                // any other cross-thread document read here would be. This is a presentation/networking admission
                // decision, never simulation state, so it is not a determinism concern; the commit below still
                // proves the policy this verdict rests on is the one the tick thread ultimately sees.
                outcome = WorldAdmissionDoor.TryAdmit(entries: admissionEntriesAtVerify, challenge: challenge, codec: codec, claim: claimEnvelope, chain: chainEnvelopes, now: DateTimeOffset.UtcNow);
            } catch (FormatException exception) {
                await WorldTcpWireFormat.WriteHelloRefusedAsync(stream: stream, reason: $"identity-refused: the presented claim or chain bytes do not decode — {exception.Message}", ct: handshakeCt).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            if (!outcome.Admitted) {
                await WorldTcpWireFormat.WriteHelloRefusedAsync(stream: stream, reason: $"identity-refused: {outcome.Refusal}: {outcome.Detail}", ct: handshakeCt).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            // Population/grant admission runs through the ordered-domain path, fed the verified identity's own
            // authored grant templates. The compare-at-commit check rides along here: TryAdmitPeerConnection
            // refuses by name if the admission policy moved since admissionEntriesAtVerify was captured above. The
            // handshake deadline covers this queue hop too — an authored rate-0/paused world may not drain it at
            // all — and RunOnTickThreadAsync's cancellation guard both releases the waiting socket at the deadline
            // and prevents the orphaned item from admitting later.
            var (admitted, admissionRefusal) = await RunOnTickThreadAsync(
                work: () => m_server.TryAdmitPeerConnection(grantTemplates: (outcome.Grants ?? []), identityDomain: (outcome.Domain ?? string.Empty), identitySubject: (outcome.Subject ?? string.Empty), expectedAdmissionEntries: admissionEntriesAtVerify, admitted: out var entry, refusal: out var reason) ? (entry, (string?)null) : (default(WorldPeerEventEntry), reason),
                ct: handshakeCt
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (admissionRefusal is { } refusalReason) {
                await WorldTcpWireFormat.WriteHelloRefusedAsync(stream: stream, reason: refusalReason, ct: handshakeCt).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            var connectionId = Interlocked.Increment(location: ref m_nextConnectionId);
            var connection = new Connection(id: connectionId, peerIndex: admitted.BodyIndex, generation: admitted.Generation, client: client, stream: stream, remoteEndpoint: remoteEndpoint, identityDomain: (outcome.Domain ?? string.Empty), identitySubject: (outcome.Subject ?? string.Empty));

            lock (m_connectionsLock) {
                m_connections.Add(item: connection);
            }

            // The handshake is over: this connection is admitted, so it leaves both the concurrent-handshake
            // ceiling and the handshake deadline behind — the frame loop below runs on the accept loop's own ct,
            // unbounded by HandshakeDeadline (an ongoing authenticated session is not a handshake).
            handshakeSlotHeld = false;
            Interlocked.Decrement(location: ref m_pendingHandshakes);

            try {
                Console.Error.WriteLine(value: $"[world.listen: admitted connection {connectionId} as {connection.Principal.Describe()} identity domain:{connection.IdentityDomain} subject:{connection.IdentitySubject} grants:{(outcome.Grants?.Count ?? 0)} from {remoteEndpoint}]");
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
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            // The accept loop was not cancelled — HandshakeDeadline fired on a connection that never finished the
            // pre-admission handshake in time (slowloris, or a genuinely dead peer). Observable by name, never a
            // silent drop.
            Console.Error.WriteLine(value: $"[world.listen: handshake from {remoteEndpoint} exceeded the {HandshakeDeadline.TotalSeconds:0}s deadline — connection refused]");
        } catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) {
            // Pre-admission socket death (during the hello exchange, before m_connections.Add above) admitted
            // nothing, so there is nothing to revoke here. A post-admission death is already revoked by the
            // connection's own teardown finally above, which runs on every exit from that block — including
            // this exception unwinding through it.
        } finally {
            if (handshakeSlotHeld) {
                Interlocked.Decrement(location: ref m_pendingHandshakes);
            }

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

            // The door stamps the connection's own admitted identity onto every kind that carries an embedded
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

    // Marshals one unit of work onto the tick thread via DrainPending and awaits its result — the one hand-off point
    // between a connection's background read loop and the single-threaded server.
    private Task<T> RunOnTickThreadAsync<T>(Func<T> work, CancellationToken ct = default) {
        var tcs = new TaskCompletionSource<T>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        m_pending.Enqueue(item: () => {
            // A canceled queue item must never execute later: the caller has already released its socket/handshake
            // slot, so committing admission here would create a population body with no connection to own it.
            if (ct.IsCancellationRequested) {
                tcs.TrySetCanceled(cancellationToken: ct);

                return;
            }

            try {
                tcs.TrySetResult(result: work());
            } catch (Exception ex) {
                tcs.TrySetException(exception: ex);
            }
        });

        // WaitAsync releases the caller promptly even when the tick queue is not draining. The guard above is the
        // other half of the contract: it prevents that still-enqueued work from taking effect if draining resumes.
        return (ct.CanBeCanceled ? tcs.Task.WaitAsync(cancellationToken: ct) : tcs.Task);
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

    private sealed class Connection(int id, int peerIndex, int generation, TcpClient client, NetworkStream stream, string remoteEndpoint, string identityDomain, string identitySubject) {
        private long m_sequence;
        private long m_correlationId;

        public int Id { get; } = id;
        public int PeerIndex { get; } = peerIndex;
        public int Generation { get; } = generation;
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = stream;
        public string RemoteEndpoint { get; } = remoteEndpoint;
        public string IdentityDomain { get; } = identityDomain;
        public string IdentitySubject { get; } = identitySubject;
        public WorldPrincipal Principal => WorldPrincipal.Peer(index: PeerIndex, generation: Generation);

        public long NextSequence() => Interlocked.Increment(location: ref m_sequence);

        public long NextCorrelation() => Interlocked.Increment(location: ref m_correlationId);
    }
}
