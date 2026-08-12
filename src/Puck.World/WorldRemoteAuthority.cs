using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A committed body's immutable address on one remote authority hop.</summary>
internal readonly record struct WorldRemoteRouteCredential(int BodyIndex, string SourceAuthority, WorldMobilityIdentity Mobility);

/// <summary>The concerns that get their own ordered connection to one peer authority.</summary>
internal enum WorldFederationLane : byte {
    /// <summary>Reserve, commit, abort, acknowledge, status.</summary>
    Transaction,

    /// <summary>Route lookups and forwarded submissions for an already-committed traveler.</summary>
    Routed,
}

/// <summary>What a transfer step's answer is evidence of.</summary>
internal enum WorldTransferStep : byte {
    /// <summary>The destination answered; the step's verdict is the destination's own.</summary>
    Answered,

    /// <summary>The transport failed, so whether the destination applied this step is unknown. A commit that ends
    /// here is in doubt, never a refusal.</summary>
    Unreachable,
}

/// <summary>One federation response, or the named reason there is none. The lane never faults a caller's task: a
/// dead peer is a refusal with a name, so a simulation-thread caller always receives an answer it can act on.</summary>
internal readonly record struct WorldFederationAnswer(WorldFederationResponse Kind, byte[] Body, WorldWireFailure Failure) {
    public bool Ok => !Failure.IsRefusal;

    /// <summary>Narrates this answer as a refusal sentence.</summary>
    public string Describe() =>
        (Failure.IsRefusal ? Failure.ToString()
            : ((Kind == WorldFederationResponse.Refusal) ? Encoding.UTF8.GetString(bytes: Body) : $"unexpected federation response {Kind}"));

    public static WorldFederationAnswer Refused(WorldWireRefusal refusal, string detail) =>
        new(Kind: WorldFederationResponse.Refusal, Body: [], Failure: new WorldWireFailure(Refusal: refusal, Detail: detail));
}

/// <summary>The remote implementation of the authority contract used by transfer and continuous projection.</summary>
/// <remarks>
/// <para>Every request rides a persistent authenticated lane (<see cref="FederatedRequestLane"/>) keyed by source
/// authority namespace and concern: connect, hello, and challenge are paid once for the lane's lifetime, never once
/// per operation.</para>
/// <para>Every request answers. It is issued once, keyed so a repeat ask claims the same answer, and bounded by
/// <see cref="RoutedRequestDeadline"/>; a lane inside its unreachable backoff answers without waiting at all, which
/// is what keeps a dead neighbour from stalling the tick. A caller that could be told "not yet" would have to hold
/// state across ticks the adjacency scan is concurrently re-deriving, so no path here returns one.</para>
/// </remarks>
internal sealed class WorldRemoteAuthority : IDisposable {
    /// <summary>The ceiling on how long a routed submission or route lookup waits for its answer. This bounds
    /// transport lifecycle, never simulation state.</summary>
    private static readonly TimeSpan RoutedRequestDeadline = TimeSpan.FromSeconds(value: 10);

    /// <summary>How long a lane waits before retrying a connect that failed, before it calls the peer down.</summary>
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(value: 50);

    /// <summary>How long a lane that failed to reach its peer answers immediately with
    /// <see cref="WorldWireRefusal.LaneUnavailable"/> before trying to connect again.</summary>
    private static readonly TimeSpan LaneBackoff = TimeSpan.FromSeconds(value: 1);

    private IPEndPoint m_endpoint;
    private readonly CancellationTokenSource m_lifetime;
    // Written from the simulation thread as reservations commit and read from socket workers resolving a forwarded
    // submission, so the table itself must be concurrent even though every write comes from the commit path.
    private readonly ConcurrentDictionary<int, WorldRemoteRouteCredential> m_credentials = new();
    private readonly ConcurrentDictionary<string, FederatedIntentPump> m_intentPumps = new(comparer: StringComparer.Ordinal);
    // Keyed by (source namespace, concern). A lane is strictly ordered request-then-response, so everything sharing
    // one blocks behind whatever is in flight on it. Transfer transactions and routed traffic therefore get separate
    // lanes: a routed submission the destination answers slowly must not delay a reserve or a commit.
    private readonly ConcurrentDictionary<(string SourceAuthority, WorldFederationLane Lane), FederatedRequestLane> m_requestLanes = new();
    private readonly ConcurrentDictionary<TransferStepKey, Task<WorldFederationAnswer>> m_transferSteps = new();
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

    /// <summary>Resolves this transfer's reservation step.</summary>
    /// <param name="request">The reservation request.</param>
    /// <returns>The destination's verdict, or a named refusal when the lane could not deliver the step.</returns>
    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
        _ = TryResolveTransferStep(sourceAuthority: request.SourceAuthority, transferId: request.TransferId, kind: WorldFederationRequest.Reserve, body: () => WorldFederationCodec.EncodeReservation(request: request), answer: out var answer);

        if (answer.Kind != WorldFederationResponse.Reservation) {
            return WorldTransferReservationReply.Refused(reason: answer.Describe());
        }

        if (!WorldFederationCodec.TryDecodeReservationReply(body: answer.Body, reply: out var decoded, failure: out var failure) || (decoded is null)) {
            return WorldTransferReservationReply.Refused(reason: $"remote authority returned an undecodable reservation verdict — {failure}");
        }

        if (decoded.Accepted) {
            AdoptReservationCredentials(request: request, reply: decoded);
        }

        return decoded;
    }

    /// <summary>Resolves this transfer's commit step.</summary>
    public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out bool accepted, out string reason) {
        accepted = false;
        reason = string.Empty;

        _ = TryResolveTransferStep(sourceAuthority: sourceAuthority, transferId: transferId, kind: WorldFederationRequest.Commit, body: () => WorldFederationCodec.EncodeCommit(sourceAuthority: sourceAuthority, transferId: transferId, members: members), answer: out var answer);

        if (!answer.Ok) {
            reason = answer.Describe();

            return WorldTransferStep.Unreachable;
        }

        if ((answer.Kind != WorldFederationResponse.Commit) ||
            !WorldFederationCodec.TryDecodeCommitReply(body: answer.Body, accepted: out accepted, reason: out reason, failure: out _)) {
            accepted = false;
            reason = answer.Describe();
        }

        return WorldTransferStep.Answered;
    }

    /// <summary>Resolves this transfer's idempotent status.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="transferId">The transfer id.</param>
    /// <param name="status">The destination's verdict on success.</param>
    /// <returns><see langword="false"/> when the peer returned no usable status; the caller reconciles again later.</returns>
    public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
        status = WorldTransferStatus.Missing;

        _ = TryResolveTransferStep(sourceAuthority: sourceAuthority, transferId: transferId, kind: WorldFederationRequest.Status, body: () => WorldFederationCodec.EncodeTransferKey(sourceAuthority: sourceAuthority, transferId: transferId), answer: out var answer);

        if ((answer.Kind != WorldFederationResponse.Status) || (answer.Body.Length != 1) || !Enum.IsDefined(value: (WorldTransferStatus)answer.Body[0])) {
            return false;
        }

        status = (WorldTransferStatus)answer.Body[0];

        return true;
    }

    /// <summary>Releases a reservation. The request is posted to the lane and never waited on: an abort's only
    /// fallback is the destination's own bounded lease expiry.</summary>
    public void Abort(string sourceAuthority, ulong transferId) {
        ForgetTransferSteps(sourceAuthority: sourceAuthority, transferId: transferId);
        Post(sourceAuthority: sourceAuthority, kind: WorldFederationRequest.Abort, body: WorldFederationCodec.EncodeTransferKey(sourceAuthority: sourceAuthority, transferId: transferId));
    }

    /// <summary>Confirms the source consumed a committed transfer. Posted, never waited on.</summary>
    public void Acknowledge(string sourceAuthority, ulong transferId) {
        ForgetTransferSteps(sourceAuthority: sourceAuthority, transferId: transferId);
        Post(sourceAuthority: sourceAuthority, kind: WorldFederationRequest.AcknowledgeTransfer, body: WorldFederationCodec.EncodeTransferKey(sourceAuthority: sourceAuthority, transferId: transferId));
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
        foreach (var lane in m_requestLanes.Values) {
            lane.Dispose();
        }
        m_requestLanes.Clear();
        m_transferSteps.Clear();
    }

    internal bool TryCredential(int bodyIndex, out string sourceAuthority, out WorldMobilityIdentity mobility) {
        if (TryRouteCredential(bodyIndex: bodyIndex, credential: out var credential)) {
            sourceAuthority = credential.SourceAuthority;
            mobility = credential.Mobility;

            return true;
        }

        sourceAuthority = string.Empty;
        mobility = default;

        return false;
    }

    internal bool TryRouteCredential(int bodyIndex, out WorldRemoteRouteCredential credential) {
        if (m_submissionCredential is { } captured) {
            credential = captured;
            return true;
        }

        if (m_credentials.TryGetValue(key: bodyIndex, value: out credential)) {
            return true;
        }

        if (bodyIndex < 0) {
            // A body-agnostic submission (a document mutation, an undo) rides whichever committed credential this
            // authority holds; the lowest index keeps that choice stable across calls.
            var lowest = -1;

            foreach (var pair in m_credentials) {
                if ((lowest < 0) || (pair.Key < lowest)) {
                    lowest = pair.Key;
                    credential = pair.Value;
                }
            }

            if (lowest >= 0) {
                return true;
            }
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
        var answer = target.AwaitAnswer(sourceAuthority: credential.SourceAuthority, kind: WorldFederationRequest.Route, body: WorldFederationCodec.EncodeRouteCredential(sourceAuthority: credential.SourceAuthority, mobility: in mobility));

        if ((answer.Kind != WorldFederationResponse.Route) || !WorldFederationCodec.TryDecodeRoute(body: answer.Body, route: out route, failure: out _)) {
            reason = answer.Describe();
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
        var target = (m_submissionAuthority ?? this);
        var answer = target.AwaitAnswer(sourceAuthority: credential.SourceAuthority, kind: WorldFederationRequest.Submission, body: WorldFederationCodec.EncodeSubmission(sourceAuthority: credential.SourceAuthority, mobility: in mobility, frame: canonical));

        if (answer.Kind != WorldFederationResponse.Completion) {
            reason = answer.Describe();
            return false;
        }

        return TryReadCompletion(body: answer.Body, result: out result, reason: out reason);
    }

    internal WorldFederationAnswer AwaitAnswer(string sourceAuthority, WorldFederationRequest kind, byte[] body) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.AwaitAnswer(sourceAuthority: sourceAuthority, kind: kind, body: body);
        }

        var lane = LaneFor(sourceAuthority: sourceAuthority, kind: kind);

        if (!lane.IsAvailable) {
            return WorldFederationAnswer.Refused(refusal: WorldWireRefusal.LaneUnavailable, detail: $"the federation lane to '{Endpoint}' is reconnecting");
        }

        var task = lane.Enqueue(kind: kind, body: body);

        return (task.Wait(timeout: RoutedRequestDeadline)
            ? task.Result
            : WorldFederationAnswer.Refused(refusal: WorldWireRefusal.LaneUnavailable, detail: $"'{Endpoint}' did not answer {kind} within {RoutedRequestDeadline.TotalSeconds:0.#}s"));
    }

    private static bool TryReadCompletion(byte[] body, out WorldSubmissionResult? result, out string reason) {
        result = null;

        using var input = new MemoryStream(body, writable: false);
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

    private void AdoptReservationCredentials(WorldTransferReservationRequest request, WorldTransferReservationReply reply) {
        for (var ordinal = 0; (ordinal < reply.BodyIndices.Count) && (ordinal < request.Members.Count); ordinal++) {
            var bodyIndex = reply.BodyIndices[ordinal];

            if (m_credentials.TryGetValue(key: bodyIndex, value: out var previous) &&
                m_intentPumps.TryGetValue(key: previous.SourceAuthority, value: out var previousPump)) {
                previousPump.Retire(mobility: previous.Mobility);
            }

            if (request.Members[ordinal].Mobility is { } mobility) {
                m_credentials[bodyIndex] = new WorldRemoteRouteCredential(BodyIndex: bodyIndex, SourceAuthority: request.SourceAuthority, Mobility: mobility.Advance());
            }
        }
    }

    private bool TryResolveTransferStep(string sourceAuthority, ulong transferId, WorldFederationRequest kind, Func<byte[]> body, out WorldFederationAnswer answer) {
        answer = default;

        if (m_submissionAuthority is { } upstream) {
            return upstream.TryResolveTransferStep(sourceAuthority: sourceAuthority, transferId: transferId, kind: kind, body: body, answer: out answer);
        }

        var lane = LaneFor(sourceAuthority: sourceAuthority, kind: kind);

        if (!lane.IsAvailable) {
            // Already known unreachable. Answering here — without a socket attempt and without waiting — is what
            // keeps a closed edge costing the tick nothing while the neighbour is away.
            answer = WorldFederationAnswer.Refused(refusal: WorldWireRefusal.LaneUnavailable, detail: $"the federation lane to '{Endpoint}' is reconnecting");

            return true;
        }

        var key = new TransferStepKey(SourceAuthority: sourceAuthority, TransferId: transferId, Kind: kind);
        var task = m_transferSteps.GetOrAdd(key: key, valueFactory: _ => lane.Enqueue(kind: kind, body: body()));

        // A transfer step MUST always answer. The adjacency scan that produced this crossing re-fires on every tick
        // the traveler is still at the seam, so a caller told "not yet" leaves its transfer queued while the scan
        // mints a second crossing for the same seat — the traveler then arrives at the destination twice. A step
        // that ran out of time is an answered refusal, which the caller resolves once: terminal for a reservation,
        // in doubt for a commit.
        var answered = (task.IsCompleted || task.Wait(timeout: RoutedRequestDeadline));

        _ = m_transferSteps.TryRemove(key: key, value: out _);
        answer = ((answered && task.IsCompletedSuccessfully)
            ? task.Result
            : WorldFederationAnswer.Refused(refusal: WorldWireRefusal.LaneUnavailable, detail: $"'{Endpoint}' did not answer {kind} within {RoutedRequestDeadline.TotalSeconds:0.#}s"));

        return true;
    }

    private void ForgetTransferSteps(string sourceAuthority, ulong transferId) {
        foreach (var kind in new[] { WorldFederationRequest.Reserve, WorldFederationRequest.Commit, WorldFederationRequest.Status }) {
            _ = m_transferSteps.TryRemove(key: new TransferStepKey(SourceAuthority: sourceAuthority, TransferId: transferId, Kind: kind), value: out _);
        }
    }

    private void Post(string sourceAuthority, WorldFederationRequest kind, byte[] body) {
        if (m_submissionAuthority is { } upstream) {
            upstream.Post(sourceAuthority: sourceAuthority, kind: kind, body: body);

            return;
        }

        var lane = LaneFor(sourceAuthority: sourceAuthority, kind: kind);

        if (lane.IsAvailable) {
            _ = lane.Enqueue(kind: kind, body: body);
        }
    }

    private static WorldFederationLane LaneOf(WorldFederationRequest kind) =>
        ((kind is WorldFederationRequest.Route or WorldFederationRequest.Submission) ? WorldFederationLane.Routed : WorldFederationLane.Transaction);

    private FederatedRequestLane LaneFor(string sourceAuthority, WorldFederationRequest kind) =>
        m_requestLanes.GetOrAdd(key: (sourceAuthority, LaneOf(kind: kind)), valueFactory: key => new FederatedRequestLane(owner: this, sourceAuthority: key.SourceAuthority));

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
        await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: ct).ConfigureAwait(false);
        await AuthenticateAsync(stream: stream, sourceAuthority: m_observerAuthority, ct: ct).ConfigureAwait(false);
        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Observe, body: default, ct: ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested) {
            var frame = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: ct).ConfigureAwait(false);
            if (!frame.Ok) {
                return false;
            }

            switch ((WorldFederationResponse)frame.Kind) {
                case WorldFederationResponse.Definition: {
                        if (!WorldFederationCodec.TryDecodeDefinition(body: frame.Body, definition: out var definition, failure: out var definitionFailure) || (definition is null)) {
                            Console.Error.WriteLine(value: $"[world.projection: remote observer '{Endpoint}' refused a definition record ({definitionFailure})]");

                            return false;
                        }

                        Volatile.Write(ref m_definition, definition);
                        sink.DeliverDefinition(definition: definition);
                        break;
                    }
                case WorldFederationResponse.Snapshot: {
                        if (!WorldFederationCodec.TryDecodeSnapshot(body: frame.Body, snapshot: out var snapshot, failure: out var snapshotFailure)) {
                            Console.Error.WriteLine(value: $"[world.projection: remote observer '{Endpoint}' refused a snapshot record ({snapshotFailure})]");

                            return false;
                        }

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

    private async Task AuthenticateAsync(NetworkStream stream, string sourceAuthority, CancellationToken ct) {
        var challenge = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: ct).ConfigureAwait(false);

        if (!challenge.Ok || (challenge.Kind != (byte)WorldFederationResponse.Challenge) || (challenge.Body.Length != WorldFederationSecurity.ChallengeBytes)) {
            throw new IOException(DescribeHandshake(read: challenge, stage: "challenge"));
        }

        var proof = m_security.Prove(sourceAuthority: sourceAuthority, challenge: challenge.Body);
        await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Authenticate, body: WorldFederationCodec.EncodeAuthentication(sourceAuthority: sourceAuthority, proof: proof), ct: ct).ConfigureAwait(false);
        var verdict = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: ct).ConfigureAwait(false);

        if (!verdict.Ok || (verdict.Kind != (byte)WorldFederationResponse.Ack)) {
            throw new IOException(DescribeHandshake(read: verdict, stage: "authentication"));
        }
    }

    private static string DescribeHandshake(WorldWireFrameRead read, string stage) =>
        (read.Ok
            ? new WorldFederationAnswer(Kind: (WorldFederationResponse)read.Kind, Body: read.Body, Failure: default).Describe()
            : $"{WorldWireRefusal.HandshakeRefused}: federation {stage} — {read.Failure}");

    private readonly record struct TransferStepKey(string SourceAuthority, ulong TransferId, WorldFederationRequest Kind);

    private readonly record struct PendingRequest(WorldFederationRequest Kind, byte[] Body, TaskCompletionSource<WorldFederationAnswer> Completion);

    // One authenticated, persistent connection. Its hello and challenge/proof exchange are paid once for the lane's
    // lifetime; requests then ride it strictly in order, request-then-response, which is what lets the peer answer
    // without a correlation id on the wire — and is also why callers of one lane queue behind each other, so which
    // concerns share a lane is a latency decision (see m_requestLanes). A lane that could not reach its peer answers
    // every request for LaneBackoff without touching a socket.
    private sealed class FederatedRequestLane : IDisposable {
        private readonly WorldRemoteAuthority m_owner;
        private readonly string m_sourceAuthority;
        private readonly Channel<PendingRequest> m_queue = Channel.CreateUnbounded<PendingRequest>(options: new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource m_lifetime;
        private readonly Task m_worker;
        private TcpClient? m_client;
        private NetworkStream? m_stream;
        private string m_connectedEndpoint = string.Empty;
        private long m_unavailableUntil;
        private int m_unavailableNoted;

        public FederatedRequestLane(WorldRemoteAuthority owner, string sourceAuthority) {
            m_owner = owner;
            m_sourceAuthority = sourceAuthority;
            m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner.m_lifetime.Token);
            m_worker = Task.Run(function: () => RunAsync(ct: m_lifetime.Token));
        }

        /// <summary>Gets a value indicating whether the lane is outside its unreachable-peer backoff window.</summary>
        public bool IsAvailable => (Stopwatch.GetTimestamp() >= Interlocked.Read(location: ref m_unavailableUntil));

        public Task<WorldFederationAnswer> Enqueue(WorldFederationRequest kind, byte[] body) {
            var pending = new PendingRequest(Kind: kind, Body: body, Completion: new TaskCompletionSource<WorldFederationAnswer>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously));

            if (!m_queue.Writer.TryWrite(item: pending)) {
                _ = pending.Completion.TrySetResult(result: WorldFederationAnswer.Refused(refusal: WorldWireRefusal.LaneUnavailable, detail: $"the federation lane to '{m_owner.Endpoint}' is closed"));
            }

            return pending.Completion.Task;
        }

        public void Dispose() {
            m_lifetime.Cancel();
            _ = m_queue.Writer.TryComplete();

            try {
                m_worker.GetAwaiter().GetResult();
            } catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException) {
                // The lane is being torn down; its socket's last error is not a caller's answer.
            }

            Drop();
            m_lifetime.Dispose();
        }

        private async Task RunAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                PendingRequest request;

                try {
                    request = await m_queue.Reader.ReadAsync(cancellationToken: ct).ConfigureAwait(false);
                } catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException) {
                    break;
                }

                var answer = await ServeAsync(request: request, ct: ct).ConfigureAwait(false);

                _ = request.Completion.TrySetResult(result: answer);
            }

            while (m_queue.Reader.TryRead(item: out var abandoned)) {
                _ = abandoned.Completion.TrySetResult(result: WorldFederationAnswer.Refused(refusal: WorldWireRefusal.LaneUnavailable, detail: $"the federation lane to '{m_owner.Endpoint}' closed before the request was sent"));
            }
        }

        // Only a failure to CONNECT may take the lane out of service, and only after a retry — a listener whose
        // backlog was momentarily full is not an absent peer. A break on an ESTABLISHED connection reconnects and
        // re-sends once without entering backoff; it is evidence about one socket, never about the peer. Slowness
        // reaches nothing here: the worker has no read deadline, so a slow peer is a waiting caller, not a lane
        // state change. Backoff refuses every request in its window without touching a socket, so widening what
        // enters it withholds traffic a live neighbour would have answered.
        private async Task<WorldFederationAnswer> ServeAsync(PendingRequest request, CancellationToken ct) {
            var connectFailures = 0;

            for (var attempt = 0; (attempt < 3) && !ct.IsCancellationRequested; attempt++) {
                var hadConnection = (m_stream is not null);

                try {
                    await EnsureConnectedAsync(ct: ct).ConfigureAwait(false);
                } catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException) {
                    Drop();

                    if (ct.IsCancellationRequested) {
                        break;
                    }

                    if (++connectFailures >= 2) {
                        return Unreachable(exception: exception);
                    }

                    try {
                        await Task.Delay(delay: ConnectRetryDelay, cancellationToken: ct).ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        break;
                    }

                    continue;
                }

                try {
                    var stream = m_stream!;

                    await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: request.Kind, body: request.Body, ct: ct).ConfigureAwait(false);

                    var response = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: ct).ConfigureAwait(false);

                    if (!response.Ok) {
                        Drop();

                        if (hadConnection) {
                            continue;
                        }

                        // A connection this lane opened itself answered with something that is not a frame. That is
                        // the peer's answer, not a transport outage, so it is reported without taking the lane down.
                        return WorldFederationAnswer.Refused(refusal: response.Failure.Refusal, detail: $"'{m_owner.Endpoint}' answered {request.Kind} with {response.Failure}");
                    }

                    _ = Interlocked.Exchange(location1: ref m_unavailableNoted, value: 0);

                    return new WorldFederationAnswer(Kind: (WorldFederationResponse)response.Kind, Body: response.Body, Failure: default);
                } catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException) {
                    Drop();

                    if (ct.IsCancellationRequested) {
                        break;
                    }

                    if (!hadConnection) {
                        // The break happened on a connection this lane had just opened, so the peer is reachable but
                        // is not completing the exchange. Report it; do not take the lane out of service for it.
                        return WorldFederationAnswer.Refused(refusal: WorldWireRefusal.ConnectionClosed, detail: $"'{m_owner.Endpoint}' broke the {request.Kind} exchange — {exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
                    }
                }
            }

            return WorldFederationAnswer.Refused(refusal: WorldWireRefusal.LaneUnavailable, detail: $"the federation lane to '{m_owner.Endpoint}' is closed");
        }

        private WorldFederationAnswer Unreachable(Exception exception) {
            _ = Interlocked.Exchange(location1: ref m_unavailableUntil, value: (Stopwatch.GetTimestamp() + (long)(LaneBackoff.TotalSeconds * Stopwatch.Frequency)));

            if (Interlocked.Exchange(location1: ref m_unavailableNoted, value: 1) == 0) {
                Console.Error.WriteLine(value: $"[world.authority unavailable: federation lane to '{m_owner.Endpoint}' is reconnecting ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")})]");
            }

            return WorldFederationAnswer.Refused(refusal: WorldWireRefusal.LaneUnavailable, detail: $"'{m_owner.Endpoint}' is unreachable — {exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }

        private async Task EnsureConnectedAsync(CancellationToken ct) {
            if ((m_stream is not null) && string.Equals(a: m_connectedEndpoint, b: m_owner.Endpoint, comparisonType: StringComparison.Ordinal)) {
                return;
            }

            Drop();

            var client = new TcpClient { NoDelay = true };

            m_client = client;

            await client.ConnectAsync(remoteEP: m_owner.m_endpoint, cancellationToken: ct).ConfigureAwait(false);

            var stream = client.GetStream();

            m_stream = stream;
            m_connectedEndpoint = m_owner.Endpoint;

            await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: ct).ConfigureAwait(false);
            await m_owner.AuthenticateAsync(stream: stream, sourceAuthority: m_sourceAuthority, ct: ct).ConfigureAwait(false);
        }

        private void Drop() {
            m_stream?.Dispose();
            m_client?.Dispose();
            m_stream = null;
            m_client = null;
            m_connectedEndpoint = string.Empty;
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
            await WorldFederationCodec.WriteHelloAsync(stream: stream, ct: ct).ConfigureAwait(false);
            await m_owner.AuthenticateAsync(stream: stream, sourceAuthority: m_sourceAuthority, ct: ct).ConfigureAwait(false);
            await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.IntentStream, body: default, ct: ct).ConfigureAwait(false);
            var opening = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: ct).ConfigureAwait(false);
            if (!opening.Ok || (opening.Kind != (byte)WorldFederationResponse.Ack)) {
                throw new IOException(DescribeHandshake(read: opening, stage: "intent stream opening"));
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
                    var body = WorldFederationCodec.EncodeIntent(sourceAuthority: m_sourceAuthority, mobility: in mobility, submission: in sent);
                    await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.Intent, body: body, ct: ct).ConfigureAwait(false);
                    var response = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: ct).ConfigureAwait(false);
                    if (!response.Ok || (response.Kind != (byte)WorldFederationResponse.Ack)) {
                        throw new IOException(DescribeHandshake(read: response, stage: "intent stream update"));
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
            await WorldFederationCodec.WriteRequestAsync(stream: stream, kind: WorldFederationRequest.IntentStreamHandoff, body: default, ct: ct).ConfigureAwait(false);
            var handoff = await WorldFederationCodec.ReadResponseAsync(stream: stream, ct: ct).ConfigureAwait(false);
            if (!handoff.Ok || (handoff.Kind != (byte)WorldFederationResponse.Ack)) {
                throw new IOException(DescribeHandshake(read: handoff, stage: "intent stream handoff"));
            }
            try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
        }

        private readonly record struct IntentKey(WorldMobilityIdentity Mobility);
        private readonly record struct AcknowledgedIntent(IntentSubmission Submission, long Timestamp);
    }
}
