using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The remote implementation of the authority contract used by transfer and continuous projection.</summary>
internal sealed class WorldRemoteAuthority : IDisposable {
    private IPEndPoint m_endpoint;
    private readonly CancellationTokenSource m_lifetime = new();
    private readonly Dictionary<int, (string SourceAuthority, ulong TransferId, int Ordinal)> m_credentials = new();
    private readonly WorldFederatedServerLink m_link;
    private readonly WorldFederationSecurity m_security;
    private readonly string m_observerAuthority;
    private readonly WorldRemoteAuthority? m_submissionAuthority;
    private readonly int m_submissionBodyIndex;
    private int m_observedBodyIndex;
    private readonly Action<int>? m_routeChanged;
    private ulong m_lastRouteProbeTick;
    private WorldDefinition m_definition;

    public WorldRemoteAuthority(string endpoint, WorldDefinition placeholder, WorldFederationSecurity security, string observerAuthority, WorldRemoteAuthority? submissionAuthority = null, int submissionBodyIndex = -1, Action<int>? routeChanged = null) {
        if (!IPEndPoint.TryParse(endpoint, out var parsed)) {
            throw new FormatException($"host.authority '{endpoint}' is not a parseable IP endpoint");
        }

        m_endpoint = parsed;
        m_definition = placeholder;
        m_security = security ?? throw new ArgumentNullException(paramName: nameof(security));
        m_observerAuthority = observerAuthority;
        m_submissionAuthority = submissionAuthority;
        m_submissionBodyIndex = submissionBodyIndex;
        m_observedBodyIndex = submissionBodyIndex;
        m_routeChanged = routeChanged;
        m_link = new WorldFederatedServerLink(authority: this);
    }

    public string Endpoint => m_endpoint.ToString();
    public WorldDefinition Definition => Volatile.Read(ref m_definition);
    public IServerLink Link => m_link;

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
                m_credentials[reply.BodyIndices[ordinal]] = (request.SourceAuthority, request.TransferId, ordinal);
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

    public void Dispose() => m_lifetime.Cancel();

    internal bool TryCredential(int bodyIndex, out string sourceAuthority, out ulong transferId, out int ordinal) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.TryCredential(bodyIndex: m_submissionBodyIndex, sourceAuthority: out sourceAuthority, transferId: out transferId, ordinal: out ordinal);
        }

        if ((bodyIndex < 0) && (m_credentials.Count > 0)) {
            var first = m_credentials.OrderBy(pair => pair.Key).First().Value;
            sourceAuthority = first.SourceAuthority;
            transferId = first.TransferId;
            ordinal = first.Ordinal;
            return true;
        }
        if (m_credentials.TryGetValue(key: bodyIndex, value: out var credential)) {
            sourceAuthority = credential.SourceAuthority;
            transferId = credential.TransferId;
            ordinal = credential.Ordinal;
            return true;
        }
        sourceAuthority = string.Empty; transferId = 0; ordinal = -1; return false;
    }

    internal bool TryForwardIntent(int bodyIndex, in IntentSubmission submission, out string reason) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.TryForwardIntent(bodyIndex: m_submissionBodyIndex, submission: in submission, reason: out reason);
        }

        if (!TryCredential(bodyIndex: bodyIndex, sourceAuthority: out var sourceAuthority, transferId: out var transferId, ordinal: out var ordinal)) {
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }

        var stamped = submission with { EntityIndex = bodyIndex };
        var response = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Intent, body: WorldFederationWireFormat.EncodeIntent(sourceAuthority: sourceAuthority, transferId: transferId, ordinal: ordinal, submission: in stamped));
        if (response.Kind != WorldFederationWireFormat.ResponseKind.Ack) {
            reason = DecodeRefusal(response);
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal bool TryDescribeRoute(int bodyIndex, out string endpoint, out int routedBodyIndex, out string reason) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.TryDescribeRoute(bodyIndex: m_submissionBodyIndex, endpoint: out endpoint, routedBodyIndex: out routedBodyIndex, reason: out reason);
        }

        endpoint = string.Empty;
        routedBodyIndex = -1;
        if (!TryCredential(bodyIndex: bodyIndex, sourceAuthority: out var sourceAuthority, transferId: out var transferId, ordinal: out var ordinal)) {
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }

        var response = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Route, body: WorldFederationWireFormat.EncodeRouteCredential(sourceAuthority: sourceAuthority, transferId: transferId, ordinal: ordinal));
        if ((response.Kind != WorldFederationWireFormat.ResponseKind.Route) || !WorldFederationWireFormat.TryDecodeRoute(body: response.Body, endpoint: out endpoint, bodyIndex: out routedBodyIndex)) {
            reason = DecodeRefusal(response);
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal bool TryForwardSubmission(int bodyIndex, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.TryForwardSubmission(bodyIndex: m_submissionBodyIndex, payload: payload, result: out result, reason: out reason);
        }

        result = null;
        if (!TryCredential(bodyIndex: bodyIndex, sourceAuthority: out var sourceAuthority, transferId: out var transferId, ordinal: out var ordinal)) {
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }
        if (!WorldFrameCodec.TryEncode(payload: payload, frame: out var canonical, failure: out var failure)) {
            reason = $"forwarded submission could not be encoded — {failure.Detail}";
            return false;
        }

        var body = WorldFederationWireFormat.EncodeSubmission(sourceAuthority: sourceAuthority, transferId: transferId, ordinal: ordinal, frame: canonical);
        var response = RoundTrip(sourceAuthority: sourceAuthority, kind: WorldFederationWireFormat.RequestKind.Submission, body: body);
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
            var response = upstream.RoundTrip(sourceAuthority: sourceAuthority, kind: kind, body: body);
            _ = RefreshObservedRoute();
            return response;
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
                await ObserveSessionAsync(sink: sink, ct: ct).ConfigureAwait(false);
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

    private async Task ObserveSessionAsync(IClientSink sink, CancellationToken ct) {
            using var client = new TcpClient();
            client.NoDelay = true;
            var observedEndpoint = m_endpoint;
            await client.ConnectAsync(remoteEP: observedEndpoint, cancellationToken: ct).ConfigureAwait(false);
            using var stream = client.GetStream();
            await WorldFederationWireFormat.WriteHelloAsync(stream: stream, ct: ct).ConfigureAwait(false);
            await AuthenticateAsync(stream: stream, sourceAuthority: m_observerAuthority, ct: ct).ConfigureAwait(false);
            await WorldFederationWireFormat.WriteRequestAsync(stream: stream, kind: WorldFederationWireFormat.RequestKind.Observe, body: [], ct: ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested) {
                var frame = await WorldFederationWireFormat.ReadFrameAsync(stream: stream, ct: ct).ConfigureAwait(false);
                if (frame is null) {
                    return;
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
                            sink.DeliverSnapshot(snapshot: in snapshot);
                            if ((m_submissionAuthority is { } upstream) && ((snapshot.Tick - m_lastRouteProbeTick) >= 15UL)) {
                                m_lastRouteProbeTick = snapshot.Tick;
                                if (RefreshObservedRoute()) {
                                    return;
                                }
                            }
                            break;
                        }
                }
            }
    }

    private bool RefreshObservedRoute() {
        if ((m_submissionAuthority is not { } upstream) ||
            !upstream.TryDescribeRoute(bodyIndex: m_submissionBodyIndex, endpoint: out var endpoint, routedBodyIndex: out var bodyIndex, reason: out _) ||
            !IPEndPoint.TryParse(endpoint, out var routedEndpoint) ||
            (routedEndpoint.Equals(m_endpoint) && (bodyIndex == m_observedBodyIndex))) {
            return false;
        }

        m_endpoint = routedEndpoint;
        m_observedBodyIndex = bodyIndex;
        m_routeChanged?.Invoke(obj: bodyIndex);
        return true;
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
}
