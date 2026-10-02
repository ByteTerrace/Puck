using Puck.Commands;
using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>One prospective traveler in a destination reservation.</summary>
/// <param name="Principal">The source-stamped acting principal for a colocated transfer.</param>
/// <param name="PreferredSlot">The body index the traveler prefers to retain.</param>
/// <param name="Identity">The attested owned-world identity carried by a federated traveler.</param>
/// <param name="Source">The traveler's authored intent source, preserved across the authority boundary.</param>
/// <param name="BodyColor">The source body's exact rendered material color, preserved across ownership.</param>
/// <param name="CatalogRig">The source body's entity-owned procedural rig, preserved across ownership. Destination
/// look authoring may deliberately override it; ordinary admission may not.</param>
/// <param name="Mobility">The traveler's immutable incarnation and current committed ownership epoch.</param>
public readonly record struct WorldTransferReservationMember(Principal Principal, int PreferredSlot, WorldIdentity? Identity, IntentSource Source, Vector3 BodyColor, byte CatalogRig, WorldMobilityIdentity? Mobility = null);
/// <summary>The destination's binding reservation request. The deadline is stated in the source authority's own
/// simulation ticks; the destination converts the remaining interval through the exact 50400 engine-tick bridge.</summary>
public sealed record WorldTransferReservationRequest(
    ulong TransferId,
    string SourceAuthority,
    int SourceRateHz,
    ulong SourceTick,
    ulong DeadlineSourceTick,
    string Border,
    int? BorderCapacity,
    bool PartyAllOrNothing,
    bool PeerAdmission,
    IReadOnlyList<WorldTransferReservationMember> Members
);
/// <summary>A transfer's destination-wide identity. SourceAuthority is the authenticated source namespace; the
/// numeric id is only required to be unique inside that namespace.</summary>
public readonly record struct WorldTransferKey(string SourceAuthority, ulong TransferId);
/// <summary>A traveler identity that survives authority-local index changes. The incarnation is minted once from a
/// complete generation-addressed origin; the epoch advances exactly once at each committed ownership handoff.</summary>
/// <param name="Incarnation">The origin address the identity was minted from; it never changes.</param>
/// <param name="Epoch">The count of committed ownership handoffs.</param>
/// <param name="DepartedFrom">The generation-addressed slot that held the traveler under the authority it last left:
/// the source stamps its own slot when it offers the traveler, and the destination keeps that stamp until it offers
/// the traveler onward itself. A generation-addressed slot names one occupant for all time, so an entity a
/// neighbour's delivered image still shows at this address is this traveler seen before its handoff, not a second
/// body.</param>
public readonly record struct WorldMobilityIdentity(WorldEntityAddress Incarnation, ulong Epoch, WorldEntityAddress DepartedFrom) {
    /// <summary>Returns the next committed ownership epoch.</summary>
    public WorldMobilityIdentity Advance() => this with { Epoch = checked((Epoch + 1UL)) };
}
/// <summary>The destination's idempotent answer about one source-scoped transfer, and its verdict on a commit.</summary>
public enum WorldTransferStatus : byte {
    /// <summary>Nothing for the transfer is live here; a commit answered this way was refused and applied nothing.</summary>
    Missing = 0,

    /// <summary>A reservation holds destination capacity and awaits its commit.</summary>
    Reserved = 1,

    /// <summary>The commit is authoritative here: its arrival is landed and its record durable.</summary>
    Committed = 2,

    /// <summary>The commit's arrival record may or may not be durable. The destination embodies none of its
    /// travelers and refuses every later step for the transfer and for each traveler; only its recovery from its
    /// crossing log answers <see cref="Committed"/> or <see cref="Missing"/>.</summary>
    Uncertain = 3,
}
/// <summary>Bounded operational counts for transfer-state churn laws and diagnostics.</summary>
public readonly record struct WorldTransferTableCounts(int ActiveTransactions, int MobilityCredentials, int MobilityLeases);
/// <summary>One named channel edge carried across an authority change. Names, rather than ordinals, keep a
/// destination's independently-authored channel order from changing the meaning of a held control. HeldValue is
/// the last admitted device-held composition value: the destination bridges it until the first real input image
/// arrives, so transport handoff cannot insert a synthetic release/press pair into one physical hold.</summary>
public readonly record struct WorldTransferChannelEdge(string Name, bool PreviousBit, FixedQ4816 HeldValue);
/// <summary>One named action register carried across an authority change. A destination accepts it only when its
/// own seat kit declares the same name and kind; its own envelope remains authoritative.</summary>
public readonly record struct WorldTransferActionRegister(string Name, ActionStateKind Kind, FixedQ4816 Value, ulong TimerTicks);
/// <summary>The minimal action continuity that prevents an authority seam from manufacturing a new input edge or
/// a fresh cooldown/charge.</summary>
public sealed record WorldTransferActionContinuity(
    IReadOnlyList<WorldTransferChannelEdge> Channels,
    IReadOnlyList<WorldTransferActionRegister> Registers
);
/// <summary>The unconsumed geometric image of one already-evaluated simulation step. Actions, timers, gravity, and
/// authored motion have run exactly once on the source authority; a destination may only sweep this segment through
/// its own contact and ownership topology. It must never call <see cref="WorldBody.Advance"/> for the represented
/// time span.</summary>
/// <param name="PreviousPosition">The mapped point at which the destination became physically relevant. For an
/// adjacency this is the counterpart seam point, not the source step's original position.</param>
/// <param name="SourceTick">The source authority tick that evaluated the motion.</param>
/// <param name="ContinuumStartEngineTick">The inclusive engine-time start of that source step.</param>
/// <param name="ContinuumEndEngineTick">The exclusive engine-time end of that source step.</param>
/// <param name="ConsumedThroughEngineTick">The latest engine-time boundary an authority has already consumed for
/// this traveler. A destination may not ordinarily advance the body from a step whose start precedes it.</param>
/// <param name="BoundaryEvents">How many ownership faces this one source step has already crossed.</param>
public readonly record struct WorldContinuumTrajectory(
    FixedVector3 PreviousPosition,
    ulong SourceTick,
    ulong ContinuumStartEngineTick,
    ulong ContinuumEndEngineTick,
    ulong ConsumedThroughEngineTick,
    byte BoundaryEvents
) {
    /// <summary>A representation-level work ceiling. Exhaustion is a deterministic safety clamp at the last
    /// confirmed owner; it is not an authored feel parameter.</summary>
    public const byte MaxBoundaryEvents = 8;
}
/// <summary>The destination's reservation verdict and assigned body indices.</summary>
public sealed record WorldTransferReservationReply(bool Accepted, string Reason, ulong DeadlineDestinationTick, IReadOnlyList<int> BodyIndices, WorldDefinition? DestinationDefinition) {
    /// <summary>Creates a named refusal.</summary>
    public static WorldTransferReservationReply Refused(string reason) => new(
        Accepted: false,
        BodyIndices: [],
        DeadlineDestinationTick: 0,
        DestinationDefinition: null,
        Reason: reason
    );
}
/// <summary>One detached source body carried into a previously reserved destination index. <c>TravelTurn</c> is the
/// traveler's accumulated arrival turn once it lands (<see cref="WorldFrameIsometry.AccumulateTurn"/>), which the
/// destination keeps on the occupant for the routes it describes.</summary>
public sealed record WorldTransferCommitMember(
    WorldIdentity? Profile,
    bool HasMappedArrival,
    string BodyMotionProgramName,
    FixedVector3 Position,
    FixedQ4816 YawRadians,
    FixedVector3 PlanarVelocity,
    FixedQ4816 VerticalVelocity,
    WorldTransferActionContinuity? ActionContinuity = null,
    WorldContinuumTrajectory? Continuum = null,
    FixedQ4816 TravelTurn = default
);
/// <summary>The transfer escrow table shared by colocated and QUIC authority transports. It owns destination capacity
/// from reserve until commit, explicit abort, or deterministic deadline expiry; it never queues a full request.</summary>
public sealed partial class WorldTransferEscrow {
    private sealed record Lease(WorldTransferReservationRequest Request, ulong DeadlineTick, int[] Slots, WorldDefinition DestinationDefinition, WorldAdmissionVerdict? Arrival);

    // A general-purpose deep copy of the reservation's traveler list — no per-member content beyond an ordinary
    // value-type array needs cloning, so this is a straight array copy rather than a per-field walk.
    private static bool TryCopyReservation(WorldTransferReservationRequest request, out WorldTransferReservationRequest owned, out string reason) {
        owned = request;
        var count = (request.Members?.Count ?? 0);

        if (
            (count <= 0) ||
            (count > WorldBodiesLimits.CapacityCeiling)
        ) { reason = "reservation traveler count is invalid"; return false; }
        owned = request with { Members = [.. request.Members!] };
        reason = string.Empty;
        return true;
    }
    private static WorldTransferReservationRequest CopyOwnedReservation(WorldTransferReservationRequest request) => request with {
        Members = [.. request.Members],
    };

    private readonly record struct MobilityAdmission(ulong Epoch, Principal Principal);
    private readonly record struct MobilityLease(WorldTransferKey Transfer, ulong ExpectedEpoch);

    private readonly WorldServer m_server;
    private readonly Func<int, string?>? m_landingRefusal;

    // Sorted by DeadlineTick so ReclaimExpired sweeps only what has actually arrived; every m_leases entry has
    // exactly one live row here, added alongside the lease and removed by ReleaseLease, whether that release is
    // driven by the sweep itself or by an explicit Abort/Commit ahead of the deadline.
    private readonly WorldDeadlineTable<WorldTransferKey> m_deadlines = new();
    private readonly List<int> m_staleBorderSlots = [];
    private readonly Dictionary<WorldTransferKey, Lease> m_leases = new();
    private readonly HashSet<WorldTransferKey> m_committed = new();
    private readonly Dictionary<WorldTransferKey, WorldTransferCommitMember[]> m_committedMembers = new();
    private readonly Dictionary<WorldTransferKey, Principal[]> m_committedPrincipals = new();
    private readonly Dictionary<WorldTransferKey, HashSet<WorldEntityAddress>> m_committedIncarnations = new();
    private readonly Dictionary<WorldEntityAddress, WorldTransferKey> m_latestCommittedTransfer = new();
    private readonly Dictionary<WorldEntityAddress, MobilityLease> m_mobilityLeases = new();
    // One stable credential row per authenticated upstream namespace and traveler incarnation. Repeated seam
    // crossings overwrite its epoch/principal; transaction ids never enter this table.
    private readonly Dictionary<(string SourceAuthority, WorldEntityAddress Incarnation), MobilityAdmission> m_mobilityAdmissions = new();
    // A committed body continues to consume its authored border capacity until it leaves that body index.
    // The population remains the source of truth: stale rows are pruned before every capacity decision.
    private readonly Dictionary<int, string> m_borderAdmissions = new();

    // The first crossing sequence this authority has not made durable; checkpointed as the recovery watermark.
    private ulong m_crossingSequence;
    // Set while a recorded arrival lands again: its record is already durable, so it is not written twice.
    private bool m_relanding;
    // Set while a re-drive lands a taped arrival: the outcome its landings must reproduce, and whether the landing
    // reached the recorded rollback.
    private WorldArrivalOutcome? m_relandOutcome;
    private bool m_relandRolledBack;

    /// <summary>Initializes a new instance of the <see cref="WorldTransferEscrow"/> class for one authority.</summary>
    /// <param name="server">The authority the escrow lands travelers in.</param>
    /// <param name="landingRefusal">An admission policy evaluated at each commit member's ordinal, returning a refusal
    /// reason or <see langword="null"/> to admit, fixed at composition. No production composition passes one; a law
    /// passes one to refuse a member after the members ahead of it have landed.</param>
    public WorldTransferEscrow(WorldServer server, Func<int, string?>? landingRefusal = null) {
        m_server = server;
        m_landingRefusal = landingRefusal;
    }

    /// <summary>Gets the first crossing sequence this authority has not yet made durable.</summary>
    public ulong CrossingSequence => m_crossingSequence;

    internal void AdvanceCrossingSequence() => m_crossingSequence = checked((m_crossingSequence + 1UL));
    // Recovery has redone every record below `next`; later records continue from there.
    internal void ResumeCrossingSequence(ulong next) => m_crossingSequence = Math.Max(
        val1: m_crossingSequence,
        val2: next
    );

    /// <summary>Captures every table this escrow owns.</summary>
    public WorldTransferEscrowCheckpoint Capture() {
        var leases = new List<WorldTransferLeaseCheckpoint>(capacity: m_leases.Count);

        foreach (var (key, lease) in m_leases) {
            leases.Add(item: new WorldTransferLeaseCheckpoint(
                Key: key,
                Request: CopyOwnedReservation(request: lease.Request),
                DeadlineTick: lease.DeadlineTick,
                Slots: [.. lease.Slots],
                DestinationDefinitionJson: WorldDefinitionSerialization.Serialize(definition: lease.DestinationDefinition),
                Arrival: lease.Arrival
            ));
        }

        var committed = new List<WorldTransferCommittedCheckpoint>(capacity: m_committed.Count);

        foreach (var key in m_committed) {
            committed.Add(item: new WorldTransferCommittedCheckpoint(
                Key: key,
                Members: (m_committedMembers.TryGetValue(
                    key: key,
                    value: out var members
                )
                ? members.Select(selector: CopyCommitMember).ToArray()
                : []),
                Principals: (m_committedPrincipals.TryGetValue(
                    key: key,
                    value: out var principals
                )
                ? [.. principals]
                : []),
                Incarnations: (m_committedIncarnations.TryGetValue(
                    key: key,
                    value: out var incarnations
                )
                ? [.. incarnations]
                : [])
            ));
        }

        return new WorldTransferEscrowCheckpoint(
            Leases: leases,
            Committed: committed,
            LatestCommittedTransfer: [.. m_latestCommittedTransfer.Select(selector: static pair => (pair.Key, pair.Value))],
            MobilityLeases: [.. m_mobilityLeases.Select(selector: static pair => (pair.Key, pair.Value.Transfer, pair.Value.ExpectedEpoch))],
            MobilityAdmissions: [.. m_mobilityAdmissions.Select(selector: static pair => (pair.Key.SourceAuthority, pair.Key.Incarnation, pair.Value.Epoch, pair.Value.Principal))],
            BorderAdmissions: [.. m_borderAdmissions.Select(selector: static pair => (pair.Key, pair.Value))],
            CrossingSequence: m_crossingSequence
        );
    }
    /// <summary>Restores every table this escrow owns from a previously captured checkpoint. Every table is cleared
    /// first — this replaces the escrow wholesale rather than merging onto it.</summary>
    public void Restore(WorldTransferEscrowCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        m_leases.Clear();
        m_deadlines.Clear();
        m_committed.Clear();
        m_committedMembers.Clear();
        m_committedPrincipals.Clear();
        m_committedIncarnations.Clear();
        m_latestCommittedTransfer.Clear();
        m_mobilityLeases.Clear();
        m_mobilityAdmissions.Clear();
        m_borderAdmissions.Clear();
        m_crossingSequence = checkpoint.CrossingSequence;

        // Commit's own "definition moved after reservation" guard is a REFERENCE check against the live
        // m_server.Definition object a live Reserve captured verbatim. A restored lease whose destination content
        // matches the server's OWN restored definition reuses that SAME object rather than a freshly deserialized
        // copy, so the guard still recognizes "this is the reservation's own destination" across a checkpoint
        // round-trip; a lease captured against a document the server has since moved past correctly deserializes its
        // own (now provably stale) snapshot instead, so the guard still refuses a commit against a moved document.
        var currentDefinitionHash = WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: m_server.Definition));

        foreach (var lease in checkpoint.Leases) {
            var destinationDefinition = (string.Equals(
                a: WorldDefinitionFileSource.ComputeContentHash(content: lease.DestinationDefinitionJson),
                b: currentDefinitionHash,
                comparisonType: StringComparison.Ordinal
            )
                ? m_server.Definition
                : WorldDefinitionSerialization.Deserialize(utf8Json: lease.DestinationDefinitionJson)
            );

            if (!TryCopyReservation(
                lease.Request,
                out var request,
                out var reason
            )) {
                throw new ArgumentException(
                    message: reason,
                    paramName: nameof(checkpoint)
                );
            }
            m_leases[lease.Key] = new Lease(
                Request: request,
                DeadlineTick: lease.DeadlineTick,
                Slots: [.. lease.Slots],
                DestinationDefinition: destinationDefinition,
                Arrival: lease.Arrival
            );
            m_deadlines.Add(
                dueTick: unchecked((long)lease.DeadlineTick),
                token: lease.Key
            );
        }

        foreach (var row in checkpoint.Committed) {
            _ = m_committed.Add(item: row.Key);
            m_committedMembers[row.Key] = row.Members.Select(selector: CopyCommitMember).ToArray();
            m_committedPrincipals[row.Key] = row.Principals;
            m_committedIncarnations[row.Key] = [.. row.Incarnations];
        }

        foreach (var row in checkpoint.LatestCommittedTransfer) {
            m_latestCommittedTransfer[row.Incarnation] = row.Transfer;
        }

        foreach (var row in checkpoint.MobilityLeases) {
            m_mobilityLeases[row.Incarnation] = new MobilityLease(
                ExpectedEpoch: row.ExpectedEpoch,
                Transfer: row.Transfer
            );
        }

        foreach (var row in checkpoint.MobilityAdmissions) {
            m_mobilityAdmissions[(row.SourceAuthority, row.Incarnation)] = new MobilityAdmission(
                Epoch: row.Epoch,
                Principal: row.Principal
            );
        }

        foreach (var row in checkpoint.BorderAdmissions) {
            m_borderAdmissions[row.Slot] = row.Border;
        }
    }

    // Whether the arrival verdict's own templates would mint Drive over the body a commit assigns. Resolved through
    // WorldAdmissionGrant.SubjectFor, so this asks exactly what WorldServer.BuildAdmissionGrants will produce.
    private static bool ArrivalDrives(WorldAdmissionVerdict? arrival, int slot) {
        if (arrival is not { } verdict) {
            return false;
        }

        foreach (var template in verdict.Templates) {
            if (template.Capability != WorldCapability.Drive) {
                continue;
            }

            var subject = template.SubjectFor(bodyIndex: slot);

            if (
                (subject.Kind == GrantSubjectKind.All) ||
                ((subject.Kind == GrantSubjectKind.Body) && (subject.Value == slot))
            ) {
                return true;
            }
        }

        return false;
    }
    private static bool CommitMatches(IReadOnlyList<WorldTransferCommitMember> left, IReadOnlyList<WorldTransferCommitMember> right) {
        if (left.Count != right.Count) {
            return false;
        }

        for (var index = 0; (index < left.Count); index++) {
            var a = left[index];
            var b = right[index];

            if (
                !IdentityMatches(
                left: a.Profile,
                right: b.Profile
            ) ||
                (a.HasMappedArrival != b.HasMappedArrival) ||
                !string.Equals(
                a: a.BodyMotionProgramName,
                b: b.BodyMotionProgramName,
                comparisonType: StringComparison.Ordinal
            ) ||
                (a.Position != b.Position) ||
                (a.YawRadians != b.YawRadians) ||
                (a.PlanarVelocity != b.PlanarVelocity) ||
                (a.VerticalVelocity != b.VerticalVelocity) ||
                (a.TravelTurn != b.TravelTurn) ||
                (a.Continuum != b.Continuum) ||
                !ActionContinuityMatches(
                left: a.ActionContinuity,
                right: b.ActionContinuity
            )
            ) {
                return false;
            }
        }

        return true;
    }
    private static bool ActionContinuityMatches(WorldTransferActionContinuity? left, WorldTransferActionContinuity? right) =>
        (ReferenceEquals(
            objA: left,
            objB: right
        ) || ((left is not null) && (right is not null) &&
        (left.Channels is not null) && (right.Channels is not null) && (left.Registers is not null) && (right.Registers is not null) &&
        left.Channels.SequenceEqual(second: right.Channels) && left.Registers.SequenceEqual(second: right.Registers)));
    // IReadOnlyList does not imply immutable storage. Retain a value image of continuity, including at checkpoint
    // capture/restore, so caller edits cannot rewrite which commit this idempotency receipt accepted.
    private static WorldTransferCommitMember CopyCommitMember(WorldTransferCommitMember member) => member with {
        ActionContinuity = ((member.ActionContinuity is { } continuity)
        ? new WorldTransferActionContinuity(
            Channels: [.. continuity.Channels],
            Registers: [.. continuity.Registers]
        )
        : null),
    };
    private static bool IdentityMatches(WorldIdentity? left, WorldIdentity? right) {
        if (ReferenceEquals(
            objA: left,
            objB: right
        )) {
            return true;
        }

        if (
            (left is null) ||
            (right is null)
        ) {
            return false;
        }

        if (
            (left.Document is not { } leftDocument) ||
            (right.Document is not { } rightDocument)
        ) {
            return (
                (left.Document is null) &&
                (right.Document is null)
            );
        }

        var leftBytes = WorldDefinitionSerialization.Serialize(definition: leftDocument);
        var rightBytes = WorldDefinitionSerialization.Serialize(definition: rightDocument);

        return leftBytes.AsSpan().SequenceEqual(other: rightBytes);
    }
    private static int PreferredOrLowestFree(bool[] consumed, int preferred, int first) {
        if (
            (preferred >= first) &&
            (((uint)preferred) < ((uint)consumed.Length)) &&
            !consumed[preferred]
        ) {
            return preferred;
        }

        for (var slot = first; (slot < consumed.Length); slot++) {
            if (!consumed[slot]) {
                return slot;
            }
        }

        return -1;
    }
    private void PruneDepartedAdmissions() {
        foreach (var slot in m_borderAdmissions.Keys) {
            if (!m_server.Population.IsActive(index: slot)) {
                m_staleBorderSlots.Add(item: slot);
            }
        }

        foreach (var slot in m_staleBorderSlots) {
            _ = m_borderAdmissions.Remove(key: slot);
        }

        m_staleBorderSlots.Clear();
    }
    private void ReleaseLease(WorldTransferKey key) {
        if (!m_leases.Remove(
            key: key,
            value: out var lease
        )) {
            return;
        }
        _ = m_deadlines.Remove(token: key);
        foreach (var member in lease.Request.Members) {
            var mobility = member.Mobility!.Value;

            if (
                m_mobilityLeases.TryGetValue(
                key: mobility.Incarnation,
                value: out var mobilityLease
            ) &&
                (mobilityLease.Transfer == key)
            ) {
                _ = m_mobilityLeases.Remove(key: mobility.Incarnation);
            }
        }
    }
    private static bool ReservationMatches(WorldTransferReservationRequest left, WorldTransferReservationRequest right) {
        if (
            (left.TransferId != right.TransferId) ||
            !string.Equals(
            a: left.SourceAuthority,
            b: right.SourceAuthority,
            comparisonType: StringComparison.Ordinal
        ) ||
            (left.SourceRateHz != right.SourceRateHz) ||
            (left.SourceTick != right.SourceTick) ||
            (left.DeadlineSourceTick != right.DeadlineSourceTick) ||
            !string.Equals(
            a: left.Border,
            b: right.Border,
            comparisonType: StringComparison.Ordinal
        ) ||
            (left.BorderCapacity != right.BorderCapacity) ||
            (left.PartyAllOrNothing != right.PartyAllOrNothing) ||
            (left.PeerAdmission != right.PeerAdmission) ||
            (left.Members.Count != right.Members.Count)
        ) {
            return false;
        }

        for (var index = 0; (index < left.Members.Count); index++) {
            var a = left.Members[index];
            var b = right.Members[index];

            if (
                (a.Principal != b.Principal) ||
                (a.PreferredSlot != b.PreferredSlot) ||
                (a.Source != b.Source) ||
                (a.BodyColor != b.BodyColor) ||
                (a.CatalogRig != b.CatalogRig) ||
                (a.Mobility != b.Mobility) ||
                !IdentityMatches(
                left: a.Identity,
                right: b.Identity
            )
            ) {
                return false;
            }
        }

        return true;
    }
    private void RetireCommittedTransaction(WorldTransferKey key) {
        if (m_committedIncarnations.Remove(
            key: key,
            value: out var incarnations
        )) {
            foreach (var incarnation in incarnations) {
                if (
                    m_latestCommittedTransfer.TryGetValue(
                    key: incarnation,
                    value: out var latest
                ) &&
                    (latest == key)
                ) {
                    _ = m_latestCommittedTransfer.Remove(key: incarnation);
                }
            }
        }
        _ = m_committed.Remove(item: key);
        _ = m_committedMembers.Remove(key: key);
        _ = m_committedPrincipals.Remove(key: key);
    }
    private void SupersedeCommittedIncarnation(WorldTransferKey key, WorldEntityAddress incarnation) {
        if (
            m_latestCommittedTransfer.TryGetValue(
            key: incarnation,
            value: out var latest
        ) &&
            (latest == key)
        ) {
            _ = m_latestCommittedTransfer.Remove(key: incarnation);
        }
        if (!m_committedIncarnations.TryGetValue(
            key: key,
            value: out var incarnations
        )) {
            return;
        }
        _ = incarnations.Remove(item: incarnation);
        if (incarnations.Count == 0) {
            RetireCommittedTransaction(key: key);
        }
    }
    private bool TryKnownMobilityEpoch(WorldEntityAddress incarnation, out ulong epoch) {
        var found = false;

        epoch = 0;
        foreach (var pair in m_mobilityAdmissions) {
            if (
                (pair.Key.Incarnation == incarnation) &&
                (!found || (pair.Value.Epoch > epoch))
            ) {
                found = true;
                epoch = pair.Value.Epoch;
            }
        }
        return found;
    }

    public void Abort(string sourceAuthority, ulong transferId) => ReleaseLease(key: new WorldTransferKey(
        SourceAuthority: sourceAuthority,
        TransferId: transferId
    ));
    /// <summary>Retires disposable exact-retry payload after the source has observed and published commit.</summary>
    public void Acknowledge(string sourceAuthority, ulong transferId) => RetireCommittedTransaction(key: new WorldTransferKey(
        SourceAuthority: sourceAuthority,
        TransferId: transferId
    ));
    /// <summary>Re-arms a committed arrival's reciprocal edge after the body has been observed fully inside its new
    /// owner's half-space. The expected identity makes slot reuse or a later arrival unable to clear another
    /// transfer's latch.</summary>
    public bool ClearArrivalBorder(int bodyIndex, string expectedBorder) =>
        (m_borderAdmissions.TryGetValue(
            key: bodyIndex,
            value: out var border
        ) &&
        string.Equals(
            a: border,
            b: expectedBorder,
            comparisonType: StringComparison.Ordinal
        ) &&
        m_borderAdmissions.Remove(key: bodyIndex));
    /// <summary>Lands a reserved cohort and makes its arrival durable before answering. An exact replay of a committed
    /// transfer answers <see cref="WorldTransferStatus.Committed"/> again; a transfer whose arrival record is uncertain
    /// answers <see cref="WorldTransferStatus.Uncertain"/> and lands nothing.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    /// <param name="members">The travelers in reservation order.</param>
    /// <param name="reason">The named refusal or uncertainty, or empty when committed.</param>
    /// <returns><see cref="WorldTransferStatus.Committed"/>, <see cref="WorldTransferStatus.Uncertain"/>, or
    /// <see cref="WorldTransferStatus.Missing"/> for a refusal.</returns>
    public WorldTransferStatus Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        var key = new WorldTransferKey(
            SourceAuthority: sourceAuthority,
            TransferId: transferId
        );

        if (IsUncertain(
            key: key,
            reason: out reason
        )) {
            return WorldTransferStatus.Uncertain;
        }
        if (m_committed.Contains(item: key)) {
            if (
                m_committedMembers.TryGetValue(
                key: key,
                value: out var committedMembers
            ) &&
                CommitMatches(
                left: committedMembers,
                right: members
            )
            ) {
                reason = string.Empty;
                return WorldTransferStatus.Committed;
            }

            reason = $"transfer {transferId} reuses a committed source-scoped id with a different commit";
            return WorldTransferStatus.Missing;
        }

        if (!m_leases.TryGetValue(
            key: key,
            value: out var lease
        )) {
            reason = $"transfer {transferId} has no live reservation";

            return WorldTransferStatus.Missing;
        }

        for (var index = 0; (index < lease.Request.Members.Count); index++) {
            var mobility = lease.Request.Members[index].Mobility!.Value;

            if (
                !m_mobilityLeases.TryGetValue(
                key: mobility.Incarnation,
                value: out var mobilityLease
            ) ||
                (mobilityLease.Transfer != key) ||
                (mobilityLease.ExpectedEpoch != mobility.Epoch)
            ) {
                reason = $"transfer {transferId} no longer owns traveler {(index + 1)}'s mobility epoch lease";
                return WorldTransferStatus.Missing;
            }
        }

        m_server.LandingArrival = true;

        try {
            return CommitLease(
            key: key,
            lease: lease,
            members: members,
            reason: out reason
        );
        } finally {
            m_server.LandingArrival = false;
            ReleaseLease(key: key);
        }
    }

    private WorldTransferStatus CommitLease(WorldTransferKey key, Lease lease, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        var transferId = key.TransferId;

        if ((m_server.NextInputTick - 1UL) >= lease.DeadlineTick) {
            reason = $"transfer {transferId} reservation expired at destination tick {lease.DeadlineTick}";

            return WorldTransferStatus.Missing;
        }

        if (!ReferenceEquals(
            objA: m_server.Definition,
            objB: lease.DestinationDefinition
        )) {
            reason = $"transfer {transferId} destination definition moved after reservation; reserve again against the current revision";

            return WorldTransferStatus.Missing;
        }

        for (var index = 0; (index < lease.Request.Members.Count); index++) {
            var mobility = lease.Request.Members[index].Mobility!.Value;

            if (
                TryKnownMobilityEpoch(
                incarnation: mobility.Incarnation,
                epoch: out var knownEpoch
            ) &&
                (knownEpoch >= mobility.Epoch)
            ) {
                reason = $"transfer {transferId} traveler {(index + 1)} lost its mobility epoch compare-and-set; expected {mobility.Epoch}, destination has consumed through {knownEpoch}";
                return WorldTransferStatus.Missing;
            }
        }

        if (members.Count != lease.Slots.Length) {
            reason = $"transfer {transferId} commit carries {members.Count} traveler(s), reservation binds {lease.Slots.Length}";

            return WorldTransferStatus.Missing;
        }

        if (members.Any(predicate: static member => ((member.ActionContinuity is { } continuity) && ((continuity.Channels is null) || (continuity.Registers is null))))) {
            reason = $"transfer {transferId} carries invalid action continuity collections";
            return WorldTransferStatus.Missing;
        }
        members = members.Select(selector: CopyCommitMember).ToArray();

        for (var index = 0; (index < members.Count); index++) {
            var member = members[index];

            if (CommitMemberFault(member: member) is { } fault) {
                reason = $"transfer {transferId} traveler {(index + 1)} {fault}";
                return WorldTransferStatus.Missing;
            }
            if (
                member.HasMappedArrival &&
                !lease.DestinationDefinition.BodyMotionPrograms.Any(predicate: program => ((program.Kind == BodyProgramKind.Motion) && string.Equals(
                    a: program.Name,
                    b: member.BodyMotionProgramName,
                    comparisonType: StringComparison.Ordinal
                )))
            ) {
                reason = $"transfer {transferId} traveler {(index + 1)} names unavailable destination motion program '{member.BodyMotionProgramName}'";
                return WorldTransferStatus.Missing;
            }
        }

        var arrival = new WorldCrossingArrival(
            Members: [.. members],
            Request: lease.Request,
            Slots: [.. lease.Slots]
        );
        var generations = new List<int>(capacity: members.Count);
        // A re-drive stops where the recorded commit rolled back; a live commit or a recovery lands every traveler.
        var stop = ((m_relandOutcome is { RolledBack: true } recorded)
            ? recorded.Generations.Count
            : -1);

        // Every landing this commit made is undone, then reported with its rollback, so a re-drive lands and rolls
        // back the same travelers: a landing advances its index's generation, which outlives it.
        void RollBack() {
            RollBackArrival(
                arrival: arrival,
                landed: generations.Count
            );
            if (generations.Count > 0) {
                m_server.ArrivalTap?.Invoke(
                    arg1: arrival,
                    arg2: new WorldArrivalOutcome(
                        Generations: [.. generations],
                        RolledBack: true
                    )
                );
            }
        }

        for (var index = 0; (index < members.Count); index++) {
            if (index == stop) {
                RollBack();
                m_relandRolledBack = true;
                reason = $"transfer {transferId} rolled back ahead of traveler {(index + 1)}, as recorded";
                return WorldTransferStatus.Missing;
            }

            var slot = lease.Slots[index];
            var reply = LandMember(
                arrival: arrival,
                index: index,
                verdict: lease.Arrival
            );

            if (!reply.Accepted) {
                RollBack();
                reason = $"body:{slot} refused reserved commit — {reply.Reason}";
                return WorldTransferStatus.Missing;
            }

            generations.Add(item: m_server.Population.Generation(index: slot));

            if (
                (m_relandOutcome is { } pinned) &&
                (generations[index] != pinned.Generations[index])
            ) {
                RollBack();
                reason = $"body:{slot} landed at generation {generations[index]} where the recorded commit landed it at {pinned.Generations[index]}";
                return WorldTransferStatus.Missing;
            }
        }
        if (stop == members.Count) {
            RollBack();
            m_relandRolledBack = true;
            reason = $"transfer {transferId} rolled back after landing every traveler, as recorded";
            return WorldTransferStatus.Missing;
        }

        // Written ahead of the answer: a destination that cannot make its arrival durable lands nothing, so a
        // restart can never lose a body the source was told had arrived. An uncertain record lands nothing either,
        // but the record may still be in the log, so the answer is neither a refusal nor a commit.
        var durability = WorldCrossingDurability.Durable;
        var durabilityReason = string.Empty;

        if (!m_relanding) {
            durability = m_server.RecordCrossingHeld(
                reason: out durabilityReason,
                record: new WorldCrossingRecord.Arrival(Value: arrival)
            );
        }
        if (durability != WorldCrossingDurability.Durable) {
            RollBack();
            if (durability == WorldCrossingDurability.Uncertain) {
                return HoldUncertain(
                    arrival: arrival,
                    detail: durabilityReason,
                    reason: out reason
                );
            }
            reason = $"transfer {transferId} arrival could not be made durable — {durabilityReason}";
            return WorldTransferStatus.Missing;
        }

        m_committed.Add(item: key);
        m_committedMembers[key] = [.. members];
        m_committedPrincipals[key] = lease.Slots.Select(selector: slot => (lease.Request.PeerAdmission
            ? m_server.Population.PeerPrincipal(index: slot)
            : lease.Request.Members[Array.IndexOf(
                array: lease.Slots,
                value: slot
            )].Principal)).ToArray();
        var committedIncarnations = new HashSet<WorldEntityAddress>();

        for (var index = 0; (index < lease.Slots.Length); index++) {
            var mobility = lease.Request.Members[index].Mobility!.Value.Advance();
            var principal = m_committedPrincipals[key][index];

            if (
                m_latestCommittedTransfer.TryGetValue(
                key: mobility.Incarnation,
                value: out var previousTransfer
            ) &&
                (previousTransfer != key)
            ) {
                SupersedeCommittedIncarnation(
                    key: previousTransfer,
                    incarnation: mobility.Incarnation
                );
            }
            m_latestCommittedTransfer[mobility.Incarnation] = key;
            _ = committedIncarnations.Add(item: mobility.Incarnation);
            // Returning to an authority refreshes every authenticated upstream alias for this incarnation so an old
            // route resolves the new live generation instead of forwarding around a loop.
            foreach (var alias in m_mobilityAdmissions.Keys.Where(predicate: candidate => (candidate.Incarnation == mobility.Incarnation)).ToArray()) {
                m_mobilityAdmissions[alias] = new MobilityAdmission(
                    Epoch: mobility.Epoch,
                    Principal: principal
                );
            }
            m_mobilityAdmissions[(lease.Request.SourceAuthority, mobility.Incarnation)] = new MobilityAdmission(
                Epoch: mobility.Epoch,
                Principal: principal
            );
        }
        m_committedIncarnations[key] = committedIncarnations;
        // Reported once the commit stands, with the generation each traveler landed at.
        m_server.ArrivalTap?.Invoke(
            arg1: arrival,
            arg2: new WorldArrivalOutcome(
                Generations: [.. generations],
                RolledBack: false
            )
        );
        reason = string.Empty;

        return WorldTransferStatus.Committed;
    }
    // The one landing: admits traveler `index` of a commit at its reserved index (a local seat by its session join, a
    // transferred peer or entity through its verified admission, whose PeerAdmitted event applies inline), then writes
    // the occupant the commit carried onto it. A live commit, a recovery and a re-drive all land here.
    private SessionReply LandMember(WorldCrossingArrival arrival, int index, WorldAdmissionVerdict? verdict) {
        var slot = arrival.Slots[index];
        var reservationMember = arrival.Request.Members[index];
        var member = arrival.Members[index];

        if (m_landingRefusal?.Invoke(index) is { } landingRefusal) {
            return new SessionReply(
                Accepted: false,
                AssignedIndex: -1,
                Reason: landingRefusal,
                RosterEcho: string.Empty
            );
        }

        SessionReply reply;

        if (arrival.Request.PeerAdmission) {
            var occupant = new WorldTransferredOccupant(
                CatalogRig: reservationMember.CatalogRig,
                TravelTurn: member.TravelTurn
            );

            reply = (reservationMember.Source.IsLive
                ? m_server.GrantTable.AdmitTransferredPeer(
                    occupant: occupant,
                    slot: slot,
                    verdict: verdict
                )
                : m_server.GrantTable.AdmitTransferredEntity(
                    identity: reservationMember.Identity,
                    occupant: occupant,
                    slot: slot,
                    source: reservationMember.Source
                ));
        } else {
            reply = m_server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: reservationMember.Principal,
                Slot: slot,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            ));
        }
        if (reply.Accepted) {
            Land(
                bodyColor: reservationMember.BodyColor,
                border: arrival.Request.Border,
                catalogRig: reservationMember.CatalogRig,
                member: member,
                mobility: reservationMember.Mobility!.Value.Advance(),
                profile: (member.Profile ?? reservationMember.Identity),
                slot: slot
            );
        }

        return reply;
    }
    // The one undo of a landing: the first `landed` travelers of a commit that rolled back leave their indices, a
    // transferred peer or entity with the grants its admission minted, a local seat by leaving its seat. A refused
    // member, an arrival record that could not be made durable and a re-drive reproducing a recorded rollback all
    // undo here.
    private void RollBackArrival(WorldCrossingArrival arrival, int landed) {
        for (var index = 0; (index < landed); index++) {
            var slot = arrival.Slots[index];

            if (arrival.Request.PeerAdmission) {
                m_server.GrantTable.RollbackTransferredEntity(slot: slot);
            } else {
                _ = m_server.Population.TryDetachSeatForTransfer(
                    profile: out _,
                    slot: slot
                );
            }
            _ = m_borderAdmissions.Remove(key: slot);
        }
    }

    /// <summary>Returns why a commit member's carried motion cannot land anywhere, or <see langword="null"/> when its
    /// shape is sound: an unreduced arrival turn, a mapped arrival naming no motion program, or a continuum interval
    /// that is empty, unconsumed, carries no or too many boundary events, or rides an unmapped arrival. A commit, a
    /// crossing-log record and a taped arrival all refuse a member this names; only a commit can also check the
    /// destination's own motion programs.</summary>
    /// <param name="member">The commit member.</param>
    /// <returns>The fault, or <see langword="null"/>.</returns>
    internal static string? CommitMemberFault(WorldTransferCommitMember member) {
        if (!WorldFrameIsometry.IsTurn(turn: member.TravelTurn)) {
            return $"carries an unreduced arrival turn {member.TravelTurn}";
        }
        if (
            member.HasMappedArrival &&
            string.IsNullOrWhiteSpace(value: member.BodyMotionProgramName)
        ) {
            return "names no destination motion program for its mapped arrival";
        }
        if (
            (member.Continuum is { } continuum) &&
            (!member.HasMappedArrival ||
             (continuum.ContinuumEndEngineTick <= continuum.ContinuumStartEngineTick) ||
             (continuum.ConsumedThroughEngineTick < continuum.ContinuumEndEngineTick) ||
             (continuum.BoundaryEvents == 0) ||
             (continuum.BoundaryEvents > WorldContinuumTrajectory.MaxBoundaryEvents))
        ) {
            return "carries an invalid continuum interval or boundary count";
        }
        return null;
    }

    // Writes an admitted occupant's carried state onto its slot: the profile, appearance and arrival turn, its committed
    // mobility identity, and, for a mapped arrival, the pose and motion the commit carried.
    private void Land(int slot, WorldIdentity? profile, Vector3 bodyColor, byte catalogRig, in WorldMobilityIdentity mobility, string border, WorldTransferCommitMember member) {
        if (profile is not null) {
            m_server.Population.SetSeatProfile(
                profile: profile,
                slot: slot
            );
        }
        m_server.Population.SetBodyColor(
            color: bodyColor,
            slot: slot
        );
        m_server.Population.SetCatalogRig(
            catalogRig: catalogRig,
            slot: slot
        );
        m_server.Population.SetTravelTurn(
            slot: slot,
            travelTurn: member.TravelTurn
        );
        // The occupant is identified before it is placed: placing resolves contact, and the identity is what tells
        // contact that a neighbour's record of the slot it departed from shows this same occupant.
        m_server.Population.SetMobility(
            index: slot,
            mobility: in mobility
        );

        if (member.HasMappedArrival) {
            m_server.Population.ApplyMappedArrival(
                actionContinuity: (member.ActionContinuity ?? new WorldTransferActionContinuity(
                    Channels: [],
                    Registers: []
                )),
                continuum: member.Continuum,
                destinationCompletedEngineTick: m_server.CompletedEngineTicks,
                motionProgramName: member.BodyMotionProgramName,
                planarVelocity: member.PlanarVelocity,
                position: member.Position,
                slot: slot,
                verticalVelocity: member.VerticalVelocity,
                yawRadians: member.YawRadians
            );
        }

        m_borderAdmissions[slot] = border;
    }

    public void ReclaimExpired(ulong tick) {
        PruneDepartedAdmissions();

        var signedTick = unchecked((long)tick);

        while (m_deadlines.TryDequeueDue(
            tick: signedTick,
            out var key
        )) {
            ReleaseLease(key: key);
        }
    }
    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);
        var key = new WorldTransferKey(
            SourceAuthority: request.SourceAuthority,
            TransferId: request.TransferId
        );

        if (m_committed.Contains(item: key)) {
            return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} already committed");
        }
        if (IsSuspended(
            reason: out var suspended,
            request: request
        )) {
            return WorldTransferReservationReply.Refused(reason: suspended);
        }

        if (!TryCopyReservation(
            owned: out request,
            reason: out var reservationReason,
            request: request
        )) {
            return WorldTransferReservationReply.Refused(reason: reservationReason);
        }
        if (m_leases.TryGetValue(
            key: key,
            value: out var existing
        )) {
            if (!ReservationMatches(
                left: existing.Request,
                right: request
            )) {
                return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} reuses an existing source-scoped id with a different reservation");
            }

            return new WorldTransferReservationReply(
                Accepted: true,
                Reason: string.Empty,
                DeadlineDestinationTick: existing.DeadlineTick,
                BodyIndices: [.. existing.Slots],
                DestinationDefinition: existing.DestinationDefinition
            );
        }

        if (request.Members.Count == 0) {
            return WorldTransferReservationReply.Refused(reason: "reservation carries no travelers");
        }

        if (TravelerRefusal(request: request) is { } travelerRefusal) {
            return WorldTransferReservationReply.Refused(reason: travelerRefusal);
        }
        if (LeaseDeadlineRefusal(
            deadline: out var deadline,
            request: request
        ) is { } deadlineRefusal) {
            return WorldTransferReservationReply.Refused(reason: deadlineRefusal);
        }

        // The admission decision runs once, here, against the authenticated source-authority namespace, and the
        // lease carries its verdict to commit: reserve and commit can never disagree about what an arrival is
        // authorized. A colocated request is produced in-process by the host that owns both authorities, so its
        // namespace is as authenticated as a federated peer's completed handshake.
        WorldAdmissionVerdict? arrival = null;

        if (request.PeerAdmission) {
            if (WorldAdmissionDoor.TryAdmitArrival(
                entries: m_server.Definition.Admission,
                sourceAuthority: request.SourceAuthority,
                verdict: out arrival
            ) is { } arrivalRefusal) {
                return WorldTransferReservationReply.Refused(reason: $"no admission entry authorizes arrivals from '{request.SourceAuthority}' ({arrivalRefusal})");
            }
        }

        PruneDepartedAdmissions();
        var firstSlot = (request.PeerAdmission
            ? m_server.Population.LocalSeatCount
            : 0
        );
        var consumed = new bool[(request.PeerAdmission
            ? m_server.Population.Capacity
            : m_server.Population.LocalSeatCount)];

        for (var slot = 0; (slot < consumed.Length); slot++) {
            consumed[slot] = m_server.Population.IsActive(index: slot);
        }

        foreach (var lease in m_leases.Values) {
            foreach (var slot in lease.Slots) {
                if (((uint)slot) < ((uint)consumed.Length)) {
                    consumed[slot] = true;
                }
            }
        }

        if (request.BorderCapacity is { } borderCapacity) {
            var heldAtBorder = (m_leases.Values.Where(predicate: lease => string.Equals(
                a: lease.Request.Border,
                b: request.Border,
                comparisonType: StringComparison.Ordinal
            )).Sum(selector: lease => lease.Slots.Length)
                + m_borderAdmissions.Count(predicate: admission => string.Equals(
                a: admission.Value,
                b: request.Border,
                comparisonType: StringComparison.Ordinal
            )));

            if ((heldAtBorder + request.Members.Count) > borderCapacity) {
                return WorldTransferReservationReply.Refused(reason: $"border '{request.Border}' is full ({heldAtBorder}/{borderCapacity} reserved); no queue was created");
            }
        }

        var slots = new int[request.Members.Count];

        for (var index = 0; (index < request.Members.Count); index++) {
            var member = request.Members[index];
            var slot = PreferredOrLowestFree(
                consumed: consumed,
                preferred: member.PreferredSlot,
                first: firstSlot
            );

            if (slot < 0) {
                return WorldTransferReservationReply.Refused(reason: $"destination has no free body index for traveler {(index + 1)}; no queue was created");
            }

            // One question at reserve, asked of whichever authority will actually drive the body at commit: may it.
            // A colocated traveler keeps its own live principal, so the live table answers. A peer arrival has no
            // principal yet, so the arrival verdict's own templates answer — the same templates commit mints, so a
            // reservation can never bind a body the mint would then fail to authorize. An autonomous traveler has no
            // driver at all and is asked only whether the slot supports its authored source.
            if (!request.PeerAdmission) {
                if (m_server.Grants.Allows(
                    principal: member.Principal,
                    capability: WorldCapability.Drive,
                    subject: GrantSubject.Body(index: slot)
                ) is { IsAllowed: false } standing) {
                    return WorldTransferReservationReply.Refused(reason: $"{member.Principal.Describe()} cannot enter body:{slot} — {standing.DescribeDenial()}");
                }
            } else if (member.Source.IsLive) {
                if (!ArrivalDrives(
                    arrival: arrival,
                    slot: slot
                )) {
                    return WorldTransferReservationReply.Refused(reason: $"traveler {(index + 1)} cannot enter body:{slot} — '{request.SourceAuthority}' is admitted but its authored admission grants confer no Drive over the body an arrival is assigned");
                }
            } else if (!m_server.Population.SupportsSource(
                index: slot,
                source: member.Source,
                refusal: out var sourceRefusal
            )) {
                return WorldTransferReservationReply.Refused(reason: $"traveler {(index + 1)} cannot enter body:{slot} — {sourceRefusal}");
            }

            consumed[slot] = true;
            slots[index] = slot;
        }

        return InstallLease(
            arrival: arrival,
            deadline: deadline,
            key: key,
            request: request,
            slots: slots
        );
    }

    // Refuses a cohort whose travelers cannot hold a lease: a catalog rig outside the catalog, no stable mobility
    // identity, an incarnation repeated within the cohort or already leased to another transfer, or a mobility epoch
    // this destination has already consumed.
    private string? TravelerRefusal(WorldTransferReservationRequest request) {
        var reservationIncarnations = new HashSet<WorldEntityAddress>();

        for (var index = 0; (index < request.Members.Count); index++) {
            if (request.Members[index].CatalogRig >= WorldLookSource.Catalog.RigCount) {
                return $"traveler {(index + 1)} catalog rig {request.Members[index].CatalogRig} is outside 0..{(WorldLookSource.Catalog.RigCount - 1)}";
            }
            if (request.Members[index].Mobility is not { } mobility) {
                return $"traveler {(index + 1)} carries no stable mobility identity";
            }
            if (!reservationIncarnations.Add(item: mobility.Incarnation)) {
                return $"traveler {(index + 1)} repeats mobility incarnation {mobility.Incarnation}";
            }
            if (m_mobilityLeases.TryGetValue(
                key: mobility.Incarnation,
                value: out var mobilityLease
            )) {
                return $"traveler {(index + 1)} mobility incarnation is already leased to transfer {mobilityLease.Transfer.TransferId} at epoch {mobilityLease.ExpectedEpoch}";
            }
            if (
                TryKnownMobilityEpoch(
                incarnation: mobility.Incarnation,
                epoch: out var knownEpoch
            ) &&
                (knownEpoch >= mobility.Epoch)
            ) {
                return $"traveler {(index + 1)} mobility epoch {mobility.Epoch} is stale; destination has consumed through {knownEpoch}";
            }
        }
        return null;
    }
    // The deadline a reservation's lease expires at here: the source's remaining lease converted onto this authority's
    // ticks through the exact engine-tick bridge. Returns why no binding lease can be made, or null.
    private string? LeaseDeadlineRefusal(WorldTransferReservationRequest request, out ulong deadline) {
        deadline = 0UL;

        if (
            (request.SourceRateHz <= 0) ||
            (request.DeadlineSourceTick <= request.SourceTick)
        ) {
            return "source lease deadline does not advance on a positive simulation rate";
        }
        if ((FixedTickConversion.TicksPerSecond % checked((ulong)request.SourceRateHz)) != 0UL) {
            return $"source simulation rate {request.SourceRateHz}Hz does not divide the exact {FixedTickConversion.TicksPerSecond}-tick bridge";
        }

        var destinationRate = m_server.Definition.SimulationRateHz;

        if (destinationRate <= 0) {
            return "destination simulation rate is stopped, so no binding lease can expire there";
        }
        if ((FixedTickConversion.TicksPerSecond % checked((ulong)destinationRate)) != 0UL) {
            return $"destination simulation rate {destinationRate}Hz does not divide the exact {FixedTickConversion.TicksPerSecond}-tick bridge";
        }

        var sourceStepTicks = (FixedTickConversion.TicksPerSecond / checked((ulong)request.SourceRateHz));
        var destinationStepTicks = (FixedTickConversion.TicksPerSecond / checked((ulong)destinationRate));
        var remainingSourceSteps = (request.DeadlineSourceTick - request.SourceTick);
        var remainingEngineTicks = checked((remainingSourceSteps * sourceStepTicks));
        var destinationSteps = checked((((remainingEngineTicks + destinationStepTicks) - 1UL) / destinationStepTicks));

        deadline = checked(((m_server.NextInputTick - 1UL) + destinationSteps));
        return null;
    }
    // Installs the lease a recorded arrival bound without deciding its reservation again. The commit stood under the
    // decision its reservation made against the state of its own moment, which a reland need not share: a grant revoked
    // between reservation and commit refuses a seat at its join, never at a second reservation. What keeps the landing
    // safe is kept: the token is neither committed nor suspended, each traveler's mobility epoch is unleased and
    // unconsumed, every body index is free and of the kind the cohort lands in, and an arriving peer's verdict comes from
    // this destination's own admission entries, which commit mints from.
    private WorldTransferReservationReply RestoreLease(WorldCrossingArrival arrival) {
        var key = arrival.Key;

        if (m_committed.Contains(item: key)) {
            return WorldTransferReservationReply.Refused(reason: $"transfer {key.TransferId} already committed");
        }
        if (IsSuspended(
            reason: out var suspended,
            request: arrival.Request
        )) {
            return WorldTransferReservationReply.Refused(reason: suspended);
        }
        if (!TryCopyReservation(
            owned: out var request,
            reason: out var reservationReason,
            request: arrival.Request
        )) {
            return WorldTransferReservationReply.Refused(reason: reservationReason);
        }
        if (TravelerRefusal(request: request) is { } travelerRefusal) {
            return WorldTransferReservationReply.Refused(reason: travelerRefusal);
        }
        if (LeaseDeadlineRefusal(
            deadline: out var deadline,
            request: request
        ) is { } deadlineRefusal) {
            return WorldTransferReservationReply.Refused(reason: deadlineRefusal);
        }

        WorldAdmissionVerdict? verdict = null;

        if (
            request.PeerAdmission &&
            (WorldAdmissionDoor.TryAdmitArrival(
                entries: m_server.Definition.Admission,
                sourceAuthority: request.SourceAuthority,
                verdict: out verdict
            ) is { } arrivalRefusal)
        ) {
            return WorldTransferReservationReply.Refused(reason: $"no admission entry authorizes arrivals from '{request.SourceAuthority}' ({arrivalRefusal})");
        }

        PruneDepartedAdmissions();
        var first = (request.PeerAdmission
            ? m_server.Population.LocalSeatCount
            : 0);
        var end = (request.PeerAdmission
            ? m_server.Population.Capacity
            : m_server.Population.LocalSeatCount);
        var held = m_leases.Values.SelectMany(selector: static lease => lease.Slots).ToHashSet();

        foreach (var slot in arrival.Slots) {
            if ((slot < first) || (slot >= end)) {
                return WorldTransferReservationReply.Refused(reason: $"body:{slot} is outside the indices {first}..{(end - 1)} this cohort lands in");
            }
            if (
                m_server.Population.IsActive(index: slot) ||
                !held.Add(item: slot)
            ) {
                return WorldTransferReservationReply.Refused(reason: $"body:{slot} is occupied or held by another reservation");
            }
        }

        return InstallLease(
            arrival: verdict,
            deadline: deadline,
            key: key,
            request: request,
            slots: [.. arrival.Slots]
        );
    }
    // Holds the reserved body indices and each traveler's mobility epoch for one transfer until its commit, abort or
    // deadline. A reservation installs it once its decision admits the cohort; a reland installs the one the recorded
    // arrival bound.
    private WorldTransferReservationReply InstallLease(WorldTransferKey key, WorldTransferReservationRequest request, int[] slots, ulong deadline, WorldAdmissionVerdict? arrival) {
        var destinationDefinition = m_server.Definition;

        m_leases.Add(
            key: key,
            value: new Lease(
                Arrival: arrival,
                DeadlineTick: deadline,
                DestinationDefinition: destinationDefinition,
                Request: request,
                Slots: slots
            )
        );
        m_deadlines.Add(
            dueTick: unchecked((long)deadline),
            token: key
        );
        foreach (var member in request.Members) {
            var mobility = member.Mobility!.Value;

            m_mobilityLeases.Add(
                key: mobility.Incarnation,
                value: new MobilityLease(
                    Transfer: key,
                    ExpectedEpoch: mobility.Epoch
                )
            );
        }

        return new WorldTransferReservationReply(
            Accepted: true,
            BodyIndices: [.. slots],
            DeadlineDestinationTick: deadline,
            DestinationDefinition: destinationDefinition,
            Reason: string.Empty
        );
    }

    /// <summary>Lands a recorded arrival again through the commit door that landed it, under the lease it bound: a lease
    /// a restored checkpoint still holds, or the one the arrival recorded, restored without deciding its reservation
    /// again, since the commit stood under the reservation's own decision. Recovery re-executes a crossing-log arrival its checkpoint does
    /// not reflect, with no recorded outcome: every traveler lands. A replay re-executes a destination tape's arrival
    /// with the outcome the commit decided live: each traveler must land at the generation it landed at, and a recorded
    /// rollback stops at the same traveler and undoes the landings ahead of it. The record is already durable, so it is
    /// not written again. The caller holds the authority gate.</summary>
    /// <param name="arrival">The recorded arrival.</param>
    /// <param name="recorded">The outcome the commit decided live, or <see langword="null"/> when every traveler must
    /// land.</param>
    /// <param name="reason">The named refusal, when the arrival did not reproduce.</param>
    /// <returns><see langword="true"/> when the arrival landed in its recorded body indices and, with
    /// <paramref name="recorded"/>, at its recorded generations and to its recorded outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arrival"/> is <see langword="null"/>.</exception>
    public bool TryReland(WorldCrossingArrival arrival, WorldArrivalOutcome? recorded, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: arrival);

        if (
            (recorded is not null) &&
            ((recorded.Generations.Count == 0) ||
             (recorded.Generations.Count > arrival.Members.Count) ||
             (!recorded.RolledBack && (recorded.Generations.Count != arrival.Members.Count)))
        ) {
            reason = $"transfer {arrival.Request.TransferId} records {recorded.Generations.Count} landing(s) for {arrival.Members.Count} traveler(s){(recorded.RolledBack ? " before its rollback" : string.Empty)}";
            return false;
        }

        if (arrival.Slots.Count != arrival.Request.Members.Count) {
            reason = $"transfer {arrival.Request.TransferId} records {arrival.Slots.Count} body index(es) for {arrival.Request.Members.Count} traveler(s)";
            return false;
        }

        // A checkpoint can hold the original reservation's lease; the exact request answers it again. Otherwise the
        // lease the arrival bound is restored as it was decided.
        var request = arrival.Request;
        var reply = (m_leases.ContainsKey(key: arrival.Key)
            ? Reserve(request: request)
            : RestoreLease(arrival: arrival));

        if (!reply.Accepted) {
            reason = $"transfer {request.TransferId} reservation refused — {reply.Reason}";
            return false;
        }
        if (!reply.BodyIndices.SequenceEqual(second: arrival.Slots)) {
            Abort(
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            );
            reason = $"transfer {request.TransferId} reserved body:[{string.Join(
                separator: ",",
                values: reply.BodyIndices
            )}] where it landed in body:[{string.Join(
                separator: ",",
                values: arrival.Slots
            )}]";
            return false;
        }

        m_relanding = true;
        m_relandOutcome = recorded;
        m_relandRolledBack = false;

        try {
            var status = Commit(
                members: arrival.Members,
                reason: out reason,
                sourceAuthority: request.SourceAuthority,
                transferId: request.TransferId
            );

            if (recorded is not { RolledBack: true }) {
                return (status == WorldTransferStatus.Committed);
            }
            if (m_relandRolledBack) {
                reason = string.Empty;
                return true;
            }
            if (status == WorldTransferStatus.Committed) {
                reason = $"transfer {request.TransferId} committed where the recorded commit rolled back";
            }
            return false;
        } finally {
            m_relanding = false;
            m_relandOutcome = null;
            m_relandRolledBack = false;
        }
    }
    public void RetireMobility(in WorldMobilityIdentity mobility) {
        var incarnation = mobility.Incarnation;

        if (m_mobilityLeases.TryGetValue(
            key: incarnation,
            value: out var mobilityLease
        )) {
            ReleaseLease(key: mobilityLease.Transfer);
        }
        if (m_latestCommittedTransfer.TryGetValue(
            key: incarnation,
            value: out var committedTransfer
        )) {
            SupersedeCommittedIncarnation(
                incarnation: incarnation,
                key: committedTransfer
            );
        }
        foreach (var key in m_mobilityAdmissions.Keys.Where(predicate: candidate => (candidate.Incarnation == incarnation)).ToArray()) {
            _ = m_mobilityAdmissions.Remove(key: key);
        }
    }
    public WorldTransferStatus Status(string sourceAuthority, ulong transferId) {
        var key = new WorldTransferKey(
            SourceAuthority: sourceAuthority,
            TransferId: transferId
        );

        return (m_uncertain.ContainsKey(key: key)
            ? WorldTransferStatus.Uncertain
            : (m_committed.Contains(item: key)
                ? WorldTransferStatus.Committed
                : (m_leases.ContainsKey(key: key)
                    ? WorldTransferStatus.Reserved
                    : WorldTransferStatus.Missing
        )));
    }
    /// <summary>Reads the authenticated source-border identity retained for one active arrived body.</summary>
    public bool TryArrivalBorder(int bodyIndex, out string border) =>
        m_borderAdmissions.TryGetValue(
            key: bodyIndex,
            value: out border!
        );
    public bool TryCommittedPrincipal(string sourceAuthority, ulong transferId, int ordinal, out Principal principal) {
        if (
            m_committedPrincipals.TryGetValue(
            key: new WorldTransferKey(
                SourceAuthority: sourceAuthority,
                TransferId: transferId
            ),
            value: out var principals
        ) &&
            (((uint)ordinal) < ((uint)principals.Length))
        ) {
            principal = principals[ordinal];
            return true;
        }

        principal = default;
        return false;
    }
    public bool TryMobilityPrincipal(string sourceAuthority, in WorldMobilityIdentity mobility, out Principal principal) {
        if (
            m_mobilityAdmissions.TryGetValue(
            key: (sourceAuthority, mobility.Incarnation),
            value: out var admission
        ) &&
            (mobility.Epoch <= admission.Epoch)
        ) {
            principal = admission.Principal;
            return true;
        }
        principal = default;
        return false;
    }

    public WorldTransferTableCounts Counts => new(
        ActiveTransactions: (m_leases.Count + m_committed.Count),
        MobilityCredentials: m_mobilityAdmissions.Count,
        MobilityLeases: m_mobilityLeases.Count
    );
}
