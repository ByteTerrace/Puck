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
/// <param name="Kind">The response kind. <see cref="WorldFederationResponse.Refusal"/> when <see cref="Ok"/> is
/// <see langword="false"/>.</param>
/// <param name="Body">The response body — a slice over the frame's own buffer, allocated per frame and never reused,
/// so it is safe to keep. Empty when <see cref="Ok"/> is <see langword="false"/>.</param>
/// <param name="Failure">The named transport refusal when nothing decoded; a lane answer of
/// <see cref="WireRefusal.RequestTimedOut"/> or an in-doubt <see cref="WireRefusal.ConnectionClosed"/> is a named
/// answer about ONE request (it may or may not have been applied), never evidence that the peer is down.</param>
public readonly record struct WorldFederationAnswer(WorldFederationResponse Kind, ReadOnlyMemory<byte> Body, WireFailure Failure) {
    /// <summary>Gets a value indicating whether the peer answered at all (no transport refusal).</summary>
    public bool Ok => !Failure.IsRefusal;

    /// <summary>Narrates this answer as a refusal sentence.</summary>
    public string Describe() =>
        (Failure.IsRefusal
            ? Failure.ToString()
            : ((Kind == WorldFederationResponse.Refusal)
                ? Encoding.UTF8.GetString(bytes: Body.Span)
                : $"unexpected federation response {Kind}"
        ));
    /// <summary>Creates a refused answer.</summary>
    /// <param name="refusal">The refusal name.</param>
    /// <param name="detail">The refusal narration.</param>
    /// <returns>The refused answer.</returns>
    public static WorldFederationAnswer Refused(WireRefusal refusal, string detail) =>
        new(
            Kind: WorldFederationResponse.Refusal,
            Body: ReadOnlyMemory<byte>.Empty,
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
    /// <summary>The detail every federation request answers with when this run holds no signing identity.</summary>
    private const string UnconfiguredDetail = "this run holds no federation signing identity";

    /// <summary>The ceiling on how long a caller waits for its answer, queue time included — the outer of the two
    /// clocks. The lane's own <see cref="LaneRequestTimeout"/> bounds one attempt on the socket; this bounds how long
    /// the caller's task waits behind whatever else is queued on the same ordered lane, so it is deliberately longer
    /// than one attempt. A caller that runs out of it answers <see cref="WireRefusal.LaneUnavailable"/> and the
    /// request stays queued for the lane to finish. This bounds transport lifecycle, never simulation state.</summary>
    private static readonly TimeSpan RoutedRequestDeadline = TimeSpan.FromSeconds(value: 10);
    /// <summary>How long a lane waits before retrying a connect that failed, before it calls the peer down.</summary>
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(value: 50);
    /// <summary>How long a lane that failed to reach its peer answers immediately with
    /// <see cref="WireRefusal.LaneUnavailable"/> before trying to connect again.</summary>
    private static readonly TimeSpan LaneBackoff = TimeSpan.FromSeconds(value: 1);
    /// <summary>The lane's per-attempt deadline — the inner of the two clocks. One attempt is connect, hello, and
    /// authenticate (when the lane has no connection) plus the request write and the response read; a peer that goes
    /// silent inside it is answered <see cref="WireRefusal.RequestTimedOut"/> once the request was written (no re-send,
    /// no backoff) or counted as a connect failure before it. Shorter than <see cref="RoutedRequestDeadline"/> so a
    /// caller's wait can cover its own attempt plus one queued ahead of it.</summary>
    private static readonly TimeSpan LaneRequestTimeout = TimeSpan.FromSeconds(value: 5);
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

    // Set once, by the first proof m_security refuses to give: an authenticator configured to verify but not to
    // prove reports IsConfigured for the host's sake, so the client side learns it cannot sign from the exchange
    // itself. Final for the run, exactly as an unconfigured authenticator is.
    private int m_cannotProve;
    private WorldDefinition m_definition;
    private long m_lastObservedTickBits;
    private WorldAuthorityRouteDescription? m_observedRoute;
    // The endpoint and its description travel as one reference so a republish swaps both atomically: every reader
    // (a lane dial, an observer connect, an Endpoint narration) sees one generation's pair, never a mix.
    private PublishedRoute m_route;
    private int m_routeRevision;
    private int m_unconfiguredNoted;

    public WorldRemoteAuthority(string endpoint, WorldDefinition placeholder, IAuthenticator security, string observerAuthority, WorldRemoteAuthority? submissionAuthority = null, WorldRemoteRouteCredential? submissionCredential = null, WorldAuthorityRouteDescription? initialRoute = null, Action<WorldAuthorityRouteDescription>? routeChanged = null, CancellationToken applicationStopping = default) {
        if (!IPEndPoint.TryParse(
            result: out var parsed,
            s: endpoint
        )) {
            throw new FormatException(message: $"host.authority '{endpoint}' is not a parseable IP endpoint");
        }

        m_route = new PublishedRoute(endpoint: parsed);
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
            m_route = new PublishedRoute(endpoint: initialEndpoint);
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
    /// <summary>Gets the current route's endpoint as text — the description cached beside the endpoint when the
    /// route was published, so reading it costs no formatting and always names the same generation the lanes dial.</summary>
    public string Endpoint => Volatile.Read(location: ref m_route).Description;
    public IServerLink Link => m_link;
    public ulong NextInputTick => (unchecked((ulong)Interlocked.Read(location: ref m_lastObservedTickBits)) + 1UL);

    /// <summary>Issues one routed request on the source namespace's lane and waits, bounded, for its answer.</summary>
    /// <remarks>This wait, and its twin in the transfer-step resolver, is reached from the tick thread: the transfer
    /// steps from <c>WorldInstanceHost.DrainPendingTransfers</c> (the host's per-tick fixed point) and the forwarded
    /// submissions and route lookups from the server's own drain, plus the routed observer's <see cref="IServerLink"/>
    /// (<see cref="WorldFederatedServerLink"/>), which the console and client drive on the thread that pumps the tick.
    /// A bounded synchronous wait is acceptable there because the contract demands an ANSWER inside the tick — a
    /// caller told "not yet" would hold state across ticks the adjacency scan is concurrently re-deriving and mint a
    /// second crossing for the same seat — and the wait is bounded twice over: the lane's own
    /// <see cref="LaneRequestTimeout"/> per attempt, then <see cref="RoutedRequestDeadline"/> here. A lane already
    /// known unreachable, or a run holding no signing identity, answers without waiting at all, so the stall is
    /// confined to the one tick that carries a request to a peer that stops answering mid-exchange — the cost the
    /// authored unavailable policy exists to absorb.</remarks>
    /// <param name="sourceAuthority">The authenticated source namespace whose lane carries the request.</param>
    /// <param name="kind">The request kind.</param>
    /// <param name="body">The encoded request leaf.</param>
    /// <returns>The peer's answer, or a named refusal when the lane could not deliver one in time.</returns>
    public WorldFederationAnswer AwaitAnswer(string sourceAuthority, WorldFederationRequest kind, byte[] body) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.AwaitAnswer(
                body: body,
                kind: kind,
                sourceAuthority: sourceAuthority
            );
        }

        if (LacksSigningIdentity()) {
            return WorldFederationAnswer.Refused(
                detail: UnconfiguredDetail,
                refusal: WireRefusal.LaneUnavailable
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
            body: answer.Body.Span,
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
    // Reads m_route exactly once, so the endpoint a lane dials and the description it records for that same connect
    // always name the same route republish generation; the description was formatted when the route was published,
    // never per attempt.
    private LaneRoute CurrentRoute() => Volatile.Read(location: ref m_route).Lane;
    private static string DescribeHandshake(WireFrameRead read, string stage) =>
        (read.Ok
            ? new WorldFederationAnswer(
                Kind: ((WorldFederationResponse)read.Kind),
                Body: read.Body,
                Failure: default
            ).Describe()
            : $"federation {stage} — {read.Failure}"
        );
    private static async Task<WorldFederationAnswer> EnqueueAnswerAsync(PersistentRequestLane<WorldFederationRequest, WorldFederationResponse> lane, WorldFederationRequest kind, byte[] body) {
        var response = await lane.Enqueue(
            body: body,
            kind: kind
        ).ConfigureAwait(continueOnCapturedContext: false);

        // A refused LaneResponse carries a default Kind (no WorldFederationResponse member); the answer's own contract
        // is that Kind is Refusal on every failure path, so the refusal is re-minted here rather than copied.
        return (response.Ok
            ? new WorldFederationAnswer(
                Kind: response.Kind,
                Body: response.Body,
                Failure: response.Failure
            )
            : WorldFederationAnswer.Refused(
                detail: response.Failure.Detail,
                refusal: response.Failure.Refusal
            )
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
    // The gate before every socket: a run started without a federation signing identity can never authenticate a
    // lane, so its requests are refused here — one stderr line per authority, then silently — rather than paid for
    // with a connect, a Hello, and a proof that throws. m_security is readonly and its configuration is fixed for
    // the run, so a true answer is final. IsConfigured alone is not the whole gate: an authenticator configured to
    // verify but not to prove passes it, and only the first proof it refuses (recorded in m_cannotProve by
    // AuthenticateAsync) reveals that — from then on this gate closes for it exactly as for an unconfigured one.
    private bool LacksSigningIdentity() {
        if (
            m_security.IsConfigured &&
            (Volatile.Read(location: ref m_cannotProve) == 0)
        ) {
            return false;
        }

        if (Interlocked.Exchange(
            location1: ref m_unconfiguredNoted,
            value: 1
        ) == 0) {
            Console.Error.WriteLine(value: $"[world.authority unavailable: federation to '{Endpoint}' is refused ({UnconfiguredDetail})]");
        }

        return true;
    }
    private PersistentRequestLane<WorldFederationRequest, WorldFederationResponse> LaneFor(string sourceAuthority, WorldFederationRequest kind) =>
        m_requestLanes.GetOrAdd(
            key: (sourceAuthority, LaneOf(kind: kind)),
            valueFactory: key => new PersistentRequestLane<WorldFederationRequest, WorldFederationResponse>(
                connectRetryDelay: ConnectRetryDelay,
                lifetime: m_lifetime.Token,
                onUnavailable: exception => Console.Error.WriteLine(value: $"[world.authority unavailable: federation lane to '{Endpoint}' is reconnecting ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")})]"),
                protocol: new WorldFederationLaneProtocol(owner: this),
                requestTimeout: LaneRequestTimeout,
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
        var observedEndpoint = Volatile.Read(location: ref m_route).Endpoint;
        var observedRouteRevision = Volatile.Read(location: ref m_routeRevision);

        await client.ConnectAsync(
            cancellationToken: ct,
            remoteEP: observedEndpoint
        ).ConfigureAwait(continueOnCapturedContext: false);
        using var stream = client.GetStream();

        await HandshakeWireFormat.WriteHelloAsync(
            ct: ct,
            key: WorldFederationCodec.WireKey,
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
                            body: frame.Body.Span,
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
                            body: frame.Body.Span,
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
            // The same gate every lane applies: an observer session authenticates too, and a run holding no signing
            // identity would otherwise reconnect and be refused four times a second for the rest of the run. Tested
            // per session, not once, because an authenticator that verifies but cannot prove is only discovered by
            // the first session's own proof.
            if (LacksSigningIdentity()) {
                return;
            }

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

        if (LacksSigningIdentity()) {
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
            location: ref m_route,
            value: new PublishedRoute(endpoint: routedEndpoint)
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
    // A Completion body is one whole downstream frame, decoded in place over the answer's own buffer.
    private static bool TryReadCompletion(ReadOnlyMemory<byte> body, out WorldSubmissionResult? result, out string reason) {
        result = null;

        if (!WorldTcpWireFormat.TryDecodeDownstream(
            body: out var completionBody,
            frame: body,
            kind: out var completionKind
        )) {
            reason = "forwarded authority returned an empty completion";
            return false;
        }

        return WorldTcpWireFormat.TryReadResult(
            body: completionBody.Span,
            kind: completionKind,
            reason: out reason,
            result: out result
        );
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

        if (LacksSigningIdentity()) {
            answer = WorldFederationAnswer.Refused(
                detail: UnconfiguredDetail,
                refusal: WireRefusal.LaneUnavailable
            );

            return true;
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
    /// <exception cref="IOException">The peer's challenge or verdict frame is malformed or refused, or this run holds
    /// no federation signing identity — every lane, observer, and intent pump gates that case before it opens a
    /// socket, so this is belt and braces for a caller that reaches the exchange some other way. The one case the
    /// gate cannot see up front is an authenticator configured to verify but not to prove: its first refused proof
    /// is thrown as this same exception and recorded, so every later gate closes on it without another
    /// socket.</exception>
    public async Task AuthenticateAsync(Stream stream, CancellationToken ct) {
        if (LacksSigningIdentity()) {
            throw new IOException(message: $"federation authentication — {UnconfiguredDetail}");
        }

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

        byte[] proof;

        try {
            proof = m_security.Prove(challenge: challenge.Body.Span);
        } catch (InvalidOperationException) {
            // The authenticator holds nothing to sign with (WorldAttestedAuthenticator built with trust entries and
            // no oracle). That is as final as an unconfigured run, so it is recorded for LacksSigningIdentity and
            // thrown in the wire vocabulary: the lane takes its ordinary two-strike path to LaneUnavailable and
            // backoff, and no later lane, observer, or intent stream pays a connect, a Hello, and a challenge for it.
            Volatile.Write(
                location: ref m_cannotProve,
                value: 1
            );

            throw new IOException(message: $"federation authentication — {UnconfiguredDetail}");
        }

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
            body: answer.Body.Span,
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
            body: answer.Body.Span,
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
            !Enum.IsDefined(value: ((WorldTransferStatus)answer.Body.Span[0]))
        ) {
            return false;
        }

        status = ((WorldTransferStatus)answer.Body.Span[0]);

        return true;
    }

    private readonly record struct TransferStepKey(string SourceAuthority, ulong TransferId, WorldFederationRequest Kind);
    // One route republish generation: the endpoint to dial and its description, formatted once here so no attempt,
    // narration, or comparison formats it again, and held as one reference so a swap is atomic.
    private sealed class PublishedRoute(IPEndPoint endpoint) {
        public string Description { get; } = endpoint.ToString();
        public IPEndPoint Endpoint { get; } = endpoint;

        public LaneRoute Lane => new(
            Description: Description,
            Endpoint: Endpoint
        );
    }
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
                // The same gate every lane applies: a run holding no signing identity can never open this stream, so
                // the worker ends here instead of reconnecting ten times a second for the rest of the run. Tested per
                // connection, not once, because an authenticator that verifies but cannot prove is only discovered
                // by the first connection's own proof.
                if (m_owner.LacksSigningIdentity()) {
                    return;
                }

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
            // One route snapshot: the endpoint dialed and the description every later comparison names are the
            // same generation, and the comparisons below are against the owner's cached description, never a
            // fresh formatting.
            var route = m_owner.CurrentRoute();

            await client.ConnectAsync(
                cancellationToken: ct,
                remoteEP: route.Endpoint
            ).ConfigureAwait(continueOnCapturedContext: false);
            var connectedEndpoint = route.Description;
            using var stream = client.GetStream();

            await HandshakeWireFormat.WriteHelloAsync(
                ct: ct,
                key: WorldFederationCodec.WireKey,
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
