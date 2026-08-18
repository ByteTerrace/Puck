using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Test furniture on the <see cref="IWorldPeerCall"/> seam (<see cref="WorldInstanceHost.SetPeerCallFault"/>):
/// forwards every call straight through to a co-hosted destination's own server, except the FIRST
/// <see cref="Commit"/>, which the destination genuinely reserves against but this decorator reports
/// <see cref="WorldTransferStep.Unreachable"/> for — a lease held, uncommitted, exactly what a lost commit
/// acknowledgement over a real socket leaves behind. Every later <see cref="Commit"/> call for the SAME instance
/// (the shape <c>ReconcileInDoubtTransfers</c> retries with) applies for real.</summary>
internal sealed class FaultingPeerCall : IWorldPeerCall {
    private readonly WorldServer m_destination;

    private int m_commitCalls;

    /// <summary>Initializes the decorator over the real destination server it forwards to.</summary>
    /// <param name="destination">The destination this decorator forwards every call to.</param>
    public FaultingPeerCall(WorldServer destination) {
        ArgumentNullException.ThrowIfNull(argument: destination);

        m_destination = destination;
    }

    /// <summary>Gets how many times <see cref="Commit"/> has been called — the first call always faults.</summary>
    public int CommitCalls => m_commitCalls;

    /// <inheritdoc/>
    public void Abort(string sourceAuthority, ulong transferId) => m_destination.AbortTransfer(
        sourceAuthority: sourceAuthority,
        transferId: transferId
    );
    /// <inheritdoc/>
    public void Acknowledge(string sourceAuthority, ulong transferId) => m_destination.AcknowledgeTransfer(
        sourceAuthority: sourceAuthority,
        transferId: transferId
    );
    /// <inheritdoc/>
    public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out bool accepted, out string reason) {
        if (Interlocked.Increment(location: ref m_commitCalls) == 1) {
            accepted = false;
            reason = string.Empty;

            return WorldTransferStep.Unreachable;
        }

        accepted = m_destination.CommitTransfer(
            members: members,
            reason: out reason,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );

        return WorldTransferStep.Answered;
    }
    /// <inheritdoc/>
    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => m_destination.ReserveTransfer(request: request);
    /// <inheritdoc/>
    public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
        status = m_destination.TransferStatus(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );

        return true;
    }
}
