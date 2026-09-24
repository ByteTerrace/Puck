using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldRemoteAuthority {
    /// <summary>Polls one exact transfer status without waiting for network I/O. Repeated polls share the in-flight
    /// request. An unavailable, malformed, or unfinished response leaves the transaction unresolved.</summary>
    /// <param name="sourceAuthority">The original authenticated source namespace.</param>
    /// <param name="transferId">The original source-scoped transaction id.</param>
    /// <param name="status">The destination verdict on success; otherwise not usable.</param>
    /// <returns>True only when a complete, valid status response is available.</returns>
    public bool PollStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
        status = default;
        if (
            !TryPollTransferStep(
            answer: out var answer,
            kind: WorldFederationRequest.Status,
            members: null,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ) ||
            !answer.Ok ||
            (answer.Kind != WorldFederationResponse.Status) ||
            (answer.Body.Length != 1) ||
            !Enum.IsDefined(value: ((WorldTransferStatus)answer.Body.Span[0]))
        ) { return false; }
        status = ((WorldTransferStatus)answer.Body.Span[0]);
        return true;
    }
    /// <summary>Polls a reserved transaction's exact commit without waiting for network I/O. The caller keeps the
    /// member payload unchanged until an answer arrives and retains source recovery for every ambiguous answer.</summary>
    /// <param name="sourceAuthority">The original authenticated source namespace.</param>
    /// <param name="transferId">The original source-scoped transaction id.</param>
    /// <param name="members">The exact retained commit payload.</param>
    /// <param name="accepted">The destination's acceptance, meaningful only for Answered.</param>
    /// <param name="reason">The refusal detail, or empty while pending.</param>
    /// <returns>Pending while in flight, Answered for a valid verdict, or Unreachable for an ambiguous response.</returns>
    public WorldTransferStep PollCommit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members,
        out bool accepted, out string reason) {
        if (!TryPollTransferStep(
            answer: out var answer,
            kind: WorldFederationRequest.Commit,
            members: members,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        )) {
            accepted = false;
            reason = string.Empty;
            return WorldTransferStep.Pending;
        }
        return DecodeCommitAnswer(
            accepted: out accepted,
            answer: answer,
            reason: out reason
        );
    }

    // The factory runs only when this key has no in-flight request. In particular, neither the commit encoder nor
    // a closure is allocated on the polling path. The transport worker owns timeouts; the simulation never waits.
    private bool TryPollTransferStep(string sourceAuthority, ulong transferId, WorldFederationRequest kind,
        IReadOnlyList<WorldTransferCommitMember>? members, out WorldFederationAnswer answer) {
        if (m_submissionAuthority is { } upstream) {
            return upstream.TryPollTransferStep(
            answer: out answer,
            kind: kind,
            members: members,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
        }
        answer = default;
        var key = new TransferStepKey(
            Kind: kind,
            SourceAuthority: sourceAuthority,
            TransferId: transferId
        );

        if (!m_transferSteps.TryGetValue(
            key: key,
            value: out var task
        )) {
            if (LacksSigningIdentity()) { return false; }
            if (!m_requestLanes.TryGetValue(
                key: (sourceAuthority, LaneOf(kind: kind)),
                value: out var lane
            )) {
                lane = LaneFor(
                kind: kind,
                sourceAuthority: sourceAuthority
            );
            }
            if (!lane.IsAvailable) { return false; }
            task = m_transferSteps.GetOrAdd(
                key,
                static (step, state) => EnqueueAnswerAsync(
                    state.Lane,
                    step.Kind,
                    ((step.Kind == WorldFederationRequest.Commit)
                ? WorldFederationCodec.EncodeCommit(
                            step.SourceAuthority,
                            step.TransferId,
                            state.Members!
                        )
                : WorldFederationCodec.EncodeTransferKey(
                            sourceAuthority: step.SourceAuthority,
                            transferId: step.TransferId
                        ))
                ),
                (Lane: lane, Members: members)
            );
        }
        if (!task.IsCompleted) { return false; }
        _ = m_transferSteps.TryRemove(item: new KeyValuePair<TransferStepKey, Task<WorldFederationAnswer>>(
            key: key,
            value: task
        ));
        answer = (task.IsCompletedSuccessfully
            ? task.Result
            : WorldFederationAnswer.Refused(
                detail: "transfer recovery request ended without a response",
                refusal: Puck.Networking.WireRefusal.ConnectionClosed
            )
        );
        return true;
    }
    private static WorldTransferStep DecodeCommitAnswer(WorldFederationAnswer answer, out bool accepted, out string reason) {
        accepted = false;
        if (
            !answer.Ok ||
            (answer.Kind != WorldFederationResponse.Commit)
        ) {
            reason = answer.Describe();
            return WorldTransferStep.Unreachable;
        }
        if (!WorldFederationCodec.TryDecodeCommitReply(
            answer.Body.Span,
            out accepted,
            out reason,
            out var failure
        )) {
            accepted = false;
            reason = $"invalid commit reply: {failure}";
            return WorldTransferStep.Unreachable;
        }
        return WorldTransferStep.Answered;
    }
}
