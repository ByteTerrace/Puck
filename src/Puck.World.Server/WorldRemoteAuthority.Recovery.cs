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
        if (!TryPollTransferStep(sourceAuthority, transferId, WorldFederationRequest.Status, null, out var answer) ||
            !answer.Ok || answer.Kind != WorldFederationResponse.Status || answer.Body.Length != 1 ||
            !Enum.IsDefined((WorldTransferStatus)answer.Body.Span[0])) { return false; }
        status = (WorldTransferStatus)answer.Body.Span[0];
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
        if (!TryPollTransferStep(sourceAuthority, transferId, WorldFederationRequest.Commit, members, out var answer)) {
            accepted = false;
            reason = string.Empty;
            return WorldTransferStep.Pending;
        }
        return DecodeCommitAnswer(answer, out accepted, out reason);
    }

    // The factory runs only when this key has no in-flight request. In particular, neither the commit encoder nor
    // a closure is allocated on the polling path. The transport worker owns timeouts; the simulation never waits.
    private bool TryPollTransferStep(string sourceAuthority, ulong transferId, WorldFederationRequest kind,
        IReadOnlyList<WorldTransferCommitMember>? members, out WorldFederationAnswer answer) {
        if (m_submissionAuthority is { } upstream) { return upstream.TryPollTransferStep(sourceAuthority, transferId, kind, members, out answer); }
        answer = default;
        var key = new TransferStepKey(sourceAuthority, transferId, kind);
        if (!m_transferSteps.TryGetValue(key, out var task)) {
            if (LacksSigningIdentity()) { return false; }
            if (!m_requestLanes.TryGetValue((sourceAuthority, LaneOf(kind)), out var lane)) { lane = LaneFor(sourceAuthority, kind); }
            if (!lane.IsAvailable) { return false; }
            task = m_transferSteps.GetOrAdd(key, static (step, state) => EnqueueAnswerAsync(state.Lane, step.Kind,
                step.Kind == WorldFederationRequest.Commit
                    ? WorldFederationCodec.EncodeCommit(step.SourceAuthority, step.TransferId, state.Members!)
                    : WorldFederationCodec.EncodeTransferKey(step.SourceAuthority, step.TransferId)), (Lane: lane, Members: members));
        }
        if (!task.IsCompleted) { return false; }
        _ = m_transferSteps.TryRemove(new KeyValuePair<TransferStepKey, Task<WorldFederationAnswer>>(key, task));
        answer = task.IsCompletedSuccessfully ? task.Result : WorldFederationAnswer.Refused(
            Puck.Networking.WireRefusal.ConnectionClosed, "transfer recovery request ended without a response");
        return true;
    }

    private static WorldTransferStep DecodeCommitAnswer(WorldFederationAnswer answer, out bool accepted, out string reason) {
        accepted = false;
        if (!answer.Ok || answer.Kind != WorldFederationResponse.Commit) {
            reason = answer.Describe();
            return WorldTransferStep.Unreachable;
        }
        if (!WorldFederationCodec.TryDecodeCommitReply(answer.Body.Span, out accepted, out reason, out var failure)) {
            accepted = false;
            reason = $"invalid commit reply: {failure}";
            return WorldTransferStep.Unreachable;
        }
        return WorldTransferStep.Answered;
    }
}
