using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>A committed body's immutable address on one remote authority hop.</summary>
public readonly record struct WorldRemoteRouteCredential(int BodyIndex, string SourceAuthority, WorldMobilityIdentity Mobility);
/// <summary>The forwarded-authority arm for a traveler whose current authority is reached over a socket.</summary>
/// <param name="authority">The remote authority holding the traveler.</param>
/// <param name="credential">The immutable route credential committed for it.</param>
public sealed class WorldRemoteForwardedAuthority(WorldRemoteAuthority authority, WorldRemoteRouteCredential credential) : IWorldForwardedAuthority {
    /// <inheritdoc/>
    public bool TryDescribeRoute(out WorldAuthorityRouteDescription route, out string reason) {
        var held = credential;

        return authority.TryDescribeRoute(
            credential: in held,
            reason: out reason,
            route: out route
        );
    }
    /// <inheritdoc/>
    public bool TryForwardIntent(in IntentSubmission submission, out string reason) {
        var held = credential;

        return authority.TryForwardIntent(
            credential: in held,
            reason: out reason,
            submission: in submission
        );
    }
    /// <inheritdoc/>
    public bool TryForwardSubmission(WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) {
        var held = credential;

        return authority.TryForwardSubmission(
            credential: in held,
            payload: payload,
            reason: out reason,
            result: out result
        );
    }
    /// <inheritdoc/>
    public void DescribeForCheckpoint(out string destinationAuthority, out WorldMobilityIdentity mobility) {
        destinationAuthority = authority.Authority;
        mobility = credential.Mobility;
    }
}

/// <summary>The concerns that get their own ordered connection to one peer authority.</summary>
internal enum WorldFederationLane : byte {
    /// <summary>Reserve, commit, abort, acknowledge, status.</summary>
    Transaction,

    /// <summary>Route lookups and forwarded submissions for an already-committed traveler.</summary>
    Routed,
}

/// <summary>What a transfer step's answer is evidence of.</summary>
public enum WorldTransferStep : byte {
    /// <summary>The destination answered; the step's verdict is the destination's own.</summary>
    Answered,

    /// <summary>The transport failed, so whether the destination applied this step is unknown. A commit that ends
    /// here is in doubt, never a refusal.</summary>
    Unreachable,
}
/// <summary>One federation response, or the named reason there is none. The lane never faults a caller's task: a
/// dead peer is a refusal with a name, so a simulation-thread caller always receives an answer it can act on.</summary>
public readonly record struct WorldFederationAnswer(WorldFederationResponse Kind, byte[] Body, WireFailure Failure) {
    public bool Ok => !Failure.IsRefusal;

    /// <summary>Narrates this answer as a refusal sentence.</summary>
    public string Describe() =>
        (Failure.IsRefusal
            ? Failure.ToString()
            : ((Kind == WorldFederationResponse.Refusal)
                ? Encoding.UTF8.GetString(bytes: Body)
                : $"unexpected federation response {Kind}"
        ));
    public static WorldFederationAnswer Refused(WireRefusal refusal, string detail) =>
        new(
            Kind: WorldFederationResponse.Refusal,
            Body: [],
            Failure: new WireFailure(
                Detail: detail,
                Refusal: refusal
            )
        );
}
/// <summary>The remote implementation of the authority contract used by transfer and continuous projection.</summary>
/// <remarks>
/// <para>Every request rides a persistent authenticated lane (<see cref="PersistentRequestLane{TRequestKind,TResponseKind}"/>) keyed by source
/// authority namespace and concern: connect, hello, and challenge are paid once for the lane's lifetime, never once
/// per operation.</para>
/// <para>Every request answers. It is issued once, keyed so a repeat ask claims the same answer, and bounded by
/// <see cref="RoutedRequestDeadline"/>; a lane inside its unreachable backoff answers without waiting at all, which
/// is what keeps a dead neighbour from stalling the tick. A caller that could be told "not yet" would have to hold
/// state across ticks the adjacency scan is concurrently re-deriving, so no path here returns one.</para>
/// </remarks>
public sealed class WorldRemoteAuthority : IDisposable {
    /// <summary>The ceiling on how long a routed submission or route lookup waits for its answer. This bounds
    /// transport lifecycle, never simulation state.</summary>
    private static readonly TimeSpan RoutedRequestDeadline = TimeSpan.FromSeconds(value: 10);
    /// <summary>How long a lane waits before retrying a connect that failed, before it calls the peer down.</summary>
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(value: 50);
    /// <summary>How long a lane that failed to reach its peer answers immediately with
    /// <see cref="WireRefusal.LaneUnavailable"/> before trying to connect again.</summary>
    private static readonly TimeSpan LaneBackoff = TimeSpan.FromSeconds(value: 1);
    // Written from the simulation thread as reservations commit and read from socket workers resolving a forwarded
    // submission, so the table itself must be concurrent even though every write comes from the commit path.
    private readonly ConcurrentDictionary<int, WorldRemoteRouteCredential> m_credentials = new();
    private readonly ConcurrentDictionary<string, FederatedIntentPump> m_intentPumps = new(comparer: StringComparer.Ordinal);
    // Keyed by (source namespace, concern). A lane is strictly ordered request-then-response, so everything sharing
    // one blocks behind whatever is in flight on it. Transfer transactions and routed traffic therefore get separate
    // lanes: a routed submission the destination answers slowly must not delay a reserve or a commit.
    private readonly ConcurrentDictionary<(string SourceAuthority, WorldFederationLane Lane), PersistentRequestLane<WorldFederationRequest, WorldFederationResponse>> m_requestLanes = new();
    private readonly ConcurrentDictionary<TransferStepKey, Task<WorldFederationAnswer>> m_transferSteps = new();
    private string m_authority = string.Empty;
    // Frames is the "nothing observed yet" value, so the first delivered document always narrates its tier once.
    private WorldDisclosureTier m_observedTier = WorldDisclosureTier.Frames;

    private readonly CancellationTokenSource m_lifetime;
    private readonly WorldFederatedServerLink m_link;
    private readonly string m_observerAuthority;
    private readonly Action<WorldAuthorityRouteDescription>? m_routeChanged;
    private readonly IAuthenticator m_security;
    private readonly WorldRemoteAuthority? m_submissionAuthority;
    private readonly WorldRemoteRouteCredential? m_submissionCredential;

    private WorldDefinition m_definition;
    private IPEndPoint m_endpoint;
    private long m_lastObservedTickBits;
    private WorldAuthorityRouteDescription? m_observedRoute;
    private int m_routeRevision;

    public WorldRemoteAuthority(string endpoint, WorldDefinition placeholder, IAuthenticator security, string observerAuthority, WorldRemoteAuthority? submissionAuthority = null, WorldRemoteRouteCredential? submissionCredential = null, WorldAuthorityRouteDescription? initialRoute = null, Action<WorldAuthorityRouteDescription>? routeChanged = null, CancellationToken applicationStopping = default) {
        if (!IPEndPoint.TryParse(
            result: out var parsed,
            s: endpoint
        )) {
            throw new FormatException(message: $"host.authority '{endpoint}' is not a parseable IP endpoint");
        }

        m_endpoint = parsed;
        m_definition = placeholder;
        m_security = (security ?? throw new ArgumentNullException(paramName: nameof(security)));
        m_observerAuthority = observerAuthority;
        m_submissionAuthority = submissionAuthority;
        m_submissionCredential = submissionCredential;
        if ((submissionAuthority is null) != (submissionCredential is null)) {
            throw new ArgumentException(message: "a routed observer requires both its submission authority and immutable route credential");
        }
        m_observedRoute = initialRoute;
        m_routeChanged = routeChanged;
        m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: applicationStopping);
        if (initialRoute is { } route) {
            if (!IPEndPoint.TryParse(
                route.Endpoint,
                out var initialEndpoint
            )) {
                throw new FormatException(message: $"route endpoint '{route.Endpoint}' is not a parseable IP endpoint");
            }
            m_endpoint = initialEndpoint;
            m_definition = route.Definition;
            m_authority = route.Entity.Authority;
            m_lastObservedTickBits = unchecked((long)route.Tick);
        }
        m_link = new WorldFederatedServerLink(authority: this);
    }

    public string Authority => Volatile.Read(location: ref m_authority);
    /// <summary>Gets a value indicating whether every established lane is outside its unreachable-peer backoff
    /// window. WALL-CLOCK transport lifecycle state (<see cref="PersistentRequestLane{TRequestKind,TResponseKind}.IsAvailable"/>),
    /// legitimate for a read-back to print and never for simulation to read — link liveness the sim acts on is
    /// tick-derived (<c>WorldEventFeed</c>'s link family). <see langword="true"/> when no lane has been opened yet:
    /// nothing has failed.</summary>
    public bool LanesAvailable {
        get {
            foreach (var lane in m_requestLanes) {
                if (!lane.Value.IsAvailable) {
                    return false;
                }
            }

            return true;
        }
    }
    public WorldDefinition Definition => Volatile.Read(location: ref m_definition);
    public string Endpoint => Volatile.Read(location: ref m_endpoint).ToString();
    public IServerLink Link => m_link;
    public ulong NextInputTick => (unchecked((ulong)Interlocked.Read(location: ref m_lastObservedTickBits)) + 1UL);

    public WorldFederationAnswer AwaitAnswer(string sourceAuthority, WorldFederationRequest kind, byte[] body) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.AwaitAnswer(
                body: body,
                kind: kind,
                sourceAuthority: sourceAuthority
            );
        }

        var lane = LaneFor(
            kind: kind,
            sourceAuthority: sourceAuthority
        );

        if (!lane.IsAvailable) {
            return WorldFederationAnswer.Refused(
                refusal: WireRefusal.LaneUnavailable,
                detail: $"the federation lane to '{Endpoint}' is reconnecting"
            );
        }

        var task = EnqueueAnswerAsync(
            body: body,
            kind: kind,
            lane: lane
        );

        return (task.Wait(timeout: RoutedRequestDeadline)
            ? task.Result
            : WorldFederationAnswer.Refused(
                refusal: WireRefusal.LaneUnavailable,
                detail: $"'{Endpoint}' did not answer {kind} within {RoutedRequestDeadline.TotalSeconds:0.#}s"
            )
        );
    }
    public bool TryCredential(int bodyIndex, out string sourceAuthority, out WorldMobilityIdentity mobility) {
        if (TryRouteCredential(
            bodyIndex: bodyIndex,
            credential: out var credential
        )) {
            sourceAuthority = credential.SourceAuthority;
            mobility = credential.Mobility;

            return true;
        }

        sourceAuthority = string.Empty;
        mobility = default;

        return false;
    }
    public bool TryDescribeRoute(int bodyIndex, out WorldAuthorityRouteDescription route, out string reason) {
        if (!TryRouteCredential(
            bodyIndex: bodyIndex,
            credential: out var credential
        )) {
            route = default;
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }

        return TryDescribeRoute(
            credential: in credential,
            reason: out reason,
            route: out route
        );
    }

    internal bool TryDescribeRoute(in WorldRemoteRouteCredential credential, out WorldAuthorityRouteDescription route, out string reason) {
        route = default;
        var target = (m_submissionAuthority ?? this);
        var mobility = credential.Mobility;
        var answer = target.AwaitAnswer(
            sourceAuthority: credential.SourceAuthority,
            kind: WorldFederationRequest.Route,
            body: WorldFederationCodec.EncodeRouteCredential(
                sourceAuthority: credential.SourceAuthority,
                mobility: in mobility
            )
        );

        if (
            (answer.Kind != WorldFederationResponse.Route) ||
            !WorldFederationCodec.TryDecodeRoute(
            body: answer.Body,
            route: out route,
            failure: out _
        )
        ) {
            reason = answer.Describe();
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool TryForwardIntent(int bodyIndex, in IntentSubmission submission, out string reason) {
        if (!TryRouteCredential(
            bodyIndex: bodyIndex,
            credential: out var credential
        )) {
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }

        return TryForwardIntent(
            credential: in credential,
            reason: out reason,
            submission: in submission
        );
    }

    internal bool TryForwardIntent(in WorldRemoteRouteCredential credential, in IntentSubmission submission, out string reason) {
        var target = (m_submissionAuthority ?? this);
        var stamped = submission with { EntityIndex = credential.BodyIndex };
        var pump = target.m_intentPumps.GetOrAdd(
            key: credential.SourceAuthority,
            valueFactory: authority => new FederatedIntentPump(
                owner: target,
                sourceAuthority: authority
            )
        );

        pump.Publish(
            mobility: credential.Mobility,
            submission: in stamped
        );

        reason = string.Empty;
        return true;
    }
    internal bool TryForwardSubmission(int bodyIndex, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) {
        if (!TryRouteCredential(
            bodyIndex: bodyIndex,
            credential: out var credential
        )) {
            result = null;
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }

        return TryForwardSubmission(
            credential: in credential,
            payload: payload,
            reason: out reason,
            result: out result
        );
    }
    internal bool TryForwardSubmission(in WorldRemoteRouteCredential credential, WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) {
        result = null;
        if (!Puck.World.Protocol.WorldFrameCodec.TryEncode(
            failure: out var failure,
            frame: out var canonical,
            payload: payload
        )) {
            reason = $"forwarded submission could not be encoded — {failure.Detail}";
            return false;
        }

        var mobility = credential.Mobility;
        var target = (m_submissionAuthority ?? this);
        var answer = target.AwaitAnswer(
            sourceAuthority: credential.SourceAuthority,
            kind: WorldFederationRequest.Submission,
            body: WorldFederationCodec.EncodeSubmission(
                sourceAuthority: credential.SourceAuthority,
                mobility: in mobility,
                frame: canonical
            )
        );

        if (answer.Kind != WorldFederationResponse.Completion) {
            reason = answer.Describe();
            return false;
        }

        return TryReadCompletion(
            body: answer.Body,
            result: out result,
            reason: out reason
        );
    }

    public bool TryRouteCredential(int bodyIndex, out WorldRemoteRouteCredential credential) {
        if (m_submissionCredential is { } captured) {
            credential = captured;
            return true;
        }

        if (m_credentials.TryGetValue(
            key: bodyIndex,
            value: out credential
        )) {
            return true;
        }

        if (bodyIndex < 0) {
            // A body-agnostic submission (a document mutation, an undo) rides whichever committed credential this
            // authority holds; the lowest index keeps that choice stable across calls.
            var lowest = -1;

            foreach (var pair in m_credentials) {
                if (
                    (lowest < 0) ||
                    (pair.Key < lowest)
                ) {
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

    private void AdoptReservationCredentials(WorldTransferReservationRequest request, WorldTransferReservationReply reply) {
        for (var ordinal = 0; ((ordinal < reply.BodyIndices.Count) && (ordinal < request.Members.Count)); ordinal++) {
            var bodyIndex = reply.BodyIndices[ordinal];

            if (
                m_credentials.TryGetValue(
                key: bodyIndex,
                value: out var previous
            ) &&
                m_intentPumps.TryGetValue(
                key: previous.SourceAuthority,
                value: out var previousPump
            )
            ) {
                previousPump.Retire(mobility: previous.Mobility);
            }

            if (request.Members[ordinal].Mobility is { } mobility) {
                m_credentials[bodyIndex] = new WorldRemoteRouteCredential(
                    BodyIndex: bodyIndex,
                    SourceAuthority: request.SourceAuthority,
                    Mobility: mobility.Advance()
                );
            }
        }
    }
    // Reads m_endpoint exactly once, so the endpoint a lane dials and the description it records for that same
    // connect always name the same route republish generation.
    private LaneRoute CurrentRoute() {
        var endpoint = Volatile.Read(location: ref m_endpoint);

        return new LaneRoute(
            Endpoint: endpoint,
            Description: endpoint.ToString()
        );
    }
    private static string DescribeHandshake(WireFrameRead read, string stage) =>
        (read.Ok
            ? new WorldFederationAnswer(
                Kind: ((WorldFederationResponse)read.Kind),
                Body: read.Body,
                Failure: default
            ).Describe()
            : $"{WireRefusal.HandshakeRefused}: federation {stage} — {read.Failure}"
        );
    private static async Task<WorldFederationAnswer> EnqueueAnswerAsync(PersistentRequestLane<WorldFederationRequest, WorldFederationResponse> lane, WorldFederationRequest kind, byte[] body) {
        var response = await lane.Enqueue(
            body: body,
            kind: kind
        ).ConfigureAwait(continueOnCapturedContext: false);

        return new WorldFederationAnswer(
            Kind: response.Kind,
            Body: response.Body,
            Failure: response.Failure
        );
    }
    private void ForgetTransferSteps(string sourceAuthority, ulong transferId) {
        foreach (var kind in new[] { WorldFederationRequest.Reserve, WorldFederationRequest.Commit, WorldFederationRequest.Status }) {
            _ = m_transferSteps.TryRemove(
                key: new TransferStepKey(
                    Kind: kind,
                    SourceAuthority: sourceAuthority,
                    TransferId: transferId
                ),
                value: out _
            );
        }
    }
    private void InvalidateAcknowledgement(in WorldRemoteRouteCredential credential) {
        if ((m_submissionAuthority ?? this).m_intentPumps.TryGetValue(
            key: credential.SourceAuthority,
            value: out var pump
        )) {
            pump.InvalidateAcknowledgement(mobility: credential.Mobility);
        }
    }
    private PersistentRequestLane<WorldFederationRequest, WorldFederationResponse> LaneFor(string sourceAuthority, WorldFederationRequest kind) =>
        m_requestLanes.GetOrAdd(
            key: (sourceAuthority, LaneOf(kind: kind)),
            valueFactory: key => new PersistentRequestLane<WorldFederationRequest, WorldFederationResponse>(
                connectRetryDelay: ConnectRetryDelay,
                lifetime: m_lifetime.Token,
                onUnavailable: exception => Console.Error.WriteLine(value: $"[world.authority unavailable: federation lane to '{Endpoint}' is reconnecting ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")})]"),
                protocol: new WorldFederationLaneProtocol(owner: this),
                route: CurrentRoute,
                sourceAuthority: key.SourceAuthority,
                unavailableBackoff: LaneBackoff
            )
        );
    private static WorldFederationLane LaneOf(WorldFederationRequest kind) =>
        ((kind is WorldFederationRequest.Route or WorldFederationRequest.Submission)
            ? WorldFederationLane.Routed
            : WorldFederationLane.Transaction
        );
    private async Task<bool> ObserveSessionAsync(IClientSink sink, CancellationToken ct) {
        using var client = new TcpClient();

        client.NoDelay = true;
        var observedEndpoint = Volatile.Read(location: ref m_endpoint);
        var observedRouteRevision = Volatile.Read(location: ref m_routeRevision);

        await client.ConnectAsync(
            cancellationToken: ct,
            remoteEP: observedEndpoint
        ).ConfigureAwait(continueOnCapturedContext: false);
        using var stream = client.GetStream();

        await WorldFederationCodec.WriteHelloAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
        await AuthenticateAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
        await WorldFederationCodec.WriteRequestAsync(
            body: default,
            ct: ct,
            kind: WorldFederationRequest.Observe,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        while (!ct.IsCancellationRequested) {
            var frame = await WorldFederationCodec.ReadResponseAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!frame.Ok) {
                return false;
            }

            switch ((WorldFederationResponse)frame.Kind) {
                case WorldFederationResponse.Definition: {
                        if (
                            !WorldFederationCodec.TryDecodeDocument(
                            body: frame.Body,
                            definition: out var definition,
                            tier: out var definitionTier,
                            failure: out var definitionFailure
                        ) ||
                            (definition is null)
                        ) {
                            Console.Error.WriteLine(value: $"[world.projection: remote observer '{Endpoint}' refused a definition record ({definitionFailure})]");

                            return false;
                        }

                        if (definitionTier != m_observedTier) {
                            m_observedTier = definitionTier;
                            Console.Error.WriteLine(value: $"[world.projection: remote observer '{Endpoint}' receives documents at tier {definitionTier}]");
                        }

                        Volatile.Write(
                            location: ref m_definition,
                            value: definition
                        );
                        sink.DeliverDefinition(definition: definition);
                        break;
                    }
                case WorldFederationResponse.Snapshot: {
                        if (!WorldFederationCodec.TryDecodeSnapshot(
                            body: frame.Body,
                            snapshot: out var snapshot,
                            failure: out var snapshotFailure
                        )) {
                            Console.Error.WriteLine(value: $"[world.projection: remote observer '{Endpoint}' refused a snapshot record ({snapshotFailure})]");

                            return false;
                        }

                        var containsObservedEntity = SnapshotContainsObservedEntity(snapshot: in snapshot);

                        if (
                            (Volatile.Read(location: ref m_routeRevision) != observedRouteRevision) ||
                            (!containsObservedEntity && RefreshObservedRoute())
                        ) {
                            // The route callback seeded the new authority's committed image. Publishing this
                            // old authority's missing-body snapshot first would create an avoidable inactive
                            // frame between two committed writers—the camera hitch the route seed exists to
                            // eliminate. Reconnect directly to the new head instead.
                            return true;
                        }
                        _ = Interlocked.Exchange(
                            location1: ref m_lastObservedTickBits,
                            value: unchecked((long)snapshot.Tick)
                        );
                        Volatile.Write(
                            location: ref m_authority,
                            value: snapshot.Authority
                        );
                        sink.DeliverSnapshot(snapshot: in snapshot);
                        break;
                    }
            }
        }
        return false;
    }
    // Authority processes may start in any order. An adjacency is durable topology, so its observation channel
    // cannot become permanently CLOSED merely because the neighbour had not bound its socket on the first tick.
    // Reconnect the same held lease until its owner releases it; the mirror keeps the last delivered revision while
    // disconnected and the authored unavailable policy remains the crossing-side safety net.
    private async Task ObserveUntilCancelledAsync(IClientSink sink, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                if (await ObserveSessionAsync(
                    ct: ct,
                    sink: sink
                ).ConfigureAwait(continueOnCapturedContext: false)) {
                    continue;
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception exception) when ((exception is IOException or SocketException or OperationCanceledException)) {
                Console.Error.WriteLine(value: $"[world.projection: remote observer '{Endpoint}' unavailable ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}); retrying]");
            }

            try {
                await Task.Delay(
                    delay: TimeSpan.FromMilliseconds(milliseconds: 250),
                    cancellationToken: ct
                ).ConfigureAwait(continueOnCapturedContext: false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            }
        }
    }
    private void Post(string sourceAuthority, WorldFederationRequest kind, byte[] body) {
        if (m_submissionAuthority is { } upstream) {
            upstream.Post(
                body: body,
                kind: kind,
                sourceAuthority: sourceAuthority
            );

            return;
        }

        var lane = LaneFor(
            kind: kind,
            sourceAuthority: sourceAuthority
        );

        if (lane.IsAvailable) {
            _ = EnqueueAnswerAsync(
                body: body,
                kind: kind,
                lane: lane
            );
        }
    }
    private bool RefreshObservedRoute() {
        if (
            (m_submissionAuthority is null) ||
            (m_submissionCredential is not { } credential) ||
            !TryDescribeRoute(
            credential: in credential,
            reason: out _,
            route: out var route
        ) ||
            !IPEndPoint.TryParse(
            route.Endpoint,
            out var routedEndpoint
        )
        ) {
            return false;
        }

        if (
            (m_observedRoute is { } observed) &&
            string.Equals(
            a: observed.Endpoint,
            b: route.Endpoint,
            comparisonType: StringComparison.Ordinal
        ) &&
            (observed.Entity == route.Entity)
        ) {
            m_observedRoute = route;
            return false;
        }

        Volatile.Write(
            location: ref m_endpoint,
            value: routedEndpoint
        );
        m_observedRoute = route;
        InvalidateAcknowledgement(credential: in credential);
        Volatile.Write(
            location: ref m_definition,
            value: route.Definition
        );
        Volatile.Write(
            location: ref m_authority,
            value: route.Entity.Authority
        );
        _ = Interlocked.Exchange(
            location1: ref m_lastObservedTickBits,
            value: unchecked((long)route.Tick)
        );
        _ = Interlocked.Increment(location: ref m_routeRevision);
        m_routeChanged?.Invoke(obj: route);
        return true;
    }
    private bool SnapshotContainsObservedEntity(in WorldSnapshot snapshot) {
        var observedEntity = m_observedRoute?.Entity;
        var bodyIndex = (observedEntity?.Index ?? (m_submissionCredential?.BodyIndex ?? -1));

        foreach (ref readonly var entry in snapshot.Entries.Span) {
            // A population slot may be reused in the same snapshot that the traveler leaves. Index+active alone
            // would then mistake the replacement occupant for the traveler and suppress the route refresh forever,
            // leaving control/camera attached to the wrong body. A durable entity address is authority/index/
            // generation; use all three whenever the committed route supplied them.
            if (
                (entry.Index == bodyIndex) &&
                entry.Active &&
                ((observedEntity is null) || ((entry.Generation == observedEntity.Value.Generation) &&
                    string.Equals(
                a: snapshot.Authority,
                b: observedEntity.Value.Authority,
                comparisonType: StringComparison.Ordinal
            )))
            ) {
                return true;
            }
        }
        return false;
    }
    private static bool TryReadCompletion(byte[] body, out WorldSubmissionResult? result, out string reason) {
        result = null;

        using var input = new MemoryStream(
            body,
            writable: false
        );
        var completion = WorldTcpWireFormat.TryReadDownstreamAsync(
            ct: default,
            stream: input
        ).GetAwaiter().GetResult();

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
                    if (frame.Body.Length < ((sizeof(byte) + sizeof(int)) + sizeof(ushort))) {
                        reason = "forwarded authority returned a truncated session completion";
                        return false;
                    }
                    var offset = (sizeof(byte) + sizeof(int));
                    var sessionReason = WorldTcpWireFormat.ReadLengthPrefixedString(
                        body: frame.Body,
                        offset: ref offset,
                        ok: out var sessionOk
                    );

                    if (!sessionOk) {
                        reason = "forwarded authority returned a truncated session completion";
                        return false;
                    }

                    result = new WorldSubmissionResult.Session(Reply: new SessionReply(
                        Accepted: (frame.Body[0] != 0),
                        AssignedIndex: BinaryPrimitives.ReadInt32LittleEndian(source: frame.Body.AsSpan(start: sizeof(byte))),
                        RosterEcho: string.Empty,
                        Reason: sessionReason
                    ));
                    reason = string.Empty;
                    return true;
                }
            case WorldTcpWireFormat.DownstreamKind.Query: {
                    if (frame.Body.Length < (sizeof(byte) + sizeof(ushort))) {
                        reason = "forwarded authority returned a truncated query completion";
                        return false;
                    }
                    var offset = sizeof(byte);
                    var queryText = WorldTcpWireFormat.ReadLengthPrefixedString(
                        body: frame.Body,
                        offset: ref offset,
                        ok: out var queryOk
                    );

                    if (!queryOk) {
                        reason = "forwarded authority returned a truncated query completion";
                        return false;
                    }

                    result = new WorldSubmissionResult.Query(Answer: new QueryAnswer(
                        Text: queryText,
                        Refused: (frame.Body[0] != 0)
                    ));
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
    private bool TryResolveTransferStep(string sourceAuthority, ulong transferId, WorldFederationRequest kind, Func<byte[]> body, out WorldFederationAnswer answer) {
        answer = default;

        if (m_submissionAuthority is { } upstream) {
            return upstream.TryResolveTransferStep(
                answer: out answer,
                body: body,
                kind: kind,
                sourceAuthority: sourceAuthority,
                transferId: transferId
            );
        }

        var lane = LaneFor(
            kind: kind,
            sourceAuthority: sourceAuthority
        );

        if (!lane.IsAvailable) {
            // Already known unreachable. Answering here — without a socket attempt and without waiting — is what
            // keeps a closed edge costing the tick nothing while the neighbour is away.
            answer = WorldFederationAnswer.Refused(
                refusal: WireRefusal.LaneUnavailable,
                detail: $"the federation lane to '{Endpoint}' is reconnecting"
            );

            return true;
        }

        var key = new TransferStepKey(
            Kind: kind,
            SourceAuthority: sourceAuthority,
            TransferId: transferId
        );
        var task = m_transferSteps.GetOrAdd(
            key: key,
            valueFactory: _ => EnqueueAnswerAsync(
                kind: kind,
                body: body(),
                lane: lane
            )
        );

        // A transfer step MUST always answer. The adjacency scan that produced this crossing re-fires on every tick
        // the traveler is still at the seam, so a caller told "not yet" leaves its transfer queued while the scan
        // mints a second crossing for the same seat — the traveler then arrives at the destination twice. A step
        // that ran out of time is an answered refusal, which the caller resolves once: terminal for a reservation,
        // in doubt for a commit.
        var answered = (task.IsCompleted || task.Wait(timeout: RoutedRequestDeadline));

        _ = m_transferSteps.TryRemove(
            key: key,
            value: out _
        );
        answer = ((answered && task.IsCompletedSuccessfully)
            ? task.Result
            : WorldFederationAnswer.Refused(
                refusal: WireRefusal.LaneUnavailable,
                detail: $"'{Endpoint}' did not answer {kind} within {RoutedRequestDeadline.TotalSeconds:0.#}s"
            )
        );

        return true;
    }

    /// <summary>Releases a reservation. The request is posted to the lane and never waited on: an abort's only
    /// fallback is the destination's own bounded lease expiry.</summary>
    public void Abort(string sourceAuthority, ulong transferId) {
        ForgetTransferSteps(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
        Post(
            sourceAuthority: sourceAuthority,
            kind: WorldFederationRequest.Abort,
            body: WorldFederationCodec.EncodeTransferKey(
                sourceAuthority: sourceAuthority,
                transferId: transferId
            )
        );
    }
    /// <summary>Confirms the source consumed a committed transfer. Posted, never waited on.</summary>
    public void Acknowledge(string sourceAuthority, ulong transferId) {
        ForgetTransferSteps(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
        Post(
            sourceAuthority: sourceAuthority,
            kind: WorldFederationRequest.AcknowledgeTransfer,
            body: WorldFederationCodec.EncodeTransferKey(
                sourceAuthority: sourceAuthority,
                transferId: transferId
            )
        );
    }
    public IDisposable AttachSink(IClientSink sink) {
        ArgumentNullException.ThrowIfNull(sink);
        var lease = CancellationTokenSource.CreateLinkedTokenSource(token: m_lifetime.Token);

        _ = Task.Run(function: () => ObserveUntilCancelledAsync(
            sink: sink,
            ct: lease.Token
        ));
        return lease;
    }
    /// <summary>Runs the challenge/proof exchange over an already Hello'd connection: reads the server's challenge,
    /// proves this instance's own configured identity against it, and confirms the resulting Ack. Asserts no
    /// namespace alongside the proof — see <see cref="IAuthenticator"/>'s own remarks.</summary>
    /// <param name="stream">The connection stream.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="IOException">The peer's challenge or verdict frame is malformed or refused.</exception>
    public async Task AuthenticateAsync(Stream stream, CancellationToken ct) {
        var challenge = await WorldFederationCodec.ReadResponseAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            !challenge.Ok ||
            (challenge.Kind != ((byte)WorldFederationResponse.Challenge)) ||
            (challenge.Body.Length != m_security.ChallengeBytes)
        ) {
            throw new IOException(message: DescribeHandshake(
                read: challenge,
                stage: "challenge"
            ));
        }

        var proof = m_security.Prove(challenge: challenge.Body);

        await WorldFederationCodec.WriteRequestAsync(
            stream: stream,
            kind: WorldFederationRequest.Authenticate,
            body: WorldFederationCodec.EncodeAuthentication(proof: proof),
            ct: ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        var verdict = await WorldFederationCodec.ReadResponseAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            !verdict.Ok ||
            (verdict.Kind != ((byte)WorldFederationResponse.Ack))
        ) {
            throw new IOException(message: DescribeHandshake(
                read: verdict,
                stage: "authentication"
            ));
        }
    }
    /// <summary>Resolves this transfer's commit step.</summary>
    public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out bool accepted, out string reason) {
        accepted = false;
        reason = string.Empty;

        _ = TryResolveTransferStep(
            sourceAuthority: sourceAuthority,
            transferId: transferId,
            kind: WorldFederationRequest.Commit,
            body: () => WorldFederationCodec.EncodeCommit(
                members: members,
                sourceAuthority: sourceAuthority,
                transferId: transferId
            ),
            answer: out var answer
        );

        if (!answer.Ok) {
            reason = answer.Describe();

            return WorldTransferStep.Unreachable;
        }

        if (
            (answer.Kind != WorldFederationResponse.Commit) ||
            !WorldFederationCodec.TryDecodeCommitReply(
            body: answer.Body,
            accepted: out accepted,
            reason: out reason,
            failure: out _
        )
        ) {
            accepted = false;
            reason = answer.Describe();
        }

        return WorldTransferStep.Answered;
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
    /// <summary>Resolves this transfer's reservation step.</summary>
    /// <param name="request">The reservation request.</param>
    /// <returns>The destination's verdict, or a named refusal when the lane could not deliver the step.</returns>
    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
        _ = TryResolveTransferStep(
            sourceAuthority: request.SourceAuthority,
            transferId: request.TransferId,
            kind: WorldFederationRequest.Reserve,
            body: () => WorldFederationCodec.EncodeReservation(request: request),
            answer: out var answer
        );

        if (answer.Kind != WorldFederationResponse.Reservation) {
            return WorldTransferReservationReply.Refused(reason: answer.Describe());
        }

        if (
            !WorldFederationCodec.TryDecodeReservationReply(
            body: answer.Body,
            reply: out var decoded,
            failure: out var failure
        ) ||
            (decoded is null)
        ) {
            return WorldTransferReservationReply.Refused(reason: $"remote authority returned an undecodable reservation verdict — {failure}");
        }

        if (decoded.Accepted) {
            AdoptReservationCredentials(
                reply: decoded,
                request: request
            );
        }

        return decoded;
    }
    /// <summary>Resolves this transfer's idempotent status.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="transferId">The transfer id.</param>
    /// <param name="status">The destination's verdict on success.</param>
    /// <returns><see langword="false"/> when the peer returned no usable status; the caller reconciles again later.</returns>
    public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
        status = WorldTransferStatus.Missing;

        _ = TryResolveTransferStep(
            sourceAuthority: sourceAuthority,
            transferId: transferId,
            kind: WorldFederationRequest.Status,
            body: () => WorldFederationCodec.EncodeTransferKey(
                sourceAuthority: sourceAuthority,
                transferId: transferId
            ),
            answer: out var answer
        );

        if (
            (answer.Kind != WorldFederationResponse.Status) ||
            (answer.Body.Length != 1) ||
            !Enum.IsDefined(value: ((WorldTransferStatus)answer.Body[0]))
        ) {
            return false;
        }

        status = ((WorldTransferStatus)answer.Body[0]);

        return true;
    }

    private readonly record struct TransferStepKey(string SourceAuthority, ulong TransferId, WorldFederationRequest Kind);
    // One authenticated, persistent control lane per source namespace. SubmitIntent only updates this pump's
    // bounded latest-value table and returns to the local simulation immediately; the background lane pays connect
    // and authentication once, then preserves request/ack ordering without making rendering or the boot clock wait
    // on a network round trip. A key has one pending value, so a slow WAN coalesces intermediate stick samples rather
    // than building latency or an unbounded queue.
    private sealed class FederatedIntentPump : IDisposable {
        private readonly CancellationTokenSource m_lifetime;
        private readonly WorldRemoteAuthority m_owner;
        private readonly string m_sourceAuthority;
        private readonly Task m_worker;

        private int m_unavailable;

        private readonly ConcurrentDictionary<IntentKey, IntentSubmission> m_pending = new();
        private readonly ConcurrentDictionary<IntentKey, AcknowledgedIntent> m_acknowledged = new();
        private readonly SemaphoreSlim m_signal = new(
            initialCount: 0,
            maxCount: 1
        );

        public FederatedIntentPump(WorldRemoteAuthority owner, string sourceAuthority) {
            m_owner = owner;
            m_sourceAuthority = sourceAuthority;
            m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: owner.m_lifetime.Token);
            m_worker = Task.Run(function: () => RunAsync(ct: m_lifetime.Token));
        }

        private async Task HandoffAsync(NetworkStream stream, CancellationToken ct) {
            // This is an intentional route handoff, not a dropped client. Tell the older authority not to
            // synthesize a neutral release: the new lane will seed the same current held state.
            await WorldFederationCodec.WriteRequestAsync(
                body: default,
                ct: ct,
                kind: WorldFederationRequest.IntentStreamHandoff,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
            var handoff = await WorldFederationCodec.ReadResponseAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                !handoff.Ok ||
                (handoff.Kind != ((byte)WorldFederationResponse.Ack))
            ) {
                throw new IOException(message: DescribeHandshake(
                    read: handoff,
                    stage: "intent stream handoff"
                ));
            }
            try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
        }
        private async Task RunAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                string? attemptedEndpoint = null;
                var established = false;

                try {
                    await m_signal.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
                    attemptedEndpoint = m_owner.Endpoint;
                    await RunConnectionAsync(
                        established: () => established = true,
                        ct: ct
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    _ = Interlocked.Exchange(
                        location1: ref m_unavailable,
                        value: 0
                    );
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch (Exception exception) when (((attemptedEndpoint is not null) && (exception is IOException or SocketException or OperationCanceledException) &&
                    !string.Equals(
                    a: attemptedEndpoint,
                    b: m_owner.Endpoint,
                    comparisonType: StringComparison.Ordinal
                ))) {
                    // The committed route moved while the old socket was in flight. The old authority is allowed to
                    // close that lane before our explicit handoff frame arrives; reconnect immediately to the new
                    // endpoint without misreporting a healthy authority change as an outage.
                    try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
                } catch (Exception exception) when ((exception is IOException or SocketException or OperationCanceledException)) {
                    if (ct.IsCancellationRequested) {
                        return;
                    }

                    // Only a lane this pump could not re-establish is an outage. An established lane ending is not
                    // evidence about the peer: a peer cancelling its own pending read closes the handle abortively
                    // (WSAECONNABORTED), so its ordinary shutdown arrives here identically to a fault, and
                    // reconnecting loses nothing because an unacknowledged row is still pending.
                    if (
                        !established &&
                        (Interlocked.Exchange(
                        location1: ref m_unavailable,
                        value: 1
                    ) == 0)
                    ) {
                        Console.Error.WriteLine(value: $"[world.authority unavailable: intent stream to '{m_owner.Endpoint}' is reconnecting ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")})]");
                    }

                    try {
                        await Task.Delay(
                            delay: TimeSpan.FromMilliseconds(milliseconds: 100),
                            cancellationToken: ct
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    }
                    if (!m_pending.IsEmpty) {
                        try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
                    }
                }
            }
        }
        private async Task RunConnectionAsync(Action established, CancellationToken ct) {
            using var client = new TcpClient();

            client.NoDelay = true;
            await client.ConnectAsync(
                cancellationToken: ct,
                remoteEP: m_owner.m_endpoint
            ).ConfigureAwait(continueOnCapturedContext: false);
            var connectedEndpoint = m_owner.Endpoint;
            using var stream = client.GetStream();

            await WorldFederationCodec.WriteHelloAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
            await m_owner.AuthenticateAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
            await WorldFederationCodec.WriteRequestAsync(
                body: default,
                ct: ct,
                kind: WorldFederationRequest.IntentStream,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
            var opening = await WorldFederationCodec.ReadResponseAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (
                !opening.Ok ||
                (opening.Kind != ((byte)WorldFederationResponse.Ack))
            ) {
                throw new IOException(message: DescribeHandshake(
                    read: opening,
                    stage: "intent stream opening"
                ));
            }

            established();

            while (!ct.IsCancellationRequested) {
                if (m_pending.IsEmpty) {
                    await m_signal.WaitAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
                }

                foreach (var pair in m_pending.ToArray()) {
                    if (!string.Equals(
                        a: connectedEndpoint,
                        b: m_owner.Endpoint,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        await HandoffAsync(
                            ct: ct,
                            stream: stream
                        ).ConfigureAwait(continueOnCapturedContext: false);
                        return;
                    }
                    var sent = pair.Value;
                    var mobility = pair.Key.Mobility;
                    var body = WorldFederationCodec.EncodeIntent(
                        mobility: in mobility,
                        sourceAuthority: m_sourceAuthority,
                        submission: in sent
                    );

                    await WorldFederationCodec.WriteRequestAsync(
                        body: body,
                        ct: ct,
                        kind: WorldFederationRequest.Intent,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    var response = await WorldFederationCodec.ReadResponseAsync(
                        ct: ct,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);

                    if (
                        !response.Ok ||
                        (response.Kind != ((byte)WorldFederationResponse.Ack))
                    ) {
                        throw new IOException(message: DescribeHandshake(
                            read: response,
                            stage: "intent stream update"
                        ));
                    }

                    if (!string.Equals(
                        a: connectedEndpoint,
                        b: m_owner.Endpoint,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        await HandoffAsync(
                            ct: ct,
                            stream: stream
                        ).ConfigureAwait(continueOnCapturedContext: false);
                        return;
                    }

                    if (
                        m_pending.TryGetValue(
                        key: pair.Key,
                        value: out var current
                    ) &&
                        (current == sent)
                    ) {
                        m_acknowledged[pair.Key] = new AcknowledgedIntent(
                            Submission: sent,
                            Timestamp: Stopwatch.GetTimestamp()
                        );
                        _ = m_pending.TryRemove(
                            key: pair.Key,
                            value: out _
                        );
                    }

                    // Covers the route revision moving between the pre-ack check and publication of the acknowledged
                    // row. Restore the exact sent state to pending before closing the older lane.
                    if (!string.Equals(
                        a: connectedEndpoint,
                        b: m_owner.Endpoint,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        m_pending[pair.Key] = sent;
                        _ = m_acknowledged.TryRemove(
                            key: pair.Key,
                            value: out _
                        );
                        await HandoffAsync(
                            ct: ct,
                            stream: stream
                        ).ConfigureAwait(continueOnCapturedContext: false);
                        return;
                    }
                }
            }
        }

        public void Dispose() {
            m_lifetime.Cancel();
            try { m_worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            m_lifetime.Dispose();
            m_signal.Dispose();
        }
        public void InvalidateAcknowledgement(WorldMobilityIdentity mobility) {
            var key = new IntentKey(Mobility: mobility);

            if (m_acknowledged.TryRemove(
                key: key,
                value: out var acknowledged
            )) {
                m_pending[key] = acknowledged.Submission;
            }
            try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
        }
        public void Publish(WorldMobilityIdentity mobility, in IntentSubmission submission) {
            var key = new IntentKey(Mobility: mobility);

            if (
                m_acknowledged.TryGetValue(
                key: key,
                value: out var acknowledged
            ) &&
                (acknowledged.Submission == submission) &&
                (Stopwatch.GetElapsedTime(startingTimestamp: acknowledged.Timestamp) < TimeSpan.FromSeconds(seconds: 1))
            ) {
                return;
            }
            m_pending[key] = submission;
            try {
                _ = m_signal.Release();
            } catch (SemaphoreFullException) {
                // One wake already covers every latest-value row in the bounded table.
            }
        }
        public void Retire(WorldMobilityIdentity mobility) {
            var key = new IntentKey(Mobility: mobility);

            _ = m_pending.TryRemove(
                key: key,
                value: out _
            );
            _ = m_acknowledged.TryRemove(
                key: key,
                value: out _
            );
        }

        private readonly record struct IntentKey(WorldMobilityIdentity Mobility);
        private readonly record struct AcknowledgedIntent(IntentSubmission Submission, long Timestamp);
    }
}
