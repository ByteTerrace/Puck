namespace Puck.World.Server;

/// <summary>
/// The federation peer-call seam, in Puck's own typed vocabulary — never <c>byte[]</c>. Synchronous throughout,
/// matching every member it wraps (<see cref="WorldServer.ReserveTransfer"/>/<see cref="WorldServer.CommitTransfer"/>/
/// <see cref="WorldServer.AbortTransfer"/>/<see cref="WorldServer.AcknowledgeTransfer"/>/
/// <see cref="WorldServer.TransferStatus"/> are all plain synchronous calls under the server's own authority gate,
/// never <c>Task</c>-shaped) and matching the one thing a tick's own outbound continuation resolution can tolerate
/// calling mid-step: a blocking call, never an awaited one. An in-process implementation calls straight through to a
/// co-located peer's <see cref="WorldServer"/>; a remote implementation serializes the same contract over the wire
/// (<see cref="WorldFederationCodec"/>). No caller branches on which implementation it holds.
/// </summary>
public interface IWorldPeerCall {
    /// <summary>Reserves a transfer on the peer this call addresses.</summary>
    /// <param name="request">The reservation request, matching <see cref="WorldServer.ReserveTransfer"/>'s own
    /// parameter shape.</param>
    /// <returns>The peer's reservation reply.</returns>
    WorldTransferReservationReply Reserve(WorldTransferReservationRequest request);
    /// <summary>Commits a previously reserved transfer on the peer this call addresses. A local peer always answers
    /// <see cref="WorldTransferStep.Answered"/>; a remote peer whose transport failed answers
    /// <see cref="WorldTransferStep.Unreachable"/> — the caller's evidence that a commit the destination may or may
    /// not have applied is IN DOUBT, never a plain refusal.</summary>
    /// <param name="sourceAuthority">The reserving authority's own endpoint identity.</param>
    /// <param name="transferId">The source-scoped transfer id the reservation was minted under.</param>
    /// <param name="members">The traveler set being committed.</param>
    /// <param name="accepted">Whether the destination's own verdict accepted the commit — meaningful only when this
    /// method returns <see cref="WorldTransferStep.Answered"/>.</param>
    /// <param name="reason">The refusal reason when <paramref name="accepted"/> is <see langword="false"/>; empty
    /// on acceptance.</param>
    /// <returns>What this answer is evidence of.</returns>
    WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out bool accepted, out string reason);
    /// <summary>Acknowledges a committed transfer, closing it on the peer this call addresses — matching
    /// <see cref="WorldServer.AcknowledgeTransfer"/>.</summary>
    /// <param name="sourceAuthority">The reserving authority's own endpoint identity.</param>
    /// <param name="transferId">The transfer id being acknowledged.</param>
    void Acknowledge(string sourceAuthority, ulong transferId);
    /// <summary>Aborts a reserved-but-uncommitted transfer on the peer this call addresses — matching
    /// <see cref="WorldServer.AbortTransfer"/>.</summary>
    /// <param name="sourceAuthority">The reserving authority's own endpoint identity.</param>
    /// <param name="transferId">The transfer id being aborted.</param>
    void Abort(string sourceAuthority, ulong transferId);
    /// <summary>Attempts to read a transfer's status from the peer this call addresses.</summary>
    /// <param name="sourceAuthority">The reserving authority's own endpoint identity.</param>
    /// <param name="transferId">The transfer id being queried.</param>
    /// <param name="status">The peer-reported status on a <see langword="true"/> return.</param>
    /// <returns><see langword="true"/> once the peer answered (including <see cref="WorldTransferStatus.Missing"/>);
    /// <see langword="false"/> when the call itself could not reach the peer (a remote implementation's transport
    /// failure — an in-process implementation always succeeds).</returns>
    bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status);
}
