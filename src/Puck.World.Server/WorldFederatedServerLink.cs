using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The routed-request door a committed traveler's link submits through: the traveler's committed credential
/// for a body, the routed lane's bounded answer, and the intent lane. <see cref="WorldRemoteAuthority"/> is the one
/// implementation outside a law.</summary>
public interface IWorldRoutedRequests {
    /// <summary>Gets the hub a held credential is narrated through, or <see langword="null"/> for none.</summary>
    WorldOutputHub? NarrationHub { get; }

    /// <summary>Issues one routed request and returns its answer without waiting for it: the peer's answer, or a named
    /// refusal when the lane cannot carry it or the peer does not answer within the routed deadline.</summary>
    /// <param name="sourceAuthority">The authenticated source namespace whose lane carries the request.</param>
    /// <param name="kind">The request kind.</param>
    /// <param name="body">The encoded request leaf.</param>
    /// <returns>The answer, completed on whatever thread the lane answers on.</returns>
    Task<WorldFederationAnswer> AnswerAsync(string sourceAuthority, WorldFederationRequest kind, byte[] body);
    /// <summary>Returns the committed credential a body's submissions travel under.</summary>
    /// <param name="bodyIndex">The body index, or -1 for a body-agnostic submission.</param>
    /// <param name="sourceAuthority">The source namespace, on success.</param>
    /// <param name="mobility">The traveler credential, on success.</param>
    /// <returns><see langword="true"/> when a committed credential exists.</returns>
    bool TryCredential(int bodyIndex, out string sourceAuthority, out WorldMobilityIdentity mobility);
    /// <summary>Forwards one intent over the intent lane.</summary>
    /// <param name="bodyIndex">The body index.</param>
    /// <param name="submission">The intent image.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the intent was accepted.</returns>
    bool TryForwardIntent(int bodyIndex, in IntentSubmission submission, out string reason);
}
/// <summary>An <see cref="IServerLink"/> whose authority is a body committed by federated transfer. A mutation that
/// carries a completion is submitted without waiting: the call returns at once and the destination's verdict, or the
/// routed deadline's named refusal, reaches the completion on whatever thread the lane answers on. Every other
/// submission waits, bounded by the routed deadline, for the answer it hands back.</summary>
/// <param name="authority">The routed-request door.</param>
public sealed class WorldFederatedServerLink(IWorldRoutedRequests authority) : IServerLink {
    /// <summary>The refusal code a mutation's completion carries when no verdict came back.</summary>
    public const string CompletionUnavailableCode = "world.transport.completion-unavailable";

    private readonly IWorldRoutedRequests m_authority = authority;
    private readonly Lock m_unavailableGate = new();
    private readonly HashSet<int> m_unavailableBodies = [];

    private void NoteAvailable(int bodyIndex) {
        lock (m_unavailableGate) {
            _ = m_unavailableBodies.Remove(item: bodyIndex);
        }
    }
    private void NoteUnavailable(int bodyIndex, string reason) {
        bool first;

        lock (m_unavailableGate) {
            first = m_unavailableBodies.Add(item: bodyIndex);
        }

        if (first) {
            if (m_authority.NarrationHub is { HasNarrationSink: true }) {
                m_authority.NarrationHub?.Narrate(
                    channel: "world.authority unavailable",
                    text: $"[world.authority unavailable: body:{bodyIndex} input/submission held ({reason})]"
                );
            }
        }
    }
    // Encodes one submission under a body's committed credential: the request to send, or null when the credential or
    // the encoding is missing (narrated).
    private (string SourceAuthority, byte[] Body)? Request(int bodyIndex, WorldSubmissionPayload payload, Guid operationId) {
        if (!m_authority.TryCredential(
            bodyIndex: bodyIndex,
            mobility: out var mobility,
            sourceAuthority: out var sourceAuthority
        )) {
            NoteUnavailable(
                bodyIndex: bodyIndex,
                reason: "committed transfer credential is unavailable"
            );
            return null;
        }
        if (!WorldFrameCodec.TryEncode(
            failure: out var failure,
            frame: out var canonical,
            operationId: operationId,
            payload: payload
        )) {
            NoteUnavailable(
                bodyIndex: bodyIndex,
                reason: $"submission could not be encoded — {failure.Detail}"
            );
            return null;
        }

        return (sourceAuthority, WorldFederationCodec.EncodeSubmission(
            frame: canonical,
            mobility: in mobility,
            sourceAuthority: sourceAuthority
        ));
    }
    // Reads one routed answer as a downstream frame: a Completion's own frame, else a refusal naming what went wrong.
    private (WorldPeerWireFormat.DownstreamKind Kind, ReadOnlyMemory<byte> Body)? Read(int bodyIndex, WorldFederationAnswer response) {
        if (response.Kind != WorldFederationResponse.Completion) {
            var narration = response.Describe();

            NoteUnavailable(
                bodyIndex: bodyIndex,
                reason: narration
            );
            return (WorldPeerWireFormat.DownstreamKind.Refusal, System.Text.Encoding.UTF8.GetBytes(s: narration));
        }

        NoteAvailable(bodyIndex: bodyIndex);

        // A Completion body is one whole downstream frame, decoded in place over the response's own buffer.
        return (WorldPeerWireFormat.TryDecodeDownstream(
            body: out var completionBody,
            frame: response.Body,
            kind: out var completionKind
        )
            ? (completionKind, completionBody)
            : null
        );
    }
    // Turns a downstream frame into the typed result a completion receives, naming the failure when there is none.
    private static WorldSubmissionResult Result((WorldPeerWireFormat.DownstreamKind Kind, ReadOnlyMemory<byte> Body)? reply) {
        var reason = "remote authority did not return a completion";

        if ((reply is { } completed) && WorldPeerWireFormat.TryReadResult(
            body: completed.Body.Span,
            kind: completed.Kind,
            result: out var result,
            reason: out reason
        )) {
            return result!;
        }

        return new WorldSubmissionResult.Refusal(
            Code: CompletionUnavailableCode,
            Detail: reason
        );
    }
    private (WorldPeerWireFormat.DownstreamKind Kind, ReadOnlyMemory<byte> Body)? Submit(int bodyIndex, WorldSubmissionPayload payload, Guid operationId = default) {
        if (Request(
            bodyIndex: bodyIndex,
            operationId: operationId,
            payload: payload
        ) is not { } request) {
            return null;
        }

        return Read(
            bodyIndex: bodyIndex,
            response: m_authority.AnswerAsync(
                body: request.Body,
                kind: WorldFederationRequest.Submission,
                sourceAuthority: request.SourceAuthority
            ).GetAwaiter().GetResult()
        );
    }
    // Sends one submission without waiting: its answer reaches the completion when the lane delivers it, or the named
    // refusal when there is none. The routed lane is ordered, so submissions keep their order.
    private void SubmitWithoutWaiting(int bodyIndex, WorldSubmissionPayload payload, Guid operationId, Action<WorldSubmissionResult> completion) {
        if (Request(
            bodyIndex: bodyIndex,
            operationId: operationId,
            payload: payload
        ) is not { } request) {
            completion(obj: Result(reply: null));

            return;
        }

        _ = CompleteAsync(
            answer: m_authority.AnswerAsync(
                body: request.Body,
                kind: WorldFederationRequest.Submission,
                sourceAuthority: request.SourceAuthority
            ),
            bodyIndex: bodyIndex,
            completion: completion
        );
    }
    private async Task CompleteAsync(Task<WorldFederationAnswer> answer, int bodyIndex, Action<WorldSubmissionResult> completion) {
        WorldSubmissionResult result;

        try {
            result = Result(reply: Read(
                bodyIndex: bodyIndex,
                response: await answer.ConfigureAwait(continueOnCapturedContext: false)
            ));
        } catch (Exception exception) {
            result = new WorldSubmissionResult.Refusal(
                Code: CompletionUnavailableCode,
                Detail: exception.Message
            );
        }

        completion(obj: result);
    }

    public void Query(WorldQuery query, Action<QueryAnswer> completion) {
        ArgumentNullException.ThrowIfNull(completion);
        var bodyIndex = query switch {
            WorldQuery.PlayerWhere where => where.Index,
            WorldQuery.PlayerChannels channels => channels.Index,
            WorldQuery.PlayerState state => state.Index,
            WorldQuery.PlayerTargets targets => targets.Index,
            WorldQuery.Contacts contacts => (contacts.Index - 1),
            WorldQuery.MusicState music => (music.Index - 1),
            WorldQuery.InstrumentState instrument => (instrument.Index - 1),
            _ => -1,
        };
        var reply = Submit(
            bodyIndex: bodyIndex,
            payload: new WorldSubmissionPayload.Query(Value: query)
        );

        if (reply is null) {
            completion(new QueryAnswer(
                Text: "remote transfer credential is unavailable",
                Refused: true
            ));
            return;
        }
        if (!WorldPeerWireFormat.TryReadResult(
            body: reply.Value.Body.Span,
            kind: reply.Value.Kind,
            reason: out var reason,
            result: out var result
        )) {
            completion(new QueryAnswer(
                Refused: true,
                Text: reason
            ));
            return;
        }

        completion(((result as WorldSubmissionResult.Query)?.Answer ?? new QueryAnswer(
            Refused: true,
            Text: $"remote authority returned unsupported completion {reply.Value.Kind} for a query"
        )));
    }
    // The one abstract member every fire-and-forget Submit* interface default forwards to. A forwarded submission
    // routes by BODY, never by principal (the credential IS the authority — see TryCredential); Command/Designation
    // carry their own entity index, so route on it directly, everything else goes out under the traveler's own
    // committed body ("any"). principal is unused here — it never rode this transport; the interface parameter
    // exists for the loopback side, which routes on it for real. Returns 0: the remote authority mints the envelope,
    // so no local correlation exists for a deferred verdict to address.
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => SubmitEnvelope(
        operationId: Guid.Empty,
        payload: payload,
        principal: principal
    );
    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId) => SubmitEnvelope(
        completion: null,
        operationId: operationId,
        payload: payload,
        principal: principal
    );
    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId, Action<WorldSubmissionResult>? completion) {
        var bodyIndex = payload switch {
            WorldSubmissionPayload.Command command => command.Value.EntityIndex,
            WorldSubmissionPayload.Designation designation => designation.Value.EntityIndex,
            _ => -1,
        };

        // A mutation's verdict is its destination's tick-boundary answer: the caller holds a completion for it, so the
        // submitting thread never waits for that tick.
        if ((payload is WorldSubmissionPayload.Mutation) && (completion is not null)) {
            SubmitWithoutWaiting(
                bodyIndex: bodyIndex,
                completion: completion,
                operationId: operationId,
                payload: payload
            );

            return 0;
        }

        var reply = Submit(
            bodyIndex: bodyIndex,
            operationId: operationId,
            payload: payload
        );

        completion?.Invoke(obj: Result(reply: reply));

        return 0;
    }
    public void SubmitIntent(in IntentSubmission submission) {
        if (m_authority.TryForwardIntent(
            bodyIndex: submission.EntityIndex,
            submission: in submission,
            reason: out var reason
        )) {
            NoteAvailable(bodyIndex: submission.EntityIndex);
        } else {
            NoteUnavailable(
                bodyIndex: submission.EntityIndex,
                reason: reason
            );
        }
    }
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
        ArgumentNullException.ThrowIfNull(completion);
        var bodyIndex = request switch { SessionRequest.Join join => join.Slot, SessionRequest.Leave leave => leave.Slot, SessionRequest.SetIdentity identity => identity.Slot, _ => -1 };
        var reply = Submit(
            bodyIndex: bodyIndex,
            payload: new WorldSubmissionPayload.Session(Value: request)
        );

        if (reply is null) {
            completion(new SessionReply(
                Accepted: false,
                AssignedIndex: -1,
                Reason: "remote transfer credential is unavailable",
                RosterEcho: string.Empty
            ));
            return;
        }
        if (!WorldPeerWireFormat.TryReadResult(
            body: reply.Value.Body.Span,
            kind: reply.Value.Kind,
            reason: out var reason,
            result: out var result
        )) {
            completion(new SessionReply(
                Accepted: false,
                AssignedIndex: -1,
                Reason: reason,
                RosterEcho: string.Empty
            ));
            return;
        }

        completion(((result as WorldSubmissionResult.Session)?.Reply ?? new SessionReply(
            false,
            -1,
            string.Empty,
            $"remote authority returned unsupported completion {reply.Value.Kind} for a session request"
        )));
    }
}
