using Puck.Commands;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Puck.Abstractions;
using Puck.Networking;
using Puck.Networking.Peers;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One admitted remote connection's read-back row — <c>world.peers</c>' one source of truth.</summary>
/// <param name="ConnectionId">The connection id this door assigned at admission.</param>
/// <param name="PeerIndex">The admitted population body index.</param>
/// <param name="Generation">The admission generation.</param>
/// <param name="RemoteEndpoint">The remote socket endpoint, as text.</param>
/// <param name="IdentityDomain">The verified admission identity's domain (see <see cref="WorldAdmissionDoor"/>).</param>
/// <param name="IdentitySubject">The verified admission identity's subject (empty for a Vouches root's chain-resolved subject).</param>
/// <param name="Tier">How much of this world's document the admitting entry authorized this connection to receive.</param>
public readonly record struct WorldPeerConnectionInfo(int ConnectionId, int PeerIndex, int Generation, string RemoteEndpoint, string IdentityDomain, string IdentitySubject, WorldDisclosureTier Tier);
/// <summary>
/// The World admission and application-protocol endpoint over the networking library's authenticated QUIC peers.
/// Per connection: the raw Hello handshake (<see cref="WorldHelloDoor"/>'s protocol-version check, then
/// <see cref="WorldAdmissionDoor"/>'s challenge-response identity check) runs off the tick thread — neither touches
/// server state beyond a read-only snapshot of the current document's admission entries — while admission
/// (<see cref="WorldServer.TryAdmitPeerConnection"/>) and every subsequent submission run on the tick thread —
/// marshaled there through the row mailbox, drained by <see cref="DrainPending"/> at the top of every fixed
/// step (<c>WorldServerStepShell.Step</c>), exactly where the design's §1.5 "deterministic fair merge" window sits.
/// v1 keeps that merge to its simplest correct shape: one global FIFO (no per-connection quotas, no bounded-queue
/// backpressure) — a trusted-LAN connection count small enough that fairness never needs more.
/// </summary>
/// <remarks>
/// v1 is strictly request-then-response per connection: a connection's dedicated read loop decodes one upstream
/// frame, awaits its tick-thread completion, writes the one downstream reply, then reads the next — so no
/// correlation id needs to travel on the wire (see <see cref="WorldPeerWireFormat"/>). This is a deliberate
/// simplification the design's own admission-budget/pipelining machinery is not part of; nothing here queues or
/// retries a connection.
/// </remarks>
public sealed class WorldPeerHost : IDisposable {
    /// <summary>The ceiling on concurrent in-flight unauthenticated handshakes (a socket accepted but not yet
    /// admitted or refused). A safety representation constant, never a document knob: it sizes the
    /// pre-admission connection table, not a per-world tunable.
    /// Sized independently of the world's population (<see cref="WorldBodiesLimits.IndexLimit"/> bounds a body index)
    /// — a stalled handshake never reaches the population table at all, so it needs its own,
    /// smaller ceiling. 64 is chosen against this class's own documented design target ("a trusted-LAN connection
    /// count small enough that fairness never needs more" — this type's remarks above): generous headroom for that
    /// target's legitimate traffic, while still bounding the socket/thread/buffer cost a flood of half-open
    /// connections can pin before any of them ever reaches a capacity check.</summary>
    private const int MaxConcurrentHandshakes = 64;

    /// <summary>Bounds authenticated authority sockets and their observation fanout. 256 allows many more
    /// simultaneous authorities than any authored world uses, while keeping each host's retained resources finite.</summary>
    public const int MaximumFederationConnections = 256;

    /// <summary>Bounds idle credential revocation latency, including while simulation is paused. Requests also
    /// recheck their credential at dispatch, so this interval never grants an expired session another request.</summary>
    public static TimeSpan CredentialSweepInterval { get; } = TimeSpan.FromSeconds(value: 1);
    /// <summary>Gets the deadline, on the host's clock, for the entire pre-admission handshake (Hello's version check
    /// through the identity door's verify and the tick-thread population admit). This bounds connection lifecycle,
    /// never simulation state — the wall-clock ban in AGENTS.md's determinism rule governs the tick, and a socket that
    /// never finishes proving who it is has not entered the tick at all
    /// (<see cref="Protocol.WorldAdmissionDoor.TryAdmit"/>'s own <c>now: DateTimeOffset</c> parameter reads the same
    /// admission clock for the identical reason). Without a deadline, a peer that completes Hello but then stalls
    /// (or never sends) the identity frame pins a socket, a read buffer, and a slot under
    /// <see cref="MaxConcurrentHandshakes"/> forever — the slowloris shape. 10 seconds is generous for any
    /// legitimate LAN or WAN client (the identity frame is at most
    /// <see cref="HandshakeWireFormat.MaxHelloIdentityBytes"/>, ~64 KiB of already-small P-256 envelopes) while still
    /// bounding the worst case to a small, fixed number of seconds rather than never.</summary>
    public static TimeSpan HandshakeDeadline { get; } = TimeSpan.FromSeconds(value: 10);
    /// <summary>Gets how long a forwarded submission or intent waits, on the host clock, before it looks up an onward
    /// route again: detaching a traveler precedes publishing its committed onward route, so ingress landing inside
    /// that window retries rather than refusing an ordinary handoff.</summary>
    public static TimeSpan RouteLookupRetryDelay { get; } = TimeSpan.FromMilliseconds(value: 4);
    /// <summary>Gets how many times a forwarded submission or intent looks up an onward route, one
    /// <see cref="RouteLookupRetryDelay"/> apart, before it forwards anyway and takes the refusal the forwarder
    /// answers.</summary>
    public const int RouteLookupAttempts = 25;

    private readonly IAuthenticator m_authenticator;
    private readonly WorldAttestationIssuer? m_issuer;
    private readonly WorldPeerNetwork m_network;
    private readonly bool m_ownsNetwork;
    private readonly WorldServer m_server;
    private readonly TimeProvider m_timeProvider;

    private Task? m_acceptLoop;
    private ITimer? m_credentialTimer;
    private int m_sweepingCredentials;
    private CancellationTokenSource? m_cts;
    private bool m_disposed;
    private int m_nextConnectionId;
    private int m_pendingHandshakes;

    // Indexed by WorldFederationRefusal; every federation refusal this door writes is counted here so the read-back
    // reports refusals by name rather than by sentence.
    private readonly int[] m_federationRefusals = new int[Enum.GetValues<WorldFederationRefusal>().Length];
    // Federation connections are long-lived and are not admitted bodies, so they are not in m_connections. They are
    // tracked independently so idle observation/intent streams expire along with request/response sessions.
    private readonly ConcurrentDictionary<PeerStream, Credential> m_federationConnections = new();
    private readonly ConcurrentDictionary<(string SourceAuthority, WorldEntityAddress Incarnation), TravelerLiveness> m_travelerLiveness = new();
    private readonly SemaphoreSlim m_federationSlots = new(initialCount: MaximumFederationConnections, maxCount: MaximumFederationConnections);
    private readonly List<Connection> m_connections = [];
    private readonly Lock m_connectionsLock = new();

    private sealed class TravelerLiveness(WorldMobilityIdentity mobility) {
        public readonly Lock Gate = new();
        public WorldMobilityIdentity Mobility = mobility;
        public int ActiveStreams;
    }

    /// <summary>Initializes a new instance of the <see cref="WorldPeerHost"/> class over the server it admits into.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <param name="timeProvider">The host clock: the handshake deadline, the refusal drain that holds a closing
    /// connection open for its peer, the instant an identity claim is verified at, and the peer deadlines of a network
    /// this host owns. Defaults to system time.</param>
    /// <param name="transportHandshakeTimeout">The wall-clock bound on each QUIC/TLS handshake of the network this host
    /// owns; <see langword="null"/> is <see cref="QuicPeerTransport.DefaultHandshakeTimeout"/>.</param>
    public WorldPeerHost(WorldServer server, TimeProvider? timeProvider = null, TimeSpan? transportHandshakeTimeout = null) : this(
        server: server,
        authenticator: new WorldAttestedAuthenticator(),
        timeProvider: timeProvider,
        transportHandshakeTimeout: transportHandshakeTimeout
    ) { }
    /// <summary>Initializes a host with an explicit federation authentication policy.</summary>
    /// <param name="server">The authoritative server.</param>
    /// <param name="authenticator">The process-scoped federation authenticator; an unconfigured instance denies federation.</param>
    /// <param name="network">The externally owned shared peer network, or null to own an ephemeral one. A supplied
    /// network's <see cref="WorldPeerNetwork.Clock"/> is this host's clock.</param>
    /// <param name="timeProvider">The host clock: the handshake deadline, the refusal drain that holds a closing
    /// connection open for its peer, the instant an identity claim is verified at, the pacing of a forwarded
    /// submission's or intent's route-lookup retry, and the peer deadlines of a network this host owns. Defaults to the
    /// supplied network's clock, else system time; a clock other than a supplied network's is refused, since one host
    /// has one clock.</param>
    /// <param name="transportHandshakeTimeout">The wall-clock bound on each QUIC/TLS handshake when this host owns its
    /// network; <see langword="null"/> is <see cref="QuicPeerTransport.DefaultHandshakeTimeout"/>. A supplied
    /// <paramref name="network"/> carries its own.</param>
    /// <param name="issuer">The host's attestation issuer, which answers a client's issuance request in place of
    /// authentication, or <see langword="null"/> when this host vouches for no one. Owned by the caller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> or <paramref name="authenticator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="timeProvider"/> is not the supplied
    /// <paramref name="network"/>'s clock.</exception>
    public WorldPeerHost(WorldServer server, IAuthenticator authenticator, WorldPeerNetwork? network = null, TimeProvider? timeProvider = null, TimeSpan? transportHandshakeTimeout = null,
        WorldAttestationIssuer? issuer = null) {
        if (
            (network is not null) &&
            (timeProvider is not null) &&
            !ReferenceEquals(
            objA: network.Clock,
            objB: timeProvider
        )
        ) {
            throw new ArgumentException(
                message: "a peer host runs on its network's clock; the supplied clock is a second one",
                paramName: nameof(timeProvider)
            );
        }

        m_timeProvider = (network?.Clock ?? (timeProvider ?? TimeProvider.System));
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: authenticator);

        m_server = server;
        m_server.PeerDisconnected += ClosePeer;
        m_authenticator = authenticator;
        m_issuer = issuer;
        m_network = (network ?? new WorldPeerNetwork(
            timeProvider: m_timeProvider,
            transportHandshakeTimeout: transportHandshakeTimeout
        ));
        m_ownsNetwork = (network is null);
    }

    /// <summary>Gets a read-back snapshot of every currently admitted connection, oldest first.</summary>
    public IReadOnlyList<WorldPeerConnectionInfo> Connections {
        get {
            lock (m_connectionsLock) {
                return [.. m_connections.Select(selector: static c => new WorldPeerConnectionInfo(
                        ConnectionId: c.Id,
                        PeerIndex: c.PeerIndex,
                        Generation: c.Generation,
                        RemoteEndpoint: c.RemoteEndpoint,
                        IdentityDomain: c.IdentityDomain,
                        IdentitySubject: c.IdentitySubject,
                        Tier: c.Tier
                    ))];
            }
        }
    }
    /// <summary>Gets every federation refusal this door has written, by name, omitting names never used.</summary>
    public IReadOnlyList<(WorldFederationRefusal Refusal, int Count)> FederationRefusals {
        get {
            var counts = new List<(WorldFederationRefusal, int)>();

            foreach (var refusal in Enum.GetValues<WorldFederationRefusal>()) {
                var count = Volatile.Read(location: ref m_federationRefusals[((int)refusal)]);

                if (count > 0) {
                    counts.Add(item: (refusal, count));
                }
            }

            return counts;
        }
    }
    /// <summary>Gets a value indicating whether the listener is currently bound.</summary>
    public bool IsListening => (ListenEndpoint is not null);
    /// <summary>Gets the bound endpoint, or <see langword="null"/> while not listening.</summary>
    public string? ListenEndpoint { get; private set; }

    private async Task AcceptLoopAsync(CancellationToken ct) {
        var incoming = m_network.Peer.IncomingLinks;

        while (!ct.IsCancellationRequested) {
            PeerStream client;

            try {
                client = m_network.OpenStream(link: await incoming.ReadAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false));
            } catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException or ChannelClosedException)) {
                break;
            }

            _ = Task.Run(function: () => HandleConnectionAsync(
                client: client,
                ct: ct
            ));
        }
    }
    private static void CloseConnection(IDisposable connection) {
        try { connection.Dispose(); } catch (Exception ex) when ((ex is SocketException or ObjectDisposedException or InvalidOperationException)) {
            // The lane is already gone; there is nothing left to end politely.
        }
    }
    private void ClosePeer(Principal principal) {
        PeerStream[] clients;
        lock (m_connectionsLock) { clients = m_connections.Where(row => row.Principal == principal).Select(row => row.Client).ToArray(); }
        foreach (var client in clients) { CloseConnection(client); }
    }
    private void CloseConnections() {
        m_credentialTimer?.Dispose();
        m_cts?.Cancel();
        foreach (var federation in m_federationConnections.Keys) { CloseConnection(connection: federation); }
        lock (m_connectionsLock) {
            foreach (var connection in m_connections) { CloseConnection(connection: connection.Client); }
        }
    }
    private async Task EvictInvalidCredentialsAsync() {
        if (Interlocked.Exchange(ref m_sweepingCredentials, 1) != 0) { return; }
        try {
            Connection[] peers;
            lock (m_connectionsLock) { peers = [.. m_connections]; }
            foreach (var (client, credential) in m_federationConnections) {
                if (!await credential.RefreshAsync(refreshTrust: true).ConfigureAwait(false)) { CloseConnection(client); }
            }
            foreach (var connection in peers) {
                if (!await connection.Credential.RefreshAsync(refreshTrust: true).ConfigureAwait(false)) { CloseConnection(connection.Client); }
            }
        } finally {
            Volatile.Write(ref m_sweepingCredentials, 0);
        }
    }
    // The tier a federated peer's own authority namespace is authored at, read at reply encoding through the
    // same admission arm that decides what an arriving traveler is minted. A namespace no admission row names
    // receives no document. A cryptographic identity alone authorizes no public federation disclosure.
    private WorldDisclosureTier DisclosureFor(string sourceAuthority, WorldAdmissionArrival kind) =>
        (((WorldAdmissionDoor.TryAdmitArrival(
            entries: m_server.AdmissionPolicy,
            sourceAuthority: sourceAuthority,
            verdict: out var verdict,
            kind: kind
        ) is null) && (verdict is not null))
            ? verdict.Tier
            : WorldDisclosureTier.Frames
        );
    private async Task FrameLoopAsync(Connection connection, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            byte[]? frame;

            try {
                frame = await HandshakeWireFormat.TryReadLengthPrefixedFrameAsync(
                    stream: connection.Stream,
                    maxTotalBytes: WorldPeerWireFormat.MaxUpstreamFrameBytes,
                    ct: ct
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception ex) when ((ex is IOException or SocketException)) {
                return;
            }

            if (frame is null) {
                return;
            }

            if (!await connection.Credential.RefreshAsync().ConfigureAwait(false)) { return; }

            if (!TryDecodeSubmissionFrame(
                failure: out var failure,
                frame: frame,
                operationId: out var operationId,
                payload: out var payload
            )) {
                await WorldPeerWireFormat.WriteRefusalAsync(
                    stream: connection.Stream,
                    reason: $"{failure}",
                    ct: ct
                ).ConfigureAwait(continueOnCapturedContext: false);

                continue;
            }

            // The door stamps the connection's own admitted identity onto every kind that carries an embedded
            // Principal (Command/Session/Mutation each read it directly rather than the envelope's copy — see
            // ApplyEnvelope's own remarks) — a handler reads the identity the door resolved, never the one the
            // client's bytes claimed.
            if (
                (payload is WorldSubmissionPayload.Mutation claimed) &&
                (claimed.Value.Principal != connection.Principal)
            ) {
                await WorldPeerWireFormat.WriteRefusalAsync(
                    stream: connection.Stream,
                    reason: "world.mutation.actor-mismatch: mutation actor does not match authenticated connection",
                    ct: ct
                ).ConfigureAwait(continueOnCapturedContext: false);
                continue;
            }
            var stamped = WorldLocalForwardedAuthority.StampPrincipal(
                payload: payload,
                principal: connection.Principal
            );
            var envelope = new SubmissionEnvelope(
                ConnectionId: connection.Id,
                SessionGeneration: connection.Generation,
                Sequence: connection.NextSequence(),
                CorrelationId: connection.NextCorrelation(),
                Principal: connection.Principal,
                Payload: stamped,
                OperationId: operationId
            );

            var completion = new TaskCompletionSource<WorldSubmissionResult>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            var submitted = await m_server.RunOnTickThreadAsync(
                work: () => {
                    if (!connection.Credential.IsValid()) { return false; }
                    m_server.Submit(
                        envelope: envelope,
                        completion: r => completion.TrySetResult(result: r)
                    );
                    return true;
                },
                ct: ct
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!submitted) { return; }

            // Mutation completion is intentionally awaited past ordered admission until the next tick drains the
            // pending operation. Cancelling this wait only abandons the response; it cannot withdraw accepted work.
            var result = await completion.Task.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

            await WorldPeerWireFormat.WriteResultAsync(
                stream: connection.Stream,
                result: result,
                ct: ct
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    private async Task HandleConnectionAsync(PeerStream client, CancellationToken ct) {
        // Ambient for the connection's whole lifetime — every downstream await runs under the same logical call
        // context, and Task.Run's caller (AcceptLoopAsync) never observes it (a fresh execution-context branch per
        // accepted socket).
        WorldNarrationScope.Current = m_server.AuthorityIdentity;

        try {
            await HandleConnectionCoreAsync(
                client: client,
                ct: ct
            ).ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            WorldNarrationScope.Current = null;
        }
    }
    private async Task HandleConnectionCoreAsync(PeerStream client, CancellationToken ct) {
        var remoteEndpoint = (client.Link.RemoteEndpoint.ToString() ?? "unknown");
        Stream stream = client;

        // The concurrent-handshake ceiling is checked before any byte crosses the wire for this connection, so a
        // flood of sockets that never speak still cannot itself grow past MaxConcurrentHandshakes. Refused cheaply
        // (no crypto, no tick-thread hop) and observably (stderr line, never a silent drop).
        if (Interlocked.Increment(location: ref m_pendingHandshakes) > MaxConcurrentHandshakes) {
            Interlocked.Decrement(location: ref m_pendingHandshakes);
            if (m_server.Output.HasNarrationSink) {
                m_server.Output.Narrate(
                    channel: "world.listen",
                    text: $"[world.listen: refused connection from {remoteEndpoint} — {MaxConcurrentHandshakes} concurrent unauthenticated handshakes already in flight]"
                );
            }

            try {
                await WorldPeerWireFormat.WriteHelloRefusedAsync(
                    ct: ct,
                    reason: $"handshake-ceiling: {MaxConcurrentHandshakes} concurrent unauthenticated handshakes already in flight",
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception ex) when ((ex is IOException or SocketException or ObjectDisposedException)) {
                // The caller could not even receive the refusal — nothing more to do.
            }

            client.Dispose();

            return;
        }

        // Held only for the pre-admission portion below — released the instant this connection is either admitted
        // (it becomes an ordinary authenticated session, no longer subject to the handshake ceiling or deadline) or
        // refused/times out/dies (the outer finally releases it).
        var handshakeSlotHeld = true;

        // The handshake deadline on the host clock, linked to the accept loop's own cancellation so shutdown still wins.
        using var handshakeDeadline = new OperationDeadline(
            caller: ct,
            timeout: HandshakeDeadline,
            timeProvider: m_timeProvider
        );

        var handshakeCt = handshakeDeadline.Token;

        try {
            // Door 1 of 2 — protocol-version compatibility, checked first and refused with its own spelling
            // ("version-mismatch: …"), never sharing a reason with the identity door below.
            var helloBuffer = new byte[HandshakeWireFormat.HelloBytes];

            if (!await HandshakeWireFormat.TryReadExactAsync(
                buffer: helloBuffer,
                ct: handshakeCt,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false)) {
                return;
            }

            var offeredKey = HandshakeWireFormat.ReadHello(hello: helloBuffer);

            if (offeredKey == WorldFederationCodec.WireKey) {
                if (!m_authenticator.IsConfigured) {
                    await WriteFederationRefusal(
                        ct: handshakeCt,
                        detail: "this authority carries no federation credentials",
                        refusal: WorldFederationRefusal.AuthenticationUnconfigured,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }

                var federationChallenge = m_authenticator.NewChallenge();

                await WorldFederationCodec.WriteResponseAsync(
                    body: federationChallenge,
                    ct: handshakeCt,
                    kind: WorldFederationResponse.Challenge,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);
                var authentication = await WorldFederationCodec.ReadRequestAsync(
                    ct: handshakeCt,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                // An issuance request takes the authentication's place: the client has no vouched identity yet and
                // asks for one, so the exchange ends with the issuer's answer and authenticates nothing.
                if (authentication.Ok && (authentication.Kind == ((byte)WorldFederationRequest.Issue))) {
                    await ServeIssuanceAsync(
                        body: authentication.Body,
                        challenge: federationChallenge,
                        clientKeyHash: client.Link.RemoteId.KeyHash,
                        ct: handshakeCt,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }

                // The authenticated identity below is DERIVED from the verified proof, never taken from anything
                // the peer's own bytes claimed — WorldFederationCodec's authentication leaf carries no source-
                // authority field at all for exactly this reason (see WorldAttestedAuthenticator's own remarks).
                if (
                    !authentication.Ok ||
                    (authentication.Kind != ((byte)WorldFederationRequest.Authenticate)) ||
                    !WorldFederationCodec.TryDecodeAuthentication(
                    body: authentication.Body.Span,
                    challenge: out var clientChallenge,
                    failure: out _,
                    proof: out var federationProof
                ) ||
                    (clientChallenge.Length != m_authenticator.ChallengeBytes)
                ) {
                    await WriteFederationRefusal(ct: handshakeCt, detail: "malformed proof", refusal: WorldFederationRefusal.AuthenticationFailed, stream: stream).ConfigureAwait(false);
                    return;
                }
                var boundChallenge = WorldFederationCodec.BindProofChallenge(
                    challenge: federationChallenge, counterChallenge: clientChallenge,
                    clientKeyHash: client.Link.RemoteId.KeyHash, serverKeyHash: m_network.Peer.Id.KeyHash,
                    serverProof: false);
                var verifiedPolicy = m_server.AdmissionPolicy;
                var identityProof = await m_authenticator.VerifyAsync(boundChallenge, federationProof, handshakeCt).ConfigureAwait(false);
                if (identityProof is null || !ReferenceEquals(verifiedPolicy, m_server.AdmissionPolicy)) {
                    await WriteFederationRefusal(ct: handshakeCt, detail: "the presented proof did not verify",
                        refusal: WorldFederationRefusal.AuthenticationFailed, stream: stream).ConfigureAwait(false);
                    return;
                }
                var federationAuthority = identityProof.Authority;
                var currentProof = identityProof;
                var hadArrivalAdmission = WorldAdmissionDoor.AdmitsAnyArrival(verifiedPolicy, federationAuthority);
                async ValueTask<VerifiedPeerIdentity?> ReverifyAsync(IReadOnlyList<WorldAdmissionEntry>? policy, CancellationToken token) {
                    var admittedNow = WorldAdmissionDoor.AdmitsAnyArrival(policy, federationAuthority);
                    if (hadArrivalAdmission && !admittedNow) { return null; }
                    hadArrivalAdmission |= admittedNow;
                    var latest = Volatile.Read(ref currentProof);
                    if (latest.TrustIsCurrent?.Invoke() != false) { return latest; }
                    var next = await m_authenticator.VerifyAsync(boundChallenge, federationProof, token).ConfigureAwait(false);
                    if (next?.Authority != federationAuthority) { return null; }
                    Volatile.Write(ref currentProof, next);
                    return next;
                }
                using var credential = new Credential(this, verifiedPolicy, identityProof, ReverifyAsync, ct);

                if (!m_federationSlots.Wait(millisecondsTimeout: 0)) {
                    await WriteFederationRefusal(
                        ct: handshakeCt,
                        detail: $"authenticated federation connection limit {MaximumFederationConnections} reached",
                        refusal: WorldFederationRefusal.LaneUnavailable,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }

                try {
                    byte[] serverProof;

                    try {
                        serverProof = m_authenticator.Prove(challenge: WorldFederationCodec.BindProofChallenge(
                            challenge: clientChallenge,
                            counterChallenge: federationChallenge,
                            clientKeyHash: client.Link.RemoteId.KeyHash,
                            serverKeyHash: m_network.Peer.Id.KeyHash,
                            serverProof: true,
                            authority: m_server.AuthorityIdentity));
                    } catch (InvalidOperationException) {
                        // A bearer-token client can already pin this transport key. A client without that pin must
                        // reject this empty proof; a verify-only host never silently authenticates itself to one.
                        serverProof = [];
                    }
                    await WorldFederationCodec.WriteResponseAsync(
                        body: WorldFederationCodec.EncodeAuthorityIdentity(authority: m_server.AuthorityIdentity, proof: serverProof),
                        ct: handshakeCt,
                        kind: WorldFederationResponse.Authenticated,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    handshakeSlotHeld = false;
                    Interlocked.Decrement(location: ref m_pendingHandshakes);
                    _ = m_federationConnections.TryAdd(
                        key: client,
                        value: credential
                    );

                    try {
                        await ServeFederationAsync(
                            stream: stream,
                            sourceAuthority: federationAuthority,
                            credential: credential,
                            ct: credential.Token
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    } catch (Exception exception) when (exception is not (IOException or SocketException or OperationCanceledException or ObjectDisposedException)) {
                        // The connection's task is not awaited, so a fault that ended it would close the peer's lanes
                        // and streams with nothing said on either side.
                        if (m_server.Output.HasNarrationSink) {
                            m_server.Output.Narrate(
                                channel: "world.federation",
                                text: $"[world.federation: the connection from '{federationAuthority}' ended on a fault ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}) in {string.Join(separator: " < ", values: new System.Diagnostics.StackTrace(e: exception).GetFrames().Take(count: 5).Select(selector: static frame => $"{frame.GetMethod()?.DeclaringType?.Name}.{frame.GetMethod()?.Name}"))}]"
                            );
                        }
                        throw;
                    } finally {
                        _ = m_federationConnections.TryRemove(
                            key: client,
                            value: out _
                        );
                        SweepDisconnectedAuthorityTravelers(sourceAuthority: federationAuthority);
                    }
                } finally {
                    _ = m_federationSlots.Release();
                }

                return;
            }

            if (!WorldHelloDoor.TryAccept(
                offeredKey: offeredKey,
                refusal: out var helloRefusal
            )) {
                await WorldPeerWireFormat.WriteHelloRefusedAsync(
                    ct: handshakeCt,
                    reason: $"version-mismatch: {helloRefusal}: wire key 0x{offeredKey:x16} != server 0x{WorldProtocol.WireProtocolKey:x16}",
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            // Door 2 of 2 — the identity door: challenge, read back the peer's claim (and chain, for a Vouches
            // identity), verify off the tick thread against a snapshot of the current document's admission entries.
            // Every refusal path here is spelled "identity-refused: …", distinct from door 1's "version-mismatch: ".
            // A disconnect before any length prefix declared a frame is not a refusal — the socket closes with no
            // reply; once a prefix has declared a frame, an incomplete body is a named refusal instead.
            var challenge = WorldAdmissionDoor.NewChallenge();

            await WorldPeerWireFormat.WriteHelloChallengeAsync(
                challenge: challenge,
                ct: handshakeCt,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            var identityRead = await HandshakeWireFormat.TryReadHelloIdentityAsync(
                ct: handshakeCt,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (identityRead is HandshakeWireFormat.HelloIdentityReadResult.Eof) {
                return;
            }

            if (identityRead is not HandshakeWireFormat.HelloIdentityReadResult.Ok identity) {
                var malformed = ((HandshakeWireFormat.HelloIdentityReadResult.Malformed)identityRead);

                await WorldPeerWireFormat.WriteHelloRefusedAsync(
                    stream: stream,
                    reason: $"identity-refused: {malformed.Reason}",
                    ct: handshakeCt
                ).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            WorldAdmissionDoor.AdmissionOutcome outcome;
            // Captured alongside the exact entries TryAdmit consults below, so the tick-thread commit can prove the
            // policy has not moved since (WorldDefinition's sections are immutable records: an unrelated
            // mutation/rebuild that never touches Admission leaves this reference unchanged; one that does mints a
            // new list — see WorldServer.TryAdmitPeerConnection's own remarks on this parameter).
            var admissionEntriesAtVerify = m_server.AdmissionPolicy;
            var clientKeyHash = client.Link.RemoteId.KeyHash;
            var serverKeyHash = m_network.Peer.Id.KeyHash;

            Puck.Attestation.SignedAttestation claimAttestation;
            Puck.Attestation.SignedAttestation[] chainAttestations;

            try {
                claimAttestation = WorldAdmissionDoor.DecodeAttestation(wire: identity.Claim);
                chainAttestations = identity.Chain.Select(selector: bytes => WorldAdmissionDoor.DecodeAttestation(wire: bytes)).ToArray();
                // Definition is read fresh, off the tick thread — an admission decision consulting a document that
                // moves mid-handshake (a concurrent world.reset/load/reload) is eventual-consistency the same way
                // any other cross-thread document read here would be. This is a presentation/networking admission
                // decision, never simulation state, so it is not a determinism concern; the commit below still
                // proves the policy this verdict rests on is the one the tick thread ultimately sees.
                outcome = WorldAdmissionDoor.TryAdmit(
                    entries: admissionEntriesAtVerify,
                    challenge: challenge,
                    clientKeyHash: clientKeyHash,
                    serverKeyHash: serverKeyHash,
                    claim: claimAttestation,
                    chain: chainAttestations,
                    now: m_timeProvider.GetUtcNow()
                );
            } catch (FormatException exception) {
                await WorldPeerWireFormat.WriteHelloRefusedAsync(
                    stream: stream,
                    reason: $"identity-refused: the presented claim or chain bytes do not decode — {exception.Message}",
                    ct: handshakeCt
                ).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            if (!outcome.Admitted) {
                await WorldPeerWireFormat.WriteHelloRefusedAsync(
                    ct: handshakeCt,
                    reason: $"identity-refused: {outcome.Refusal}: {outcome.Detail}",
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            Connection? activeConnection = null;
            var initialTrustIdentity = outcome.Verdict!.TrustIdentity;
            using var peerCredential = new Credential(this, admissionEntriesAtVerify, new(outcome.Domain!, outcome.ValidThrough), (policy, _) => {
                var next = WorldAdmissionDoor.TryAdmit(policy, challenge, clientKeyHash, serverKeyHash, claimAttestation, chainAttestations, m_timeProvider.GetUtcNow());
                if (!next.Admitted || (next.Verdict!.TrustIdentity != initialTrustIdentity)) {
                    return new ValueTask<VerifiedPeerIdentity?>(result: null);
                }
                if (Volatile.Read(location: ref activeConnection) is { } live) {
                    live.Tier = next.Tier;
                }
                return new ValueTask<VerifiedPeerIdentity?>(result: new VerifiedPeerIdentity(next.Domain!, next.ValidThrough));
            }, ct);

            // Population/grant admission runs through the ordered-domain path, fed the verified identity's own
            // authored grant templates. The compare-at-commit check rides along here: TryAdmitPeerConnection
            // refuses by name if the admission policy moved since admissionEntriesAtVerify was captured above. The
            // handshake deadline covers this queue hop too — an authored rate-0/paused world may not drain it at
            // all — and RunOnTickThreadAsync's cancellation guard both releases the waiting socket at the deadline
            // and prevents the orphaned item from admitting later.
            var (admitted, admissionRefusal) = await m_server.RunOnTickThreadAsync(
                work: () => (!peerCredential.IsValid()
                ? (default(WorldPeerEventEntry), "the verified credential expired or its admission entry was removed before admission")
                : (m_server.TryAdmitPeerConnection(
                    verdict: outcome.Verdict,
                    expectedAdmissionEntries: admissionEntriesAtVerify,
                    admitted: out var entry,
                    refusal: out var reason
                )
                ? (entry, ((string?)null))
                : (default(WorldPeerEventEntry), reason))),
                ct: handshakeCt
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (admissionRefusal is { } refusalReason) {
                await WorldPeerWireFormat.WriteHelloRefusedAsync(
                    ct: handshakeCt,
                    reason: refusalReason,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            var connectionId = Interlocked.Increment(location: ref m_nextConnectionId);
            var connection = new Connection(
                id: connectionId,
                peerIndex: admitted.BodyIndex,
                generation: admitted.Generation,
                client: client,
                stream: stream,
                remoteEndpoint: remoteEndpoint,
                identityDomain: (outcome.Domain ?? string.Empty),
                identitySubject: (outcome.Subject ?? string.Empty),
                tier: outcome.Tier,
                credential: peerCredential
            );
            Volatile.Write(location: ref activeConnection, value: connection);

            lock (m_connectionsLock) {
                m_connections.Add(item: connection);
            }

            // The handshake is over: this connection is admitted, so it leaves both the concurrent-handshake
            // ceiling and the handshake deadline behind — the frame loop below runs on the accept loop's own ct,
            // unbounded by HandshakeDeadline (an ongoing authenticated session is not a handshake).
            handshakeSlotHeld = false;
            Interlocked.Decrement(location: ref m_pendingHandshakes);

            try {
                if (m_server.Output.HasNarrationSink) {
                    m_server.Output.Narrate(
                        channel: "world.listen",
                        text: $"[world.listen: admitted connection {connectionId} as {connection.Principal.Describe()} identity domain:{connection.IdentityDomain} subject:{connection.IdentitySubject} grants:{(outcome.Grants?.Count ?? 0)} from {remoteEndpoint}]"
                    );
                }
                await WorldPeerWireFormat.WriteHelloAcceptedAsync(
                    stream: stream,
                    peerIndex: connection.PeerIndex,
                    generation: connection.Generation,
                    connectionId: connectionId,
                    ct: ct
                ).ConfigureAwait(continueOnCapturedContext: false);

                await FrameLoopAsync(
                    connection: connection,
                    ct: peerCredential.Token
                ).ConfigureAwait(continueOnCapturedContext: false);
            } finally {
                lock (m_connectionsLock) {
                    m_connections.Remove(item: connection);
                }

                await m_server.RunOnTickThreadAsync(work: () => {
                    m_server.DisconnectPeerConnection(peer: admitted);

                    return true;
                }).ConfigureAwait(continueOnCapturedContext: false);
                if (m_server.Output.HasNarrationSink) {
                    m_server.Output.Narrate(
                        channel: "world.listen",
                        text: $"[world.listen: disconnected connection {connectionId} ({connection.Principal.Describe()})]"
                    );
                }
            }
        } catch (OperationCanceledException) {
            // The accept loop was not cancelled — HandshakeDeadline fired on a connection that never finished the
            // pre-admission handshake in time (slowloris, or a genuinely dead peer). Observable by name, never a
            // silent drop.
            if (!ct.IsCancellationRequested && handshakeSlotHeld && m_server.Output.HasNarrationSink) {
                m_server.Output.Narrate(
                    channel: "world.listen",
                    text: $"[world.listen: handshake from {remoteEndpoint} exceeded the {HandshakeDeadline.TotalSeconds:0}s deadline — connection refused]"
                );
            }
        } catch (Exception ex) when ((ex is IOException or SocketException or ObjectDisposedException)) {
            // Pre-admission socket death (during the hello exchange, before m_connections.Add above) admitted
            // nothing, so there is nothing to revoke here. A post-admission death is already revoked by the
            // connection's own teardown finally above, which runs on every exit from that block — including
            // this exception unwinding through it.
        } finally {
            // A completed QUIC write only hands bytes to the transport. Immediate connection disposal can
            // discard the final refusal before the peer reads it. Keep the handshake slot while draining, using
            // the networking library's same bounded refusal window on the host's clock; process shutdown still
            // cancels immediately.
            using (var drain = new OperationDeadline(
                caller: ct,
                timeout: PeerWireProtocol.RefusalDrainTimeout,
                timeProvider: m_timeProvider
            )) {
                await StreamDrain.UntilClosedAsync(
                    client,
                    drain.Token
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
            if (handshakeSlotHeld) {
                Interlocked.Decrement(location: ref m_pendingHandshakes);
            }

            client.Dispose();
        }
    }
    // sourceAuthority is the namespace THIS CONNECTION authenticated as, which is what the credential table is keyed
    // by — never the traveller's origin authority carried inside its incarnation.
    // Resolves one forwarded submission to the typed result its authority answers with: at once for most payloads, and
    // for a buffered mutation at that authority's tick boundary, so the answer written back is the verdict the
    // destination applied, never a refusal standing in for a result that has not happened yet.
    private async Task<(WorldSubmissionResult? Result, string Reason)> ResolveForwardedSubmissionAsync(string sourceAuthority, WorldMobilityIdentity mobility, WorldSubmissionPayload payload, Guid operationId, Credential credential, CancellationToken ct) {
        var answered = new TaskCompletionSource<WorldSubmissionResult>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        void Answer(WorldSubmissionResult result) => answered.TrySetResult(result: result);

        // A forwarded submission can cross into another authority. Recheck at ingress, then leave each
        // authority's own gate to the existing forwarding path; never carry the source gate across a hop.
        credential.RequireValid();
        if (await ExecuteAuthenticatedAsync(credential, () => WorldLocalForwardedAuthority.TryApplySubmission(
            completion: Answer,
            mobility: in mobility,
            operationId: operationId,
            payload: payload,
            reason: out _,
            server: m_server,
            sourceAuthority: sourceAuthority
        )).ConfigureAwait(false)) {
            return (await answered.Task.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false), string.Empty);
        }

        if (m_server.TransferForwarder is not { } forwarder) {
            return (null, string.Empty);
        }

        // A routed query/command can arrive before the commit path publishes its onward route just like a held-stick
        // update can; retain the one typed request at this authority until it does.
        await AwaitOnwardRouteAsync(
            credential: credential,
            ct: ct,
            forwarder: forwarder,
            mobility: mobility
        ).ConfigureAwait(continueOnCapturedContext: false);
        credential.RequireValid();

        var forwarded = await ExecuteAuthenticatedAsync(credential, () => {
            var accepted = forwarder.TryForwardSubmission(completion: Answer, mobility: in mobility, operationId: operationId,
                payload: payload, reason: out var reason, source: m_server);
            return (accepted, reason);
        }).ConfigureAwait(false);
        return (forwarded.accepted ? await answered.Task.WaitAsync(ct).ConfigureAwait(false) : null, forwarded.reason);
    }
    // Detach necessarily precedes publication of the committed onward route, so a request landing inside that window
    // finds none yet. Waits, on the host clock, until the forwarder publishes one or the attempts run out; a refusal
    // after that is the forwarder's own and is never retried.
    private async Task AwaitOnwardRouteAsync(Credential credential, CancellationToken ct, IWorldTransferForwarder forwarder, WorldMobilityIdentity mobility) {
        for (var attempt = 0; (attempt < RouteLookupAttempts); attempt++) {
            credential.RequireValid();
            if (forwarder.HasOnwardRoute(
                mobility: in mobility,
                source: m_server
            )) {
                return;
            }

            await Task.Delay(
                cancellationToken: ct,
                delay: RouteLookupRetryDelay,
                timeProvider: m_timeProvider
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    private Task<T> ExecuteAuthenticatedAsync<T>(Credential credential, Func<T> operation) =>
        m_server.RunOnTickThreadAsync(() => {
            credential.RequireValid();
            return operation();
        }, credential.Token);

    private Task<Principal?> TransferredPrincipalAsync(Credential credential, WorldMobilityIdentity mobility, string sourceAuthority) =>
        ExecuteAuthenticatedAsync<Principal?>(credential, () =>
            ((WorldAdmissionDoor.TryAdmitArrival(m_server.AdmissionPolicy, sourceAuthority, out _, WorldAdmissionArrival.Visitor) is null) &&
            m_server.TryTransferredPrincipal(sourceAuthority, mobility, out var principal)) ? principal : null);

    // A destination asks this authority, as a companion's title authority, whether a hop is currently granted to it.
    // The destination is the namespace this connection authenticated as, never one the body names.
    private async Task<bool> ServeHopQueryAsync(Stream stream, string sourceAuthority, Credential credential, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeHopQuery(
            body: body.Span,
            failure: out var failure,
            incarnations: out var incarnations,
            key: out var key,
            phase: out var phase
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"hop query — {failure}",
                refusal: WorldFederationRefusal.FrameMalformed,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        var answers = await ExecuteAuthenticatedAsync(credential: credential, operation: () => m_server.ConfirmHops(
            destination: sourceAuthority,
            incarnations: incarnations,
            key: key,
            phase: phase
        ))!;

        await WorldFederationCodec.WriteResponseAsync(
            body: WorldFederationCodec.EncodeHopQueryReply(answers: answers),
            ct: ct,
            kind: WorldFederationResponse.HopQueryReply,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
    // A companion's title authority asks this destination what it holds under a hop's key, or a custodian tells this
    // title authority it settled a hop. The asker is the namespace this connection authenticated as; a settlement
    // notice must name a key that namespace minted.
    private async Task<bool> ServeCompanionHopAsync(Stream stream, string sourceAuthority, Credential credential, WorldFederationRequest kind, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeCompanionHop(
            body: body.Span,
            failure: out var failure,
            incarnation: out var incarnation,
            key: out var key
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"{kind} — {failure}",
                refusal: WorldFederationRefusal.FrameMalformed,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }
        if (kind == WorldFederationRequest.CustodyQuery) {
            var answer = await ExecuteAuthenticatedAsync(credential: credential, operation: () => m_server.QueryCustody(
                asker: sourceAuthority,
                incarnation: incarnation,
                key: key
            ));

            await WorldFederationCodec.WriteResponseAsync(
                body: new[] { ((byte)answer) },
                ct: ct,
                kind: WorldFederationResponse.CustodyReply,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }
        if (!string.Equals(a: key.SourceAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
            await WriteFederationRefusal(
                ct: ct,
                detail: "the settlement notice names a transfer namespace this connection did not authenticate as",
                refusal: WorldFederationRefusal.SourceAuthorityMismatch,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        var acked = await ExecuteAuthenticatedAsync(credential, () => m_server.SettleHop(key, incarnation, sourceAuthority)).ConfigureAwait(false);

        await WorldFederationCodec.WriteResponseAsync(
            body: new[] { (acked ? (byte)1 : (byte)0) },
            ct: ct,
            kind: WorldFederationResponse.HopSettledAck,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
    // A custodian asks this authority, as a companion's title authority, to authorize its onward hop. The custodian is
    // the namespace this connection authenticated as, and the crossing's key must be one that namespace minted.
    private async Task<bool> ServeHopRequestAsync(Stream stream, string sourceAuthority, Credential credential, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeHopRequest(
            body: body.Span,
            destination: out var destination,
            failure: out var failure,
            key: out var key,
            members: out var members
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"hop request — {failure}",
                refusal: WorldFederationRefusal.FrameMalformed,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }
        if (!string.Equals(a: key.SourceAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
            await WriteFederationRefusal(
                ct: ct,
                detail: "the hop request names a transfer namespace this connection did not authenticate as",
                refusal: WorldFederationRefusal.SourceAuthorityMismatch,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        var answers = await ExecuteAuthenticatedAsync(credential: credential, operation: () => m_server.RequestHop(
            destination: destination,
            key: key,
            members: members,
            source: sourceAuthority
        ));

        await WorldFederationCodec.WriteResponseAsync(
            body: WorldFederationCodec.EncodeHopAnswer(answers: answers),
            ct: ct,
            kind: WorldFederationResponse.HopAnswer,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
    private async Task<bool> ServeCommitAsync(Stream stream, string sourceAuthority, Credential credential, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeCommit(
            body: body.Span,
            sourceAuthority: out var carriedAuthority,
            transferId: out var transferId,
            members: out var members,
            failure: out var failure
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"commit — {failure}",
                refusal: WorldFederationRefusal.FrameMalformed,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(
            a: carriedAuthority,
            b: sourceAuthority,
            comparisonType: StringComparison.Ordinal
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: "the commit names a source authority this connection did not authenticate as",
                refusal: WorldFederationRefusal.SourceAuthorityMismatch,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        var commit = await ExecuteAuthenticatedAsync(credential, () => m_server.CommitTransferAsync(sourceAuthority,
            transferId, members, credential.RequireValid, ct)).ConfigureAwait(false);
        var (status, commitReason) = await commit.ConfigureAwait(false);

        await WorldFederationCodec.WriteResponseAsync(
            stream: stream,
            kind: WorldFederationResponse.Commit,
            body: WorldFederationCodec.EncodeCommitReply(
                reason: commitReason,
                status: status
            ),
            ct: ct
        ).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
    // A federation connection is a persistent authenticated lane. Requests ride it strictly request-then-response
    // and in order, which is what lets the peer omit a correlation id. Observe and IntentStream take the connection
    // over and stream on it until it closes.
    private async Task ServeFederationAsync(Stream stream, string sourceAuthority, Credential credential, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            var frame = await WorldFederationCodec.ReadRequestAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!await credential.RefreshAsync().ConfigureAwait(false)) { return; }

            if (!frame.Ok) {
                if (frame.Failure.Refusal != WireRefusal.ConnectionClosed) {
                    await WriteFederationRefusal(
                        stream: stream,
                        refusal: WorldFederationRefusal.FrameMalformed,
                        detail: frame.Failure.ToString(),
                        ct: ct
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }

                return;
            }

            if (!await ServeFederationRequestAsync(
                stream: stream,
                sourceAuthority: sourceAuthority,
                credential: credential,
                kind: ((WorldFederationRequest)frame.Kind),
                body: frame.Body,
                ct: ct
            ).ConfigureAwait(continueOnCapturedContext: false)) {
                return;
            }
        }
    }
    // Returns whether the lane stays open for another request.
    private async Task<bool> ServeFederationRequestAsync(Stream stream, string sourceAuthority, Credential credential, WorldFederationRequest kind, ReadOnlyMemory<byte> body, CancellationToken ct) {
        switch (kind) {
            case WorldFederationRequest.Reserve:
                return await ServeReserveAsync(
                    body: body,
                    credential: credential,
                    ct: ct,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Commit:
                return await ServeCommitAsync(
                    body: body,
                    credential: credential,
                    ct: ct,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.HopQuery:
                return await ServeHopQueryAsync(
                    body: body,
                    credential: credential,
                    ct: ct,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.CustodyQuery:
            case WorldFederationRequest.HopSettled:
                return await ServeCompanionHopAsync(
                    body: body,
                    credential: credential,
                    ct: ct,
                    kind: kind,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.HopRequest:
                return await ServeHopRequestAsync(
                    body: body,
                    credential: credential,
                    ct: ct,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Abort:
            case WorldFederationRequest.AcknowledgeTransfer:
            case WorldFederationRequest.Status:
                return await ServeTransferKeyAsync(
                    body: body,
                    credential: credential,
                    ct: ct,
                    kind: kind,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Submission:
                return await ServeSubmissionAsync(
                    body: body,
                    credential: credential,
                    ct: ct,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Route:
                return await ServeRouteAsync(
                    credential: credential,
                    body: body,
                    ct: ct,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

            case WorldFederationRequest.Prototype:
                if (!WorldFederationCodec.TryDecodePrototypeRequest(body: body.Span, pin: out var pin, traveler: out var traveler)) {
                    await WriteFederationRefusal(ct: ct, detail: "invalid prototype request", refusal: WorldFederationRefusal.FrameMalformed, stream: stream).ConfigureAwait(continueOnCapturedContext: false);
                    return true;
                }
                var prototype = await WorldProjectionPrototypeFetch.FetchAsync(
                    server: m_server,
                    pin: pin,
                    sourceAuthority: sourceAuthority,
                    ceiling: DisclosureFor(sourceAuthority: sourceAuthority, kind: ((traveler is null) ? WorldAdmissionArrival.Session : WorldAdmissionArrival.Visitor)),
                    traveler: traveler,
                    requireValid: credential.RequireValid
                ).ConfigureAwait(false);
                if (prototype is null) {
                    await WriteFederationRefusal(ct: ct, detail: "the current projection does not disclose this prototype", refusal: WorldFederationRefusal.PrototypeUndisclosed, stream: stream).ConfigureAwait(continueOnCapturedContext: false);
                } else {
                    await WorldFederationCodec.WriteResponseAsync(body: prototype, ct: ct, kind: WorldFederationResponse.Prototype, stream: stream).ConfigureAwait(continueOnCapturedContext: false);
                }
                return true;

            case WorldFederationRequest.ObserveTraveler:
                if (!WorldFederationCodec.TryDecodeTravelerObservation(
                    body.Span,
                    out var observation,
                    out var failure
                )) {
                    await WriteFederationRefusal(
                        stream,
                        WorldFederationRefusal.FrameMalformed,
                        failure.ToString(),
                        ct
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return false;
                }
                if (!string.Equals(
                    a: sourceAuthority,
                    b: observation.SourceAuthority,
                    comparisonType: StringComparison.Ordinal
                )) {
                    await WriteFederationRefusal(
                        ct: ct,
                        detail: "traveler observation source differs from its authenticated namespace",
                        refusal: WorldFederationRefusal.SourceAuthorityMismatch,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return false;
                }
                observation = observation with {
                    Ceiling = ((WorldDisclosureTier)Math.Min(
                    val1: ((byte)DisclosureFor(sourceAuthority: sourceAuthority, kind: WorldAdmissionArrival.Visitor)),
                    val2: ((byte)observation.Ceiling)
                )),
                };
                try {
                    await BeginTravelerStreamAsync(sourceAuthority: sourceAuthority, mobility: observation.Mobility, ct: ct).ConfigureAwait(false);
                    var refusal = await WorldTravelerProjection.StreamAsync(
                        m_server,
                        observation,
                        (m_server.DeclaredEndpoint ?? ListenEndpoint ?? m_server.AuthorityIdentity),
                        stream,
                        ct,
                        credential.IsValid
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    if (refusal is not null) {
                        await WriteFederationRefusal(
                            ct: ct,
                            detail: refusal,
                            refusal: WorldFederationRefusal.RouteUnknown,
                            stream: stream
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    }
                } finally {
                    EndTravelerStream(sourceAuthority: sourceAuthority, mobility: observation.Mobility, handoff: false);
                }
                return false;

            case WorldFederationRequest.Observe:
                await StreamProjectionAsync(
                    credential: credential,
                    ct: ct,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                return false;

            case WorldFederationRequest.IntentStream:
                if (body.Length != 0) {
                    await WriteFederationRefusal(
                        ct: ct,
                        detail: "the intent stream opening carries a payload",
                        refusal: WorldFederationRefusal.PayloadUnexpected,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    return false;
                }

                await StreamFederatedIntentsAsync(
                    credential: credential,
                    ct: ct,
                    sourceAuthority: sourceAuthority,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                return false;

            default:
                await WriteFederationRefusal(
                    ct: ct,
                    detail: $"{kind} is not accepted on an open federation lane",
                    refusal: WorldFederationRefusal.RequestUnsupported,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                return false;
        }
    }
    // The one document-reply door: current credentials, current policy and encoding share the authority gate.
    private Task<byte[]> EncodeFederationReplyAsync(string sourceAuthority, Credential credential, Func<WorldDisclosureTier, byte[]> encode, WorldAdmissionArrival kind = WorldAdmissionArrival.Visitor) =>
        ExecuteAuthenticatedAsync(credential: credential, operation: () => encode(DisclosureFor(sourceAuthority: sourceAuthority, kind: kind)));

    private async Task<bool> ServeReserveAsync(Stream stream, string sourceAuthority, Credential credential, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (
            !WorldFederationCodec.TryDecodeReservation(
            body: body.Span,
            request: out var request,
            failure: out var failure
        ) ||
            (request is null)
        ) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"reservation — {failure}",
                refusal: WorldFederationRefusal.FrameMalformed,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(
            a: request.SourceAuthority,
            b: sourceAuthority,
            comparisonType: StringComparison.Ordinal
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: "the reservation names a source authority this connection did not authenticate as",
                refusal: WorldFederationRefusal.SourceAuthorityMismatch,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        var reserve = await ExecuteAuthenticatedAsync(credential, () => m_server.ReserveTransferAsync(request, credential.RequireValid, ct)).ConfigureAwait(false);
        var reply = await reserve.ConfigureAwait(false);

        await WorldFederationCodec.WriteResponseAsync(
            stream: stream,
            kind: WorldFederationResponse.Reservation,
            body: await EncodeFederationReplyAsync(sourceAuthority: sourceAuthority, credential: credential, encode: _ => WorldFederationCodec.EncodeReservationReply(reply: reply)),
            ct: ct
        ).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
    private async Task<bool> ServeRouteAsync(Stream stream, string sourceAuthority, Credential credential, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeRouteCredential(
            body: body.Span,
            failure: out var failure,
            mobility: out var mobility,
            sourceAuthority: out var carriedAuthority
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"route credential — {failure}",
                refusal: WorldFederationRefusal.FrameMalformed,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(
            a: carriedAuthority,
            b: sourceAuthority,
            comparisonType: StringComparison.Ordinal
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: "the route credential names a source authority this connection did not authenticate as",
                refusal: WorldFederationRefusal.SourceAuthorityMismatch,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (await TransferredPrincipalAsync(credential, mobility, sourceAuthority).ConfigureAwait(false) is not { } principal) {
            await WriteFederationRefusal(
                ct: ct,
                detail: "the route credential names no committed transfer body",
                refusal: WorldFederationRefusal.CredentialUnknown,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        var declaredEndpoint = (m_server.DeclaredEndpoint ?? ListenEndpoint);
        // Read the body and its address inside the same gated operation that decided it is live.
        var local = ((declaredEndpoint is { } localEndpoint)
            ? await ExecuteAuthenticatedAsync(credential, () => (WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(
                principal: principal,
                server: m_server
            )
                ? WorldLocalForwardedAuthority.DescribeRoute(
                    endpoint: localEndpoint,
                    principal: principal,
                    server: m_server
                )
                : (WorldAuthorityRouteDescription?)null))
            : null
        );

        var routeReason = string.Empty;
        if (local is null && m_server.TransferForwarder is { } forwarder) {
            var pending = await ExecuteAuthenticatedAsync(credential, () => forwarder.DescribeForwardingAsync(m_server, mobility)).ConfigureAwait(false);
            (local, routeReason) = await pending.ConfigureAwait(false);
        }
        if (local is not { } route) {
            await WriteFederationRefusal(
                stream: stream,
                refusal: WorldFederationRefusal.RouteUnknown,
                detail: ((routeReason.Length > 0)
                ? routeReason
                : "the traveler has no live or forwarded route"),
                ct: ct
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        await WorldFederationCodec.WriteResponseAsync(
            stream: stream,
            kind: WorldFederationResponse.Route,
            body: await EncodeFederationReplyAsync(sourceAuthority: sourceAuthority, credential: credential, encode: tier => WorldFederationCodec.EncodeRoute(route: in route, tier: tier)),
            ct: ct
        ).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
    private async Task<bool> ServeSubmissionAsync(Stream stream, string sourceAuthority, Credential credential, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeSubmission(
            body: body.Span,
            failure: out var failure,
            frame: out var submittedFrame,
            mobility: out var mobility,
            sourceAuthority: out var carriedAuthority
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"submission credential — {failure}",
                refusal: WorldFederationRefusal.FrameMalformed,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(
            a: carriedAuthority,
            b: sourceAuthority,
            comparisonType: StringComparison.Ordinal
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: "the submission names a source authority this connection did not authenticate as",
                refusal: WorldFederationRefusal.SourceAuthorityMismatch,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!TryDecodeSubmissionFrame(
            failure: out var codecFailure,
            frame: submittedFrame,
            operationId: out var operationId,
            payload: out var payload
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"submission frame — {codecFailure}",
                refusal: WorldFederationRefusal.FrameMalformed,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (await TransferredPrincipalAsync(credential, mobility, sourceAuthority).ConfigureAwait(false) is not { } principal) {
            await WriteFederationRefusal(
                ct: ct,
                detail: "the submission credential names no committed transfer body",
                refusal: WorldFederationRefusal.CredentialUnknown,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (
            (payload is WorldSubmissionPayload.Mutation claimed) &&
            (claimed.Value.Principal != principal)
        ) {
            await WriteFederationRefusal(
                ct: ct,
                detail: "world.mutation.actor-mismatch: mutation actor does not match authenticated credential",
                refusal: WorldFederationRefusal.SubmissionRefused,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
            return true;
        }
        var (result, forwardReason) = await ResolveForwardedSubmissionAsync(
            credential: credential,
            ct: ct,
            mobility: mobility,
            operationId: operationId,
            payload: payload,
            sourceAuthority: sourceAuthority
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (result is null) {
            await WriteFederationRefusal(
                stream: stream,
                refusal: WorldFederationRefusal.SubmissionRefused,
                detail: ((forwardReason.Length > 0)
                ? forwardReason
                : "the submission names no live or forwarded transfer body"),
                ct: ct
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (
            (payload is WorldSubmissionPayload.Session { Value: SessionRequest.Leave }) &&
            (result is WorldSubmissionResult.Session { Reply.Accepted: true })
        ) {
            _ = m_travelerLiveness.TryRemove(key: (sourceAuthority, mobility.Incarnation), value: out _);
            await m_server.RunOnTickThreadAsync(() => { m_server.RetireTransferredMobility(in mobility); return true; }).ConfigureAwait(false);
        }

        using var completion = new MemoryStream();

        await WorldPeerWireFormat.WriteResultAsync(
            ct: ct,
            result: result,
            stream: completion
        ).ConfigureAwait(continueOnCapturedContext: false);
        // The completion stream's own buffer feeds the one framed write directly; nothing keeps it past the await.
        await WorldFederationCodec.WriteResponseAsync(
            stream: stream,
            kind: WorldFederationResponse.Completion,
            body: completion.GetBuffer().AsMemory(
                length: checked((int)completion.Length),
                start: 0
            ),
            ct: ct
        ).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
    private async Task<bool> ServeTransferKeyAsync(Stream stream, string sourceAuthority, Credential credential, WorldFederationRequest kind, ReadOnlyMemory<byte> body, CancellationToken ct) {
        if (!WorldFederationCodec.TryDecodeTransferKey(
            body: body.Span,
            failure: out var failure,
            sourceAuthority: out var carriedAuthority,
            transferId: out var transferId
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"{kind} transfer key — {failure}",
                refusal: WorldFederationRefusal.FrameMalformed,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (!string.Equals(
            a: carriedAuthority,
            b: sourceAuthority,
            comparisonType: StringComparison.Ordinal
        )) {
            await WriteFederationRefusal(
                ct: ct,
                detail: $"the {kind} names a transfer namespace this connection did not authenticate as",
                refusal: WorldFederationRefusal.SourceAuthorityMismatch,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        if (kind == WorldFederationRequest.Status) {
            var (status, landed) = await ExecuteAuthenticatedAsync(credential: credential, operation: () => {
                var answered = m_server.TransferStatus(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );

                return (answered, ((answered == WorldTransferStatus.Committed)
                    ? m_server.TransferLanded(
                        sourceAuthority: sourceAuthority,
                        transferId: transferId
                    )
                    : null));
            });

            await WorldFederationCodec.WriteResponseAsync(
                body: WorldFederationCodec.EncodeStatus(
                    landed: landed,
                    status: status
                ),
                ct: ct,
                kind: WorldFederationResponse.Status,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            return true;
        }

        _ = await ExecuteAuthenticatedAsync(credential: credential, operation: () => {
            if (kind == WorldFederationRequest.Abort) {
                m_server.AbortTransfer(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            } else {
                m_server.AcknowledgeTransfer(
                    sourceAuthority: sourceAuthority,
                    transferId: transferId
                );
            }
            return true;
        });

        await WorldFederationCodec.WriteResponseAsync(
            body: default,
            ct: ct,
            kind: WorldFederationResponse.Ack,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        return true;
    }
    // A long-lived authenticated control lane. The socket carries state updates, not impulses: the WorldServer
    // republishes the latest accepted image on every destination tick until another update or this connection's
    // finally releases its lease. Request/ack remains ordered, while connection setup and challenge authentication
    // are paid once rather than once per simulation tick.
    private async Task StreamFederatedIntentsAsync(Stream stream, string sourceAuthority, Credential credential, CancellationToken ct) {
        var leaseId = WorldFederatedIntentLease.Next();
        var touched = new Dictionary<WorldMobilityIdentity, (Principal Principal, IntentSubmission Submission)>();
        var leasedMobilities = new HashSet<WorldMobilityIdentity>();
        var forwardRelease = true;

        await WorldFederationCodec.WriteResponseAsync(
            body: default,
            ct: ct,
            kind: WorldFederationResponse.Ack,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        try {
            while (!ct.IsCancellationRequested) {
                var frame = await WorldFederationCodec.ReadRequestAsync(
                    ct: ct,
                    stream: stream
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!await credential.RefreshAsync().ConfigureAwait(false)) { return; }

                if (!frame.Ok) {
                    if (frame.Failure.Refusal != WireRefusal.ConnectionClosed) {
                        await WriteFederationRefusal(
                            stream: stream,
                            refusal: WorldFederationRefusal.FrameMalformed,
                            detail: frame.Failure.ToString(),
                            ct: ct
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    }
                    return;
                }
                if (frame.Kind == ((byte)WorldFederationRequest.IntentStreamHandoff)) {
                    if (frame.Body.Length != 0) {
                        await WriteFederationRefusal(
                            ct: ct,
                            detail: "the intent stream handoff carries a payload",
                            refusal: WorldFederationRefusal.PayloadUnexpected,
                            stream: stream
                        ).ConfigureAwait(continueOnCapturedContext: false);
                        return;
                    }

                    // The same client is deliberately moving this held-state lane to the newly published authority.
                    // Its new lane immediately seeds the current state, so forwarding a synthetic neutral from this
                    // older lane would race that seed and could cancel a still-held stick after the handoff.
                    forwardRelease = false;
                    await WorldFederationCodec.WriteResponseAsync(
                        body: default,
                        ct: ct,
                        kind: WorldFederationResponse.Ack,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }
                if (frame.Kind != ((byte)WorldFederationRequest.Intent)) {
                    await WriteFederationRefusal(
                        stream: stream,
                        refusal: WorldFederationRefusal.RequestUnsupported,
                        detail: $"{((WorldFederationRequest)frame.Kind)} is not accepted on an open intent stream",
                        ct: ct
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }
                if (!WorldFederationCodec.TryDecodeIntent(
                    body: frame.Body.Span,
                    sourceAuthority: out var carriedAuthority,
                    mobility: out var mobility,
                    submission: out var submission,
                    failure: out var intentFailure
                )) {
                    await WriteFederationRefusal(
                        ct: ct,
                        detail: $"intent \u2014 {intentFailure}",
                        refusal: WorldFederationRefusal.FrameMalformed,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }
                if (!string.Equals(
                    a: carriedAuthority,
                    b: sourceAuthority,
                    comparisonType: StringComparison.Ordinal
                )) {
                    await WriteFederationRefusal(
                        ct: ct,
                        detail: "the intent names a source authority this connection did not authenticate as",
                        refusal: WorldFederationRefusal.SourceAuthorityMismatch,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }
                if (leasedMobilities.Add(item: mobility)) {
                    await BeginTravelerStreamAsync(
                        ct: ct,
                        mobility: mobility,
                        sourceAuthority: sourceAuthority
                    ).ConfigureAwait(continueOnCapturedContext: false);
                }

                // Taken under the authority gate on this connection's own thread, as a forwarded arm in the same process
                // takes it. Waiting for the tick thread here would take one batch a tick, the rate batches are made, so
                // a stall would leave a backlog that never drains. The credential and the source authority's live
                // admission are checked inside the same gated operation as the publish, for every batch: the gate
                // serializes them with the tick that installs an admission change, so a batch is never published
                // under a policy that install has already revoked.
                var (resolved, accepted) = m_server.ExecuteAuthorityOperation(operation: () => {
                    credential.RequireValid();

                    return ((WorldAdmissionDoor.TryAdmitArrival(m_server.AdmissionPolicy, sourceAuthority, out _, WorldAdmissionArrival.Visitor) is null)
                        ? WorldLocalForwardedAuthority.TryApplyIntent(m_server, sourceAuthority, in mobility, leaseId, in submission)
                        : (((IntentSubmission?)null), false));
                });
                if (resolved is not { } stamped) {
                    await WriteFederationRefusal(
                        ct: ct,
                        detail: "the intent names no committed transfer body",
                        refusal: WorldFederationRefusal.CredentialUnknown,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }

                var reason = string.Empty;
                if (!accepted && m_server.TransferForwarder is { } forwarder) {
                    // Socket ingress can land before the committed onward route is published; retain this state update
                    // rather than turning an ordinary handoff into a visible control-lane outage.
                    await AwaitOnwardRouteAsync(
                        credential: credential,
                        ct: ct,
                        forwarder: forwarder,
                        mobility: mobility
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    credential.RequireValid();
                    accepted = await ExecuteAuthenticatedAsync(credential, () =>
                        (WorldAdmissionDoor.TryAdmitArrival(m_server.AdmissionPolicy, sourceAuthority, out _, WorldAdmissionArrival.Visitor) is null) &&
                        forwarder.TryForwardIntent(
                            mobility: in mobility,
                            reason: out reason,
                            source: m_server,
                            submission: in stamped
                        )).ConfigureAwait(false);
                }

                if (!accepted) {
                    var dropReason = ((reason.Length > 0)
                        ? reason
                        : "the intent names no live or forwarded transfer body");
                    m_server.NoteFederatedDrop(bodyIndex: stamped.EntityIndex, reason: dropReason);
                    await WriteFederationRefusal(
                        stream: stream,
                        refusal: WorldFederationRefusal.IntentRefused,
                        detail: dropReason,
                        ct: ct
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }

                // An accepted update is not answered: the lane writes without waiting, and the authority's answer to an
                // input travels in its deliveries. Only a refusal, or a handoff's acknowledgement, is written here.
                touched[mobility] = (Principal: stamped.Principal, Submission: stamped);
            }
        } finally {
            foreach (var leased in leasedMobilities) {
                EndTravelerStream(sourceAuthority: sourceAuthority, mobility: leased, handoff: !forwardRelease);
            }
            _ = m_server.RunOnTickThreadAsync(() => {
                m_server.ReleaseFederatedIntents(leaseId: leaseId);

                // A stream may now terminate at an older authority in a forwarding chain. Forward capture loss through
                // that same chain; disconnect cancels the episode and must not fabricate a scoring release.
                foreach (var pair in touched) {
                    if (!forwardRelease) {
                        break;
                    }
                    if (
                        m_server.ExecuteAuthorityOperation(operation: () => WorldLocalForwardedAuthority.IsLiveTransferredPrincipal(
                        server: m_server,
                        principal: pair.Value.Principal
                    )) ||
                        (m_server.TransferForwarder is not { } forwarder)
                    ) {
                        continue;
                    }

                    var released = pair.Value.Submission with {
                        Intent = default,
                        Input = pair.Value.Submission.Input with { CaptureLost = true, Image = default, Contributions = default, Events = default },
                    };
                    var mobility = pair.Key;

                    if (!forwarder.TryForwardIntent(
                        mobility: in mobility,
                        reason: out var reason,
                        source: m_server,
                        submission: in released
                    )) {
                        if (m_server.Output.HasNarrationSink) {
                            m_server.Output.Narrate(
                                channel: "world.authority unavailable",
                                text: $"[world.authority unavailable: traveler {mobility.Incarnation} release could not follow its committed route ({reason})]"
                            );
                        }
                    }
                }
                return true;
            });
        }
    }
    private async Task BeginTravelerStreamAsync(string sourceAuthority, WorldMobilityIdentity mobility, CancellationToken ct) {
        var entry = m_travelerLiveness.GetOrAdd(
            key: (sourceAuthority, mobility.Incarnation),
            valueFactory: static (_, m) => new TravelerLiveness(mobility: m),
            factoryArgument: mobility
        );

        lock (entry.Gate) {
            if (mobility.Epoch > entry.Mobility.Epoch) {
                entry.Mobility = mobility;
            }
            entry.ActiveStreams++;
        }
        _ = await m_server.RunOnTickThreadAsync(
            work: () => m_server.GrantTable.TryResumeTransferredPeer(sourceAuthority: sourceAuthority, mobility: in mobility),
            ct: ct
        ).ConfigureAwait(false);
    }

    private bool HasFederationConnectionFrom(string sourceAuthority) {
        foreach (var credential in m_federationConnections.Values) {
            if (string.Equals(a: credential.Authority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }
        return false;
    }

    private void EndTravelerStream(string sourceAuthority, WorldMobilityIdentity mobility, bool handoff) {
        if (!m_travelerLiveness.TryGetValue(key: (sourceAuthority, mobility.Incarnation), value: out var entry)) {
            return;
        }
        var becameIdle = false;
        WorldMobilityIdentity latest;

        lock (entry.Gate) {
            if (entry.ActiveStreams > 0) {
                entry.ActiveStreams--;
            }
            latest = entry.Mobility;
            becameIdle = (!handoff && (entry.ActiveStreams == 0));
        }
        if (becameIdle && !HasFederationConnectionFrom(sourceAuthority: sourceAuthority)) {
            ScheduleIdleTravelerPark(sourceAuthority: sourceAuthority, entry: entry, fallback: latest);
        }
    }

    private void SweepDisconnectedAuthorityTravelers(string sourceAuthority) {
        if (HasFederationConnectionFrom(sourceAuthority: sourceAuthority)) {
            return;
        }
        foreach (var (key, entry) in m_travelerLiveness) {
            if (!string.Equals(a: key.SourceAuthority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
                continue;
            }
            WorldMobilityIdentity latest;
            bool idle;
            lock (entry.Gate) {
                latest = entry.Mobility;
                idle = (entry.ActiveStreams == 0);
            }
            if (idle) {
                ScheduleIdleTravelerPark(sourceAuthority: sourceAuthority, entry: entry, fallback: latest);
            }
        }
        _ = m_server.RunOnTickThreadAsync(work: () => {
            if (HasFederationConnectionFrom(sourceAuthority: sourceAuthority)) {
                return false;
            }
            m_server.GrantTable.ParkUnattachedTransferredPeers(
                sourceAuthority: sourceAuthority,
                hasActiveStream: incarnation => {
                    if (!m_travelerLiveness.TryGetValue(key: (sourceAuthority, incarnation), value: out var live)) {
                        return false;
                    }
                    lock (live.Gate) {
                        return (live.ActiveStreams > 0);
                    }
                }
            );
            return true;
        });
    }

    private void ScheduleIdleTravelerPark(string sourceAuthority, TravelerLiveness entry, WorldMobilityIdentity fallback) {
        _ = m_server.RunOnTickThreadAsync(work: () => {
            if (HasFederationConnectionFrom(sourceAuthority: sourceAuthority)) {
                return false;
            }
            WorldMobilityIdentity mobility;
            lock (entry.Gate) {
                if (entry.ActiveStreams > 0) {
                    return false;
                }
                mobility = entry.Mobility;
            }
            if (mobility == default) {
                mobility = fallback;
            }
            m_server.GrantTable.ParkTransferredPeer(sourceAuthority: sourceAuthority, mobility: in mobility);
            if (!m_server.ExecuteAuthorityOperation(operation: () => m_server.TryTransferredPrincipal(sourceAuthority: sourceAuthority, mobility: in mobility, principal: out _))) {
                _ = m_travelerLiveness.TryRemove(key: (sourceAuthority, mobility.Incarnation), value: out _);
            }
            return true;
        });
    }

    // The lane authenticated a source authority's namespace, which names no body here, so it observes as the public
    // observer. A seat reads its own disclosure through ObserveTraveler, whose credential resolves to its principal.
    private async Task StreamProjectionAsync(Stream stream, string sourceAuthority, Credential credential, CancellationToken ct) {
        var policy = m_server.AdmissionPolicy;

        var tier = DisclosureFor(sourceAuthority: sourceAuthority, kind: WorldAdmissionArrival.Session);
        if (tier == WorldDisclosureTier.Frames) {
            await WriteFederationRefusal(ct: ct, detail: "no public federation document disclosure is authorized",
                refusal: WorldFederationRefusal.AuthenticationFailed, stream: stream).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }
        var view = m_server.DeliveryView(null, tier);
        var sink = new WorldFederationProjectionSink(
            isCurrent: () => (ReferenceEquals(objA: policy, objB: m_server.AdmissionPolicy) && credential.IsValid()),
            server: m_server,
            tier: tier,
            view: view,
            disclosure: () => view.Bodies
        );
        // The sink resolves the live policy and redacts only a sampled frame. Attaching it unfiltered here avoids
        // copying a 4,096-body radius view on every authority tick merely to discard seven out of eight copies.
        var lease = await ExecuteAuthenticatedAsync(credential: credential, operation: () => m_server.AttachSink(sink: sink));

        try {
            await sink.StreamAsync(
                ct: ct,
                output: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception exception) when ((exception is IOException or SocketException or OperationCanceledException)) {
            // Observer lifetime is the socket lifetime.
        } finally {
            // WorldOutputHub's subscriber list is not itself guarded, so the release must hold the same authority
            // gate the attach did — otherwise this dispose races a tick publishing through that list.
            _ = m_server.RunOnTickThreadAsync(work: () => {
                lease.Dispose();
                sink.Release();

                return true;
            });
        }
    }
    // The one decode step both submission ingress paths share (the interactive frame loop and a federated peer's
    // forwarded submission, once unwrapped): a live payload, or a named WorldCodecFailure — each caller writes its
    // own dialect's refusal frame from it. A session principal is the admitting world's own, so a remote payload
    // naming one anywhere is refused here, before either path reads it.
    private static bool TryDecodeSubmissionFrame(ReadOnlySpan<byte> frame, out WorldSubmissionPayload payload, out Guid operationId, out WorldCodecFailure failure) {
        if (
            !Puck.World.Protocol.WorldFrameCodec.TryDecode(
            failure: out failure,
            frame: frame,
            operationId: out operationId,
            payload: out var decoded
        ) ||
            (decoded is null)
        ) {
            payload = null!;

            return false;
        }

        if (WorldSubmissionPrincipals.NamesSession(payload: decoded)) {
            failure = new WorldCodecFailure(
                Detail: "a session principal belongs to the world that admitted it; a remote submission never names one",
                Refusal: WorldCodecRefusal.SessionPrincipalRemote
            );
            payload = null!;

            return false;
        }

        payload = decoded;

        return true;
    }
    // Answers one issuance request with the issuer's bindings or a named refusal. The issuer checks the key's proof
    // of possession over the transport-bound challenge before it asks the sign-in provider anything.
    private async Task ServeIssuanceAsync(Stream stream, byte[] challenge, ReadOnlyMemory<byte> body, string clientKeyHash, CancellationToken ct) {
        if (m_issuer is null) {
            await WriteFederationRefusal(ct: ct, detail: "this authority runs no issuer", refusal: WorldFederationRefusal.IssuanceRefused, stream: stream).ConfigureAwait(false);
            return;
        }
        if (!WorldFederationCodec.TryDecodeIssuance(body: body.Span, credential: out var credential, failure: out var failure, possession: out var possession, subjectPublicKeyInfo: out var subjectKey)) {
            await WriteFederationRefusal(ct: ct, detail: $"malformed issuance request ({failure})", refusal: WorldFederationRefusal.FrameMalformed, stream: stream).ConfigureAwait(false);
            return;
        }
        var issued = await m_issuer.IssueAsync(
            boundChallenge: WorldFederationCodec.BindIssuanceChallenge(
                challenge: challenge,
                clientKeyHash: clientKeyHash,
                serverKeyHash: m_network.Peer.Id.KeyHash,
                subjectPublicKeyInfo: subjectKey
            ),
            cancellationToken: ct,
            credential: credential,
            possession: possession,
            subjectPublicKeyInfo: subjectKey
        ).ConfigureAwait(false);

        if (issued is not { } bindings) {
            await WriteFederationRefusal(ct: ct, detail: "the sign-in or the key's proof of possession did not verify", refusal: WorldFederationRefusal.IssuanceRefused, stream: stream).ConfigureAwait(false);
            return;
        }
        await WorldFederationCodec.WriteResponseAsync(
            body: WorldFederationCodec.EncodeIssued(rootBinding: bindings.RootBinding, subjectBinding: bindings.SubjectBinding),
            ct: ct,
            kind: WorldFederationResponse.Issued,
            stream: stream
        ).ConfigureAwait(false);
    }
    private Task WriteFederationRefusal(Stream stream, WorldFederationRefusal refusal, string detail, CancellationToken ct) {
        Interlocked.Increment(location: ref m_federationRefusals[((int)refusal)]);

        return WorldFederationCodec.WriteRefusalAsync(
            ct: ct,
            detail: detail,
            refusal: refusal,
            stream: stream
        );
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_server.PeerDisconnected -= ClosePeer;

        CloseConnections();
        if (m_ownsNetwork) { m_network.Dispose(); }

        lock (m_connectionsLock) {
            m_connections.Clear();
        }

        m_cts?.Dispose();
    }
    /// <summary>Drains every tick-thread work item enqueued by a connection since the last drain — admissions,
    /// submissions, and disconnects alike. MUST run on the tick thread, before <c>WorldServer.Step</c>, so it never
    /// races the single-threaded server/population/grant state.</summary>
    public void DrainPending() => m_server.DrainMailbox();
    /// <summary>Completes once tick-thread work is queued for <see cref="DrainPending"/>, immediately when some
    /// already is. A loop that only needs to step when a connection has work waits here instead of polling.</summary>
    /// <param name="cancellationToken">Abandons the wait; queued work is unaffected.</param>
    /// <returns>A task that completes when at least one work item is queued.</returns>
    public ValueTask WaitForPendingWorkAsync(CancellationToken cancellationToken) => m_server.WaitForPendingWorkAsync(cancellationToken);
    /// <summary>Binds the listener and starts accepting connections in the background. The composition root calls this
    /// only when <c>host.listen</c>/<c>--listen</c> names an endpoint; when it is absent the call is skipped entirely,
    /// so no listener ever binds.</summary>
    /// <param name="listen">The <c>host:port</c> endpoint to bind.</param>
    /// <exception cref="FormatException"><paramref name="listen"/> is not a parseable IP endpoint.</exception>
    /// <exception cref="ListenEndpointUnavailableException">This host cannot bind <paramref name="listen"/>: the address
    /// is in use, the operating system refused it, or QUIC is not offered here.</exception>
    public void Start(string listen) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: listen);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        if (IsListening) { throw new InvalidOperationException(message: "The World peer endpoint is already listening."); }

        if (!IPEndPoint.TryParse(
            result: out var endpoint,
            s: listen
        )) {
            throw new FormatException(message: $"host.listen '{listen}' is not a parseable \"ip:port\" endpoint (a hostname is not accepted).");
        }

        var cts = new CancellationTokenSource();
        var lifetime = cts.Token;

        try {
            ListenEndpoint = m_network.Peer.ListenAsync(
                ct: lifetime,
                endpoint: endpoint
            ).GetAwaiter().GetResult().ToString();
        } catch (Exception failure) when ((ListenEndpointUnavailableException.Classify(
            endpoint: listen,
            failure: failure,
            transport: "quic"
        ) is { } unavailable)) {
            cts.Dispose();

            throw unavailable;
        }
        m_cts = cts;
        m_credentialTimer = m_timeProvider.CreateTimer(
            callback: unused => _ = EvictInvalidCredentialsAsync(),
            state: null,
            dueTime: CredentialSweepInterval,
            period: CredentialSweepInterval
        );
        m_acceptLoop = Task.Run(function: () => AcceptLoopAsync(ct: lifetime));
        if (m_server.Output.HasNarrationSink) {
            m_server.Output.Narrate(
                channel: "world.listen",
                text: $"[world.listen: bound {ListenEndpoint} transport=quic peer={m_network.Peer.Id.Domain}]"
            );
        }
    }
    /// <summary>Closes live ingress while retaining authoritative connection bookkeeping for final capture.</summary>
    public void SuspendIngress() => CloseConnections();

    // Network readers and queued work consult one immutable verdict. Readers and the idle timer refresh it after a trust change.
    private sealed class Credential : IDisposable {
        private sealed record Verdict(IReadOnlyList<WorldAdmissionEntry>? Policy, VerifiedPeerIdentity Proof, long VerifiedAt);
        private readonly WorldPeerHost m_host;
        private readonly Func<IReadOnlyList<WorldAdmissionEntry>?, CancellationToken, ValueTask<VerifiedPeerIdentity?>> m_verify;
        private readonly CancellationTokenSource m_lifetime;
        private Verdict? m_verdict;

        public Credential(WorldPeerHost host, IReadOnlyList<WorldAdmissionEntry>? policy, VerifiedPeerIdentity proof,
            Func<IReadOnlyList<WorldAdmissionEntry>?, CancellationToken, ValueTask<VerifiedPeerIdentity?>> verify, CancellationToken ct) {
            m_host = host;
            m_verify = verify;
            m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Authority = proof.Authority;
            Token = m_lifetime.Token;
            m_verdict = new(policy, proof, host.m_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        }
        public string Authority { get; }
        public CancellationToken Token { get; }
        public void RequireValid() {
            if (!IsValid()) { throw new OperationCanceledException("the authenticated session credential is no longer valid"); }
        }
        private bool WithinWindow(Verdict verdict) {
            var now = m_host.m_timeProvider.GetUtcNow().ToUnixTimeSeconds();
            return now >= verdict.VerifiedAt && now <= verdict.Proof.ValidThrough;
        }
        public bool IsValid() => Volatile.Read(ref m_verdict) is { } verdict &&
            ReferenceEquals(verdict.Policy, m_host.m_server.AdmissionPolicy) &&
            WithinWindow(verdict) && verdict.Proof.TrustIsCurrent?.Invoke() != false && !Token.IsCancellationRequested;

        public async ValueTask<bool> RefreshAsync(bool refreshTrust = false) {
            var prior = Volatile.Read(ref m_verdict);
            if (prior is null || Token.IsCancellationRequested) { return false; }
            if (WithinWindow(prior)) {
                if (refreshTrust && prior.Proof.RefreshTrustAsync is { } refresh) {
                    await refresh(Token).ConfigureAwait(false);
                }
                var policy = m_host.m_server.AdmissionPolicy;
                if (ReferenceEquals(policy, prior.Policy) && prior.Proof.TrustIsCurrent?.Invoke() != false) { return true; }
                try {
                    if (await m_verify(policy, Token).ConfigureAwait(false) is { } through) {
                        Interlocked.CompareExchange(ref m_verdict, new(policy, through, prior.VerifiedAt), prior);
                        return IsValid();
                    }
                } catch (OperationCanceledException) when (Token.IsCancellationRequested) { return false; }
            }
            if (Interlocked.Exchange(ref m_verdict, null) is not null) {
                try { await m_lifetime.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
            }
            return false;
        }
        public void Dispose() {
            Interlocked.Exchange(ref m_verdict, null);
            m_lifetime.Dispose();
        }
    }
    private sealed class Connection(int id, int peerIndex, int generation, PeerStream client, Stream stream, string remoteEndpoint, string identityDomain, string identitySubject, WorldDisclosureTier tier, Credential credential) {
        private long m_correlationId;
        private long m_sequence;
        private int m_tier = ((int)tier);

        public int Id { get; } = id;
        public int PeerIndex { get; } = peerIndex;
        public int Generation { get; } = generation;
        public PeerStream Client { get; } = client;
        public Stream Stream { get; } = stream;
        public string RemoteEndpoint { get; } = remoteEndpoint;
        public string IdentityDomain { get; } = identityDomain;
        public string IdentitySubject { get; } = identitySubject;
        public WorldDisclosureTier Tier {
            get => ((WorldDisclosureTier)Volatile.Read(location: ref m_tier));
            set => Volatile.Write(location: ref m_tier, value: ((int)value));
        }
        public Credential Credential { get; } = credential;

        public Principal Principal => Principal.Peer(
            index: PeerIndex,
            generation: Generation
        );

        public long NextCorrelation() => Interlocked.Increment(location: ref m_correlationId);
        public long NextSequence() => Interlocked.Increment(location: ref m_sequence);
    }
}
