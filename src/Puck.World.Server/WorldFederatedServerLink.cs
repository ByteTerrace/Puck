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
    bool TryRouteCredential(int bodyIndex, out WorldRemoteRouteCredential credential);

    /// <summary>Sends a submission whose destination verdict arrives asynchronously through the completion.</summary>
    bool TryForwardSubmission(int bodyIndex, WorldSubmissionPayload payload, Guid operationId, Action<WorldSubmissionResult> completion, out string reason) {
        if (!TryRouteCredential(bodyIndex, out var credential)) {
            reason = $"forwarded body:{bodyIndex} has no committed destination credential";
            return false;
        }

        return WorldRemoteAuthority.TrySendSubmission(this, in credential, payload, operationId, completion, out reason);
    }
    /// <summary>Forwards one intent over the intent lane.</summary>
    /// <param name="bodyIndex">The body index.</param>
    /// <param name="submission">The intent image.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the intent was accepted.</returns>
    bool TryForwardIntent(int bodyIndex, in IntentSubmission submission, out string reason);
}
/// <summary>Routes inputs and submissions for a committed traveler. Each completion receives the destination's
/// verdict or the transport refusal when the ordered lane answers.</summary>
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
    private void SubmitWithoutWaiting(int bodyIndex, WorldSubmissionPayload payload, Guid operationId, Action<WorldSubmissionResult> completion) {
        if (!m_authority.TryForwardSubmission(bodyIndex, payload, operationId, Complete, out var reason)) {
            Complete(new WorldSubmissionResult.Refusal(CompletionUnavailableCode, reason));
        }

        void Complete(WorldSubmissionResult result) {
            if (result is WorldSubmissionResult.Refusal { Code: CompletionUnavailableCode } unavailable) {
                NoteUnavailable(bodyIndex, unavailable.Detail);
            } else {
                NoteAvailable(bodyIndex);
            }

            completion(result);
        }
    }

    // Routes by body like every forwarded submission: the remote authority answers the traveler whose credential the
    // query rides, so principal never crosses this transport (see SubmitEnvelope).
    public void Query(WorldQuery query, Principal principal, Action<QueryAnswer> completion) {
        ArgumentNullException.ThrowIfNull(completion);
        var bodyIndex = query switch {
            WorldQuery.PlayerWhere where => where.Index,
            WorldQuery.PlayerChannels channels => channels.Index,
            WorldQuery.PlayerInput input => input.Index,
            WorldQuery.PlayerAffordances affordances => affordances.Index,
            WorldQuery.PlayerPlace place => place.Index,
            WorldQuery.PlayerState state => state.Index,
            WorldQuery.PlayerTargets targets => targets.Index,
            WorldQuery.Contacts contacts => (contacts.Index - 1),
            WorldQuery.MusicClock music => (music.Index - 1),
            WorldQuery.InstrumentState instrument => (instrument.Index - 1),
            _ => -1,
        };
        SubmitWithoutWaiting(bodyIndex, new WorldSubmissionPayload.Query(query), Guid.Empty, result =>
            completion(result is WorldSubmissionResult.Query answered ? answered.Answer :
                new QueryAnswer((result as WorldSubmissionResult.Refusal)?.Detail ?? "unexpected remote query completion", Refused: true)));
    }
    // The one abstract member every fire-and-forget Submit* interface default forwards to. A forwarded submission
    // routes by BODY, never by principal (the credential IS the authority — see TryRouteCredential); Command/Designation
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

        SubmitWithoutWaiting(bodyIndex, payload, operationId, completion ?? (static _ => { }));
        return 0;
    }
    public void SubmitIntent(in IntentSubmission submission) {
        if (m_authority.TryForwardIntent(
            bodyIndex: submission.EntityIndex,
            submission: in submission,
            reason: out var reason
        )) {
            RcProbe.Note($"link.intent body:{submission.EntityIndex} forwarded owner={submission.InputOwner?.Describe() ?? "none"} principal={submission.Principal.Describe()}");
            if (!submission.Input.Events.IsEmpty) {
                RcProbe.Note("link.intent events", $"tick={submission.Tick} batchTick={submission.Input.Tick} revision={submission.Input.Revision} epoch={submission.Input.Epoch} events={submission.Input.Events.Length} firstCapture={submission.Input.Events.Span[0].CaptureTick} offset={submission.Input.Events.Span[0].Offset}");
            }
            NoteAvailable(bodyIndex: submission.EntityIndex);
        } else {
            RcProbe.Note($"link.intent body:{submission.EntityIndex} refused: {reason}");
            NoteUnavailable(
                bodyIndex: submission.EntityIndex,
                reason: reason
            );
        }
    }
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
        ArgumentNullException.ThrowIfNull(completion);
        var bodyIndex = request switch { SessionRequest.Join join => join.Slot, SessionRequest.Leave leave => leave.Slot, SessionRequest.SetIdentity identity => identity.Slot, _ => -1 };
        SubmitWithoutWaiting(bodyIndex, new WorldSubmissionPayload.Session(request), Guid.Empty, result =>
            completion(result is WorldSubmissionResult.Session answered ? answered.Reply :
                new SessionReply(false, -1, string.Empty, (result as WorldSubmissionResult.Refusal)?.Detail ?? "unexpected remote session completion")));
    }
}
