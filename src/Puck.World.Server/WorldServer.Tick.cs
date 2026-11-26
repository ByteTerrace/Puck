using Puck.Commands;
using Puck.Hosting;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Gets the tick facade — the ordered domain and the intent queue, the step clock, the contribution
    /// fold and its read-back, and the per-tick engagement, transfer, field, music, response, placement-deal and
    /// board-enforcement work.</summary>
    public WorldTick Tick { get { ThrowIfOutsideWindow(); return m_tick; } }
    internal IDisposable? PendingReplayInputs { get; set; }
    /// <summary>Gets whether this activation has frozen for retirement, without taking the authority gate — the
    /// raw read a caller already holding it needs.</summary>
    public bool AuthorityRetiring => m_authorityRetiring;
    /// <summary>Gets the search jobs over the arena, rebuilt with the rules on every install.</summary>
    public ArenaSearch Search { get { ThrowIfOutsideWindow(); return m_search; } }
    /// <summary>Gets the transfer escrow — bounded transfer transactions, mobility credentials and in-flight
    /// leases.</summary>
    public WorldTransferEscrow TransferEscrow { get { ThrowIfOutsideWindow(); return m_transferEscrow; } }

    /// <inheritdoc cref="WorldTick.AbortTransfer"/>
    public void AbortTransfer(string sourceAuthority, ulong transferId, WorldTransferCaller? caller = null) =>
        m_tick.AbortTransfer(
            caller: caller,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
    /// <inheritdoc cref="WorldTick.AcknowledgeTransfer"/>
    public void AcknowledgeTransfer(string sourceAuthority, ulong transferId, WorldTransferCaller? caller = null) =>
        m_tick.AcknowledgeTransfer(
            caller: caller,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
    /// <inheritdoc cref="WorldTick.Advance"/>
    [Replaces(root: typeof(StateArena))]
    public void Advance(ulong stepTicks) => m_tick.Advance(stepTicks: stepTicks);

    /// <summary>Gets a value indicating whether the next step replays recorded input: set by the replay tape as it feeds
    /// a recorded tick into this server's doors (<see cref="WorldReplayTape.InjectDriveTick"/>), and cleared once that
    /// step's engagement fold has read it.</summary>
    public bool ReplaysInput { get; internal set; }

    /// <inheritdoc cref="WorldTick.ApplyIntentSubmission"/>
    public WorldIntentAdmission ApplyIntentSubmission(WorldBody body, in IntentSubmission submission) =>
        m_tick.ApplyIntentSubmission(
            body: body,
            submission: in submission
        );
    /// <summary>Re-applies a recorded server event, an authority input, through the population and grant doors.</summary>
    /// <param name="serverEvent">The recorded event.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serverEvent"/> is <see langword="null"/>.</exception>
    public void ApplyServerEvent(WorldServerEvent serverEvent) => m_tick.ApplyServerEvent(
        input: true,
        serverEvent: serverEvent
    );
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
    public Task<(WorldTransferStatus Status, string Reason)> CommitTransferAsync(string sourceAuthority, ulong transferId,
        IReadOnlyList<WorldTransferCommitMember> members, Action? authorize = null, CancellationToken ct = default, WorldTransferCaller? caller = null) {
        var key = new WorldTransferKey(sourceAuthority, transferId);
        // A caller the door refuses is answered before any companion's home is asked to confirm a hop for it.
        var tickets = ExecuteAuthorityOperation(() => (m_transferEscrow.Admit(caller: caller, sourceAuthority: sourceAuthority).Refused
            ? []
            : m_transferEscrow.CommitHopQuestion(key)));
        // The arrival answers only once its record is durable and the cohort has landed.
        return Flatten(answer: WithConfirmedHopsAsync(key, tickets, WorldHopPhase.Commit, confirmed => DecideCrossingAsync(decide: () => {
            using var prepare = PrepareArrivals();
            var status = m_tick.CommitTransfer(caller: caller, confirmed: confirmed, members: members, reason: out var reason,
                sourceAuthority: sourceAuthority, transferId: transferId);
            return (status, reason);
        }, ct: ct), authorize, ct));
    }
    // A decision reached without waiting on home answers through its own task, which completes the moment the decision
    // does; only one that waited on home goes through an unwrapping proxy.
    private static Task<T> Flatten<T>(Task<Task<T>> answer) => (answer.IsCompletedSuccessfully
        ? answer.Result
        : answer.Unwrap());
    /// <summary>Gets the closed rewind group's boundary, or <see langword="null"/> for a world outside any. It is
    /// installed once, before admission, and never changes during an activation.</summary>
    public WorldTransferBoundary? TransferBoundary => m_transferEscrow.Boundary;
    /// <inheritdoc cref="WorldTransferEscrow.CustodyRefusal"/>
    public string? CustodyRefusal() => m_transferEscrow.CustodyRefusal();
    /// <inheritdoc cref="WorldTransferEscrow.TravelerEnded"/>
    public bool TravelerEnded(WorldTransferCaller? caller, string sourceAuthority, WorldMobilityIdentity mobility) =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.TravelerEnded(caller: caller, mobility: in mobility, sourceAuthority: sourceAuthority));
    /// <inheritdoc cref="WorldTick.ConstrainTransferAuthorities"/>
    public void ConstrainTransferAuthorities(WorldTransferBoundary boundary) => m_tick.ConstrainTransferAuthorities(boundary: boundary);
    /// <inheritdoc cref="WorldTick.DescribeFields"/>
    public string DescribeFields() => m_tick.DescribeFields();
    /// <inheritdoc cref="WorldTick.DescribeMusicClock"/>
    public string DescribeMusicClock() => m_tick.DescribeMusicClock();
    /// <inheritdoc cref="WorldTick.DescribePlacements"/>
    public string DescribePlacements(WorldStateReadView view) => m_tick.DescribePlacements(view: view);
    /// <inheritdoc cref="WorldTick.DescribeResponses"/>
    public string DescribeResponses() => m_tick.DescribeResponses();
    /// <inheritdoc cref="WorldTick.DispatchServerEvent"/>
    public void DispatchServerEvent(WorldServerEvent serverEvent, bool ordered, bool input) =>
        m_tick.DispatchServerEvent(
            input: input,
            ordered: ordered,
            serverEvent: serverEvent
        );
    /// <inheritdoc cref="WorldTick.DrainAdministrative"/>
    [Replaces(root: typeof(StateArena))]
    public bool DrainAdministrative() {
        m_output.Collect();
        DrainMailbox();
        var applied = m_tick.DrainAdministrative();
        PrepareUnsteppedAdjacencyPresentation();
        return applied;
    }
    /// <inheritdoc cref="WorldTick.Lapse"/>
    public ulong Lapse(ulong ticks) => m_tick.Lapse(ticks: ticks);
    /// <inheritdoc cref="WorldTick.SettleLapsed"/>
    public bool SettleLapsed() {
        m_output.Collect();
        DrainMailbox();
        return m_tick.SettleLapsed();
    }
    /// <summary>Gets whether an intent is queued for the next step: input addressed to a body or a session of this
    /// world that only a step applies.</summary>
    public bool HasQueuedIntents {
        get {
            lock (AuthorityGate) { return (m_tick.Intents.Count != 0); }
        }
    }
    /// <inheritdoc cref="WorldTick.EnqueueIntent"/>
    public void EnqueueIntent(in IntentSubmission submission) => m_tick.EnqueueIntent(submission: in submission);
    /// <inheritdoc cref="WorldTick.PublishFederatedIntent"/>
    public void PublishFederatedIntent(long leaseId, in IntentSubmission submission) =>
        m_tick.PublishFederatedIntent(
            leaseId: leaseId,
            submission: in submission
        );

    /// <inheritdoc cref="WorldTick.NoteFederatedDrop"/>
    internal void NoteFederatedDrop(int bodyIndex, string? reason) => m_tick.NoteFederatedDrop(bodyIndex: bodyIndex, reason: reason);
    /// <inheritdoc cref="WorldTick.ReadClock"/>
    public long ReadClock(ClockFacet facet) => m_tick.ReadClock(facet: facet);
    /// <inheritdoc cref="WorldTick.ReleaseFederatedIntents"/>
    public void ReleaseFederatedIntents(long leaseId) => m_tick.ReleaseFederatedIntents(leaseId: leaseId);
    /// <summary>Restarts lattice stores alongside a whole-document state rebuild.</summary>
    public void ReseedLatticeFields(WorldDefinition definition) {
        foreach (var region in Population.FieldRegions) {
            region.Lattice.Reseed(definition.Generation?.WorldSeed ?? 0UL);
        }
        m_tick.PaintLatticeDraws(definition);
    }
    /// <inheritdoc cref="WorldTick.RepaintChangedLatticeDraws"/>
    public void RepaintChangedLatticeDraws(WorldDefinition previous, WorldDefinition current) =>
        m_tick.RepaintChangedLatticeDraws(
            current: current,
            previous: previous
        );
    /// <inheritdoc cref="WorldTick.ReserveTransfer"/>
    public Task<WorldTransferReservationReply> ReserveTransferAsync(WorldTransferReservationRequest request,
        Action? authorize = null, CancellationToken ct = default, WorldTransferCaller? caller = null) {
        ArgumentNullException.ThrowIfNull(request);
        var key = new WorldTransferKey(request.SourceAuthority, request.TransferId);
        // A reservation the door refuses, or a guest's that carries a companion, is refused before any companion's
        // home is asked to confirm a hop for it.
        var tickets = ExecuteAuthorityOperation(() => {
            var admission = m_transferEscrow.Admit(caller: caller, sourceAuthority: request.SourceAuthority);

            return ((admission.Refused || admission.IsGuest) ? [] : m_transferEscrow.ReservationHopQuestion(request));
        });
        // The acceptance answers only once the lease record is durable.
        return Flatten(answer: WithConfirmedHopsAsync(key, tickets, WorldHopPhase.Reservation,
            confirmed => DecideCrossingAsync(decide: () => m_tick.ReserveTransfer(caller: caller, confirmed: confirmed, request: request), ct: ct),
            authorize, ct));
    }
    /// <inheritdoc cref="WorldTick.RetireTransferredMobility"/>
    public void RetireTransferredMobility(in WorldMobilityIdentity mobility) => m_tick.RetireTransferredMobility(mobility: in mobility);
    /// <inheritdoc cref="WorldTick.Step"/>
    [Replaces(root: typeof(StateArena))]
    public void Step(in FixedStepContext context) => m_tick.Step(context: in context);
    /// <summary>Submits a loopback envelope: the in-process client's door. Its completion is the host loop's, so it is
    /// routed through the server's outbox: one the tick or another thread raises reaches the client when the outbox
    /// collects. A completion raised on the collector between ticks still runs before this returns.</summary>
    /// <param name="envelope">The envelope.</param>
    /// <param name="completion">The client's typed result callback, or <see langword="null"/>.</param>
    void IWorldServerHost.Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion) =>
        m_tick.Submit(completion: m_output.Route(action: completion), envelope: envelope);

    void IWorldServerHost.RequireInlineSubmission() => ThrowIfTickPending();

    /// <summary>Captures the owned profiles for the local roster while the authority is settled.</summary>
    public WorldRosterProfiles CaptureRosterProfiles() {
        using var gate = EnterAuthorityGate();
        return m_profiles.CaptureRosterProfiles();
    }

    /// <inheritdoc cref="WorldTick.Submit"/>
    public void Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null) =>
        m_tick.Submit(
            completion: completion,
            envelope: envelope
        );
    /// <inheritdoc cref="WorldTick.TransferStatus"/>
    public WorldTransferStatus TransferStatus(string sourceAuthority, ulong transferId, WorldTransferCaller? caller = null) =>
        m_tick.TransferStatus(
            caller: caller,
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
    public bool TryFindPossessedInhabitant(string placementId, out int bodyIndex, out Grantee holder) =>
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
    /// <summary>Gets the number of arrived bodies whose authenticated source border this authority retains
    /// (<see cref="TryTransferArrivalBorder"/>).</summary>
    public int TransferArrivalBorders => TransferEscrow.ArrivalBorders;
    /// <inheritdoc cref="WorldTick.TryTransferArrivalBorder"/>
    public bool TryTransferArrivalBorder(int bodyIndex, out string border) =>
        m_tick.TryTransferArrivalBorder(
            bodyIndex: bodyIndex,
            border: out border
        );
    /// <inheritdoc cref="WorldTick.TryTransferredPrincipal(string, ulong, int, out Principal)"/>
    public bool TryTransferredPrincipal(string sourceAuthority, ulong transferId, int ordinal, out Principal principal) =>
        m_tick.TryTransferredPrincipal(
            ordinal: ordinal,
            principal: out principal,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        );
    /// <inheritdoc cref="WorldTick.TryTransferredPrincipal(string, in WorldMobilityIdentity, out Principal)"/>
    public bool TryTransferredPrincipal(string sourceAuthority, in WorldMobilityIdentity mobility, out Principal principal) =>
        m_tick.TryTransferredPrincipal(
            mobility: in mobility,
            principal: out principal,
            sourceAuthority: sourceAuthority
        );
}
