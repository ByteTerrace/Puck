using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
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
    private readonly WorldFederationSecurity m_federationSecurity;
    // Indexed by WorldFederationRefusal; every federation refusal this door writes is counted here so the read-back
    // reports refusals by name rather than by sentence.
    private readonly int[] m_federationRefusals = new int[Enum.GetValues<WorldFederationRefusal>().Length];
    // Federation connections are long-lived and are not admitted bodies, so they are not in m_connections. They are
    // tracked here only so shutdown can half-close them; the value is unused.
    private readonly ConcurrentDictionary<TcpClient, byte> m_federationConnections = new();
    private readonly ConcurrentQueue<Action> m_pending = new();
    private readonly List<Connection> m_connections = [];
    private readonly Lock m_connectionsLock = new();
    private TcpListener? m_listener;
    private CancellationTokenSource? m_cts;
    private Task? m_acceptLoop;
    private int m_nextConnectionId;
    private long m_nextFederationIntentLease;
    private int m_pendingHandshakes;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="WorldTcpHost"/> class over the server it admits into.</summary>
    /// <param name="server">The authoritative server.</param>
    public WorldTcpHost(WorldServer server) : this(server: server, federationSecurity: new WorldFederationSecurity(secret: null)) { }

    /// <summary>Initializes a host with an explicit federation authentication policy.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <param name="federationSecurity">The process-scoped federation authenticator; an unconfigured instance denies federation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public WorldTcpHost(WorldServer server, WorldFederationSecurity federationSecurity) {
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: federationSecurity);

        m_server = server;
        m_federationSecurity = federationSecurity;
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

    /// <summary>Gets every federation refusal this door has written, by name, omitting names never used.</summary>
    public IReadOnlyList<(WorldFederationRefusal Refusal, int Count)> FederationRefusals {
        get {
            var counts = new List<(WorldFederationRefusal, int)>();

            foreach (var refusal in Enum.GetValues<WorldFederationRefusal>()) {
                var count = Volatile.Read(location: ref m_federationRefusals[(int)refusal]);

                if (count > 0) {
                    counts.Add(item: (refusal, count));
                }
            }

            return counts;
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

            if (offeredKey == WorldFederationCodec.WireKey) {
                if (!m_federationSecurity.IsConfigured) {
                    await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.AuthenticationUnconfigured, detail: "this authority carries no federation credentials", ct: handshakeCt).ConfigureAwait(false);
                    return;
                }

                var federationChallenge = m_federationSecurity.NewChallenge();
                await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Challenge, body: federationChallenge, ct: handshakeCt).ConfigureAwait(false);
                var authentication = await WorldFederationCodec.ReadRequestAsync(stream: stream, ct: handshakeCt).ConfigureAwait(false);

                if (!authentication.Ok
                    || (authentication.Kind != (byte)WorldFederationRequest.Authenticate)
                    || !WorldFederationCodec.TryDecodeAuthentication(body: authentication.Body, sourceAuthority: out var federationAuthority, proof: out var federationProof, failure: out _)
                    || !m_federationSecurity.Verify(sourceAuthority: federationAuthority, challenge: federationChallenge, proof: federationProof)) {
                    await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.AuthenticationFailed, detail: "the presented source authority and proof did not verify", ct: handshakeCt).ConfigureAwait(false);
                    return;
                }

                await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Ack, body: default, ct: handshakeCt).ConfigureAwait(false);
                handshakeSlotHeld = false;
                Interlocked.Decrement(location: ref m_pendingHandshakes);
                _ = m_federationConnections.TryAdd(key: client, value: 0);

                try {
                    await ServeFederationAsync(stream: stream, sourceAuthority: federationAuthority, ct: ct).ConfigureAwait(continueOnCapturedContext: false);
                } finally {
                    _ = m_federationConnections.TryRemove(key: client, value: out _);
                }

                return;
            }

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
                work: () => m_server.TryAdmitPeerConnection(verdict: outcome.Verdict, expectedAdmissionEntries: admissionEntriesAtVerify, admitted: out var entry, refusal: out var reason) ? (entry, (string?)null) : (default(WorldPeerEventEntry), reason),
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

    // A federation connection is a persistent authenticated lane. Requests ride it strictly request-then-response
    // and in order, which is what lets the peer omit a correlation id. Observe and IntentStream take the connection
    // over and stream on it until it closes.
    private async Task ServeFederationAsync(NetworkStream stream, string sourceAuthority, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            var frame = await WorldFederationCodec.ReadRequestAsync(stream: stream, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            if (!frame.Ok) {
                if (frame.Failure.Refusal != WorldWireRefusal.ConnectionClosed) {
                    await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.FrameMalformed, detail: frame.Failure.ToString(), ct: ct).ConfigureAwait(continueOnCapturedContext: false);
                }

                return;
            }

            if (!await ServeFederationRequestAsync(stream: stream, sourceAuthority: sourceAuthority, kind: (WorldFederationRequest)frame.Kind, body: frame.Body, ct: ct).ConfigureAwait(continueOnCapturedContext: false)) {
                return;
            }
        }
    }

    // Returns whether the lane stays open for another request.
    private async Task<bool> ServeFederationRequestAsync(NetworkStream stream, string sourceAuthority, WorldFederationRequest kind, byte[] body, CancellationToken ct) {
        switch (kind) {
            case WorldFederationRequest.Reserve:
                return await ServeReserveAsync(stream: stream, sourceAuthority: sourceAuthority, body: body, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Commit:
                return await ServeCommitAsync(stream: stream, sourceAuthority: sourceAuthority, body: body, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Abort:
            case WorldFederationRequest.AcknowledgeTransfer:
            case WorldFederationRequest.Status:
                return await ServeTransferKeyAsync(stream: stream, sourceAuthority: sourceAuthority, kind: kind, body: body, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Submission:
                return await ServeSubmissionAsync(stream: stream, sourceAuthority: sourceAuthority, body: body, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Route:
                return await ServeRouteAsync(stream: stream, sourceAuthority: sourceAuthority, body: body, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Observe:
                await StreamProjectionAsync(stream: stream, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

                return false;

            case WorldFederationRequest.IntentStream:
                if (body.Length != 0) {
                    await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.PayloadUnexpected, detail: "the intent stream opening carries a payload", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

                    return false;
                }

                await StreamFederatedIntentsAsync(stream: stream, sourceAuthority: sourceAuthority, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

                return false;

            default:
                await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.RequestUnsupported, detail: $"{kind} is not accepted on an open federation lane", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

                return false;
        }
    }

    private async Task<bool> ServeReserveAsync(NetworkStream stream, string sourceAuthority, byte[] body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeReservation(body: body, defaults: m_server.Definition.PlayerDefaults, request: out var request, failure: out var failure) || (request is null)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.FrameMalformed, detail: $"reservation — {failure}", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(a: request.SourceAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.SourceAuthorityMismatch, detail: "the reservation names a source authority this connection did not authenticate as", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        // Reserve/commit/status are authority transactions, not ordinary gameplay submissions. They run on this
        // socket worker under WorldServer's authority gate rather than through DrainPending: a destination that
        // could only answer on its own next tick would deadlock two hosts crossing into one another at once.
        var reply = m_server.ReserveTransfer(request: request);

        await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Reservation, body: WorldFederationCodec.EncodeReservationReply(reply: reply), ct: ct).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }

    private async Task<bool> ServeCommitAsync(NetworkStream stream, string sourceAuthority, byte[] body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeCommit(body: body, sourceAuthority: out var carriedAuthority, transferId: out var transferId, members: out var members, failure: out var failure)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.FrameMalformed, detail: $"commit — {failure}", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(a: carriedAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.SourceAuthorityMismatch, detail: "the commit names a source authority this connection did not authenticate as", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        var accepted = m_server.CommitTransfer(sourceAuthority: sourceAuthority, transferId: transferId, members: members, reason: out var commitReason);

        await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Commit, body: WorldFederationCodec.EncodeCommitReply(accepted: accepted, reason: (accepted ? string.Empty : commitReason)), ct: ct).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }

    private async Task<bool> ServeTransferKeyAsync(NetworkStream stream, string sourceAuthority, WorldFederationRequest kind, byte[] body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeTransferKey(body: body, sourceAuthority: out var carriedAuthority, transferId: out var transferId, failure: out var failure)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.FrameMalformed, detail: $"{kind} transfer key — {failure}", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(a: carriedAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.SourceAuthorityMismatch, detail: $"the {kind} names a transfer namespace this connection did not authenticate as", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (kind == WorldFederationRequest.Status) {
            var status = m_server.TransferStatus(sourceAuthority: sourceAuthority, transferId: transferId);

            await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Status, body: new[] { (byte)status }, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (kind == WorldFederationRequest.Abort) {
            m_server.AbortTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
        } else {
            m_server.AcknowledgeTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
        }

        await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Ack, body: default, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }

    private async Task<bool> ServeSubmissionAsync(NetworkStream stream, string sourceAuthority, byte[] body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeSubmission(body: body, sourceAuthority: out var carriedAuthority, mobility: out var mobility, frame: out var submittedFrame, failure: out var failure)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.FrameMalformed, detail: $"submission credential — {failure}", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(a: carriedAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.SourceAuthorityMismatch, detail: "the submission names a source authority this connection did not authenticate as", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!WorldFrameCodec.TryDecode(frame: submittedFrame, payload: out var payload, failure: out var codecFailure) || (payload is null)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.FrameMalformed, detail: $"submission frame — {codecFailure}", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!m_server.TryTransferredPrincipal(sourceAuthority: sourceAuthority, mobility: in mobility, principal: out var principal)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.CredentialUnknown, detail: "the submission credential names no committed transfer body", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        var (result, forwardReason) = await ResolveForwardedSubmissionAsync(principal: principal, mobility: mobility, payload: payload, ct: ct).ConfigureAwait(continueOnCapturedContext: false);

        if (result is null) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.SubmissionRefused, detail: (forwardReason.Length > 0 ? forwardReason : "the submission names no live or forwarded transfer body"), ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if ((payload is WorldSubmissionPayload.Session { Value: SessionRequest.Leave }) &&
            (result is WorldSubmissionResult.Session { Reply.Accepted: true })) {
            m_server.RetireTransferredMobility(mobility: in mobility);
        }

        using var completion = new MemoryStream();

        await WorldTcpWireFormat.WriteResultAsync(stream: completion, result: result, ct: ct).ConfigureAwait(continueOnCapturedContext: false);
        await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Completion, body: completion.ToArray(), ct: ct).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }

    private async Task<(WorldSubmissionResult? Result, string Reason)> ResolveForwardedSubmissionAsync(WorldPrincipal principal, WorldMobilityIdentity mobility, WorldSubmissionPayload payload, CancellationToken ct) {
        // The liveness test and the submit it authorizes are one gated operation: a body that detaches between two
        // separate gate acquisitions would let a submission apply to a slot its traveler no longer owns.
        var applied = m_server.ExecuteAuthorityOperation(operation: () => {
            if (!IsLiveTransferredPrincipal(principal: principal)) {
                return (Live: false, Result: (WorldSubmissionResult?)null);
            }

            var stamped = StampPrincipal(payload: payload, principal: principal);

            if ((stamped is WorldSubmissionPayload.Session { Value: SessionRequest.Leave }) &&
                m_server.Population.TryCaptureTransferredPeer(index: principal.Index, peer: out var peer)) {
                m_server.DisconnectPeerConnection(peer: peer);

                return (Live: true, Result: (WorldSubmissionResult?)new WorldSubmissionResult.Session(new SessionReply(Accepted: true, AssignedIndex: (principal.Index + 1), RosterEcho: string.Empty, Reason: string.Empty)));
            }

            WorldSubmissionResult? captured = null;

            m_server.Submit(envelope: new SubmissionEnvelope(ConnectionId: principal.Index, SessionGeneration: principal.Generation, Sequence: 0, CorrelationId: 0, Principal: principal, Payload: stamped), completion: value => captured = value);

            return (Live: true, Result: captured);
        });

        if (applied.Live) {
            return (applied.Result, string.Empty);
        }

        if (m_server.TransferForwarder is not { } forwarder) {
            return (null, string.Empty);
        }

        // Detach precedes publication of the committed onward route. A routed query/command can arrive in that small
        // interval just like a held-stick update can; retain the one typed request at this authority until the
        // commit path publishes its immutable credential.
        var reason = string.Empty;

        for (var attempt = 0; attempt < 25; attempt++) {
            if (forwarder.TryForwardSubmission(source: m_server, mobility: in mobility, payload: payload, result: out var forwarded, reason: out reason)) {
                return (forwarded, reason);
            }

            if (!reason.Contains(value: "no committed onward route", comparisonType: StringComparison.Ordinal)) {
                break;
            }

            await Task.Delay(delay: TimeSpan.FromMilliseconds(4), cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
        }

        return (null, reason);
    }

    private async Task<bool> ServeRouteAsync(NetworkStream stream, string sourceAuthority, byte[] body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeRouteCredential(body: body, sourceAuthority: out var carriedAuthority, mobility: out var mobility, failure: out var failure)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.FrameMalformed, detail: $"route credential — {failure}", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(a: carriedAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.SourceAuthorityMismatch, detail: "the route credential names a source authority this connection did not authenticate as", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!m_server.TryTransferredPrincipal(sourceAuthority: sourceAuthority, mobility: in mobility, principal: out var principal)) {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.CredentialUnknown, detail: "the route credential names no committed transfer body", ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        var declaredEndpoint = (m_server.Definition.Host.Authority ?? ListenEndpoint);
        // Read the body and its address inside the same gated operation that decided it is live.
        var local = ((declaredEndpoint is { } localEndpoint)
            ? m_server.ExecuteAuthorityOperation(operation: () => (IsLiveTransferredPrincipal(principal: principal) ? DescribeLocalRoute(principal: principal, localEndpoint: localEndpoint) : (WorldAuthorityRouteDescription?)null))
            : null);

        WorldAuthorityRouteDescription route;
        var routeReason = string.Empty;

        if (local is { } described) {
            route = described;
        } else if ((m_server.TransferForwarder is { } forwarder) && forwarder.TryDescribeForwarding(source: m_server, mobility: in mobility, route: out route, reason: out routeReason)) {
            // The composition root recursively resolved the final authority.
        } else {
            await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.RouteUnknown, detail: (routeReason.Length > 0 ? routeReason : "the traveler has no live or forwarded route"), ct: ct).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Route, body: WorldFederationCodec.EncodeRoute(route: in route), ct: ct).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }

    private WorldAuthorityRouteDescription DescribeLocalRoute(WorldPrincipal principal, string localEndpoint) {
        var body = m_server.Population.EntryBody(index: principal.Index) ??
            throw new InvalidOperationException(message: $"live transferred {principal.Describe()} has no body");

        return new WorldAuthorityRouteDescription(
            Endpoint: localEndpoint,
            Entity: new WorldEntityAddress(
                Authority: m_server.AuthorityIdentity,
                Index: principal.Index,
                Generation: m_server.Population.Generation(index: principal.Index)),
            Tick: (m_server.NextInputTick - 1UL),
            Position: body.FixedPosition,
            Orientation: body.FixedOrientation,
            BodyColor: m_server.Population.BodyColor(index: principal.Index),
            Kit: m_server.Population.KitIndex(index: principal.Index),
            Look: m_server.Population.LookIndex(index: principal.Index),
            CatalogRig: m_server.Population.CatalogRig(index: principal.Index),
            PlacementId: m_server.Population.InhabitantPlacementId(index: principal.Index),
            Definition: m_server.Definition);
    }

    private Task WriteFederationRefusal(NetworkStream stream, WorldFederationRefusal refusal, string detail, CancellationToken ct) {
        Interlocked.Increment(location: ref m_federationRefusals[(int)refusal]);

        return WorldFederationCodec.WriteRefusalAsync(stream: stream, refusal: refusal, detail: detail, ct: ct);
    }

    private async Task StreamProjectionAsync(NetworkStream stream, CancellationToken ct) {
        var sink = new FederationProjectionSink();
        var lease = m_server.ExecuteAuthorityOperation(operation: () => m_server.AttachSink(sink: sink));

        try {
            await foreach (var item in sink.Frames.ReadAllAsync(cancellationToken: ct).ConfigureAwait(false)) {
                await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: item.Kind, body: item.Body, ct: ct).ConfigureAwait(false);
            }
        } catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) {
            // Observer lifetime is the socket lifetime.
        } finally {
            // WorldOutputHub's subscriber list is not itself guarded, so the release must hold the same authority
            // gate the attach did — otherwise this dispose races a tick publishing through that list.
            m_server.ExecuteAuthorityOperation(operation: () => {
                lease.Dispose();

                return true;
            });
        }
    }

    private sealed class FederationProjectionSink : IClientSink {
        private readonly Channel<(WorldFederationResponse Kind, byte[] Body)> m_frames = Channel.CreateBounded<(WorldFederationResponse, byte[])>(new BoundedChannelOptions(capacity: 8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        public ChannelReader<(WorldFederationResponse Kind, byte[] Body)> Frames => m_frames.Reader;
        public void DeliverSnapshot(in WorldSnapshot snapshot) => Write(kind: WorldFederationResponse.Snapshot, body: WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot));
        public void DeliverDefinition(WorldDefinition definition) => Write(kind: WorldFederationResponse.Definition, body: WorldFederationCodec.EncodeDefinition(definition: definition));
        public void DeliverAnswer(in QueryAnswer answer) { }
        public void DeliverComposition(WorldComposition composition) { }
        public void DeliverSessionLever(WorldSessionLever lever) { }
        private void Write(WorldFederationResponse kind, byte[] body) {
            if (!m_frames.Writer.TryWrite(item: (kind, body))) {
                _ = m_frames.Writer.TryComplete(error: new IOException("federation observer exceeded its bounded projection backlog"));
            }
        }
    }

    // A long-lived authenticated control lane. The socket carries state updates, not impulses: the WorldServer
    // republishes the latest accepted image on every destination tick until another update or this connection's
    // finally releases its lease. Request/ack remains ordered, while connection setup and challenge authentication
    // are paid once rather than once per simulation tick.
    private async Task StreamFederatedIntentsAsync(NetworkStream stream, string sourceAuthority, CancellationToken ct) {
        var leaseId = Interlocked.Increment(location: ref m_nextFederationIntentLease);
        var touched = new Dictionary<WorldMobilityIdentity, (WorldPrincipal Principal, IntentSubmission Submission)>();
        var forwardRelease = true;
        await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Ack, body: default, ct: ct).ConfigureAwait(false);

        try {
            while (!ct.IsCancellationRequested) {
                var frame = await WorldFederationCodec.ReadRequestAsync(stream: stream, ct: ct).ConfigureAwait(false);
                if (!frame.Ok) {
                    if (frame.Failure.Refusal != WorldWireRefusal.ConnectionClosed) {
                        await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.FrameMalformed, detail: frame.Failure.ToString(), ct: ct).ConfigureAwait(false);
                    }
                    return;
                }
                if (frame.Kind == (byte)WorldFederationRequest.IntentStreamHandoff) {
                    if (frame.Body.Length != 0) {
                        await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.PayloadUnexpected, detail: "the intent stream handoff carries a payload", ct: ct).ConfigureAwait(false);
                        return;
                    }

                    // The same client is deliberately moving this held-state lane to the newly published authority.
                    // Its new lane immediately seeds the current state, so forwarding a synthetic neutral from this
                    // older lane would race that seed and could cancel a still-held stick after the handoff.
                    forwardRelease = false;
                    await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Ack, body: default, ct: ct).ConfigureAwait(false);
                    return;
                }
                if (frame.Kind != (byte)WorldFederationRequest.Intent) {
                    await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.RequestUnsupported, detail: $"{(WorldFederationRequest)frame.Kind} is not accepted on an open intent stream", ct: ct).ConfigureAwait(false);
                    return;
                }
                if (!WorldFederationCodec.TryDecodeIntent(body: frame.Body, sourceAuthority: out var carriedAuthority, mobility: out var mobility, submission: out var submission, failure: out var intentFailure)) {
                    await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.FrameMalformed, detail: $"intent \u2014 {intentFailure}", ct: ct).ConfigureAwait(false);
                    return;
                }
                if (!string.Equals(a: carriedAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
                    await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.SourceAuthorityMismatch, detail: "the intent names a source authority this connection did not authenticate as", ct: ct).ConfigureAwait(false);
                    return;
                }
                if (!m_server.TryTransferredPrincipal(sourceAuthority: sourceAuthority, mobility: in mobility, principal: out var principal)) {
                    await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.CredentialUnknown, detail: "the intent names no committed transfer body", ct: ct).ConfigureAwait(false);
                    return;
                }

                var stamped = submission with { EntityIndex = principal.Index, Principal = principal };
                var accepted = false;
                var reason = string.Empty;

                if (m_server.ExecuteAuthorityOperation(operation: () => {
                    if (!IsLiveTransferredPrincipal(principal: principal)) {
                        return false;
                    }

                    m_server.PublishFederatedIntent(leaseId: leaseId, submission: in stamped);

                    return true;
                })) {
                    accepted = true;
                } else if (m_server.TransferForwarder is { } forwarder) {
                    // Detach necessarily precedes publication of the committed onward route. Socket ingress can land
                    // inside that tiny window; retain this state update and retry the route lookup rather than
                    // turning an ordinary handoff into a visible control-lane outage.
                    for (var attempt = 0; attempt < 25; attempt++) {
                        accepted = forwarder.TryForwardIntent(source: m_server, mobility: in mobility, submission: in stamped, reason: out reason);
                        if (accepted) {
                            break;
                        }
                        await Task.Delay(delay: TimeSpan.FromMilliseconds(4), cancellationToken: ct).ConfigureAwait(false);
                    }
                }

                if (!accepted) {
                    await WriteFederationRefusal(stream: stream, refusal: WorldFederationRefusal.IntentRefused, detail: (reason.Length > 0 ? reason : "the intent names no live or forwarded transfer body"), ct: ct).ConfigureAwait(false);
                    return;
                }

                touched[mobility] = (Principal: principal, Submission: stamped);
                await WorldFederationCodec.WriteResponseAsync(stream: stream, kind: WorldFederationResponse.Ack, body: default, ct: ct).ConfigureAwait(false);
            }
        } finally {
            m_server.ReleaseFederatedIntents(leaseId: leaseId);

            // A stream may now terminate at an older authority in a forwarding chain. Publish an explicit neutral
            // image through that same chain so the final writer never retains a held stick after its originating
            // connection vanished.
            foreach (var pair in touched) {
                if (!forwardRelease) {
                    break;
                }
                if (m_server.ExecuteAuthorityOperation(operation: () => IsLiveTransferredPrincipal(principal: pair.Value.Principal)) || (m_server.TransferForwarder is not { } forwarder)) {
                    continue;
                }

                var released = pair.Value.Submission with { Intent = default, HeldChannels = default };
                var mobility = pair.Key;
                if (!forwarder.TryForwardIntent(source: m_server, mobility: in mobility, submission: in released, reason: out var reason)) {
                    Console.Error.WriteLine(value: $"[world.authority unavailable: traveler {mobility.Incarnation} release could not follow its committed route ({reason})]");
                }
            }
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

    // MUST be called inside WorldServer.ExecuteAuthorityOperation: it reads population state the tick thread writes,
    // and every caller pairs that read with the act it authorizes inside one gated operation.
    private bool IsLiveTransferredPrincipal(WorldPrincipal principal) =>
        ((principal.Kind == PrincipalKind.Peer) &&
         ((uint)principal.Index < (uint)m_server.Population.Capacity) &&
         m_server.Population.IsActive(index: principal.Index) &&
         (m_server.Population.PeerPrincipal(index: principal.Index) == principal));

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

        // Half-close every federation lane BEFORE cancelling its read: cancelling a pending socket read closes the
        // handle abortively, and a peer blocked on that lane reads the abort (WSAECONNABORTED) as an outage rather
        // than as this authority ending an ordinary session. A FIN gives it a clean end-of-stream instead.
        foreach (var federation in m_federationConnections.Keys) {
            try {
                federation.Client.Shutdown(how: SocketShutdown.Send);
            } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) {
                // The lane is already gone; there is nothing left to end politely.
            }
        }

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
