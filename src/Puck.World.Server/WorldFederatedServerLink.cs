using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>An <see cref="IServerLink"/> whose authority is a body committed by federated transfer.</summary>
internal sealed class WorldFederatedServerLink(WorldRemoteAuthority authority) : IServerLink {
    private readonly WorldRemoteAuthority m_authority = authority;
    private readonly HashSet<int> m_unavailableBodies = [];

    private void NoteUnavailable(int bodyIndex, string reason) {
        if (m_unavailableBodies.Add(item: bodyIndex)) {
            if (m_authority.NarrationHub is { HasNarrationSink: true }) {
                m_authority.NarrationHub?.Narrate(
                    channel: "world.authority unavailable",
                    text: $"[world.authority unavailable: body:{bodyIndex} input/submission held ({reason})]"
                );
            }
        }
    }
    private (WorldPeerWireFormat.DownstreamKind Kind, ReadOnlyMemory<byte> Body)? Submit(int bodyIndex, WorldSubmissionPayload payload, Guid operationId = default) {
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
            payload: payload,
            operationId: operationId
        )) {
            NoteUnavailable(
                bodyIndex: bodyIndex,
                reason: $"submission could not be encoded — {failure.Detail}"
            );
            return null;
        }

        var body = WorldFederationCodec.EncodeSubmission(
            frame: canonical,
            mobility: in mobility,
            sourceAuthority: sourceAuthority
        );
        var response = m_authority.AwaitAnswer(
            body: body,
            kind: WorldFederationRequest.Submission,
            sourceAuthority: sourceAuthority
        );

        if (response.Kind != WorldFederationResponse.Completion) {
            var narration = response.Describe();

            NoteUnavailable(
                bodyIndex: bodyIndex,
                reason: narration
            );
            return (WorldPeerWireFormat.DownstreamKind.Refusal, System.Text.Encoding.UTF8.GetBytes(s: narration));
        }

        _ = m_unavailableBodies.Remove(item: bodyIndex);

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
    private (WorldPeerWireFormat.DownstreamKind Kind, ReadOnlyMemory<byte> Body)? SubmitAny(WorldSubmissionPayload payload, Guid operationId = default) => Submit(
        bodyIndex: -1,
        payload: payload,
        operationId: operationId
    );

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
    public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal) => SubmitEnvelope(payload, principal, Guid.Empty);

    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal, Guid operationId) => SubmitEnvelope(payload, principal, operationId, null);

    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal, Guid operationId, Action<WorldSubmissionResult>? completion) {
        var reply = (payload switch {
            WorldSubmissionPayload.Command command => Submit(
                bodyIndex: command.Value.EntityIndex,
                payload: payload,
                operationId: operationId
            ),
            WorldSubmissionPayload.Designation designation => Submit(
                bodyIndex: designation.Value.EntityIndex,
                payload: payload,
                operationId: operationId
            ),
            _ => SubmitAny(payload: payload, operationId: operationId),
        });

        // Fire-and-forget submissions retain their historical return shape. A typed caller can use the additive
        // callback overload; the response was already decoded by Submit and is intentionally not reconstructed here
        // until the server link's response routing is moved to the shared completion owner.
        if (completion is not null) {
            var completionReason = "remote authority did not return a completion";
            WorldSubmissionResult? result = null;
            var reason = string.Empty;
            var decoded = reply is { } completed && WorldPeerWireFormat.TryReadResult(
                body: completed.Body.Span,
                kind: completed.Kind,
                result: out result,
                reason: out reason
            );
            if (decoded) {
                completion(result!);
            } else {
                if (reply is { }) { completionReason = reason; }
                completion(new WorldSubmissionResult.Refusal(
                    Code: "world.transport.completion_unavailable",
                    Detail: completionReason
                ));
            }
        }

        return 0;
    }
    public void SubmitIntent(in IntentSubmission submission) {
        if (m_authority.TryForwardIntent(
            bodyIndex: submission.EntityIndex,
            submission: in submission,
            reason: out var reason
        )) {
            _ = m_unavailableBodies.Remove(item: submission.EntityIndex);
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
