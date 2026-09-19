using Puck.Hosting;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the tick facade — the ordered domain and the intent queue, the step clock, the contribution
    /// fold and its read-back, and the per-tick engagement, transfer, field, music, response, placement-deal and
    /// board-enforcement work.</summary>
    public WorldTick Tick => m_tick;
    /// <summary>Gets whether this activation has frozen for retirement, without taking the authority gate — the
    /// raw read a caller already holding it needs.</summary>
    public bool AuthorityRetiring => m_authorityRetiring;
    /// <summary>Gets the search jobs over the arena, rebuilt with the rules on every install.</summary>
    public ArenaSearch Search => m_search;
    /// <summary>Gets the routine a landed search job's writes install through.</summary>
    public Func<IReadOnlyList<ArenaSearchWrite>, bool> SearchApply => m_searchApply;
    /// <summary>Gets the transfer escrow — bounded transfer transactions, mobility credentials and in-flight
    /// leases.</summary>
    public WorldTransferEscrow TransferEscrow => m_transferEscrow;

    /// <inheritdoc cref="WorldTick.AbortTransfer"/>
    public void AbortTransfer(string sourceAuthority, ulong transferId) =>
        m_tick.AbortTransfer(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
    /// <inheritdoc cref="WorldTick.AcknowledgeTransfer"/>
    public void AcknowledgeTransfer(string sourceAuthority, ulong transferId) =>
        m_tick.AcknowledgeTransfer(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
    /// <inheritdoc cref="WorldTick.Advance"/>
    public void Advance(ulong stepTicks) => m_tick.Advance(stepTicks: stepTicks);
    /// <inheritdoc cref="WorldTick.ApplyIntentSubmission"/>
    public GrantVerdict ApplyIntentSubmission(WorldBody body, in IntentSubmission submission) =>
        m_tick.ApplyIntentSubmission(
            body: body,
            submission: in submission
        );
    /// <inheritdoc cref="WorldTick.ApplyServerEvent"/>
    public void ApplyServerEvent(WorldServerEvent serverEvent) => m_tick.ApplyServerEvent(serverEvent: serverEvent);
    /// <inheritdoc cref="WorldTick.CheckEngagePolicy"/>
    public bool CheckEngagePolicy(int entityIndex, GrantSubject target, out string reason) =>
        m_tick.CheckEngagePolicy(
            entityIndex: entityIndex,
            reason: out reason,
            target: target
        );
    /// <inheritdoc cref="WorldTick.ClearTransferArrivalBorder"/>
    public bool ClearTransferArrivalBorder(int bodyIndex, string expectedBorder) =>
        m_tick.ClearTransferArrivalBorder(
            bodyIndex: bodyIndex,
            expectedBorder: expectedBorder
        );
    /// <inheritdoc cref="WorldTick.CommitTransfer"/>
    public bool CommitTransfer(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) =>
        m_tick.CommitTransfer(
            members: members,
            reason: out reason,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
    /// <inheritdoc cref="WorldTick.ConstrainTransferAuthorities"/>
    public void ConstrainTransferAuthorities(Func<string, bool> allowed) => m_tick.ConstrainTransferAuthorities(allowed: allowed);
    /// <inheritdoc cref="WorldTick.DescribeFields"/>
    public string DescribeFields() => m_tick.DescribeFields();
    /// <inheritdoc cref="WorldTick.DescribeMusicState"/>
    public string DescribeMusicState() => m_tick.DescribeMusicState();
    /// <inheritdoc cref="WorldTick.DescribePlacements"/>
    public string DescribePlacements() => m_tick.DescribePlacements();
    /// <inheritdoc cref="WorldTick.DescribeResponses"/>
    public string DescribeResponses() => m_tick.DescribeResponses();
    /// <inheritdoc cref="WorldTick.DispatchServerEvent"/>
    public void DispatchServerEvent(WorldServerEvent serverEvent, bool ordered) =>
        m_tick.DispatchServerEvent(
            ordered: ordered,
            serverEvent: serverEvent
        );
    /// <inheritdoc cref="WorldTick.DrainAdministrative"/>
    public bool DrainAdministrative() => m_tick.DrainAdministrative();
    /// <inheritdoc cref="WorldTick.EnqueueIntent"/>
    public void EnqueueIntent(in IntentSubmission submission) => m_tick.EnqueueIntent(submission: in submission);
    /// <inheritdoc cref="WorldTick.PublishFederatedIntent"/>
    public void PublishFederatedIntent(long leaseId, in IntentSubmission submission) =>
        m_tick.PublishFederatedIntent(
            leaseId: leaseId,
            submission: in submission
        );
    /// <inheritdoc cref="WorldTick.MusicEmbellishmentTap"/>
    public Action<string>? MusicEmbellishmentTap {
        get => m_tick.MusicEmbellishmentTap;
        set => m_tick.MusicEmbellishmentTap = value;
    }
    /// <inheritdoc cref="WorldTick.MusicLayerTap"/>
    public Action<IReadOnlyList<string>>? MusicLayerTap {
        get => m_tick.MusicLayerTap;
        set => m_tick.MusicLayerTap = value;
    }
    /// <inheritdoc cref="WorldTick.MusicTransitionTap"/>
    public Action<ulong>? MusicTransitionTap {
        get => m_tick.MusicTransitionTap;
        set => m_tick.MusicTransitionTap = value;
    }
    /// <inheritdoc cref="WorldTick.ReadClockPhaseError"/>
    public long ReadClockPhaseError() => m_tick.ReadClockPhaseError();
    /// <inheritdoc cref="WorldTick.ReleaseFederatedIntents"/>
    public void ReleaseFederatedIntents(long leaseId) => m_tick.ReleaseFederatedIntents(leaseId: leaseId);
    /// <inheritdoc cref="WorldTick.RepaintChangedLatticeDraws"/>
    public void RepaintChangedLatticeDraws(WorldDefinition previous, WorldDefinition current) =>
        m_tick.RepaintChangedLatticeDraws(
            current: current,
            previous: previous
        );
    /// <inheritdoc cref="WorldTick.ReserveTransfer"/>
    public WorldTransferReservationReply ReserveTransfer(WorldTransferReservationRequest request) => m_tick.ReserveTransfer(request: request);
    /// <inheritdoc cref="WorldTick.RetireTransferredMobility"/>
    public void RetireTransferredMobility(in WorldMobilityIdentity mobility) => m_tick.RetireTransferredMobility(mobility: in mobility);
    /// <inheritdoc cref="WorldTick.Step"/>
    public void Step(in FixedStepContext context) => m_tick.Step(context: in context);
    /// <inheritdoc cref="WorldTick.Submit"/>
    public void Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null) =>
        m_tick.Submit(
            completion: completion,
            envelope: envelope
        );
    /// <inheritdoc cref="WorldTick.TransferStatus"/>
    public WorldTransferStatus TransferStatus(string sourceAuthority, ulong transferId) =>
        m_tick.TransferStatus(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
    /// <inheritdoc cref="WorldTick.TryDriveGateVerdict"/>
    public bool TryDriveGateVerdict(int bodyIndex, out GrantVerdict verdict) =>
        m_tick.TryDriveGateVerdict(
            bodyIndex: bodyIndex,
            verdict: out verdict
        );
    /// <inheritdoc cref="WorldTick.TryFindPossessedInhabitant"/>
    public bool TryFindPossessedInhabitant(string placementId, out int bodyIndex, out WorldPrincipal holder) =>
        m_tick.TryFindPossessedInhabitant(
            bodyIndex: out bodyIndex,
            holder: out holder,
            placementId: placementId
        );
    /// <inheritdoc cref="WorldTick.TryLinkLiveness"/>
    public bool TryLinkLiveness(string adjacencyName, out long staleTicks, out bool dropped) =>
        m_tick.TryLinkLiveness(
            adjacencyName: adjacencyName,
            dropped: out dropped,
            staleTicks: out staleTicks
        );
    /// <inheritdoc cref="WorldTick.TryTransferArrivalBorder"/>
    public bool TryTransferArrivalBorder(int bodyIndex, out string border) =>
        m_tick.TryTransferArrivalBorder(
            bodyIndex: bodyIndex,
            border: out border
        );
    /// <inheritdoc cref="WorldTick.TryTransferredPrincipal(string, ulong, int, out WorldPrincipal)"/>
    public bool TryTransferredPrincipal(string sourceAuthority, ulong transferId, int ordinal, out WorldPrincipal principal) =>
        m_tick.TryTransferredPrincipal(
            ordinal: ordinal,
            principal: out principal,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
    /// <inheritdoc cref="WorldTick.TryTransferredPrincipal(string, in WorldMobilityIdentity, out WorldPrincipal)"/>
    public bool TryTransferredPrincipal(string sourceAuthority, in WorldMobilityIdentity mobility, out WorldPrincipal principal) =>
        m_tick.TryTransferredPrincipal(
            mobility: in mobility,
            principal: out principal,
            sourceAuthority: sourceAuthority
        );
}
