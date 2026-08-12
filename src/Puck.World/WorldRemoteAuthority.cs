using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A committed body's immutable address on one remote authority hop.</summary>
internal readonly record struct WorldRemoteRouteCredential(int BodyIndex, string SourceAuthority, WorldMobilityIdentity Mobility);

/// <summary>The remote implementation of the authority contract used by transfer and continuous projection.</summary>
internal sealed class WorldRemoteAuthority : IDisposable {
    private IPEndPoint m_endpoint;
    private readonly CancellationTokenSource m_lifetime;
    private readonly Dictionary<int, (string SourceAuthority, WorldMobilityIdentity Mobility)> m_credentials = new();
    private readonly ConcurrentDictionary<string, FederatedIntentPump> m_intentPumps = new(comparer: StringComparer.Ordinal);
    private readonly WorldFederatedServerLink m_link;
    private readonly WorldFederationSecurity m_security;
    private readonly string m_observerAuthority;
    private readonly WorldRemoteAuthority? m_submissionAuthority;
    private readonly WorldRemoteRouteCredential? m_submissionCredential;
    private WorldAuthorityRouteDescription? m_observedRoute;
    private readonly Action<WorldAuthorityRouteDescription>? m_routeChanged;
    private int m_routeRevision;
    private long m_lastObservedTickBits;
    private string m_authority = string.Empty;
    private WorldDefinition m_definition;

    public WorldRemoteAuthority(string endpoint, WorldDefinition placeholder, WorldFederationSecurity security, string observerAuthority, WorldRemoteAuthority? submissionAuthority = null, WorldRemoteRouteCredential? submissionCredential = null, WorldAuthorityRouteDescription? initialRoute = null, Action<WorldAuthorityRouteDescription>? routeChanged = null, CancellationToken applicationStopping = default) {
        if (!IPEndPoint.TryParse(endpoint, out var parsed)) {
            throw new FormatException($"host.authority '{endpoint}' is not a parseable IP endpoint");
        }

        m_endpoint = parsed;
        m_definition = placeholder;
        m_security = security ?? throw new ArgumentNullException(paramName: nameof(security));
        m_observerAuthority = observerAuthority;
        m_submissionAuthority = submissionAuthority;
        m_submissionCredential = submissionCredential;
        if ((submissionAuthority is null) != (submissionCredential is null)) {
            throw new ArgumentException(message: "a routed observer requires both its submission authority and immutable route credential");
        }
        m_observedRoute = initialRoute;
        m_routeChanged = routeChanged;
        m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        if (initialRoute is { } route) {
            if (!IPEndPoint.TryParse(route.Endpoint, out var initialEndpoint)) {
                throw new FormatException($"route endpoint '{route.Endpoint}' is not a parseable IP endpoint");
            }
            m_endpoint = initialEndpoint;
            m_definition = route.Definition;
            m_authority = route.Entity.Authority;
            m_lastObservedTickBits = unchecked((long)route.Tick);
        }
        m_link = new WorldFederatedServerLink(authority: this);
    }

    public string Endpoint => m_endpoint.ToString();
    public WorldDefinition Definition => Volatile.Read(ref m_definition);
    public IServerLink Link => m_link;
    public ulong NextInputTick => (unchecked((ulong)Interlocked.Read(ref m_lastObservedTickBits)) + 1UL);
    public string Authority => Volatile.Read(ref m_authority);

    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
        var frame = RoundTrip(sourceAuthority: request.SourceAuthority, kind: WorldFederationWireFormat.RequestKind.Reserve, body: WorldFederationWireFormat.EncodeReservation(request: request));
        if (frame.Kind != WorldFederationWireFormat.ResponseKind.Reservation) {
            return WorldTransferReservationReply.Refused(reason: DecodeRefusal(frame));
        }

        var reply = WorldFederationWireFormat.DecodeReservationReply(body: frame.Body);
        if (reply.Accepted) {
            if ((reply.BodyIndices.Count != request.Members.Count) || (reply.DestinationDefinition is null)) {
                try {
                    Abort(sourceAuthority: request.SourceAuthority, transferId: request.TransferId);
                } catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) {
                    // The malformed acceptance is already a terminal refusal locally; a failed best-effort abort
                    // expires under the destination lease rather than being treated as a valid binding.
                }

                return WorldTransferReservationReply.Refused(reason: "remote authority returned a malformed accepted reservation (body count or destination definition missing)");
            }

            for (var ordinal = 0; ordinal < reply.BodyIndices.Count; ordinal++) {
                var bodyIndex = reply.BodyIndices[ordinal];
                if (m_credentials.TryGetValue(key: bodyIndex, value: out var previous) &&
                    m_intentPumps.TryGetValue(key: previous.SourceAuthority, value: out var previousPump)) {
                    previousPump.Retire(mobility: previous.Mobility);
                }
                m_credentials[bodyIndex] = (request.SourceAuthority, request.Members[ordinal].Mobility!.Value.Advance());
            }
        }
        return reply;
    }

    public bool Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        var frame = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Commit, body: WorldFederationWireFormat.EncodeCommit(sourceAuthority: sourceAuthority, transferId: transferId, members: members));

        if (frame.Kind != WorldFederationWireFormat.ResponseKind.Commit) {
            reason = DecodeRefusal(frame);
            return false;
        }

        using var input = new MemoryStream(frame.Body, writable: false);
        using var reader = new BinaryReader(input);
        var accepted = reader.ReadBoolean();
        reason = reader.ReadString();
        return accepted;
    }

    public void Abort(string sourceAuthority, ulong transferId) {
        _ = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Abort, body: WorldFederationWireFormat.EncodeTransferKey(sourceAuthority: sourceAuthority, transferId: transferId));
    }

    public void Acknowledge(string sourceAuthority, ulong transferId) {
        _ = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.AcknowledgeTransfer, body: WorldFederationWireFormat.EncodeTransferKey(sourceAuthority: sourceAuthority, transferId: transferId));
    }

    public WorldTransferStatus Status(string sourceAuthority, ulong transferId) {
        var frame = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Status, body: WorldFederationWireFormat.EncodeTransferKey(sourceAuthority: sourceAuthority, transferId: transferId));

        if ((frame.Kind != WorldFederationWireFormat.ResponseKind.Status) || (frame.Body.Length != 1) || !Enum.IsDefined(value: (WorldTransferStatus)frame.Body[0])) {
            throw new IOException(DecodeRefusal(frame));
        }

        return (WorldTransferStatus)frame.Body[0];
    }

    public IDisposable AttachSink(IClientSink sink) {
        ArgumentNullException.ThrowIfNull(sink);
        var lease = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
        _ = Task.Run(function: () => ObserveUntilCancelledAsync(sink: sink, ct: lease.Token));
        return lease;
    }

    public void Dispose() {
        m_lifetime.Cancel();
        foreach (var pump in m_intentPumps.Values) {
            pump.Dispose();
        }
        m_intentPumps.Clear();
    }

    internal bool TryCredential(int bodyIndex, out string sourceAuthority, out WorldMobilityIdentity mobility) {
        if (m_submissionCredential is { } captured) {
            sourceAuthority = captured.SourceAuthority;
            mobility = captured.Mobility;
            return true;
        }

        if ((bodyIndex < 0) && (m_credentials.Count > 0)) {
            var first = m_credentials.OrderBy(pair => pair.Key).First().Value;
            sourceAuthority = first.SourceAuthority;
            mobility = first.Mobility;
            return true;
        }
        if (m_credentials.TryGetValue(key: bodyIndex, value: out var credential)) {
            sourceAuthority = credential.SourceAuthority;
            mobility = credential.Mobility;
            return true;
        }
        sourceAuthority = string.Empty; mobility = default; return false;
    }

    internal bool TryRouteCredential(int bodyIndex, out WorldRemoteRouteCredential credential) {
        if (m_submissionCredential is { } captured) {
            credential = captured;
            return true;
        }
        if (TryCredential(bodyIndex: bodyIndex, sourceAuthority: out var sourceAuthority, mobility: out var mobility)) {
            credential = new WorldRemoteRouteCredential(BodyIndex: bodyIndex, SourceAuthority: sourceAuthority, Mobility: mobility);
            return true;
        }

        credential = default;
        return false;
    }

    internal bool TryForwardIntent(int bodyIndex, in IntentSubmission submission, out string reason) {
        if (!TryRouteCredential(bodyIndex: bodyIndex, credential: out var credential)) {
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }

        return TryForwardIntent(credential: in credential, submission: in submission, reason: out reason);
    }

    internal bool TryForwardIntent(in WorldRemoteRouteCredential credential, in IntentSubmission submission, out string reason) {
        var target = (m_submissionAuthority ?? this);
        var stamped = submission with { EntityIndex = credential.BodyIndex };
        var pump = target.m_intentPumps.GetOrAdd(key: credential.SourceAuthority, valueFactory: authority => new FederatedIntentPump(owner: target, sourceAuthority: authority));
        pump.Publish(mobility: credential.Mobility, submission: in stamped);

        reason = string.Empty;
        return true;
    }

    internal bool TryDescribeRoute(int bodyIndex, out WorldAuthorityRouteDescription route, out string reason) {
        if (!TryRouteCredential(bodyIndex: bodyIndex, credential: out var credential)) {
            route = default;
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }

        return TryDescribeRoute(credential: in credential, route: out route, reason: out reason);
    }

    internal bool TryDescribeRoute(in WorldRemoteRouteCredential credential, out WorldAuthorityRouteDescription route, out string reason) {
        route = default;
        var target = (m_submissionAuthority ?? this);
        var mobility = credential.Mobility;
        var response = target.RoundTrip(sourceAuthority: credential.SourceAuthority, kind: WorldFederationWireFormat.RequestKind.Route, body: WorldFederationWireFormat.EncodeRouteCredential(sourceAuthority: credential.SourceAuthority, mobility: in mobility));
        if ((response.Kind != WorldFederationWireFormat.ResponseKind.Route) || !WorldFederationWireFormat.TryDecodeRoute(body: response.Body, route: out route)) {
            reason = DecodeRefusal(response);
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal bool TryForwardSubmission(int bodyIndex, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) {
        if (!TryRouteCredential(bodyIndex: bodyIndex, credential: out var credential)) {
            result = null;
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }

        return TryForwardSubmission(credential: in credential, payload: payload, result: out result, reason: out reason);
    }

    internal bool TryForwardSubmission(in WorldRemoteRouteCredential credential, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) {
        result = null;
        if (!WorldFrameCodec.TryEncode(payload: payload, frame: out var canonical, failure: out var failure)) {
            reason = $"forwarded submission could not be encoded — {failure.Detail}";
            return false;
        }

        var mobility = credential.Mobility;
        var body = WorldFederationWireFormat.EncodeSubmission(sourceAuthority: credential.SourceAuthority, mobility: in mobility, frame: canonical);
        var target = (m_submissionAuthority ?? this);
        var response = target.RoundTrip(sourceAuthority: credential.SourceAuthority, kind: WorldFederationWireFormat.RequestKind.Submission, body: body);
        if (response.Kind != WorldFederationWireFormat.ResponseKind.Completion) {
            reason = DecodeRefusal(response);
            return false;
        }

        using var input = new MemoryStream(response.Body, writable: false);
        var completion = WorldTcpWireFormat.TryReadDownstreamAsync(stream: input, ct: default).GetAwaiter().GetResult();
        if (completion is null) {
            reason = "forwarded authority returned an empty completion";
            return false;
        }

        var frame = completion.Value;
        switch (frame.Kind) {
            case WorldTcpWireFormat.DownstreamKind.Ack:
                result = WorldSubmissionResult.Ack.Instance;
                reason = string.Empty;
                return true;
            case WorldTcpWireFormat.DownstreamKind.Session: {
                    if (frame.Body.Length < (sizeof(byte) + sizeof(int) + sizeof(ushort))) {
                        reason = "forwarded authority returned a truncated session completion";
                        return false;
                    }
                    var offset = (sizeof(byte) + sizeof(int));
                    result = new WorldSubmissionResult.Session(new SessionReply(
                        Accepted: (frame.Body[0] != 0),
                        AssignedIndex: BinaryPrimitives.ReadInt32LittleEndian(source: frame.Body.AsSpan(start: sizeof(byte))),
                        RosterEcho: string.Empty,
                        Reason: WorldTcpWireFormat.ReadLengthPrefixedString(body: frame.Body, offset: ref offset)));
                    reason = string.Empty;
                    return true;
                }
            case WorldTcpWireFormat.DownstreamKind.Query: {
                    if (frame.Body.Length < (sizeof(byte) + sizeof(ushort))) {
                        reason = "forwarded authority returned a truncated query completion";
                        return false;
                    }
                    var offset = sizeof(byte);
                    result = new WorldSubmissionResult.Query(new QueryAnswer(
                        Text: WorldTcpWireFormat.ReadLengthPrefixedString(body: frame.Body, offset: ref offset),
                        Refused: (frame.Body[0] != 0)));
                    reason = string.Empty;
                    return true;
                }
            case WorldTcpWireFormat.DownstreamKind.Refusal:
                reason = WorldTcpWireFormat.DecodeText(body: frame.Body);
                return false;
            default:
                reason = $"forwarded authority returned unsupported completion {frame.Kind}";
                return false;
        }
    }

    internal (WorldFederationWireFormat.ResponseKind Kind, byte[] Body) RoundTrip(string sourceAuthority, WorldFederationWireFormat.RequestKind kind, byte[] body) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.RoundTrip(sourceAuthority: sourceAuthority, kind: kind, body: body);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(m_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        client.NoDelay = true;
        client.ConnectAsync(remoteEP: m_endpoint, cancellationToken: timeout.Token).AsTask().GetAwaiter().GetResult();
        using var stream = client.GetStream();
        WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: timeout.Token).GetAwaiter().GetResult();
        AuthenticateAsync(stream: stream, sourceAuthority: sourceAuthority, ct: timeout.Token).GetAwaiter().GetResult();
        WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: kind, body: body, ct: timeout.Token).GetAwaiter().GetResult();
        var frame = WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: timeout.Token).GetAwaiter().GetResult() ?? throw new IOException("remote authority closed without a verdict");
        return ((WorldFederationWireFormat.ResponseKind)frame.Kind, frame.Body);
    }

    // Authority processes may start in any order. An adjacency is durable topology, so its observation channel
    // cannot become permanently CLOSED merely because the neighbour had not bound its socket on the first tick.
    // Reconnect the same held lease until its owner releases it; the mirror keeps the last delivered revision while
    // disconnected and the authored unavailable policy remains the crossing-side safety net.
    private async Task ObserveUntilCancelledAsync(IClientSink sink, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                if (await ObserveSessionAsync(sink: sink, ct: ct).ConfigureAwait(false)) {
                    continue;
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) {
                Console.Error.WriteLine(value: $"[world.projection: remote observer '{Endpoint}' unavailable ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}); retrying]");
            }

            try {
                await Task.Delay(delay: TimeSpan.FromMilliseconds(250), cancellationToken: ct).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            }
        }
    }

    private async Task<bool> ObserveSessionAsync(IClientSink sink, CancellationToken ct) {
            using var client = new TcpClient();
            client.NoDelay = true;
            var observedEndpoint = m_endpoint;
            var observedRouteRevision = Volatile.Read(ref m_routeRevision);
            await client.ConnectAsync(remoteEP: observedEndpoint, cancellationToken: ct).ConfigureAwait(false);
            using var stream = client.GetStream();
            await WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: ct).ConfigureAwait(false);
            await AuthenticateAsync(stream: stream, sourceAuthority: m_observerAuthority, ct: ct).ConfigureAwait(false);
            await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Observe, body: [], ct: ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested) {
                var frame = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);
                if (frame is null) {
                    return false;
                }

                switch ((WorldFederationWireFormat.ResponseKind)frame.Value.Kind) {
                    case WorldFederationWireFormat.ResponseKind.Definition: {
                            var definition = WorldFederationWireFormat.DecodeDefinition(body: frame.Value.Body);
                            Volatile.Write(ref m_definition, definition);
                            sink.DeliverDefinition(definition: definition);
                            break;
                        }
                    case WorldFederationWireFormat.ResponseKind.Snapshot: {
                            var snapshot = WorldFederationWireFormat.DecodeSnapshot(body: frame.Value.Body);
                            var containsObservedEntity = SnapshotContainsObservedEntity(snapshot: in snapshot);
                            if ((Volatile.Read(ref m_routeRevision) != observedRouteRevision) ||
                                (!containsObservedEntity && RefreshObservedRoute())) {
                                // The route callback seeded the new authority's committed image. Publishing this
                                // old authority's missing-body snapshot first would create an avoidable inactive
                                // frame between two committed writers—the camera hitch the route seed exists to
                                // eliminate. Reconnect directly to the new head instead.
                                return true;
                            }
                            _ = Interlocked.Exchange(location1: ref m_lastObservedTickBits, value: unchecked((long)snapshot.Tick));
                            Volatile.Write(ref m_authority, snapshot.Authority);
                            sink.DeliverSnapshot(snapshot: in snapshot);
                            break;
                        }
                }
            }
            return false;
    }

    private bool SnapshotContainsObservedEntity(in WorldSnapshot snapshot) {
        var observedEntity = m_observedRoute?.Entity;
        var bodyIndex = (observedEntity?.Index ?? m_submissionCredential?.BodyIndex ?? -1);
        foreach (ref readonly var entry in snapshot.Entries.Span) {
            // A population slot may be reused in the same snapshot that the traveler leaves. Index+active alone
            // would then mistake the replacement occupant for the traveler and suppress the route refresh forever,
            // leaving control/camera attached to the wrong body. A durable entity address is authority/index/
            // generation; use all three whenever the committed route supplied them.
            if ((entry.Index == bodyIndex) && entry.Active &&
                ((observedEntity is null) || ((entry.Generation == observedEntity.Value.Generation) &&
                    string.Equals(a: snapshot.Authority, b: observedEntity.Value.Authority, comparisonType: StringComparison.Ordinal)))) {
                return true;
            }
        }
        return false;
    }

    private bool RefreshObservedRoute() {
        if ((m_submissionAuthority is null) || (m_submissionCredential is not { } credential) ||
            !TryDescribeRoute(credential: in credential, route: out var route, reason: out _) ||
            !IPEndPoint.TryParse(route.Endpoint, out var routedEndpoint)) {
            return false;
        }

        if ((m_observedRoute is { } observed) &&
            string.Equals(a: observed.Endpoint, b: route.Endpoint, comparisonType: StringComparison.Ordinal) &&
            (observed.Entity == route.Entity)) {
            m_observedRoute = route;
            return false;
        }

        m_endpoint = routedEndpoint;
        m_observedRoute = route;
        InvalidateAcknowledgement(credential: in credential);
        Volatile.Write(ref m_definition, route.Definition);
        Volatile.Write(ref m_authority, route.Entity.Authority);
        _ = Interlocked.Exchange(location1: ref m_lastObservedTickBits, value: unchecked((long)route.Tick));
        _ = Interlocked.Increment(ref m_routeRevision);
        m_routeChanged?.Invoke(obj: route);
        return true;
    }

    private void InvalidateAcknowledgement(in WorldRemoteRouteCredential credential) {
        if ((m_submissionAuthority ?? this).m_intentPumps.TryGetValue(key: credential.SourceAuthority, value: out var pump)) {
            pump.InvalidateAcknowledgement(mobility: credential.Mobility);
        }
    }

    private static string DecodeRefusal((WorldFederationWireFormat.ResponseKind Kind, byte[] Body) frame) =>
        ((frame.Kind == WorldFederationWireFormat.ResponseKind.Refusal) ? Encoding.UTF8.GetString(frame.Body) : $"unexpected federation response {frame.Kind}");

    private async Task AuthenticateAsync(NetworkStream stream, string sourceAuthority, CancellationToken ct) {
        var challenge = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);

        if ((challenge is null) || (challenge.Value.Kind != (byte)WorldFederationWireFormat.ResponseKind.Challenge) || (challenge.Value.Body.Length != WorldFederationSecurity.ChallengeBytes)) {
            throw new IOException(challenge is null ? "remote authority closed before federation challenge" : DecodeRefusal(((WorldFederationWireFormat.ResponseKind)challenge.Value.Kind, challenge.Value.Body)));
        }

        var proof = m_security.Prove(sourceAuthority: sourceAuthority, challenge: challenge.Value.Body);
        await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Authenticate, body: WorldFederationWireFormat.EncodeAuthentication(sourceAuthority: sourceAuthority, proof: proof), ct: ct).ConfigureAwait(false);
        var verdict = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);

        if ((verdict is null) || (verdict.Value.Kind != (byte)WorldFederationWireFormat.ResponseKind.Ack)) {
            throw new IOException(verdict is null ? "remote authority closed during federation authentication" : DecodeRefusal(((WorldFederationWireFormat.ResponseKind)verdict.Value.Kind, verdict.Value.Body)));
        }
    }

    // One authenticated, persistent control lane per source namespace. SubmitIntent only updates this pump's
    // bounded latest-value table and returns to the local simulation immediately; the background lane pays connect
    // and authentication once, then preserves request/ack ordering without making rendering or the boot clock wait
    // on a network round trip. A key has one pending value, so a slow WAN coalesces intermediate stick samples rather
    // than building latency or an unbounded queue.
    private sealed class FederatedIntentPump : IDisposable {
        private readonly WorldRemoteAuthority m_owner;
        private readonly string m_sourceAuthority;
        private readonly ConcurrentDictionary<IntentKey, IntentSubmission> m_pending = new();
        private readonly ConcurrentDictionary<IntentKey, AcknowledgedIntent> m_acknowledged = new();
        private readonly SemaphoreSlim m_signal = new(initialCount: 0, maxCount: 1);
        private readonly CancellationTokenSource m_lifetime;
        private readonly Task m_worker;
        private int m_unavailable;

        public FederatedIntentPump(WorldRemoteAuthority owner, string sourceAuthority) {
            m_owner = owner;
            m_sourceAuthority = sourceAuthority;
            m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner.m_lifetime.Token);
            m_worker = Task.Run(function: () => RunAsync(ct: m_lifetime.Token));
        }

        public void Publish(WorldMobilityIdentity mobility, in IntentSubmission submission) {
            var key = new IntentKey(Mobility: mobility);
            if (m_acknowledged.TryGetValue(key: key, value: out var acknowledged) &&
                (acknowledged.Submission == submission) &&
                (Stopwatch.GetElapsedTime(startingTimestamp: acknowledged.Timestamp) < TimeSpan.FromSeconds(1))) {
                return;
            }
            m_pending[key] = submission;
            try {
                _ = m_signal.Release();
            } catch (SemaphoreFullException) {
                // One wake already covers every latest-value row in the bounded table.
            }
        }

        public void InvalidateAcknowledgement(WorldMobilityIdentity mobility) {
            var key = new IntentKey(Mobility: mobility);
            if (m_acknowledged.TryRemove(key: key, value: out var acknowledged)) {
                m_pending[key] = acknowledged.Submission;
            }
            try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
        }

        public void Retire(WorldMobilityIdentity mobility) {
            var key = new IntentKey(Mobility: mobility);
            _ = m_pending.TryRemove(key: key, value: out _);
            _ = m_acknowledged.TryRemove(key: key, value: out _);
        }

        public void Dispose() {
            m_lifetime.Cancel();
            try { m_worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            m_lifetime.Dispose();
            m_signal.Dispose();
        }

        private async Task RunAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                string? attemptedEndpoint = null;
                try {
                    await m_signal.WaitAsync(cancellationToken: ct).ConfigureAwait(false);
                    attemptedEndpoint = m_owner.Endpoint;
                    await RunConnectionAsync(ct: ct).ConfigureAwait(false);
                    _ = Interlocked.Exchange(location1: ref m_unavailable, value: 0);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch (Exception exception) when ((attemptedEndpoint is not null) && (exception is IOException or SocketException or OperationCanceledException) &&
                    !string.Equals(a: attemptedEndpoint, b: m_owner.Endpoint, comparisonType: StringComparison.Ordinal)) {
                    // The committed route moved while the old socket was in flight. The old authority is allowed to
                    // close that lane before our explicit handoff frame arrives; reconnect immediately to the new
                    // endpoint without misreporting a healthy authority change as an outage.
                    try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
                } catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) {
                    if (ct.IsCancellationRequested) {
                        return;
                    }
                    if (Interlocked.Exchange(location1: ref m_unavailable, value: 1) == 0) {
                        Console.Error.WriteLine(value: $"[world.authority unavailable: intent stream to '{m_owner.Endpoint}' is reconnecting ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")})]");
                    }
                    try {
                        await Task.Delay(delay: TimeSpan.FromMilliseconds(100), cancellationToken: ct).ConfigureAwait(false);
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    }
                    if (!m_pending.IsEmpty) {
                        try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
                    }
                }
            }
        }

        private async Task RunConnectionAsync(CancellationToken ct) {
            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(remoteEP: m_owner.m_endpoint, cancellationToken: ct).ConfigureAwait(false);
            var connectedEndpoint = m_owner.Endpoint;
            using var stream = client.GetStream();
            await WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: ct).ConfigureAwait(false);
            await m_owner.AuthenticateAsync(stream: stream, sourceAuthority: m_sourceAuthority, ct: ct).ConfigureAwait(false);
            await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.IntentStream, body: [], ct: ct).ConfigureAwait(false);
            var opening = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);
            if ((opening is null) || (opening.Value.Kind != (byte)WorldFederationWireFormat.ResponseKind.Ack)) {
                throw new IOException(opening is null ? "remote authority closed while opening intent stream" : DecodeRefusal(((WorldFederationWireFormat.ResponseKind)opening.Value.Kind, opening.Value.Body)));
            }

            while (!ct.IsCancellationRequested) {
                if (m_pending.IsEmpty) {
                    await m_signal.WaitAsync(cancellationToken: ct).ConfigureAwait(false);
                }

                foreach (var pair in m_pending.ToArray()) {
                    if (!string.Equals(a: connectedEndpoint, b: m_owner.Endpoint, comparisonType: StringComparison.Ordinal)) {
                        await HandoffAsync(stream: stream, ct: ct).ConfigureAwait(false);
                        return;
                    }
                    var sent = pair.Value;
                    var mobility = pair.Key.Mobility;
                    var body = WorldFederationWireFormat.EncodeIntent(sourceAuthority: m_sourceAuthority, mobility: in mobility, submission: in sent);
                    await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Intent, body: body, ct: ct).ConfigureAwait(false);
                    var response = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);
                    if ((response is null) || (response.Value.Kind != (byte)WorldFederationWireFormat.ResponseKind.Ack)) {
                        throw new IOException(response is null ? "remote authority closed its intent stream without a verdict" : DecodeRefusal(((WorldFederationWireFormat.ResponseKind)response.Value.Kind, response.Value.Body)));
                    }

                    if (!string.Equals(a: connectedEndpoint, b: m_owner.Endpoint, comparisonType: StringComparison.Ordinal)) {
                        await HandoffAsync(stream: stream, ct: ct).ConfigureAwait(false);
                        return;
                    }

                    if (m_pending.TryGetValue(key: pair.Key, value: out var current) && (current == sent)) {
                        m_acknowledged[pair.Key] = new AcknowledgedIntent(Submission: sent, Timestamp: Stopwatch.GetTimestamp());
                        _ = m_pending.TryRemove(key: pair.Key, value: out _);
                    }

                    // Covers the route revision moving between the pre-ack check and publication of the acknowledged
                    // row. Restore the exact sent state to pending before closing the older lane.
                    if (!string.Equals(a: connectedEndpoint, b: m_owner.Endpoint, comparisonType: StringComparison.Ordinal)) {
                        m_pending[pair.Key] = sent;
                        _ = m_acknowledged.TryRemove(key: pair.Key, value: out _);
                        await HandoffAsync(stream: stream, ct: ct).ConfigureAwait(false);
                        return;
                    }
                }
            }
        }

        private async Task HandoffAsync(NetworkStream stream, CancellationToken ct) {
            // This is an intentional route handoff, not a dropped client. Tell the older authority not to
            // synthesize a neutral release: the new lane will seed the same current held state.
            await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.IntentStreamHandoff, body: [], ct: ct).ConfigureAwait(false);
            var handoff = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);
            if ((handoff is null) || (handoff.Value.Kind != (byte)WorldFederationWireFormat.ResponseKind.Ack)) {
                throw new IOException(handoff is null ? "remote authority closed during intent stream handoff" : DecodeRefusal(((WorldFederationWireFormat.ResponseKind)handoff.Value.Kind, handoff.Value.Body)));
            }
            try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
        }

        private readonly record struct IntentKey(WorldMobilityIdentity Mobility);
        private readonly record struct AcknowledgedIntent(IntentSubmission Submission, long Timestamp);
    }
}
