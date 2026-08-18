using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Re-materializes every live federation stream's latest device state into this authority tick. A row is
    // accepted only while the same peer principal still occupies its slot; an onward transfer leaves the old row
    // inert, and slot reuse can never inherit it. ApplyIntentSubmission remains the one Drive/grant/input-hold door.
    private void ApplyFederatedIntents() {
        for (var index = 0; (index < m_federatedIntents.Length); index++) {
            ref readonly var state = ref m_federatedIntents[index];

            if (
                !state.Active ||
                (Body(index: index) is not { } body) ||
                !m_population.IsAdmittedPeer(bodyIndex: index) ||
                (m_population.PeerPrincipal(index: index) != state.Principal)
            ) {
                continue;
            }

            var submission = state.Submission with { EntityIndex = index, Principal = state.Principal };

            _ = ApplyIntentSubmission(
                body: body,
                submission: in submission
            );
        }
    }

    /// <summary>Releases a reservation before commit. A destination that already committed ignores the abort.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    public void AbortTransfer(string sourceAuthority, ulong transferId) =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Abort(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));
    /// <summary>Retires the acknowledged transaction while preserving stable mobility replay protection.</summary>
    public void AcknowledgeTransfer(string sourceAuthority, ulong transferId) =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Acknowledge(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));
    /// <summary>Clears one exact authenticated arrival-border latch after reciprocal hysteresis is satisfied.
    /// Callers already execute under the authority operation gate.</summary>
    public bool ClearTransferArrivalBorder(int bodyIndex, string expectedBorder) =>
        m_transferEscrow.ClearArrivalBorder(
            bodyIndex: bodyIndex,
            expectedBorder: expectedBorder
        );
    /// <summary>Commits detached bodies into a live reservation. A repeated committed id is idempotently accepted;
    /// an expired or absent reservation is refused.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    /// <param name="members">The travelers in reservation order.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns>Whether the commit is authoritative at this destination.</returns>
    public bool CommitTransfer(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        var resolvedReason = string.Empty;
        var accepted = ExecuteAuthorityOperation(operation: () => m_transferEscrow.Commit(
            members: members,
            reason: out resolvedReason,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));

        reason = resolvedReason;
        return accepted;
    }
    /// <summary>Publishes one authenticated federation stream's latest device image. The image is held as replicated
    /// input state and reapplied once per destination tick; it is not consumed merely because this socket update was
    /// sparse relative to the destination clock.</summary>
    public void PublishFederatedIntent(long leaseId, in IntentSubmission submission) {
        if (
            (leaseId <= 0) ||
            (((uint)submission.EntityIndex) >= ((uint)m_federatedIntents.Length))
        ) {
            return;
        }

        var published = submission;

        ExecuteAuthorityOperation(operation: () => {
            ref var state = ref m_federatedIntents[published.EntityIndex];

            state = new FederatedIntentState(
                LeaseId: leaseId,
                Principal: published.Principal,
                Submission: published,
                Active: true
            );
        });
    }
    /// <summary>Releases every device image still owned by one closing federation stream. Lease comparison makes
    /// reconnect replacement atomic: a superseded stream cannot release the newer writer.</summary>
    public void ReleaseFederatedIntents(long leaseId) {
        if (leaseId <= 0) {
            return;
        }

        ExecuteAuthorityOperation(operation: () => {
            for (var index = 0; (index < m_federatedIntents.Length); index++) {
                if (
                    m_federatedIntents[index].Active &&
                    (m_federatedIntents[index].LeaseId == leaseId)
                ) {
                    m_federatedIntents[index] = default;
                }
            }
        });
    }
    /// <summary>Reserves destination body indices under a binding transfer lease. The same method backs loopback
    /// colocation and the TCP authority door; callers never reserve population capacity by inspecting it directly.</summary>
    /// <param name="request">The source-tick deadline, border policy, and prospective travelers.</param>
    /// <returns>The destination's verdict and assigned body indices.</returns>
    public WorldTransferReservationReply ReserveTransfer(WorldTransferReservationRequest request) =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Reserve(request: request));
    /// <summary>Terminally retires a traveler incarnation after its accepted leave has propagated through this hop.</summary>
    public void RetireTransferredMobility(in WorldMobilityIdentity mobility) {
        var credential = mobility;

        ExecuteAuthorityOperation(operation: () => m_transferEscrow.RetireMobility(mobility: in credential));
    }
    /// <summary>Returns the destination's idempotent view of a source-scoped transfer.</summary>
    public WorldTransferStatus TransferStatus(string sourceAuthority, ulong transferId) =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Status(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));
    /// <summary>Reads the authenticated source-border identity for an active escrow-arrived body. Callers already
    /// under the authority operation gate use this to apply reciprocal adjacency hysteresis.</summary>
    public bool TryTransferArrivalBorder(int bodyIndex, out string border) =>
        m_transferEscrow.TryArrivalBorder(
            bodyIndex: bodyIndex,
            border: out border
        );
    /// <summary>Resolves the ordinary peer principal a committed federated transfer assigned.</summary>
    public bool TryTransferredPrincipal(string sourceAuthority, ulong transferId, int ordinal, out WorldPrincipal principal) {
        var resolved = default(WorldPrincipal);
        var found = ExecuteAuthorityOperation(operation: () => m_transferEscrow.TryCommittedPrincipal(
            ordinal: ordinal,
            principal: out resolved,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));

        principal = resolved;
        return found;
    }
    /// <summary>Resolves a stable incarnation/epoch credential without retaining its disposable transfer id.</summary>
    public bool TryTransferredPrincipal(string sourceAuthority, in WorldMobilityIdentity mobility, out WorldPrincipal principal) {
        var resolved = default(WorldPrincipal);
        var credential = mobility;
        var found = ExecuteAuthorityOperation(operation: () => m_transferEscrow.TryMobilityPrincipal(
            mobility: in credential,
            principal: out resolved,
            sourceAuthority: sourceAuthority
        ));

        principal = resolved;
        return found;
    }

    private readonly record struct FederatedIntentState(long LeaseId, WorldPrincipal Principal, IntentSubmission Submission, bool Active);
}
