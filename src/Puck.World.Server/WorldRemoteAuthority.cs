using Puck.Assets;
using System.Collections.Concurrent;
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
    public WorldForwardingDestination DescribeForCheckpoint() => new(
        authority.PeerAuthority,
        credential.SourceAuthority,
        credential.Mobility,
        authority.Endpoint,
        authority.Definition
    );
    /// <inheritdoc/>
    public ValueTask<byte[]?> FetchPrototypeAsync(ContentPin pin, WorldDisclosureTier ceiling, byte remainingHops) =>
        authority.FetchPrototypeAsync(pin: pin, traveler: new WorldTravelerObservation(credential.SourceAuthority, credential.Mobility, ceiling, remainingHops));
    /// <inheritdoc/>
    public Task<string?> StreamProjectionAsync(Stream output, WorldDisclosureTier ceiling, byte remainingHops, CancellationToken ct) =>
        authority.RelayProjectionAsync(
            new(
                credential.SourceAuthority,
                credential.Mobility,
                ceiling,
                remainingHops
            ),
            output,
            ct
        );
    /// <inheritdoc/>
    public ValueTask<(WorldAuthorityRouteDescription? Route, string Reason)> DescribeRouteAsync() => authority.DescribeRouteAsync(credential);
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
    public bool TryForwardSubmission(WorldSubmissionPayload payload, Guid operationId, Action<WorldSubmissionResult> completion, out string reason) {
        var held = credential;

        return authority.TryForwardSubmission(
            completion: completion,
            credential: in held,
            operationId: operationId,
            payload: payload,
            reason: out reason
        );
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

    /// <summary>The asynchronous recovery request is still in flight. Its exact transaction remains held.</summary>
    Pending,
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
/// keeps unavailable peers off the tick thread. A crossing holds its seats and consumes the reply at a tick boundary.</para>
/// </remarks>
public sealed partial class WorldRemoteAuthority : IWorldRoutedRequests, IWorldCustodyPeer, IDisposable {
    /// <summary>The detail every federation request answers with when this run holds no signing identity.</summary>
    private const string UnconfiguredDetail = "this run holds no federation signing identity";

    /// <summary>How long a lane that failed to reach its peer answers immediately with
    /// <see cref="WireRefusal.LaneUnavailable"/> before trying to connect again.</summary>
    private static readonly TimeSpan LaneBackoff = TimeSpan.FromSeconds(value: 1);
    /// <summary>How long an observer session that ended waits before it reconnects.</summary>
    private static readonly TimeSpan ObserveRetryDelay = TimeSpan.FromMilliseconds(value: 250);
    /// <summary>How long an intent stream that failed waits before it reconnects.</summary>
    private static readonly TimeSpan IntentRetryDelay = TimeSpan.FromMilliseconds(value: 100);
    /// <summary>How long a written intent suppresses a second write of the identical submission.</summary>
    private static readonly TimeSpan IntentRepeatWindow = TimeSpan.FromSeconds(value: 1);

    /// <summary>Gets the ceiling on how long a caller waits for its answer, queue time included — the outer of the two
    /// deadlines, run on the host clock (<see cref="WorldPeerNetwork.Clock"/>). The lane's own
    /// <see cref="LaneRequestTimeout"/> bounds one attempt on the socket; this bounds how long the caller's task waits
    /// behind whatever else is queued on the same ordered lane, so it is deliberately longer than one attempt. A caller
    /// that runs out of it answers <see cref="WireRefusal.LaneUnavailable"/> and the request stays queued for the lane
    /// to finish. This bounds transport lifecycle, never simulation state.</summary>
    public static TimeSpan RoutedRequestDeadline { get; } = TimeSpan.FromSeconds(value: 10);
    /// <summary>Gets the lane's per-attempt deadline — the inner of the two, run on the host clock. One attempt is
    /// connect, hello, and authenticate (when the lane has no connection) plus the request write and the response read;
    /// a peer that goes silent inside it is answered <see cref="WireRefusal.RequestTimedOut"/> once the request was
    /// written (no re-send, no backoff) or counted as a connect failure before it. Shorter than
    /// <see cref="RoutedRequestDeadline"/> so a caller's wait can cover its own attempt plus one queued ahead of
    /// it.</summary>
    public static TimeSpan LaneRequestTimeout { get; } = TimeSpan.FromSeconds(value: 5);
    /// <summary>Gets how long a lane waits, on the host clock, before retrying a connect that failed, before it calls
    /// the peer down.</summary>
    public static TimeSpan ConnectRetryDelay { get; } = TimeSpan.FromMilliseconds(value: 50);

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

    private readonly string m_peerAuthority;

    // Frames is the "nothing observed yet" value, so the first delivered document always narrates its tier once.
    private WorldDisclosureTier m_observedTier = WorldDisclosureTier.Frames;

    private readonly TimeProvider m_clock;
    private readonly CancellationTokenSource m_lifetime;
    private readonly WorldFederatedServerLink m_link;
    private readonly string m_observerAuthority;
    private readonly Action<WorldAuthorityRouteDescription>? m_routeChanged;
    private readonly IAuthenticator m_security;
    private readonly WorldPeerNetwork m_network;
    private readonly WorldRemoteAuthority? m_submissionAuthority;
    private readonly WorldRemoteRouteCredential? m_submissionCredential;

    // Set once, by the first proof m_security refuses to give: an authenticator configured to verify but not to
    // prove reports IsConfigured for the host's sake, so the client side learns it cannot sign from the exchange
    // itself. Final for the run, exactly as an unconfigured authenticator is.
    private int m_cannotProve;
    private readonly WorldDefinition m_initialDefinition;
    private WorldDefinition m_definition;
    private long m_lastObservedTickBits;

    private readonly Lock m_observedRouteGate = new();

    private WorldAuthorityRouteDescription? m_observedRoute;
    private bool m_routeDeliveryPending;

    // The physical entry stays fixed even when a traveler's logical destination changes.
    private readonly PublishedRoute m_route;

    private int m_unconfiguredNoted;

    // The local instance's own hub, so this lane's narration reaches whatever sink is attached to it — null for a
    // caller with no local server (leaves this lane's narration undelivered, never a fallback to Console.Error).
    private readonly WorldOutputHub? m_narrationHub;
    // The outbox that holds stream deliveries and route updates arriving on socket workers while the host loop is
    // outside its boundary window, replayed on the pump when the host collects.
    private readonly WorldOutputHub m_outbox;
    private readonly Action<byte>? m_deliverObservedRoute;

    public WorldRemoteAuthority(string endpoint, WorldDefinition placeholder, IAuthenticator security, string observerAuthority, string expectedAuthority, WorldRemoteAuthority? submissionAuthority = null, WorldRemoteRouteCredential? submissionCredential = null, WorldAuthorityRouteDescription? initialRoute = null, Action<WorldAuthorityRouteDescription>? routeChanged = null, CancellationToken applicationStopping = default, WorldPeerNetwork? network = null, WorldOutputHub? narrationHub = null, WorldLinkBoundary? boundary = null) {
        if (string.IsNullOrWhiteSpace(value: expectedAuthority)) { throw new ArgumentException(message: "an outbound authority requires its intended namespace", paramName: nameof(expectedAuthority)); }
        m_narrationHub = (narrationHub ?? submissionAuthority?.m_narrationHub);
        m_outbox = new WorldOutputHub { Boundary = (boundary ?? submissionAuthority?.m_outbox.Boundary) };
        if (!PeerEndpoint.TryParse(
            endpoint: out var parsed,
            value: endpoint
        )) {
            throw new FormatException(message: $"host.authority '{endpoint}' is not a valid peer host and port");
        }

        m_route = new PublishedRoute(endpoint: parsed);
        m_initialDefinition = placeholder;
        m_definition = placeholder;
        m_security = (security ?? throw new ArgumentNullException(paramName: nameof(security)));
        // One peer network per host: a routed observer dials through its transaction authority's, and a root authority
        // through the host's own. Never a second network — that would be a second identity and a second clock.
        m_network = (submissionAuthority?.m_network ?? (network ?? throw new InvalidOperationException(message: $"federation to '{endpoint}' needs its host's peer network, and this instance is local-only")));
        m_clock = m_network.Clock;
        m_observerAuthority = observerAuthority;
        m_peerAuthority = expectedAuthority;
        m_submissionAuthority = submissionAuthority;
        m_submissionCredential = submissionCredential;
        if ((submissionAuthority is null) != (submissionCredential is null)) {
            throw new ArgumentException(message: "a routed observer requires both its submission authority and immutable route credential");
        }
        m_observedRoute = initialRoute;
        m_routeChanged = routeChanged;
        m_deliverObservedRoute = ((routeChanged is null) ? null : m_outbox.Route<byte>(action: _ => DeliverPendingRoute()));
        m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: applicationStopping);
        if (initialRoute is { } route) {
            // A logical destination can be a colocated world with no public listener. Keep the authenticated
            // entry endpoint; its source-scoped forwarding chain relays this traveler's projection.
            m_definition = route.Definition;
            m_authority = route.Entity.Authority;
            m_lastObservedTickBits = unchecked((long)route.Tick);
        }
        m_link = new WorldFederatedServerLink(authority: this);
    }

    /// <summary>Gets the hub this authority's narration is delivered through, or <see langword="null"/> when none
    /// was attached — the seam its own <see cref="WorldFederatedServerLink"/> narrates a held credential through
    /// too, rather than carrying a second hub reference.</summary>
    public WorldOutputHub? NarrationHub => m_narrationHub;
    /// <summary>Gets the number of deliveries recorded while the host's boundary window was closed and not yet replayed.</summary>
    public int PendingDeliveries => m_outbox.PendingRecords;

    /// <summary>Replays every delivery and route change recorded while the host's boundary window was closed. Call on
    /// the host loop's thread inside its boundary window.</summary>
    public void CollectDeliveries() => m_outbox.Collect();
    public string Authority => ((Volatile.Read(location: ref m_authority) is { Length: > 0 } observed)
        ? observed
        : PeerAuthority
    );
    /// <summary>Gets the intended first-hop transaction namespace supplied at construction.
    /// Reconnection checks this namespace before sending a request. It is distinct from a traveler's onward route.</summary>
    public string PeerAuthority => m_peerAuthority;
    /// <summary>Gets the store the content fetched from this authority is kept in, bounded apart from every other
    /// authority's (<see cref="WorldProjectionContent.AuthorityStore"/>).</summary>
    public ContentAddressedStore FetchedContent => WorldProjectionContent.AuthorityStore(authority: m_peerAuthority);
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
    /// <summary>Gets the fixed authenticated entry endpoint as cached text, without per-read formatting.</summary>
    public string Endpoint => m_route.Description;
    public IServerLink Link => m_link;
    public ulong NextInputTick => (unchecked((ulong)Interlocked.Read(location: ref m_lastObservedTickBits)) + 1UL);

    /// <summary>Issues one routed request on the source namespace's lane and returns its answer without waiting for it:
    /// the peer's answer, or a named refusal when the lane cannot carry it or the peer does not answer within
    /// <see cref="RoutedRequestDeadline"/> on the host clock.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace whose lane carries the request.</param>
    /// <param name="kind">The request kind.</param>
    /// <param name="body">The encoded request leaf.</param>
    /// <returns>The answer, completed on whatever thread the lane answers on.</returns>
    public Task<WorldFederationAnswer> AnswerAsync(string sourceAuthority, WorldFederationRequest kind, byte[] body) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.AnswerAsync(
                body: body,
                kind: kind,
                sourceAuthority: sourceAuthority
            );
        }

        if (LacksSigningIdentity()) {
            return Task.FromResult(result: WorldFederationAnswer.Refused(
                detail: UnconfiguredDetail,
                refusal: WireRefusal.LaneUnavailable
            ));
        }

        var lane = LaneFor(
            kind: kind,
            sourceAuthority: sourceAuthority
        );

        if (!lane.IsAvailable) {
            return Task.FromResult(result: WorldFederationAnswer.Refused(
                refusal: WireRefusal.LaneUnavailable,
                detail: $"the federation lane to '{Endpoint}' is reconnecting"
            ));
        }

        return WithinDeadlineAsync(
            answer: EnqueueAnswerAsync(
                body: body,
                kind: kind,
                lane: lane
            ),
            clock: m_clock,
            endpoint: Endpoint,
            kind: kind
        );
    }
    /// <summary>Bounds one routed answer by <see cref="RoutedRequestDeadline"/> on a clock: the answer when it arrives in
    /// time, else a named <see cref="WireRefusal.LaneUnavailable"/> refusal.</summary>
    /// <param name="answer">The pending answer.</param>
    /// <param name="clock">The clock the deadline elapses on.</param>
    /// <param name="endpoint">The peer's endpoint, as the refusal names it.</param>
    /// <param name="kind">The request kind, as the refusal names it.</param>
    /// <returns>The answer, or the refusal.</returns>
    public static async Task<WorldFederationAnswer> WithinDeadlineAsync(Task<WorldFederationAnswer> answer, TimeProvider clock, string endpoint, WorldFederationRequest kind) {
        ArgumentNullException.ThrowIfNull(argument: answer);
        ArgumentNullException.ThrowIfNull(argument: clock);

        try {
            return await answer.WaitAsync(
                timeout: RoutedRequestDeadline,
                timeProvider: clock
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (TimeoutException) {
            return WorldFederationAnswer.Refused(
                refusal: WireRefusal.LaneUnavailable,
                detail: $"'{endpoint}' did not answer {kind} within {RoutedRequestDeadline.TotalSeconds:0.#}s"
            );
        }
    }
    public ValueTask<(WorldAuthorityRouteDescription? Route, string Reason)> DescribeRouteAsync(int bodyIndex) =>
        TryRouteCredential(bodyIndex, out var credential) ? DescribeRouteAsync(credential) :
            new((null, $"forwarded body:{bodyIndex} has no committed destination credential"));

    internal async ValueTask<(WorldAuthorityRouteDescription? Route, string Reason)> DescribeRouteAsync(WorldRemoteRouteCredential credential) {
        var target = m_submissionAuthority ?? this;
        var mobility = credential.Mobility;
        var answer = await target.AnswerAsync(credential.SourceAuthority, WorldFederationRequest.Route,
            WorldFederationCodec.EncodeRouteCredential(credential.SourceAuthority, in mobility)).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (answer.Kind != WorldFederationResponse.Route) { return (null, answer.Describe()); }
        return WorldFederationCodec.TryDecodeRoute(answer.Body.Span, target.Definition, out var route, out var failure) ? (route, string.Empty) : (null, failure.ToString());
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
    // Forwards one submission over the routed lane and answers its completion with the destination's typed result,
    // which a destination sends once its own verdict exists (a buffered mutation's at its tick boundary).
    internal bool TryForwardSubmission(in WorldRemoteRouteCredential credential, WorldSubmissionPayload payload, Guid operationId, Action<WorldSubmissionResult> completion, out string reason) =>
        TrySendSubmission(this, in credential, payload, operationId, completion, out reason);

    internal static bool TrySendSubmission(IWorldRoutedRequests authority, in WorldRemoteRouteCredential credential, WorldSubmissionPayload payload, Guid operationId, Action<WorldSubmissionResult> completion, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: completion);

        if (!Puck.World.Protocol.WorldFrameCodec.TryEncode(
            failure: out var failure,
            frame: out var canonical,
            operationId: operationId,
            payload: payload
        )) {
            reason = $"forwarded submission could not be encoded — {failure.Detail}";
            return false;
        }

        var mobility = credential.Mobility;
        var pending = authority.AnswerAsync(
            sourceAuthority: credential.SourceAuthority,
            kind: WorldFederationRequest.Submission,
            body: WorldFederationCodec.EncodeSubmission(
                sourceAuthority: credential.SourceAuthority,
                mobility: in mobility,
                frame: canonical
            )
        );

        _ = CompleteAsync();
        reason = string.Empty;
        return true;

        async Task CompleteAsync() {
            WorldSubmissionResult? result = null;
            string detail;

            try {
                var answer = await pending.ConfigureAwait(false);
                if (answer.Kind == WorldFederationResponse.Completion) {
                    _ = TryReadCompletion(answer.Body, out result, out detail);
                } else {
                    detail = answer.Describe();
                }
            } catch (Exception exception) {
                detail = exception.Message;
            }

            completion(result ?? new WorldSubmissionResult.Refusal(WorldFederatedServerLink.CompletionUnavailableCode, detail));
        }
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

    internal void AdoptReservationCredentials(WorldTransferReservationRequest request, WorldTransferReservationReply reply) {
        var accepted = reply.LeasedIds(request);
        var members = request.Members.Where((member, ordinal) => accepted.Contains(member.IdAt(ordinal))).ToArray();
        for (var ordinal = 0; ordinal < reply.BodyIndices.Count && ordinal < members.Length; ordinal++) {
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

            if (members[ordinal].Mobility is { } mobility) {
                m_credentials[bodyIndex] = new WorldRemoteRouteCredential(
                    BodyIndex: bodyIndex,
                    SourceAuthority: request.SourceAuthority,
                    Mobility: mobility.Advance()
                );
            }
        }
    }
    private LaneRoute CurrentRoute() => m_route.Lane;
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
    private void ResendLastIntent(in WorldRemoteRouteCredential credential) {
        if ((m_submissionAuthority ?? this).m_intentPumps.TryGetValue(
            key: credential.SourceAuthority,
            value: out var pump
        )) {
            pump.ResendLastIntent(mobility: credential.Mobility);
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
            if (m_narrationHub is { HasNarrationSink: true }) {
                m_narrationHub?.Narrate(
                    channel: "world.authority unavailable",
                    text: $"[world.authority unavailable: federation to '{Endpoint}' is refused ({UnconfiguredDetail})]"
                );
            }
        }

        return true;
    }
    private PersistentRequestLane<WorldFederationRequest, WorldFederationResponse> LaneFor(string sourceAuthority, WorldFederationRequest kind) =>
        m_requestLanes.GetOrAdd(
            key: (sourceAuthority, LaneOf(kind: kind)),
            valueFactory: key => new PersistentRequestLane<WorldFederationRequest, WorldFederationResponse>(
                connect: m_network.ConnectAsync,
                connectRetryDelay: ConnectRetryDelay,
                lifetime: m_lifetime.Token,
                onUnavailable: exception => m_narrationHub?.Narrate(
                    channel: "world.authority unavailable",
                    text: $"[world.authority unavailable: federation lane to '{Endpoint}' is reconnecting ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")})]"
                ),
                protocol: new WorldFederationLaneProtocol(owner: this),
                requestTimeout: LaneRequestTimeout,
                route: CurrentRoute,
                sourceAuthority: key.SourceAuthority,
                timeProvider: m_clock,
                unavailableBackoff: LaneBackoff
            )
        );
    private static WorldFederationLane LaneOf(WorldFederationRequest kind) =>
        ((kind is WorldFederationRequest.Route or WorldFederationRequest.Submission or WorldFederationRequest.Prototype)
            ? WorldFederationLane.Routed
            : WorldFederationLane.Transaction
        );
    // The current observation session's ingress, so the span it holds closes when the session ends.
    private sealed class ObservedStream {
        public WorldStreamIngress? Session;
    }
    // A session that ended can no longer tell its reader of a cut, so the span it was showing closes: its reader drops
    // what that stream delivered and takes the next session's stream as a new admission. The close is the reader's own
    // transport speaking, never a record, so nothing about it is taken from the wire.
    private static void CloseObserved(WorldSinkRoute sink, ObservedStream observed) {
        if ((Interlocked.Exchange(location1: ref observed.Session, value: null) is { } session) && (session.Span.Stream is var stream and not 0UL)) {
            sink.CloseSpan(stream: stream);
        }
    }
    private async Task<bool> ObserveSessionAsync(WorldSinkRoute sink, ObservedStream observed, CancellationToken ct) {
        var upstream = (m_submissionAuthority ?? this);
        var observedEndpoint = upstream.m_route.Endpoint;

        await using var stream = await m_network.ConnectAsync(
            ct: ct,
            endpoint: observedEndpoint
        ).ConfigureAwait(continueOnCapturedContext: false);

        await HandshakeWireFormat.WriteHelloAsync(
            ct: ct,
            key: WorldFederationCodec.WireKey,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
        await upstream.AuthenticateAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);
        await WorldFederationCodec.WriteRequestAsync(
            body: ((m_submissionCredential is { } credential)
            ? WorldFederationCodec.EncodeTravelerObservation(request: new(
                    credential.SourceAuthority,
                    credential.Mobility
                ))
            : default),
            ct: ct,
            kind: (m_submissionCredential.HasValue
            ? WorldFederationRequest.ObserveTraveler
            : WorldFederationRequest.Observe),
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        // The projection this session holds: a presentation-tier delta merges over it, and a new session, or a new
        // generation of this one, starts from a whole projection again.
        byte[]? Fetch(ContentPin pin) => upstream.FetchPrototype(pin: pin, traveler: ((m_submissionCredential is { } credential)
            ? new WorldTravelerObservation(credential.SourceAuthority, credential.Mobility) : null));
        var seedDefinition = upstream.m_initialDefinition;
        var hold = new WorldProjectionHold(basis: () => Definition, content: upstream.FetchedContent, fetch: Fetch, seed: seedDefinition);
        WorldDefinition? replica = null;
        WorldDocumentVersion replicaVersion = default;
        // The session's one door: every record it reads, a route included, passes this ingress on its stamp before
        // anything the record carries is decoded into the hold or published by this authority, and a record out of
        // order (another stream, an earlier generation, an ordinal not ahead) ends the session, which closes the
        // reader's span. The reader's own ingress orders the records again across sessions.
        var ingress = new WorldStreamIngress();
        var bodies = new WorldSnapshotReceiver();

        observed.Session = ingress;
        bool Admits(ReadOnlySpan<byte> record, out WorldStreamStamp stamp, out ReadOnlySpan<byte> body, out WireFailure failure) {
            if (!WorldFederationCodec.TryReadStamped(record: record, stream: out stamp, body: out body, failure: out failure)) { return false; }
            if (ingress.Admit(stream: in stamp)) { return true; }
            failure = new WireFailure(Detail: "an observation record is out of its stream's order", Refusal: WireRefusal.PayloadMalformed);
            return false;
        }

        while (!ct.IsCancellationRequested) {
            var frame = await WorldFederationCodec.ReadResponseAsync(
                ct: ct,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!frame.Ok) {
                return false;
            }

            switch ((WorldFederationResponse)frame.Kind) {
                case WorldFederationResponse.ProjectionInvalidated:
                    return m_submissionCredential.HasValue;
                case WorldFederationResponse.Refusal:
                    // The frame's body is a span over pooled memory that will not outlive this scope, so — unlike
                    // every other site here — the sink check comes first and the decode happens only behind it,
                    // rather than being captured into Narrate's own deferred formatter.
                    if (m_narrationHub is { } hub) {
                        var refusalReason = Encoding.UTF8.GetString(bytes: frame.Body.Span);

                        if (hub.HasNarrationSink) {
                            hub.Narrate(
                                channel: "world.projection",
                                text: $"[world.projection: remote observer '{Endpoint}' refused ({refusalReason})]"
                            );
                        }
                    }
                    return false;
                case WorldFederationResponse.Route:
                    if (
                        !m_submissionCredential.HasValue ||
                        !Admits(record: frame.Body.Span, stamp: out _, body: out var routeBody, failure: out _) ||
                        !WorldFederationCodec.TryDecodeRoute(
                        routeBody,
                        Definition,
                        out var route,
                        out _
                    )
                    ) {
                        return false;
                    }
                    ObserveRoute(route: route);
                    break;
                case WorldFederationResponse.Cut: {
                        if (!WorldFederationCodec.TryDecodeCut(body: frame.Body.Span, stream: out var cut, terminal: out var terminal) ||
                            !ingress.Cut(stream: in cut, terminal: terminal)) {
                            return false;
                        }
                        // The ended span's projection goes with it: the generation the cut opens starts from its whole
                        // projection, which nothing of the ended span can complete.
                        hold = new WorldProjectionHold(basis: () => Definition, content: upstream.FetchedContent, fetch: Fetch, seed: seedDefinition);
                        bodies = new WorldSnapshotReceiver();
                        replica = null;
                        replicaVersion = default;
                        sink.DeliverCut(stream: cut, terminal: terminal);
                        if (terminal) {
                            return false;
                        }
                        break;
                    }
                case WorldFederationResponse.Definition: {
                        if (
                            !Admits(record: frame.Body.Span, stamp: out var definitionStream, body: out var definitionBody, failure: out var definitionFailure) ||
                            !WorldFederationCodec.TryDecodeDocument(
                            body: definitionBody,
                            content: upstream.FetchedContent,
                            definition: out var definition,
                            fetch: Fetch,
                            hold: hold,
                            tier: out var definitionTier,
                            failure: out definitionFailure,
                            version: out var definitionVersion
                        ) ||
                            (definition is null) ||
                            !definitionVersion.IsDelivered
                        ) {
                            if (m_narrationHub is { HasNarrationSink: true }) {
                                m_narrationHub?.Narrate(
                                    channel: "world.projection",
                                    text: $"[world.projection: remote observer '{Endpoint}' refused a definition record ({(definitionFailure.IsRefusal ? definitionFailure.ToString() : "a live definition carries no delivered version")})]"
                                );
                            }

                            return false;
                        }

                        if (definitionTier != m_observedTier) {
                            m_observedTier = definitionTier;
                            if (m_narrationHub is { HasNarrationSink: true }) {
                                m_narrationHub?.Narrate(
                                    channel: "world.projection",
                                    text: $"[world.projection: remote observer '{Endpoint}' receives documents at tier {definitionTier}]"
                                );
                            }
                        }

                        Volatile.Write(
                            location: ref m_definition,
                            value: definition
                        );
                        replica = definitionTier == WorldDisclosureTier.Replica ? definition : null;
                        replicaVersion = definitionVersion;
                        sink.DeliverDefinition(
                            definition: definition,
                            stream: definitionStream,
                            version: definitionVersion
                        );
                        break;
                    }
                case WorldFederationResponse.PresentationRows: {
                        if (replica is null ||
                            !Admits(frame.Body.Span, out var rowStream, out var rowBody, out _) ||
                            !WorldFederationCodec.TryDecodePresentationRows(rowBody, replica, out var rows, out var changed, out var rowVersion) ||
                            rowVersion.Activation != replicaVersion.Activation || rowVersion.Sequence <= replicaVersion.Sequence) { return false; }
                        replica = changed!;
                        replicaVersion = rowVersion;
                        Volatile.Write(ref m_definition, replica);
                        sink.DeliverPresentation(replica, rows!, rowVersion, rowStream);
                        break;
                    }
                case WorldFederationResponse.ProjectionDelta: {
                        if (!Admits(record: frame.Body.Span, stamp: out var deltaStream, body: out var deltaBody, failure: out var deltaFailure) || !WorldFederationCodec.TryDecodeProjectionDelta(
                            body: deltaBody,
                            definition: out var merged,
                            failure: out deltaFailure,
                            hold: hold,
                            stamp: out var deltaStamp,
                            valuesOnly: out var valuesOnly,
                            version: out var deltaVersion
                        )) {
                            if (m_narrationHub is { HasNarrationSink: true }) {
                                m_narrationHub?.Narrate(
                                    channel: "world.projection",
                                    text: $"[world.projection: remote observer '{Endpoint}' refused a projection delta ({deltaFailure})]"
                                );
                            }

                            return false;
                        }

                        Volatile.Write(
                            location: ref m_definition,
                            value: merged
                        );

                        if (valuesOnly) {
                            sink.DeliverState(
                                definition: merged,
                                stamp: in deltaStamp,
                                stream: deltaStream,
                                version: deltaVersion
                            );
                        } else {
                            sink.DeliverDefinition(
                                definition: merged,
                                stream: deltaStream,
                                version: deltaVersion
                            );
                        }

                        break;
                    }
                case WorldFederationResponse.Snapshot:
                case WorldFederationResponse.SnapshotDelta: {
                        if (!Admits(record: frame.Body.Span, stamp: out var snapshotStream, body: out var snapshotBody, failure: out var snapshotFailure) || !bodies.TryReceive(snapshotStream, (WorldFederationResponse)frame.Kind == WorldFederationResponse.SnapshotDelta, snapshotBody, out var snapshot)) {
                            if (m_narrationHub is { HasNarrationSink: true }) {
                                m_narrationHub?.Narrate(
                                    channel: "world.projection",
                                    text: $"[world.projection: remote observer '{Endpoint}' refused a snapshot record ({snapshotFailure})]"
                                );
                            }

                            return false;
                        }

                        _ = Interlocked.Exchange(
                            location1: ref m_lastObservedTickBits,
                            value: unchecked((long)snapshot.Tick)
                        );
                        Volatile.Write(
                            location: ref m_authority,
                            value: snapshot.Authority
                        );
                        sink.DeliverSnapshot(snapshot: in snapshot, stream: snapshotStream);
                        await stream.WriteAsync(frame.Body[..WorldFederationCodec.StreamStampBytes], ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                        break;
                    }
            }
        }
        return false;
    }
    // Authority processes may start in any order. An adjacency is durable topology, so its observation channel
    // cannot become permanently CLOSED merely because the neighbour had not bound its socket on the first tick.
    // Reconnect the same held lease until its owner releases it. Each session is a stream of its own: when one ends,
    // by a lost connection, an invalidation or the lease's release, the span it held closes, so the reader shows
    // nothing of it while disconnected, and the authored unavailable policy remains the crossing-side safety net.
    private async Task ObserveUntilCancelledAsync(WorldSinkRoute sink, CancellationToken ct) {
        var observed = new ObservedStream();

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
                    observed: observed,
                    sink: sink
                ).ConfigureAwait(continueOnCapturedContext: false)) {
                    continue;
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception exception) when ((exception is IOException or SocketException or OperationCanceledException)) {
                if (m_narrationHub is { HasNarrationSink: true }) {
                    m_narrationHub?.Narrate(
                        channel: "world.projection",
                        text: $"[world.projection: remote observer '{Endpoint}' unavailable ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")}); retrying]"
                    );
                }
            } finally {
                CloseObserved(observed: observed, sink: sink);
            }

            try {
                await Task.Delay(
                    cancellationToken: ct,
                    delay: ObserveRetryDelay,
                    timeProvider: m_clock
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

    /// <summary>Takes one observed route: the latest head of the traveler's route, which this authority's observation
    /// delivers frame by frame. It becomes the route a later <see cref="PublishClaim"/> redelivers, and is reported to the
    /// route's owner on the pump when this authority's outbox records, or at once otherwise. Serialized with
    /// <see cref="PublishClaim"/> by the route gate.</summary>
    /// <param name="route">The observed route.</param>
    public void ObserveRoute(WorldAuthorityRouteDescription route) {
        lock (m_observedRouteGate) {
            var changed = ((m_observedRoute is not { } observed) || (observed.Entity != route.Entity));

            m_observedRoute = route;
            if (
                changed &&
                (m_submissionCredential is { } credential)
            ) { ResendLastIntent(credential: in credential); }
            Volatile.Write(
                location: ref m_authority,
                value: route.Entity.Authority
            );
            _ = Interlocked.Exchange(
                location1: ref m_lastObservedTickBits,
                value: unchecked((long)route.Tick)
            );
            NotifyObservedRoute(route: route);
        }
    }
    /// <summary>Publishes the claim that makes a seat follow this route, then delivers the latest observed route to
    /// it, both under the route gate. Observation starts before the claim exists, so a route observed earlier reached an
    /// owner with no seat to update; it is delivered here, once the claim stands. A route observed while the claim is
    /// published waits on the gate and arrives after this delivery, so an older route can never overtake a newer one.
    /// The ordering belongs to this one method, never to the threads that happen to call it.</summary>
    /// <param name="publish">Publishes the seat claim.</param>
    public void PublishClaim(Action publish) {
        ArgumentNullException.ThrowIfNull(argument: publish);

        lock (m_observedRouteGate) {
            publish();

            if (m_observedRoute is { } route) {
                NotifyObservedRoute(route: route);
            }
        }
    }

    private void NotifyObservedRoute(WorldAuthorityRouteDescription route) {
        if (m_routeChanged is not { } listener) {
            return;
        }
        if (m_outbox.Records) {
            if (!m_routeDeliveryPending) {
                m_routeDeliveryPending = true;
                m_deliverObservedRoute!(obj: 0);
            }
        } else {
            m_routeDeliveryPending = false;
            listener(obj: route);
        }
    }
    private void DeliverPendingRoute() {
        WorldAuthorityRouteDescription route;

        lock (m_observedRouteGate) {
            if (!m_routeDeliveryPending || (m_observedRoute is not { } observed)) {
                return;
            }
            m_routeDeliveryPending = false;
            route = observed;
        }
        m_routeChanged!(obj: route);
    }

    // A Completion body is one whole downstream frame, decoded in place over the answer's own buffer.
    private static bool TryReadCompletion(ReadOnlyMemory<byte> body, out WorldSubmissionResult? result, out string reason) {
        result = null;

        if (!WorldPeerWireFormat.TryDecodeDownstream(
            body: out var completionBody,
            frame: body,
            kind: out var completionKind
        )) {
            reason = "forwarded authority returned an empty completion";
            return false;
        }

        return WorldPeerWireFormat.TryReadResult(
            body: completionBody.Span,
            kind: completionKind,
            reason: out reason,
            result: out result
        );
    }
    // Correlation names what a step is about beyond its source-scoped id, so two steps of one kind about different
    // questions (a hop query about another authority's key, other companions or the other phase) never share an answer.
    private bool TryResolveTransferStep(string sourceAuthority, ulong transferId, WorldFederationRequest kind, Func<byte[]> body, out WorldFederationAnswer answer, string correlation = "") {
        var key = new TransferStepKey(sourceAuthority, transferId, kind, correlation);
        if (!m_transferSteps.TryGetValue(key, out var task)) {
            task = AnswerAsync(sourceAuthority, kind, body());
            m_transferSteps[key] = task;
        }
        if (!task.IsCompleted) {
            answer = default;
            return false;
        }
        m_transferSteps.TryRemove(key, out _);
        answer = task.Result;
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
        var lease = new ObservationLease(parent: m_lifetime.Token);
        var route = m_outbox.RouteTo(
            sink: sink,
            fault: exception => {
                lease.Dispose();
                if (m_narrationHub is { HasNarrationSink: true }) {
                    m_narrationHub.Narrate(
                        channel: "world.projection",
                        text: $"[world.projection: remote observer '{Endpoint}' sink {sink.GetType().Name} threw — detached] {exception}"
                    );
                }
            }
        );

        _ = Task.Run(function: () => ObserveUntilCancelledAsync(
            ct: lease.Token,
            sink: route
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
        if (stream is not Puck.Networking.Peers.PeerStream peer) {
            throw new IOException(message: "Federation authentication requires a certificate-bound peer stream.");
        }
        var pinnedTransport = (m_security is IRemoteIdentityVerifier { HasRemoteIdentityPin: true });

        if (pinnedTransport && !((IRemoteIdentityVerifier)m_security).AcceptsRemoteIdentity(keyHash: peer.Link.RemoteId.KeyHash)) {
            throw new IOException(message: "The remote transport identity does not match this connection's credential policy.");
        }
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

        var clientChallenge = m_security.NewChallenge();
        byte[] proof;

        try {
            proof = m_security.Prove(challenge: WorldFederationCodec.BindProofChallenge(
                challenge: challenge.Body.Span,
                counterChallenge: clientChallenge,
                clientKeyHash: m_network.Peer.Id.KeyHash,
                serverKeyHash: peer.Link.RemoteId.KeyHash,
                serverProof: false));
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
            body: WorldFederationCodec.EncodeAuthentication(challenge: clientChallenge, proof: proof),
            ct: ct
        ).ConfigureAwait(continueOnCapturedContext: false);
        var verdict = await WorldFederationCodec.ReadResponseAsync(
            ct: ct,
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (
            !verdict.Ok ||
            (verdict.Kind != ((byte)WorldFederationResponse.Authenticated))
        ) {
            throw new IOException(message: DescribeHandshake(
                read: verdict,
                stage: "authentication"
            ));
        }
        if (!WorldFederationCodec.TryDecodeAuthorityIdentity(
            verdict.Body.Span,
            out var peerAuthority,
            out var serverProof,
            out var identityFailure
        )) {
            throw new IOException(message: $"federation authentication — invalid destination identity ({identityFailure})");
        }
        if (!pinnedTransport && (
            (await m_security.VerifyAsync(
                challenge: WorldFederationCodec.BindProofChallenge(
                    challenge: clientChallenge,
                    counterChallenge: challenge.Body.Span,
                    clientKeyHash: m_network.Peer.Id.KeyHash,
                    serverKeyHash: peer.Link.RemoteId.KeyHash,
                    serverProof: true,
                    authority: peerAuthority),
                proof: serverProof, ct: ct).ConfigureAwait(false))?.Authority != peerAuthority
        )) {
            throw new IOException(message: "federation authentication — destination did not prove its authority on this transport");
        }
        // Routed observations use their transaction authority's authenticated entry connection too, so private
        // onward world names never become socket endpoints or weaken the original peer namespace pin.
        if (!string.Equals(a: m_peerAuthority, b: peerAuthority, comparisonType: StringComparison.Ordinal)) {
            throw new IOException(message: $"federation destination authority mismatch: expected '{m_peerAuthority}', received '{peerAuthority}'");
        }
    }
    /// <summary>Resolves this transfer's commit step.</summary>
    public async Task<(WorldTransferStep Step, WorldTransferStatus Status, string Reason)> CommitAsync(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members) {
        var answer = await AnswerAsync(sourceAuthority, WorldFederationRequest.Commit,
            WorldFederationCodec.EncodeCommit(sourceAuthority, transferId, members)).ConfigureAwait(false);
        var step = DecodeCommitAnswer(answer, out var status, out var reason);
        return (step, status, reason);
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
    public async Task<WorldTransferReservationReply> ReserveAsync(WorldTransferReservationRequest request) {
        var answer = await AnswerAsync(request.SourceAuthority, WorldFederationRequest.Reserve,
            WorldFederationCodec.EncodeReservation(request)).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        if (answer.Kind != WorldFederationResponse.Reservation) {
            // A request or answer the lane lost decided nothing; an unconfigured federation identity never will.
            return WorldTransferReservationReply.Refused(reason: answer.Describe()) with {
                Transient = ((answer.Failure.Refusal is WireRefusal.LaneUnavailable or WireRefusal.RequestTimedOut or WireRefusal.ConnectionClosed) && !LacksSigningIdentity()),
            };
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

        return decoded;
    }
    /// <summary>Resolves this transfer's idempotent status.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="transferId">The transfer id.</param>
    /// <param name="status">The destination's verdict on success.</param>
    /// <returns><see langword="false"/> when the peer returned no usable status; the caller reconciles again later.</returns>
    public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
        status = WorldTransferStatus.Missing;

        if (!TryResolveTransferStep(
            sourceAuthority: sourceAuthority,
            transferId: transferId,
            kind: WorldFederationRequest.Status,
            body: () => WorldFederationCodec.EncodeTransferKey(
                sourceAuthority: sourceAuthority,
                transferId: transferId
            ),
            answer: out var answer
        )) { return false; }

        return ((answer.Kind == WorldFederationResponse.Status) && WorldFederationCodec.TryDecodeStatus(
            body: answer.Body.Span,
            landed: out _,
            status: out status
        ));
    }
    /// <summary>Reads the request-local ids a committed transfer landed at this peer.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace.</param>
    /// <param name="transferId">The transfer id.</param>
    /// <param name="landed">The landed ids; <see langword="null"/> when the peer holds no commit for the key.</param>
    /// <returns><see langword="false"/> when the peer returned no usable answer.</returns>
    public bool TryLanded(string sourceAuthority, ulong transferId, out IReadOnlyList<int>? landed) {
        landed = null;

        if (!TryResolveTransferStep(
            sourceAuthority: sourceAuthority,
            transferId: transferId,
            kind: WorldFederationRequest.Status,
            body: () => WorldFederationCodec.EncodeTransferKey(
                sourceAuthority: sourceAuthority,
                transferId: transferId
            ),
            answer: out var answer,
            correlation: "landed"
        )) { return false; }

        if ((answer.Kind != WorldFederationResponse.Status) || !WorldFederationCodec.TryDecodeStatus(
            body: answer.Body.Span,
            landed: out var ids,
            status: out _
        )) {
            return false;
        }
        landed = ids;
        return true;
    }
    /// <summary>Asks this peer, as a companion's title authority, whether each hop under <paramref name="key"/> is
    /// currently granted to <paramref name="destination"/>: the lane authenticates as the destination, so the peer
    /// answers for that namespace alone.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnations">The companions.</param>
    /// <param name="destination">The asking destination: this lane's authenticated namespace.</param>
    /// <param name="phase">The decision the question belongs to; it correlates the answer to this question alone.</param>
    public async ValueTask<IReadOnlyList<ulong?>?> ConfirmHopsAsync(WorldTransferKey key, IReadOnlyList<WorldEntityAddress> incarnations, string destination, WorldHopPhase phase) {
        var question = WorldFederationCodec.EncodeHopQuery(incarnations: incarnations, key: key, phase: phase);

        var answer = await AnswerAsync(destination, WorldFederationRequest.HopQuery, question).ConfigureAwait(false);

        return (((answer.Kind == WorldFederationResponse.HopQueryReply) && WorldFederationCodec.TryDecodeHopQueryReply(
            answers: out var answers,
            body: answer.Body.Span,
            failure: out _
        ) && (answers.Length == incarnations.Count))
            ? answers
            : null);
    }
    /// <summary>Asks this peer, as a hop's destination, what it holds for <paramref name="incarnation"/> under
    /// <paramref name="key"/>: the lane authenticates as <paramref name="asker"/>, the companion's title authority.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <param name="asker">The asking title authority: this lane's authenticated namespace.</param>
    /// <returns>The answer; <see cref="WorldCustodyAnswer.Uncertain"/> when the peer gave no usable answer.</returns>
    public WorldCustodyAnswer QueryCustody(WorldTransferKey key, WorldEntityAddress incarnation, string asker) {
        var question = WorldFederationCodec.EncodeCompanionHop(incarnation: incarnation, key: key);

        if (!TryResolveTransferStep(
            sourceAuthority: asker,
            transferId: key.TransferId,
            kind: WorldFederationRequest.CustodyQuery,
            body: () => question,
            answer: out var answer,
            correlation: Convert.ToHexString(inArray: question)
        )) { return WorldCustodyAnswer.Uncertain; }

        return (((answer.Kind == WorldFederationResponse.CustodyReply) && (answer.Body.Length == 1) && Enum.IsDefined(value: ((WorldCustodyAnswer)answer.Body.Span[0])))
            ? (WorldCustodyAnswer)answer.Body.Span[0]
            : WorldCustodyAnswer.Uncertain);
    }
    /// <summary>Tells this peer, as a companion's title authority, that <paramref name="source"/> settled its hop: the
    /// lane authenticates as the source.</summary>
    /// <param name="key">The hop's key, minted by <paramref name="source"/>.</param>
    /// <param name="incarnation">The companion.</param>
    /// <param name="source">The settling custodian: this lane's authenticated namespace.</param>
    /// <returns><see langword="true"/> once the peer acknowledged the notice.</returns>
    public bool SettleHop(WorldTransferKey key, WorldEntityAddress incarnation, string source) {
        var question = WorldFederationCodec.EncodeCompanionHop(incarnation: incarnation, key: key);

        if (!TryResolveTransferStep(
            sourceAuthority: source,
            transferId: key.TransferId,
            kind: WorldFederationRequest.HopSettled,
            body: () => question,
            answer: out var answer,
            correlation: Convert.ToHexString(inArray: question)
        )) { return false; }

        return ((answer.Kind == WorldFederationResponse.HopSettledAck) && (answer.Body.Length == 1) && (answer.Body.Span[0] == 1));
    }
    /// <summary>Asks this peer, as a companion's title authority, to authorize <paramref name="source"/>'s onward hop:
    /// the lane authenticates as the source.</summary>
    /// <param name="key">The crossing's key, minted by <paramref name="source"/>.</param>
    /// <param name="source">The asking custodian: this lane's authenticated namespace.</param>
    /// <param name="destination">Where the crossing goes.</param>
    /// <param name="members">The companions.</param>
    /// <returns>One answer per companion; each <see cref="WorldHopVerdict.Unavailable"/> when the peer gave no usable
    /// answer.</returns>
    public async ValueTask<IReadOnlyList<WorldHopAnswer>> RequestHopAsync(WorldTransferKey key, string source, string destination, IReadOnlyList<WorldHopRequestMember> members) {
        var question = WorldFederationCodec.EncodeHopRequest(destination: destination, key: key, members: members);

        var answer = await AnswerAsync(source, WorldFederationRequest.HopRequest, question).ConfigureAwait(false);

        return (((answer.Kind == WorldFederationResponse.HopAnswer) && WorldFederationCodec.TryDecodeHopAnswer(
            answers: out var answers,
            body: answer.Body.Span,
            failure: out _
        ) && (answers.Length == members.Count))
            ? answers
            : [.. members.Select(selector: static member => new WorldHopAnswer(Incarnation: member.Incarnation, Next: 0UL, Verdict: WorldHopVerdict.Unavailable))]);
    }

    private readonly record struct TransferStepKey(string SourceAuthority, ulong TransferId, WorldFederationRequest Kind, string Correlation = "");
    // One route republish generation: the endpoint to dial and its description, formatted once here so no attempt,
    // narration, or comparison formats it again, and held as one reference so a swap is atomic.
    private sealed class PublishedRoute(EndPoint endpoint) {
        public string Description { get; } = PeerEndpoint.Format(endpoint: endpoint);
        public EndPoint Endpoint { get; } = endpoint;

        public LaneRoute Lane => new(
            Description: Description,
            Endpoint: Endpoint
        );
    }
    // One authenticated, persistent control lane per source namespace. SubmitIntent only queues a batch and returns to
    // the local simulation immediately; the background lane pays connect and authentication once, then writes each
    // traveller's batches in order without waiting for an answer to any, so rendering and the boot clock never wait on
    // a network round trip and no batch waits behind another's. Batches are never merged: one can carry a pointer's
    // movement or a source's return to neutral that no event marks. A traveller's queue is bounded, and one that
    // fills is replaced by a single lost-capture batch, which restarts the traveller's input at the authority.
    private sealed class FederatedIntentPump : IDisposable {
        private readonly CancellationTokenSource m_lifetime;
        private readonly WorldRemoteAuthority m_owner;
        private readonly string m_sourceAuthority;
        private readonly Task m_worker;

        private int m_unavailable;

        private readonly ConcurrentDictionary<IntentKey, ConcurrentQueue<IntentSubmission>> m_pending = new();

        private bool HasPending => m_pending.Values.Any(predicate: static queue => !queue.IsEmpty);

        private readonly ConcurrentDictionary<IntentKey, SentIntent> m_sent = new();
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

        private async Task HandoffAsync(Stream stream, Task<WireFrameRead> answer, CancellationToken ct) {
            // This is an intentional route handoff, not a dropped client. Tell the older authority not to
            // synthesize a neutral release: the new lane will seed the same current held state.
            await WorldFederationCodec.WriteRequestAsync(
                body: default,
                ct: ct,
                kind: WorldFederationRequest.IntentStreamHandoff,
                stream: stream
            ).ConfigureAwait(continueOnCapturedContext: false);
            var handoff = await answer.ConfigureAwait(continueOnCapturedContext: false);

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
                    // (WSAECONNABORTED), so its ordinary shutdown arrives here identically to a fault. The authority
                    // restarts the traveller's input on the new lane and publishes the epoch it continues from.
                    if (
                        !established &&
                        (Interlocked.Exchange(
                        location1: ref m_unavailable,
                        value: 1
                    ) == 0)
                    ) {
                        if (m_owner.m_narrationHub is { HasNarrationSink: true }) {
                            m_owner.m_narrationHub?.Narrate(
                                channel: "world.authority unavailable",
                                text: $"[world.authority unavailable: intent stream to '{m_owner.Endpoint}' is reconnecting ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")})]"
                            );
                        }
                    }

                    try {
                        await Task.Delay(
                            cancellationToken: ct,
                            delay: IntentRetryDelay,
                            timeProvider: m_owner.m_clock
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    }
                    if (HasPending) {
                        try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
                    }
                } catch (Exception exception) {
                    // Not a transport failure: the lane could not encode or carry a batch. A worker that ended here
                    // would leave the seat's input going nowhere for the rest of the run with nothing said, so the
                    // lane names the failure every time and starts again. The batch that raised it was already taken
                    // from its queue, so the next connection does not meet it.
                    if (m_owner.m_narrationHub is { HasNarrationSink: true }) {
                        m_owner.m_narrationHub?.Narrate(
                            channel: "world.authority unavailable",
                            text: $"[world.authority unavailable: intent stream to '{m_owner.Endpoint}' failed and is restarting ({exception.GetType().Name}: {exception.Message.ReplaceLineEndings(replacementText: " ")})]"
                        );
                    }

                    try {
                        await Task.Delay(
                            cancellationToken: ct,
                            delay: IntentRetryDelay,
                            timeProvider: m_owner.m_clock
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    }
                    try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
                }
            }
        }
        private async Task RunConnectionAsync(Action established, CancellationToken ct) {
            // One route snapshot: the endpoint dialed and the description every later comparison names are the
            // same generation, and the comparisons below are against the owner's cached description, never a
            // fresh formatting.
            var route = m_owner.CurrentRoute();

            var connectedEndpoint = route.Description;
            await using var stream = await m_owner.m_network.ConnectAsync(
                route.Endpoint,
                ct
            ).ConfigureAwait(continueOnCapturedContext: false);

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

            // The lane writes each batch as it is made and waits for no answer to one: the authority answers an input in
            // its deliveries, and a lane that waited carried one batch a round trip, which left a backlog whenever a
            // round trip outlasted the interval between batches. The one pending read is the lane's only answer: the
            // authority's refusal, or its acknowledgement of a handoff.
            var answer = WorldFederationCodec.ReadResponseAsync(
                ct: ct,
                stream: stream
            );

            while (!ct.IsCancellationRequested) {
                if (!HasPending) {
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token: ct);
                    var signalled = m_signal.WaitAsync(cancellationToken: waiting.Token);

                    _ = await Task.WhenAny(signalled, answer).ConfigureAwait(continueOnCapturedContext: false);
                    if (!signalled.IsCompleted) {
                        // Withdrawn, so this wait cannot take a wake meant for the next connection.
                        waiting.Cancel();
                    }
                    try {
                        await signalled.ConfigureAwait(continueOnCapturedContext: false);
                    } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    }
                }
                if (answer.IsCompleted) {
                    var read = await answer.ConfigureAwait(continueOnCapturedContext: false);
                    var described = DescribeHandshake(read: read, stage: "intent stream update");

                    // A frame the authority wrote is its word on why the stream ends, unlike a lane that only closed:
                    // it is said every time, since the authority restarts every traveller's input on the next stream.
                    if (read.Ok && (m_owner.m_narrationHub is { HasNarrationSink: true } hub)) {
                        hub.Narrate(
                            channel: "world.authority unavailable",
                            text: $"[world.authority unavailable: '{m_owner.Endpoint}' ended the intent stream: {described}]"
                        );
                    }
                    throw new IOException(message: described);
                }
                if (!string.Equals(
                    a: connectedEndpoint,
                    b: m_owner.Endpoint,
                    comparisonType: StringComparison.Ordinal
                )) {
                    await HandoffAsync(
                        answer: answer,
                        ct: ct,
                        stream: stream
                    ).ConfigureAwait(continueOnCapturedContext: false);
                    return;
                }

                foreach (var pair in m_pending.ToArray()) {
                    var mobility = pair.Key.Mobility;

                    while (pair.Value.TryDequeue(result: out var sent)) {
                        await WorldFederationCodec.WriteRequestAsync(
                            body: WorldFederationCodec.EncodeIntent(
                                mobility: in mobility,
                                sourceAuthority: m_sourceAuthority,
                                submission: in sent
                            ),
                            ct: ct,
                            kind: WorldFederationRequest.Intent,
                            stream: stream
                        ).ConfigureAwait(continueOnCapturedContext: false);
                        m_sent[pair.Key] = new SentIntent(Submission: sent, Timestamp: m_owner.m_clock.GetTimestamp());
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
        public void ResendLastIntent(WorldMobilityIdentity mobility) {
            var key = new IntentKey(Mobility: mobility);

            if (m_sent.TryRemove(
                key: key,
                value: out var last
            )) {
                var queue = m_pending.GetOrAdd(key: key, valueFactory: static _ => new());

                lock (queue) { if (queue.IsEmpty) { queue.Enqueue(item: last.Submission); } }
            }
            try { _ = m_signal.Release(); } catch (SemaphoreFullException) { }
        }
        public void Publish(WorldMobilityIdentity mobility, in IntentSubmission submission) {
            var key = new IntentKey(Mobility: mobility);

            if (
                m_sent.TryGetValue(
                key: key,
                value: out var last
            ) &&
                (last.Submission == submission) &&
                (m_owner.m_clock.GetElapsedTime(startingTimestamp: last.Timestamp) < IntentRepeatWindow)
            ) {
                return;
            }
            var pending = m_pending.GetOrAdd(key: key, valueFactory: static _ => new());

            lock (pending) {
                if (pending.Count >= 1024) {
                    pending.Clear();
                    pending.Enqueue(item: submission with {
                        Intent = default,
                        Input = submission.Input with { CaptureLost = true, Image = default, Events = default, Contributions = default },
                    });
                } else { pending.Enqueue(item: submission); }
            }
            try {
                _ = m_signal.Release();
            } catch (SemaphoreFullException) {
                // One wake already covers the bounded ordered queues.
            }
        }
        public void Retire(WorldMobilityIdentity mobility) {
            var key = new IntentKey(Mobility: mobility);

            _ = m_pending.TryRemove(
                key: key,
                value: out _
            );
            _ = m_sent.TryRemove(
                key: key,
                value: out _
            );
        }

        private readonly record struct IntentKey(WorldMobilityIdentity Mobility);
        private readonly record struct SentIntent(IntentSubmission Submission, long Timestamp);
    }
}
