using Puck.Commands;
using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>One prospective traveler in a destination reservation.</summary>
/// <param name="Principal">The source-stamped acting principal for a colocated transfer.</param>
/// <param name="PreferredSlot">The body index the traveler prefers to retain.</param>
/// <param name="Identity">The traveler's identity projection at reservation: everything of its identity a crossing
/// discloses, and nothing of the owned document behind it.</param>
/// <param name="Source">The traveler's authored intent source, preserved across the authority boundary.</param>
/// <param name="BodyColor">The source body's exact rendered material color, preserved across ownership.</param>
/// <param name="CatalogRig">The source body's entity-owned procedural rig, preserved across ownership. Destination
/// look authoring may deliberately override it; ordinary admission may not.</param>
/// <param name="Mobility">The traveler's immutable incarnation and current committed ownership epoch.</param>
/// <param name="RemoteHuman">The sender's statement that a human holds the traveler whatever its control source (a
/// human set Idle before it left). It can only add a human: the destination lands as a human every traveler that
/// carries the Live source or this statement (<see cref="LandsHuman"/>), so no sender can exempt a body from the
/// <c>networkPlayers</c> cap.</param>
/// <param name="MemberId">The request-local id the source assigned this traveler when it enqueued the crossing; it never
/// changes when the destination filters the cohort. A negative id stands for the traveler's ordinal.</param>
/// <param name="Companion">What the traveler carries as a companion, or <see langword="null"/> for one that is not:
/// the destination decides such a member on its own (its role, a duplicate, its capacity, home's confirmation of the
/// hop) and lands it autonomous, never counted against the <c>networkPlayers</c> cap.</param>
public readonly record struct WorldTransferReservationMember(Principal Principal, int PreferredSlot, WorldIdentityProjection? Identity, IntentSource Source, Vector3 BodyColor, byte CatalogRig, WorldMobilityIdentity? Mobility = null, bool RemoteHuman = false, int MemberId = -1, WorldTransferCompanion? Companion = null) {
    /// <summary>Returns the member's request-local id, given its ordinal in the request the source sent.</summary>
    /// <param name="ordinal">The member's ordinal in the source's request.</param>
    /// <returns>The member id.</returns>
    public int IdAt(int ordinal) => ((MemberId >= 0) ? MemberId : ordinal);

    /// <summary>Gets whether the destination lands this traveler as a remote human: through the peer door, with the
    /// arrival's Drive grants and its control following it, counted against the <c>networkPlayers</c> cap. The
    /// destination decides it from what it hands out, never from a sender's claim of autonomy: a traveler that keeps
    /// the Live source keeps an outside driver, so it is a human whatever the sender says. Every other traveler lands
    /// autonomous, authored by the destination with no Drive grant and no route, which gives an outside party
    /// nothing to drive and so needs no cap (the owner's ruling: a companion is not a networked player).</summary>
    public bool LandsHuman => ((Companion is null) && (Source.IsLive || RemoteHuman));
}
/// <summary>Why a destination refused one companion of a reservation it otherwise accepted.</summary>
public enum WorldCompanionRefusal : byte {
    /// <summary>The destination declares nothing for the companion's role.</summary>
    Role = 0,

    /// <summary>A body or a live lease here already names the incarnation; transient.</summary>
    Duplicate = 1,

    /// <summary>The destination has no free body for it.</summary>
    Full = 2,

    /// <summary>Home did not confirm a currently valid grant of this hop.</summary>
    Unverified = 3,

    /// <summary>The title's hop is still pending at home; transient.</summary>
    Pending = 4,

    /// <summary>Home answered that the copy is not the title's current custody.</summary>
    Stale = 5,

    /// <summary>Home answered that the title was released.</summary>
    Released = 6,

    /// <summary>The authority asked did not mint the incarnation.</summary>
    NotMyTitle = 7,

    /// <summary>The destination could not decide now; transient.</summary>
    Unavailable = 8,
}
/// <summary>One companion a destination refused while it accepted the rest of the reservation.</summary>
/// <param name="MemberId">The refused member's request-local id.</param>
/// <param name="Reason">Why.</param>
/// <param name="Detail">The refusal's text.</param>
public readonly record struct WorldMemberRefusal(int MemberId, WorldCompanionRefusal Reason, string Detail);
/// <summary>The destination's binding reservation request. The deadline is stated in the source authority's own
/// simulation ticks; the destination converts the remaining interval through the exact 50400 engine-tick bridge.</summary>
/// <param name="TransferId">The transfer key's id: allocated durably by its source before this request is sent, and
/// never used by that source again.</param>
/// <param name="SourceAuthority">The transfer key's authenticated source namespace.</param>
/// <param name="SourceRateHz">The source's simulation rate, in ticks per second.</param>
/// <param name="SourceTick">The source tick the request was made at.</param>
/// <param name="DeadlineSourceTick">The source tick the lease expires at.</param>
/// <param name="Border">The stable border identity used for destination admission accounting.</param>
/// <param name="BorderCapacity">The border's authored capacity, or <see langword="null"/> for none.</param>
/// <param name="PartyAllOrNothing">Whether the cohort binds as one transaction.</param>
/// <param name="PeerAdmission">Whether the travelers land through the peer admission door.</param>
/// <param name="Members">The prospective travelers, in cohort order.</param>
/// <param name="SourceFloor">The source's settled floor: every id below it, from this source, is settled there (arrived
/// or returned) or was never sent, so the source will never reserve, commit or ask under one again. It is at most
/// <paramref name="TransferId"/>. The destination keeps no key row below a source's highest floor and answers such a key
/// <see cref="WorldTransferStatus.Missing"/>; zero claims nothing.</param>
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
    IReadOnlyList<WorldTransferReservationMember> Members,
    ulong SourceFloor = 0UL
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
    /// <summary>Nothing for the transfer is live here, and nothing ever will be: the key is durably dead, so no later
    /// reservation leases it and no later commit lands under it. A commit answered this way was refused and applied
    /// nothing.</summary>
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
/// <param name="ActiveTransactions">The live leases, retained commits and uncertain arrivals.</param>
/// <param name="MobilityCredentials">The authenticated source aliases of admitted travelers.</param>
/// <param name="MobilityLeases">The traveler incarnations a live lease holds.</param>
/// <param name="TransferKeys">The key ledger's rows: every key holding anything here (a live lease, a retained commit,
/// an uncertain arrival or a settled key above its source's floor) and one floor per source.</param>
public readonly record struct WorldTransferTableCounts(int ActiveTransactions, int MobilityCredentials, int MobilityLeases, int TransferKeys);
/// <summary>One named action register carried across an authority change. A destination accepts it only when its
/// own seat kit declares the same name and kind; its own envelope remains authoritative.</summary>
public readonly record struct WorldTransferActionRegister(string Name, ActionStateKind Kind, FixedQ4816 Value, ulong TimerTicks);
/// <summary>Committed action registers carried across authorities. Input holds, edges and partial patterns reset
/// at the crossing and never travel with these simulation effects.</summary>
public sealed record WorldTransferActionContinuity(
    IReadOnlyList<WorldTransferActionRegister> Registers
) {
    /// <summary>Gets the continuity that carries no register.</summary>
    public static WorldTransferActionContinuity Empty { get; } = new(
        Registers: []
    );
}
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

    /// <summary>Gets whether the segment is one a carrier may hold: a nonempty engine-time interval, consumed at least
    /// through its end, that has crossed between one and <see cref="MaxBoundaryEvents"/> ownership faces. Every
    /// decoder and the escrow refuse a segment that is not.</summary>
    public bool IsWellFormed => (
        (ContinuumEndEngineTick > ContinuumStartEngineTick) &&
        (ConsumedThroughEngineTick >= ContinuumEndEngineTick) &&
        (BoundaryEvents is > 0 and <= MaxBoundaryEvents)
    );
}
/// <summary>The destination's reservation verdict and assigned body indices.</summary>
/// <param name="Accepted">Whether the destination leased any member.</param>
/// <param name="Reason">The refusal's text, or empty.</param>
/// <param name="DeadlineDestinationTick">The destination tick the lease expires at.</param>
/// <param name="BodyIndices">The body index each accepted member is bound to, in the order of
/// <paramref name="AcceptedMembers"/>.</param>
/// <param name="DestinationDefinition">The destination's definition the lease is bound to.</param>
/// <param name="AcceptedMembers">The request-local ids of the members the destination leased, in request order; absent
/// leases every member, in order.</param>
/// <param name="Refusals">Each companion the destination refused while it leased the rest.</param>
public sealed record WorldTransferReservationReply(bool Accepted, string Reason, ulong DeadlineDestinationTick, IReadOnlyList<int> BodyIndices, WorldDefinition? DestinationDefinition, IReadOnlyList<int>? AcceptedMembers = null, IReadOnlyList<WorldMemberRefusal>? Refusals = null) {
    /// <summary>Gets each companion the destination refused while it leased the rest.</summary>
    public IReadOnlyList<WorldMemberRefusal> Refusals { get; init; } = (Refusals ?? []);
    /// <summary>Gets a value indicating whether the refusal decided nothing: the request or its answer was lost on the
    /// lane, or the destination could not decide yet (it is latched on a record it cannot call durable, or its lease
    /// record did not land). The source keeps its attempt and sends the same request again.</summary>
    public bool Transient { get; init; }
    /// <summary>Returns the request-local ids the destination leased: <see cref="AcceptedMembers"/>, or every member of
    /// <paramref name="request"/> in order when the reply names none.</summary>
    /// <param name="request">The request this reply answers.</param>
    /// <returns>The leased member ids, in request order.</returns>
    public IReadOnlyList<int> LeasedIds(WorldTransferReservationRequest request) => (AcceptedMembers ?? [.. request.Members.Select(selector: static (member, ordinal) => member.IdAt(ordinal: ordinal))]);

    /// <summary>Creates a named refusal.</summary>
    public static WorldTransferReservationReply Refused(string reason) => new(
        Accepted: false,
        BodyIndices: [],
        DeadlineDestinationTick: 0,
        DestinationDefinition: null,
        Reason: reason
    );
}
/// <summary>How a committed traveler is set down at its destination.</summary>
public enum WorldTransferArrival : byte {
    /// <summary>The destination's own seat spawn: the commit's pose and motion are not applied.</summary>
    Spawn = 0,

    /// <summary>A portal or adjacency mapping: the commit's pose, velocity, motion program, action registers and
    /// continuum carry the traveler's motion across, and the destination must declare its motion program.</summary>
    Mapped = 1,

    /// <summary>An explicit pose (<c>body.pose spawn:&lt;id&gt;</c> carrying a seat home): the destination's own spawn,
    /// set down standing still at the commit's position and yaw. Nothing of the traveler's motion is carried, so the
    /// destination needs none of its motion programs.</summary>
    Posed = 2,
}
/// <summary>One detached source body carried into a previously reserved destination index. <c>TravelTurn</c> is the
/// traveler's accumulated arrival turn once it lands (<see cref="WorldFrameIsometry.AccumulateTurn"/>), which the
/// destination keeps on the occupant for the routes it describes. <c>Profile</c> is the traveler's identity projection
/// at commit, which the destination lands in place of the reservation's. <c>MemberId</c> is the request-local id of the
/// leased member it lands (negative stands for its ordinal); <c>Companion</c> is what a companion carries, its ticket the
/// one home granted for this hop.</summary>
public sealed record WorldTransferCommitMember(
    WorldIdentityProjection? Profile,
    WorldTransferArrival Arrival,
    string BodyMotionProgramName,
    FixedVector3 Position,
    FixedQ4816 YawRadians,
    FixedVector3 PlanarVelocity,
    FixedQ4816 VerticalVelocity,
    WorldTransferActionContinuity? ActionContinuity = null,
    WorldContinuumTrajectory? Continuum = null,
    FixedQ4816 TravelTurn = default,
    int MemberId = -1,
    WorldTransferCompanion? Companion = null
) {
    /// <summary>Gets the action registers the traveller carries. Carrying none is the empty continuity: an
    /// absent one reads as <see cref="WorldTransferActionContinuity.Empty"/>, so a commit means the same whichever
    /// carrier built it.</summary>
    public WorldTransferActionContinuity ActionContinuity { get; init; } = (ActionContinuity ?? WorldTransferActionContinuity.Empty);
}
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

    // Home's confirmations for the commit being decided, asked for this commit by this destination's own host: a
    // companion lands only under the grant as it stands at the commit. Null outside a live commit.
    private IReadOnlyDictionary<(WorldTransferKey Key, WorldEntityAddress Incarnation), ulong>? m_commitConfirmations;
    // The request-local ids each committed transfer landed: a companion the commit left out is absent.
    private readonly Dictionary<WorldTransferKey, int[]> m_committedLanded = new();

    // The first crossing sequence this authority has not made durable; checkpointed as the recovery watermark.
    private ulong m_crossingSequence;
    // Set while a recorded arrival lands again: its record is already durable, so it is not written twice.
    private bool m_relanding;
    // The body indices the commit being decided landed at and rolled back: the generations those landings took outlive
    // the rollback, and the key's tombstone names them so recovery advances them too.
    private readonly List<int> m_rolledBack = [];
    // Set while a re-drive lands a taped arrival: the outcome its landings must reproduce, and whether the landing
    // reached the recorded rollback.
    private WorldArrivalOutcome? m_relandOutcome;
    // Set while a recorded arrival lands again: the companions it re-adopted, which its landing re-adopts again.
    private IReadOnlyList<WorldEntityAddress> m_relandReadopted = [];
    private IReadOnlyList<int> m_relandLeased = [];
    private bool m_relandRolledBack;

    // A re-drive's one detached identity per owned id. Live, an id names one owned object, so every seat a recorded
    // switch or home adoption binds under it shares that object; the re-drive binds them to one detached identity the
    // same way, for the whole re-drive. Only Restore clears it.
    private readonly Dictionary<string, WorldIdentity> m_detachedIdentities = new(comparer: StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="WorldTransferEscrow"/> class for one authority.</summary>
    /// <param name="server">The authority the escrow lands travelers in.</param>
    /// <param name="landingRefusal">An admission policy evaluated at each commit member's ordinal, returning a refusal
    /// reason or <see langword="null"/> to admit, fixed at composition. No production composition passes one; a law
    /// passes one to refuse a member after the members ahead of it have landed.</param>
    public WorldTransferEscrow(WorldServer server, Func<int, string?>? landingRefusal = null) {
        m_server = server;
        m_landingRefusal = landingRefusal;
        Custody = new WorldCompanionCustody(authority: () => m_server.AuthorityIdentity);
    }

    /// <summary>Gets this authority's custody reducer: the titles it holds as home and the custody facts it holds as a
    /// custodian, folded from its durable custody records.</summary>
    public WorldCompanionCustody Custody { get; }

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
                : []),
                Landed: (m_committedLanded.TryGetValue(
                    key: key,
                    value: out var landed
                )
                ? [.. landed]
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
            CrossingSequence: m_crossingSequence,
            SpentKeys: [.. m_spent.Select(selector: static pair => (pair.Key, pair.Value.Committed, pair.Value.Durable))],
            Floors: [.. m_floors.Select(selector: static pair => (pair.Key, pair.Value))],
            Custody: [.. Custody.History]
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
        m_committedLanded.Clear();
        m_latestCommittedTransfer.Clear();
        m_mobilityLeases.Clear();
        m_mobilityAdmissions.Clear();
        m_borderAdmissions.Clear();
        m_detachedIdentities.Clear();
        m_spent.Clear();
        m_rows.Clear();
        m_sourceRows.Clear();
        m_floors.Clear();
        m_crossingSequence = checkpoint.CrossingSequence;

        // A restored row was admitted when it was made; every lease and retained commit below holds one too.
        foreach (var row in checkpoint.SpentKeys) {
            EnterRow(key: row.Key);
            m_spent[row.Key] = new SpentKey(
                Committed: row.Committed,
                Durable: row.Durable
            );
        }
        foreach (var row in checkpoint.Floors) {
            m_floors[row.SourceAuthority] = row.Floor;
        }
        Custody.Restore(history: checkpoint.Custody);

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
            EnterRow(key: lease.Key);
        }

        foreach (var row in checkpoint.Committed) {
            _ = m_committed.Add(item: row.Key);
            EnterRow(key: row.Key);
            m_committedMembers[row.Key] = row.Members.Select(selector: CopyCommitMember).ToArray();
            m_committedPrincipals[row.Key] = row.Principals;
            m_committedIncarnations[row.Key] = [.. row.Incarnations];
            m_committedLanded[row.Key] = [.. row.Landed];
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
                !WorldIdentityProjectionWire.Matches(
                left: a.Profile,
                right: b.Profile
            ) ||
                (a.Arrival != b.Arrival) ||
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
    private static bool ActionContinuityMatches(WorldTransferActionContinuity left, WorldTransferActionContinuity right) =>
        (ReferenceEquals(
            objA: left,
            objB: right
        ) || ((left.Registers is not null) && (right.Registers is not null) &&
        left.Registers.SequenceEqual(second: right.Registers)));
    // IReadOnlyList does not imply immutable storage. Retain a value image of continuity, including at checkpoint
    // capture/restore, so caller edits cannot rewrite which commit this idempotency receipt accepted.
    private static WorldTransferCommitMember CopyCommitMember(WorldTransferCommitMember member) => member with {
        ActionContinuity = new WorldTransferActionContinuity(
            Registers: [.. member.ActionContinuity.Registers]
        ),
    };
    private static int PreferredOrLowestFree(bool[] consumed, int preferred, int first, int limit) {
        if (
            (preferred >= first) &&
            (preferred < limit) &&
            (((uint)preferred) < ((uint)consumed.Length)) &&
            !consumed[preferred]
        ) {
            return preferred;
        }

        for (var slot = first; ((slot < limit) && (slot < consumed.Length)); slot++) {
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
            (left.SourceFloor != right.SourceFloor) ||
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
                !WorldIdentityProjectionWire.Matches(
                left: a.Identity,
                right: b.Identity
            )
            ) {
                return false;
            }
        }

        return true;
    }
    // Drops a committed transaction's retry payload and keeps its outcome in the key ledger, so the key answers
    // Committed for as long as its source can ask.
    private void RetireCommittedTransaction(WorldTransferKey key) {
        if (!m_committed.Remove(item: key)) {
            return;
        }
        Spend(
            committed: true,
            key: key
        );
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
        _ = m_committedMembers.Remove(key: key);
        _ = m_committedLanded.Remove(key: key);
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

    /// <summary>Releases a live reservation; its key is dead here from then on. A committed, uncertain or unknown key
    /// is left as it is.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    public void Abort(string sourceAuthority, ulong transferId) => KillLease(key: new WorldTransferKey(
        SourceAuthority: sourceAuthority,
        TransferId: transferId
    ));
    /// <summary>Retires disposable exact-retry payload after the source has observed and published commit. The key keeps
    /// answering <see cref="WorldTransferStatus.Committed"/>.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
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
    /// <summary>Lands a reserved cohort and makes its arrival durable before answering. A committed key answers
    /// <see cref="WorldTransferStatus.Committed"/> to every later commit, its retry payload retired or not and whatever
    /// payload the commit carries: a different one lands nothing and is named in <paramref name="reason"/>. A transfer
    /// whose arrival record is uncertain answers <see cref="WorldTransferStatus.Uncertain"/> and lands nothing. A refusal
    /// kills the key's lease and answers <see cref="WorldTransferStatus.Missing"/> only once the key's death is durable,
    /// so no later reservation or commit under it is admitted; a death that cannot be made durable answers
    /// <see cref="WorldTransferStatus.Uncertain"/>.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    /// <param name="members">The travelers in reservation order.</param>
    /// <param name="reason">The named refusal or uncertainty, the mismatch a committed key's different payload carried,
    /// or empty when the commit stands as sent.</param>
    /// <param name="confirmed">Home's answers to this destination's own question for this commit: the epoch each
    /// companion's currently granted hop lands it at. A leased companion with no matching answer is left out of the
    /// landing while the rest land.</param>
    /// <returns><see cref="WorldTransferStatus.Committed"/>, <see cref="WorldTransferStatus.Uncertain"/>, or
    /// <see cref="WorldTransferStatus.Missing"/> for a refusal.</returns>
    public WorldTransferStatus Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason, IReadOnlyDictionary<(WorldTransferKey Key, WorldEntityAddress Incarnation), ulong>? confirmed = null) {
        var key = new WorldTransferKey(
            SourceAuthority: sourceAuthority,
            TransferId: transferId
        );
        var leased = m_leases.ContainsKey(key: key);

        m_commitConfirmations = confirmed;
        m_rolledBack.Clear();
        WorldTransferStatus status;

        try {
            status = CommitUnder(
                key: key,
                members: members,
                reason: out reason
            );
        } finally {
            m_commitConfirmations = null;
        }

        // A relanding arrival is already durable, and a refused one leaves recovery or the re-drive to decide. A
        // committed key never reaches here: it answers Committed.
        if (
            (status != WorldTransferStatus.Missing) ||
            m_relanding
        ) {
            return status;
        }
        // The refused commit released its lease; the key's row now holds its death.
        if (leased) {
            ReleaseLease(key: key);
            Spend(
                committed: false,
                key: key
            );
        }
        return AnswerMissing(
            burned: [.. m_rolledBack],
            key: key,
            reason: ref reason
        );
    }
    private WorldTransferStatus CommitUnder(WorldTransferKey key, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        var transferId = key.TransferId;

        if (IsUncertain(
            key: key,
            reason: out reason
        )) {
            return WorldTransferStatus.Uncertain;
        }
        if (
            m_spent.TryGetValue(
                key: key,
                value: out var spent
            ) &&
            spent.Committed
        ) {
            return WorldTransferStatus.Committed;
        }
        // A committed key answers its own fate: the commit that stood. A different payload under it lands nothing and
        // is named, but the key stays committed.
        if (m_committed.Contains(item: key)) {
            reason = (CommitMatches(
                left: m_committedMembers[key],
                right: members
            )
                ? string.Empty
                : $"transfer {transferId} is committed with a different commit; this one landed nothing");
            return WorldTransferStatus.Committed;
        }
        if (IsBelowFloor(key: key)) {
            reason = $"transfer {transferId} is below its source's settled floor";
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
        for (var index = 0; (index < members.Count); index++) {
            var leasedId = lease.Request.Members[index].IdAt(ordinal: index);

            if (((members[index].MemberId < 0) ? index : members[index].MemberId) != leasedId) {
                reason = $"transfer {transferId} commit member {(index + 1)} is not the member the reservation leased there";

                return WorldTransferStatus.Missing;
            }
        }

        if (members.Any(predicate: static member => ((member.ActionContinuity is not { Registers: not null })))) {
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
                (member.Arrival == WorldTransferArrival.Mapped) &&
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

        var offered = new WorldCrossingArrival(
            Members: [.. members],
            Request: lease.Request,
            Slots: [.. lease.Slots]
        );
        var generations = new List<int>(capacity: members.Count);
        var landedOrdinals = new List<int>(capacity: members.Count);
        // A re-drive stops where the recorded commit rolled back; a live commit or a recovery lands every traveler.
        var stop = ((m_relandOutcome is { RolledBack: true } recorded)
            ? recorded.Generations.Count
            : -1);

        // Every landing this commit made is undone, then reported with its rollback, so a re-drive lands and rolls
        // back the same travelers: a landing advances its index's generation, which outlives it.
        void RollBack() {
            var landedSoFar = Narrowed(
                arrival: offered,
                ordinals: landedOrdinals
            );

            m_rolledBack.AddRange(collection: landedSoFar.Slots.Take(count: generations.Count));
            RollBackArrival(
                arrival: landedSoFar,
                landed: generations.Count
            );
            if (generations.Count > 0) {
                m_server.ArrivalTap?.Invoke(
                    arg1: landedSoFar,
                    arg2: new WorldArrivalOutcome(
                        Adopted: [],
                        Generations: [.. generations],
                        RolledBack: true
                    )
                );
            }
        }

        for (var index = 0; (index < members.Count); index++) {
            if (landedOrdinals.Count == stop) {
                RollBack();
                m_relandRolledBack = true;
                reason = $"transfer {transferId} rolled back ahead of traveler {(index + 1)}, as recorded";
                return WorldTransferStatus.Missing;
            }

            var slot = lease.Slots[index];
            var companion = lease.Request.Members[index].Companion;

            // A companion lands only under home's grant as it stands at this commit, and the ticket the commit carries
            // is the one the reservation leased. One that does not is left out; the rest land. A recovery or a re-drive
            // lands what the durable arrival already decided.
            if ((companion is not null) && !m_relanding && !CommitConfirmed(
                carried: members[index].Companion,
                key: key,
                leased: lease.Request.Members[index]
            )) {
                continue;
            }

            var reply = LandMember(
                arrival: offered,
                index: index,
                verdict: lease.Arrival
            );

            if (!reply.Accepted) {
                if ((companion is not null) && !m_relanding) {
                    continue;
                }
                RollBack();
                reason = $"body:{slot} refused reserved commit — {reply.Reason}";
                return WorldTransferStatus.Missing;
            }

            landedOrdinals.Add(item: index);
            generations.Add(item: m_server.Population.Generation(index: slot));

            if (
                (m_relandOutcome is { } pinned) &&
                (generations[^1] != pinned.Generations[(generations.Count - 1)])
            ) {
                RollBack();
                reason = $"body:{slot} landed at generation {generations[^1]} where the recorded commit landed it at {pinned.Generations[(generations.Count - 1)]}";
                return WorldTransferStatus.Missing;
            }
        }
        if (landedOrdinals.Count == stop) {
            RollBack();
            m_relandRolledBack = true;
            reason = $"transfer {transferId} rolled back after landing every traveler, as recorded";
            return WorldTransferStatus.Missing;
        }
        if (
            (landedOrdinals.Count == 0) ||
            lease.Request.Members.Select(selector: static (member, ordinal) => (member, ordinal)).Any(predicate: pair => (pair.member.Companion is null) && !landedOrdinals.Contains(item: pair.ordinal))
        ) {
            RollBack();
            reason = $"transfer {transferId} commit landed no traveler";
            return WorldTransferStatus.Missing;
        }

        // The arrival is what landed: its record, its tape entry and its outcome name only the landed members, with the
        // generation each took, so a recovery and a re-drive land exactly them.
        var arrival = Narrowed(
            arrival: offered,
            ordinals: landedOrdinals
        ) with {
            Generations = [.. generations],
            Leased = (m_relanding
                ? [.. m_relandLeased]
                : ((landedOrdinals.Count < lease.Request.Members.Count)
                    ? [.. lease.Request.Members.Select(selector: static (member, ordinal) => member.IdAt(ordinal: ordinal))]
                    : [])),
            Readopted = (m_relanding
                ? [.. m_relandReadopted]
                : [.. landedOrdinals.Where(predicate: ordinal => IsReturn(member: lease.Request.Members[ordinal])).Select(selector: ordinal => lease.Request.Members[ordinal].Mobility!.Value.Incarnation)]),
        };

        lease = lease with {
            Request = arrival.Request,
            Slots = [.. arrival.Slots],
        };

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
        m_committedLanded[key] = [.. arrival.Request.Members.Select(selector: static (member, ordinal) => member.IdAt(ordinal: ordinal))];
        // The arrival's custody effect (a re-adoption at home) folds wherever it lands: live, in recovery and in a re-drive.
        Custody.Apply(record: new WorldCrossingRecord.Arrival(Value: arrival));
        // A relanded arrival is the key's last durable record: it supersedes the death its lease record left.
        _ = m_spent.Remove(key: key);
        m_committedMembers[key] = [.. offered.Members];
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
        var adopted = AdoptHomeProfiles(arrival: arrival);
        // Reported once the commit stands, with the generation each traveler landed at and the projection each
        // traveler that came home is bound to.
        var bindings = WorldCompanionCustody.Bindings(arrival: arrival);

        m_server.ArrivalTap?.Invoke(
            arg1: arrival,
            arg2: new WorldArrivalOutcome(
                Adopted: adopted,
                Bindings: [.. arrival.Request.Members.Select(selector: member => bindings.Where(predicate: bound => (bound.Companion == member.Mobility?.Incarnation)).Select(selector: static bound => (WorldCompanionBinding?)bound.Binding).FirstOrDefault())],
                Generations: [.. generations],
                Readopted: arrival.Readopted,
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

        // An ordinary join can take a reserved seat before commit. Join itself is idempotent over an active seat;
        // a transfer must refuse it rather than overwrite its occupant or detach it during a later rollback.
        if (m_server.Population.IsActive(index: slot)) {
            return new SessionReply(
                Accepted: false,
                AssignedIndex: -1,
                Reason: $"reserved body:{slot} is no longer free",
                RosterEcho: string.Empty
            );
        }

        SessionReply reply;
        var profile = ArrivingProfile(
            arrival: arrival,
            index: index
        );

        if (arrival.Request.PeerAdmission || (reservationMember.Companion is not null)) {
            var occupant = new WorldTransferredOccupant(
                CatalogRig: reservationMember.CatalogRig,
                TravelTurn: member.TravelTurn
            );

            reply = (reservationMember.LandsHuman
                ? m_server.GrantTable.AdmitTransferredPeer(
                    occupant: occupant,
                    slot: slot,
                    source: reservationMember.Source,
                    verdict: verdict
                )
                : m_server.GrantTable.AdmitTransferredEntity(
                    identity: profile,
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
                profile: profile,
                slot: slot
            );
            // A companion that came home is its placement's inhabitant again, bound as the placement is installed now.
            if (IsReturn(member: reservationMember) && (Custody.PlacementOf(incarnation: reservationMember.Mobility!.Value.Incarnation) is { } placementId)) {
                _ = m_server.Population.ReadoptInhabitant(
                    definition: m_server.Definition,
                    placementId: placementId,
                    slot: slot
                );
            }
        }

        return reply;
    }

    /// <summary>Binds a re-drive's taped identity to the one detached identity its id names: built from the first
    /// projection taped under the id, and adopting each later one, so every seat a recorded switch or home adoption
    /// binds under one id shares it, as the live seats shared the one owned object.</summary>
    /// <param name="taped">The projection the tape recorded for the binding.</param>
    /// <param name="detached">The detached identity to bind.</param>
    /// <param name="reason">Why the detached identity refused the taped projection, or empty.</param>
    /// <returns><see langword="true"/> when the projection is bound.</returns>
    internal bool TryBindDetached(in WorldIdentityProjection taped, out WorldIdentity detached, out string reason) {
        var carried = WorldIdentity.FromProjection(
            defaults: m_server.Definition.PlayerDefaults,
            projection: in taped
        );

        if (!m_detachedIdentities.TryGetValue(
            key: taped.Id,
            value: out detached!
        )) {
            detached = carried;
            m_detachedIdentities.Add(
                key: taped.Id,
                value: detached
            );
            reason = string.Empty;
            return true;
        }

        return detached.TryAdopt(carried: carried, reason: out reason);
    }

    // The identity traveler `index` lands as: the projection its commit carried, or its reservation's when the commit
    // carries none, rebuilt against this world's player defaults. Nothing of the owned document behind it ever arrives.
    // Home adoption waits until every landing succeeds and the arrival is durable.
    private WorldIdentity? ArrivingProfile(WorldCrossingArrival arrival, int index) {
        if ((arrival.Members[index].Profile ?? arrival.Request.Members[index].Identity) is not { } projection) {
            return null;
        }

        return WorldIdentity.FromProjection(
            defaults: m_server.Definition.PlayerDefaults,
            projection: in projection
        );
    }
    // Catalog writes cannot be undone by detaching a seat. Adopt only after the arrival stands, on recovery too;
    // a taped rollback never reaches this point. Returns, in member order, the projection each traveler that came home
    // is bound to (what its owned identity holds once the adoption stands, a partial one included) and null for a
    // traveler that did not; the tape records it as part of the arrival's outcome.
    // Several travelers across arrivals may come home under one owned identity: live, they bind the one object, so each
    // adoption writes into what the others hold, and the tape records every binding once every adoption has run. A
    // re-drive binds travelers whose taped projections name one identity to one shared detached identity, so they
    // alias exactly as they did live.
    private WorldIdentityProjection?[] AdoptHomeProfiles(WorldCrossingArrival arrival) {
        var adopted = new WorldIdentityProjection?[arrival.Slots.Count];
        var bound = new WorldIdentity?[arrival.Slots.Count];

        for (var index = 0; (index < arrival.Slots.Count); index++) {
            var slot = arrival.Slots[index];
            var carried = m_server.Population.EntryBody(index: slot)?.Profile;
            var owned = ((carried is null)
                ? null
                : HomeIdentity(arrival: arrival, index: index, id: carried.Id));

            // A re-drive of a recorded outcome is a function of its tape: a traveler the tape records coming home binds
            // the projection the live adoption bound, facts and records included, in a detached identity that saves
            // nothing, and nothing of the live catalog enters it. Where the owned identity has moved since the
            // recording is reported, read-only.
            if (m_relandOutcome is { } recorded) {
                if (recorded.Adopted[index] is not { } taped) {
                    continue;
                }

                if (!TryBindDetached(detached: out var detached, reason: out var adoptionReason, taped: in taped)) {
                    throw ReplayRefusal.ArrivalRefused.Raise(message: $"home identity '{taped.Id}' could not reproduce its recorded adoption — {adoptionReason}");
                }
                if (owned is not null) {
                    WorldReplaySnapshot.ReportAdoptionDrift(
                        current: owned,
                        narrationHub: m_server.Profiles.NarrationHub,
                        taped: WorldIdentity.FromProjection(defaults: m_server.Definition.PlayerDefaults, projection: in taped),
                        used: WorldReplaySnapshot.HomeArrivalTapedUsed
                    );
                }
                BindHomeProfile(profile: detached, slot: slot);
                adopted[index] = taped;
                continue;
            }
            if (
                (carried is null) ||
                (owned is null)
            ) {
                continue;
            }
            if (
                !m_server.Profiles.TryAdopt(carried: carried, owned: owned, reason: out var reason) &&
                m_server.Output.HasNarrationSink
            ) {
                m_server.Output.Narrate(
                    channel: "world.identity",
                    text: $"[world.identity: world:{owned.Id} came home and did not adopt everything it carried — {reason}]"
                );
            }
            BindHomeProfile(profile: owned, slot: slot);
            bound[index] = owned;
        }

        // Projected after every adoption: a later traveler's adoption into a shared identity is part of what each
        // traveler bound to it holds.
        for (var index = 0; (index < bound.Length); index++) {
            if (bound[index] is { } identity) {
                adopted[index] = identity.Project();
            }
        }

        return adopted;
    }
    // Binds a home traveler's identity, keeping the material color the ordinary landing already installed.
    private void BindHomeProfile(WorldIdentity profile, int slot) {
        var color = m_server.Population.BodyColor(index: slot);

        m_server.Population.SetSeatProfile(profile: profile, slot: slot);
        m_server.Population.SetBodyColor(color: color, slot: slot);
    }
    // The owned identity a local seat of this authority left with, when traveler `index` is that seat coming home: a
    // colocated arrival of this process's own local seats (a remote one admits peers, and a remote incarnation claim is
    // unauthenticated) at the seat this authority minted it at (WorldServer.HomeSeatIdentity).
    private WorldIdentity? HomeIdentity(WorldCrossingArrival arrival, int index, string id) => ((
        !arrival.Request.PeerAdmission &&
        (arrival.Request.Members[index].Mobility is { } mobility)
    )
        ? m_server.HomeSeatIdentity(
            id: id,
            mobility: mobility,
            slot: arrival.Slots[index]
        )
        : null);
    // The one undo of a landing: the first `landed` travelers of a commit that rolled back leave their indices, a
    // transferred peer or entity with the grants its admission minted, a local seat by leaving its seat. A refused
    // member, an arrival record that could not be made durable and a re-drive reproducing a recorded rollback all
    // undo here.
    private void RollBackArrival(WorldCrossingArrival arrival, int landed) {
        for (var index = 0; (index < landed); index++) {
            var slot = arrival.Slots[index];

            if (arrival.Request.PeerAdmission || (arrival.Request.Members[index].Companion is not null)) {
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
    /// shape is sound: an arrival kind outside <see cref="WorldTransferArrival"/>, an unreduced arrival turn, a mapped
    /// arrival naming no motion program, or a continuum interval that is empty, unconsumed, carries no or too many
    /// boundary events, or rides an arrival that is not mapped. A commit, a crossing-log record and a taped arrival all
    /// refuse a member this names; only a commit can also check the destination's own motion programs.</summary>
    /// <param name="member">The commit member.</param>
    /// <returns>The fault, or <see langword="null"/>.</returns>
    internal static string? CommitMemberFault(WorldTransferCommitMember member) {
        if (!Enum.IsDefined(value: member.Arrival)) {
            return $"carries an unknown arrival kind {(byte)member.Arrival}";
        }
        if (!WorldFrameIsometry.IsTurn(turn: member.TravelTurn)) {
            return $"carries an unreduced arrival turn {member.TravelTurn}";
        }
        if (
            (member.Arrival == WorldTransferArrival.Mapped) &&
            string.IsNullOrWhiteSpace(value: member.BodyMotionProgramName)
        ) {
            return "names no destination motion program for its mapped arrival";
        }
        if (
            (member.Continuum is { } continuum) &&
            ((member.Arrival != WorldTransferArrival.Mapped) || !continuum.IsWellFormed)
        ) {
            return "carries an invalid continuum interval or boundary count";
        }
        return null;
    }

    // Writes an admitted occupant's carried state onto its slot: the profile, appearance and arrival turn, its committed
    // mobility identity, and the commit's pose: with its carried motion for a mapped arrival, standing still for a posed one.
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

        if (member.Arrival == WorldTransferArrival.Posed) {
            m_server.Population.ApplyPosedArrival(
                position: member.Position,
                slot: slot,
                yawRadians: member.YawRadians
            );
        } else if (member.Arrival == WorldTransferArrival.Mapped) {
            m_server.Population.ApplyMappedArrival(
                actionContinuity: member.ActionContinuity,
                continuum: member.Continuum,
                destinationCompletedEngineTick: m_server.CompletedEngineTicks,
                motionProgramName: member.BodyMotionProgramName,
                planarVelocity: member.PlanarVelocity,
                position: member.Position,
                slot: slot,
                verticalVelocity: member.VerticalVelocity,
                yawRadians: member.YawRadians
            );
            // The carried registers are the body's own state, so the size, kit and look they select are the body's
            // before it first advances here.
            m_server.Population.SyncBodyCells(definition: m_server.Definition);
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
            KillLease(key: key);
        }
    }
    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request, IReadOnlyDictionary<(WorldTransferKey Key, WorldEntityAddress Incarnation), ulong>? confirmed = null) {
        ArgumentNullException.ThrowIfNull(argument: request);
        var key = new WorldTransferKey(
            SourceAuthority: request.SourceAuthority,
            TransferId: request.TransferId
        );

        if (
            m_committed.Contains(item: key) ||
            (m_spent.TryGetValue(
                key: key,
                value: out var spent
            ) && spent.Committed)
        ) {
            return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} already committed");
        }
        if (m_spent.ContainsKey(key: key)) {
            return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} was already leased or answered here, and a key is leased at most once");
        }
        if (IsSuspended(
            reason: out var suspended,
            request: request
        )) {
            return WorldTransferReservationReply.Refused(reason: suspended) with { Transient = true };
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
            var leasedIds = existing.Request.Members.Select(selector: static (member, ordinal) => member.IdAt(ordinal: ordinal)).ToArray();

            if (!ReservationMatches(
                left: existing.Request,
                right: Leased(
                    ids: leasedIds,
                    request: request
                )
            )) {
                return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} reuses an existing source-scoped id with a different reservation");
            }

            return new WorldTransferReservationReply(
                Accepted: true,
                AcceptedMembers: leasedIds,
                Reason: string.Empty,
                DeadlineDestinationTick: existing.DeadlineTick,
                BodyIndices: [.. existing.Slots],
                DestinationDefinition: existing.DestinationDefinition,
                Refusals: [.. request.Members.Select(selector: static (member, ordinal) => member.IdAt(ordinal: ordinal)).Where(predicate: id => !leasedIds.Contains(value: id)).Select(selector: static id => new WorldMemberRefusal(
                    Detail: "refused when this transfer was reserved",
                    MemberId: id,
                    Reason: WorldCompanionRefusal.Unverified
                ))]
            );
        }

        if (IsBelowFloor(key: key)) {
            return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} is below its source's settled floor");
        }
        if (request.SourceFloor > request.TransferId) {
            return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} claims a settled floor {request.SourceFloor} above its own id");
        }
        if (request.Members.Count == 0) {
            return WorldTransferReservationReply.Refused(reason: "reservation carries no travelers");
        }
        if (RowRefusal(
            floor: request.SourceFloor,
            key: key
        ) is { } rowRefusal) {
            return WorldTransferReservationReply.Refused(reason: rowRefusal);
        }
        if (((m_leases.Count + m_committed.Count) + m_uncertain.Count) >= WorldTransferLimits.Transactions) {
            return WorldTransferReservationReply.Refused(reason: $"transfer history capacity is full ({WorldTransferLimits.Transactions}); acknowledge commits or retire travelers before reserving again");
        }
        var additionalAdmissions = request.Members.Count(predicate: member => ((member.Mobility is { } mobility) &&
            !m_mobilityAdmissions.ContainsKey(key: (request.SourceAuthority, mobility.Incarnation))));
        // Each live lease holds space for every alias its commit may add. Failed and aborted leases release it.
        if (((((long)m_mobilityAdmissions.Count) + m_mobilityLeases.Count) + additionalAdmissions) > WorldTransferLimits.MobilityAdmissions) {
            return WorldTransferReservationReply.Refused(reason: $"transfer mobility admission capacity is full ({WorldTransferLimits.MobilityAdmissions}); retire travelers before reserving again");
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
            if (NetworkPlayersRefusal(request: request) is { } capRefusal) {
                return WorldTransferReservationReply.Refused(reason: capRefusal);
            }
        }

        PruneDepartedAdmissions();
        var firstSlot = (request.PeerAdmission
            ? m_server.Population.LocalSeatCount
            : 0
        );
        // A seat-door traveler takes a local seat; a peer and a companion take a peer slot.
        var seatLimit = (request.PeerAdmission
            ? m_server.Population.Capacity
            : m_server.Population.LocalSeatCount);
        var consumed = new bool[m_server.Population.Capacity];

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
        // A held slot waits for its own body: only that companion's verified return binds it.
        for (var slot = 0; (slot < consumed.Length); slot++) {
            consumed[slot] |= (Custody.HeldPlacement(slot: slot) is not null);
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

        var slotList = new List<int>(capacity: request.Members.Count);
        var acceptedOrdinals = new List<int>(capacity: request.Members.Count);
        var refusals = new List<WorldMemberRefusal>();

        for (var index = 0; (index < request.Members.Count); index++) {
            var member = request.Members[index];

            if (member.Companion is { } companion) {
                if (CompanionRefusal(
                    companion: companion,
                    confirmed: confirmed,
                    consumed: consumed,
                    key: key,
                    member: member,
                    slot: out var companionSlot
                ) is { } companionRefusal) {
                    refusals.Add(item: new WorldMemberRefusal(
                        Detail: companionRefusal.Detail,
                        MemberId: member.IdAt(ordinal: index),
                        Reason: companionRefusal.Reason
                    ));
                    continue;
                }
                consumed[companionSlot] = true;
                slotList.Add(item: companionSlot);
                acceptedOrdinals.Add(item: index);
                continue;
            }

            var slot = PreferredOrLowestFree(
                consumed: consumed,
                preferred: member.PreferredSlot,
                first: firstSlot,
                limit: seatLimit
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
            } else {
                if (
                    member.LandsHuman &&
                    !ArrivalDrives(
                    arrival: arrival,
                    slot: slot
                )
                ) {
                    return WorldTransferReservationReply.Refused(reason: $"traveler {(index + 1)} cannot enter body:{slot} — '{request.SourceAuthority}' is admitted but its authored admission grants confer no Drive over the body an arrival is assigned");
                }
                if (!m_server.Population.SupportsSource(
                    index: slot,
                    source: member.Source,
                    refusal: out var sourceRefusal
                )) {
                    return WorldTransferReservationReply.Refused(reason: $"traveler {(index + 1)} cannot enter body:{slot} — {sourceRefusal}");
                }
            }

            consumed[slot] = true;
            slotList.Add(item: slot);
            acceptedOrdinals.Add(item: index);
        }

        if (acceptedOrdinals.Count == 0) {
            return WorldTransferReservationReply.Refused(reason: ((refusals.Count > 0)
                ? $"{refusals[0].Reason}: {refusals[0].Detail}"
                : "reservation leases no traveler"));
        }

        var acceptedIds = acceptedOrdinals.Select(selector: ordinal => request.Members[ordinal].IdAt(ordinal: ordinal)).ToArray();
        var slots = slotList.ToArray();

        request = Leased(
            ids: acceptedIds,
            request: request
        );

        // Written ahead of the acceptance: a recovered destination finds the key leased, so it never leases it again.
        // A lease that is not known durable changes nothing here. An uncertain one may still be in the log, but this
        // activation records nothing more, so it can neither lease the key again nor answer it Missing; its recovery
        // finds the record and declares the key dead.
        var durability = m_server.RecordCrossingHeld(
            reason: out var leaseDetail,
            record: new WorldCrossingRecord.Lease(
                Floor: request.SourceFloor,
                Key: key
            )
        );

        if (durability != WorldCrossingDurability.Durable) {
            return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} lease could not be made durable — {leaseDetail}") with { Transient = true };
        }
        RaiseFloor(
            floor: request.SourceFloor,
            sourceAuthority: request.SourceAuthority
        );
        return InstallLease(
            arrival: arrival,
            deadline: deadline,
            key: key,
            request: request,
            slots: slots
        ) with {
            AcceptedMembers = acceptedIds,
            Refusals = refusals,
        };
    }
    // The request narrowed to the members named by `ids`, in request order, each carrying its id explicitly so the
    // narrowing never renumbers a member.
    private static WorldTransferReservationRequest Leased(WorldTransferReservationRequest request, IReadOnlyList<int> ids) => request with {
        Members = [.. request.Members.Select(selector: static (member, ordinal) => member with { MemberId = member.IdAt(ordinal: ordinal) }).Where(predicate: member => ids.Contains(value: member.MemberId))],
    };
    /// <summary>Returns the hop tickets this destination asks home to confirm before it decides
    /// <paramref name="request"/>: one per companion it would otherwise take (a role declared here, no body or live lease
    /// here naming its incarnation) whose ticket names this very hop. None when the key already holds a lease, an arrival
    /// or a death here or this authority is latched, since each of those answers without home's word; a companion coming
    /// home is decided from this authority's own records and asks nobody. The caller holds the authority gate.</summary>
    /// <param name="request">The reservation request.</param>
    /// <returns>The tickets to confirm, in member order; empty when the decision needs no confirmation.</returns>
    public IReadOnlyList<WorldCustodyTicket> ReservationHopQuestion(WorldTransferReservationRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);

        var key = new WorldTransferKey(
            SourceAuthority: request.SourceAuthority,
            TransferId: request.TransferId
        );

        if (m_committed.Contains(item: key) || m_spent.ContainsKey(key: key) || m_leases.ContainsKey(key: key) || m_server.CustodyLatched || IsSuspended(reason: out _, request: request)) {
            return [];
        }
        var tickets = new List<WorldCustodyTicket>();

        foreach (var member in (request.Members ?? [])) {
            if (
                (member.Companion is { Ticket: { } ticket } companion) &&
                (ticket.Hop == key) &&
                (member.Mobility is { } mobility) &&
                !string.Equals(a: mobility.Incarnation.Authority, b: m_server.AuthorityIdentity, comparisonType: StringComparison.Ordinal) &&
                (m_server.Definition.FindRole(name: companion.Role) is not null) &&
                !m_mobilityLeases.ContainsKey(key: mobility.Incarnation) &&
                !EmbodiesIncarnation(incarnation: mobility.Incarnation)
            ) {
                tickets.Add(item: ticket);
            }
        }
        return tickets;
    }
    /// <summary>Returns the hop tickets this destination asks home to confirm before it lands a commit under
    /// <paramref name="key"/>: every companion its live lease holds for a hop (a member it will refuse at the landing
    /// included), since the reservation's answer is spent. None when no live lease holds the key, the key is committed or
    /// uncertain here, or this authority is latched, since each of those answers without home's word. The caller holds
    /// the authority gate.</summary>
    /// <param name="key">The transfer key.</param>
    /// <returns>The tickets to confirm, in member order; empty when the commit needs no confirmation.</returns>
    public IReadOnlyList<WorldCustodyTicket> CommitHopQuestion(WorldTransferKey key) {
        if (m_committed.Contains(item: key) || m_server.CustodyLatched || IsUncertain(key: key, reason: out _) || !m_leases.TryGetValue(key: key, value: out var lease)) {
            return [];
        }
        return [.. lease.Request.Members.Where(predicate: member => !IsReturn(member: member)).Select(selector: static member => member.Companion?.Ticket).OfType<WorldCustodyTicket>().Where(predicate: ticket => (ticket.Hop == key))];
    }
    // Decides one companion of a reservation on its own: its role declared here, no body or live lease here naming its
    // incarnation, a ticket for this very hop at the epoch above the one it leaves, home's confirmation that the grant is
    // current (asked for this reservation by this destination's own host, never taken from the sender), and a free peer
    // slot its source can occupy. A companion coming home is a return, decided by home's own custody records.
    private (WorldCompanionRefusal Reason, string Detail)? CompanionRefusal(WorldTransferCompanion companion, WorldTransferReservationMember member, WorldTransferKey key, bool[] consumed, IReadOnlyDictionary<(WorldTransferKey Key, WorldEntityAddress Incarnation), ulong>? confirmed, out int slot) {
        slot = -1;
        var mobility = member.Mobility!.Value;
        var incarnation = mobility.Incarnation;

        if (!string.Equals(a: companion.Home, b: incarnation.Authority, comparisonType: StringComparison.Ordinal)) {
            return (WorldCompanionRefusal.Unverified, "the companion's home is not its incarnation's title authority");
        }
        if (string.Equals(a: incarnation.Authority, b: m_server.AuthorityIdentity, comparisonType: StringComparison.Ordinal)) {
            return ReturnRefusal(
                custodian: key.SourceAuthority,
                mobility: mobility,
                slot: out slot
            );
        }
        if (m_server.Definition.FindRole(name: companion.Role) is null) {
            return (WorldCompanionRefusal.Role, $"this world declares nothing for role '{companion.Role}'");
        }
        if (m_mobilityLeases.ContainsKey(key: incarnation) || EmbodiesIncarnation(incarnation: incarnation)) {
            return (WorldCompanionRefusal.Duplicate, $"a body or a live lease here already names {incarnation}");
        }
        if (
            (companion.Ticket is not { } ticket) ||
            (ticket.Incarnation != incarnation) ||
            (ticket.Hop != key) ||
            (ticket.Epoch != checked((mobility.Epoch + 1UL))) ||
            !string.Equals(a: ticket.Home, b: incarnation.Authority, comparisonType: StringComparison.Ordinal) ||
            (confirmed is null) ||
            !confirmed.TryGetValue(key: (key, incarnation), value: out var next) ||
            (next != ticket.Epoch)
        ) {
            return (WorldCompanionRefusal.Unverified, $"home did not confirm a current grant of {incarnation}'s hop under transfer {key.TransferId}");
        }

        slot = PreferredOrLowestFree(
            consumed: consumed,
            preferred: member.PreferredSlot,
            first: m_server.Population.LocalSeatCount,
            limit: consumed.Length
        );
        if (slot < 0) {
            return (WorldCompanionRefusal.Full, "no free body index for a companion");
        }
        if (!m_server.Population.SupportsSource(
            index: slot,
            source: member.Source,
            refusal: out var sourceRefusal
        )) {
            slot = -1;
            return (WorldCompanionRefusal.Full, sourceRefusal);
        }
        return null;
    }
    // Decides a companion coming home through the one verification door (WorldCompanionCustody.VerifyReturn): the copy
    // the title says is away, at exactly its custodian and epoch, binds its held slot and never another; home never
    // refuses it for capacity. A latched home verifies nothing, and a live lease or body naming the incarnation answers
    // Duplicate until it is gone.
    private (WorldCompanionRefusal Reason, string Detail)? ReturnRefusal(string custodian, WorldMobilityIdentity mobility, out int slot) {
        var incarnation = mobility.Incarnation;

        slot = -1;
        if (m_server.CustodyLatched) {
            return (WorldCompanionRefusal.Unavailable, "home is latched on a crossing record it cannot yet call durable");
        }
        if (Custody.VerifyReturn(custodian: custodian, epoch: mobility.Epoch, incarnation: incarnation) is { } refused) {
            return (refused, $"home's records do not hold {incarnation} away with '{custodian}' at epoch {mobility.Epoch}");
        }
        if (m_mobilityLeases.ContainsKey(key: incarnation) || EmbodiesIncarnation(incarnation: incarnation)) {
            return (WorldCompanionRefusal.Duplicate, $"a body or a live lease here already names {incarnation}");
        }

        var held = incarnation.Index;

        if (m_server.Population.IsActive(index: held) || m_leases.Values.Any(predicate: lease => lease.Slots.Contains(value: held))) {
            return (WorldCompanionRefusal.Duplicate, $"the held slot body:{held} is not yet free");
        }
        slot = held;
        return null;
    }
    // Whether this commit may land a leased companion: a return is verified again against home's records as they stand
    // at the commit (a recall or an install since the lease leaves it out); a hop's ticket is the one leased, and home
    // confirmed, for this commit, the grant at the epoch the lease expects.
    private bool CommitConfirmed(WorldTransferCompanion? carried, WorldTransferKey key, WorldTransferReservationMember leased) => (IsReturn(member: leased)
        ? ((carried?.Ticket == leased.Companion?.Ticket) && !m_server.CustodyLatched && (Custody.VerifyReturn(custodian: key.SourceAuthority, epoch: leased.Mobility!.Value.Epoch, incarnation: leased.Mobility!.Value.Incarnation) is null))
        : CommitGranted(carried: carried, key: key, leased: leased));
    // Whether a member is a companion coming back to the authority that holds its title.
    private bool IsReturn(WorldTransferReservationMember member) => ((member.Companion is not null) && (member.Mobility is { } mobility) && string.Equals(a: mobility.Incarnation.Authority, b: m_server.AuthorityIdentity, comparisonType: StringComparison.Ordinal));
    private bool CommitGranted(WorldTransferCompanion? carried, WorldTransferKey key, WorldTransferReservationMember leased) => (
        (carried?.Ticket is { } ticket) &&
        (leased.Companion?.Ticket == ticket) &&
        (m_commitConfirmations is { } confirmed) &&
        confirmed.TryGetValue(key: (key, ticket.Incarnation), value: out var next) &&
        (next == ticket.Epoch)
    );
    // The arrival narrowed to the members at `ordinals`, in order, each carrying its id explicitly.
    private static WorldCrossingArrival Narrowed(WorldCrossingArrival arrival, IReadOnlyList<int> ordinals) => new(
        Members: [.. ordinals.Select(selector: ordinal => arrival.Members[ordinal] with { MemberId = arrival.Request.Members[ordinal].IdAt(ordinal: ordinal) })],
        Request: arrival.Request with {
            Members = [.. ordinals.Select(selector: ordinal => arrival.Request.Members[ordinal] with { MemberId = arrival.Request.Members[ordinal].IdAt(ordinal: ordinal) })],
        },
        Slots: [.. ordinals.Select(selector: ordinal => arrival.Slots[ordinal])]
    );
    // What this destination holds for a companion under a hop's key, from durable facts alone: its arrival record (kept
    // in the custody reducer whatever became of the transaction), a commit without companions, an uncertain arrival, a
    // live lease, or the key's death, made durable before it is answered.
    internal WorldCustodyAnswer CustodyAnswer(WorldTransferKey key, WorldEntityAddress incarnation) {
        if (Custody.ArrivalHolds(incarnation: incarnation, key: key) is { } landed) {
            return (landed ? WorldCustodyAnswer.Landed : WorldCustodyAnswer.NotLanded);
        }
        if (IsUncertain(key: key, reason: out _)) {
            return WorldCustodyAnswer.Uncertain;
        }
        if (m_committed.Contains(item: key) || (m_spent.TryGetValue(key: key, value: out var spent) && spent.Committed)) {
            return WorldCustodyAnswer.NotLanded;
        }
        if (m_leases.ContainsKey(key: key)) {
            return WorldCustodyAnswer.Reserved;
        }

        var reason = string.Empty;

        return ((AnswerMissing(key: key, reason: ref reason) == WorldTransferStatus.Missing)
            ? WorldCustodyAnswer.Dead
            : WorldCustodyAnswer.Uncertain);
    }
    /// <summary>Returns the request-local ids a committed transfer landed here: from its live receipt, or, once that
    /// receipt is retired or superseded, from the durable arrival of a cohort that carried companions (the only kind
    /// that lands part of its members). <see langword="null"/> when this destination holds no commit for the key (none
    /// yet, refused, or an acknowledged cohort without companions, which landed whole).</summary>
    /// <param name="sourceAuthority">The source namespace.</param>
    /// <param name="transferId">The transfer id.</param>
    /// <returns>The landed ids, or <see langword="null"/>.</returns>
    public IReadOnlyList<int>? Landed(string sourceAuthority, ulong transferId) {
        var key = new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: transferId);

        return (m_committedLanded.TryGetValue(key: key, value: out var landed)
            ? landed
            : Custody.ArrivalLanded(key: key));
    }
    // Whether an active body here is the incarnation.
    private bool EmbodiesIncarnation(WorldEntityAddress incarnation) {
        for (var index = 0; (index < m_server.Population.Capacity); index++) {
            if (
                m_server.Population.IsActive(index: index) &&
                (m_server.Population.ResolveIncarnation(authority: m_server.AuthorityIdentity, index: index) == incarnation)
            ) {
                return true;
            }
        }
        return false;
    }

    // Refuses a peer cohort whose humans would exceed the networkPlayers cap once every human already admitted and every
    // human another live lease holds a body for has landed, so a reservation never binds a human its commit must refuse.
    private string? NetworkPlayersRefusal(WorldTransferReservationRequest request) {
        var arriving = request.Members.Count(predicate: static member => member.LandsHuman);

        if (arriving == 0) {
            return null;
        }

        var held = m_leases.Values
            .Where(predicate: static lease => lease.Request.PeerAdmission)
            .Sum(selector: static lease => lease.Request.Members.Count(predicate: static member => member.LandsHuman));
        var cap = m_server.Population.NetworkPlayers;

        return ((((long)m_server.Population.RemoteHumanCount + held) + arriving) > cap)
            ? $"the networkPlayers admission cap ({cap}) is already met"
            : null;
    }
    // Refuses a cohort whose travelers cannot hold a lease: a catalog rig outside the catalog, no stable mobility
    // identity, an incarnation repeated within the cohort or already leased to another transfer, or a mobility epoch
    // this destination has already consumed.
    private string? TravelerRefusal(WorldTransferReservationRequest request) {
        var reservationIncarnations = new HashSet<WorldEntityAddress>();
        var memberIds = new HashSet<int>();

        for (var index = 0; (index < request.Members.Count); index++) {
            // A member is decided, leased, landed and settled by its id, so no two members may share one.
            if (!memberIds.Add(item: request.Members[index].IdAt(ordinal: index))) {
                return $"traveler {(index + 1)} repeats member id {request.Members[index].IdAt(ordinal: index)}";
            }
            if (request.Members[index].CatalogRig >= WorldLookSource.Catalog.RigCount) {
                return $"traveler {(index + 1)} catalog rig {request.Members[index].CatalogRig} is outside 0..{(WorldLookSource.Catalog.RigCount - 1)}";
            }
            if (request.Members[index].Mobility is not { } mobility) {
                return $"traveler {(index + 1)} carries no stable mobility identity";
            }
            if (mobility.Epoch == ulong.MaxValue) {
                return $"traveler {(index + 1)} mobility epoch cannot advance";
            }
            if (!reservationIncarnations.Add(item: mobility.Incarnation)) {
                return $"traveler {(index + 1)} repeats mobility incarnation {mobility.Incarnation}";
            }
            if (request.Members[index].Companion is not null) {
                continue;
            }
            // An owned traveller moves only under a hop its title authority grants, which only its companion leaf asks
            // for: stripped of the leaf, it is refused rather than landed as an ordinary traveller.
            if (mobility.Incarnation.Titled) {
                return $"traveler {(index + 1)} names titled incarnation {mobility.Incarnation} without the companion leaf its title authority's hop requires";
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
        // The source supplies both the interval and the rate. Widen before multiplication and rounding so a
        // well-formed hostile frame receives a refusal, rather than escaping the wire door as an overflow.
        var remainingEngineTicks = (((UInt128)remainingSourceSteps) * sourceStepTicks);
        var destinationSteps = (((remainingEngineTicks + destinationStepTicks) - 1UL) / destinationStepTicks);
        var destinationDeadline = (((UInt128)(m_server.NextInputTick - 1UL)) + destinationSteps);

        if (destinationDeadline > ulong.MaxValue) {
            return "source lease deadline exceeds the destination tick range";
        }
        if (remainingEngineTicks > (((UInt128)WorldTransferLimits.LeaseSeconds) * FixedTickConversion.TicksPerSecond)) {
            return $"source lease duration exceeds the maximum of {WorldTransferLimits.LeaseSeconds} seconds";
        }
        deadline = ((ulong)destinationDeadline);
        return null;
    }
    // Installs the lease a recorded arrival bound without deciding its reservation again. The commit stood under the
    // decision its reservation made against the state of its own moment, which a reland need not share: a grant revoked
    // between reservation and commit refuses a seat at its join, never at a second reservation. What keeps the landing
    // safe is kept: the token is neither committed nor suspended, each traveler's mobility epoch is unleased and
    // unconsumed, every body index is of the kind the cohort lands in, and an arriving peer's verdict comes from this
    // destination's own admission entries. Only the indices that actually landed must be free: a later occupied index
    // can be the reason the live commit rolled back, and the recorded stop never lands or undoes that occupant.
    private WorldTransferReservationReply RestoreLease(WorldCrossingArrival arrival, int landings) {
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

        for (var index = 0; (index < arrival.Slots.Count); index++) {
            var slot = arrival.Slots[index];
            // A companion lands through the entity door in a peer index, whatever door the rest of its cohort used.
            var (memberFirst, memberEnd) = ((arrival.Request.Members[index].Companion is not null)
                ? (m_server.Population.LocalSeatCount, m_server.Population.Capacity)
                : (first, end));

            if ((slot < memberFirst) || (slot >= memberEnd)) {
                return WorldTransferReservationReply.Refused(reason: $"body:{slot} is outside the indices {memberFirst}..{(memberEnd - 1)} this cohort member lands in");
            }
            if (
                ((index < landings) && m_server.Population.IsActive(index: slot)) ||
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
    // arrival bound. The key takes its row here: a reservation admitted it before writing its lease, and a reland
    // reproduces a lease that was admitted when it was made.
    private WorldTransferReservationReply InstallLease(WorldTransferKey key, WorldTransferReservationRequest request, int[] slots, ulong deadline, WorldAdmissionVerdict? arrival) {
        var destinationDefinition = m_server.Definition;

        EnterRow(key: key);
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
    /// with the outcome the commit decided live: each traveler must land at the generation it landed at, a recorded
    /// rollback stops at the same traveler and undoes the landings ahead of it, and a traveler coming home binds the
    /// projection the live adoption bound it to, detached. The record is already durable, so it is
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
             (!recorded.RolledBack && (recorded.Generations.Count != arrival.Members.Count)) ||
             (recorded.Adopted.Count != (recorded.RolledBack ? 0 : recorded.Generations.Count)))
        ) {
            reason = $"transfer {arrival.Request.TransferId} records {recorded.Generations.Count} landing(s) and {recorded.Adopted.Count} home binding(s) for {arrival.Members.Count} traveler(s){(recorded.RolledBack ? " before its rollback" : string.Empty)}";
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
            : RestoreLease(
                arrival: arrival,
                landings: (recorded?.Generations.Count ?? arrival.Members.Count)
            ));

        if (!reply.Accepted) {
            reason = $"transfer {request.TransferId} reservation refused — {reply.Reason}";
            return false;
        }
        if (!reply.BodyIndices.SequenceEqual(second: arrival.Slots)) {
            // Released, not killed: the recorded arrival decides the key, and a recovery that can land it later must.
            ReleaseLease(key: arrival.Key);
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
        m_relandReadopted = arrival.Readopted;
        m_relandLeased = arrival.Leased;
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
            m_relandReadopted = [];
            m_relandLeased = [];
            m_relandRolledBack = false;
        }
    }
    public void RetireMobility(in WorldMobilityIdentity mobility) {
        var incarnation = mobility.Incarnation;

        if (m_mobilityLeases.TryGetValue(
            key: incarnation,
            value: out var mobilityLease
        )) {
            KillLease(key: mobilityLease.Transfer);
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
    /// <summary>Answers what this destination holds for a source-scoped transfer. A key whose retry payload is retired
    /// still answers <see cref="WorldTransferStatus.Committed"/>. A key holding nothing live answers
    /// <see cref="WorldTransferStatus.Missing"/> only once its death is durable, tombstoning it first when no earlier
    /// record shows it, so no later reservation or commit under it is admitted; a tombstone that cannot be made durable
    /// answers <see cref="WorldTransferStatus.Uncertain"/>. The caller holds the authority gate.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    /// <returns>The transfer's status here.</returns>
    public WorldTransferStatus Status(string sourceAuthority, ulong transferId) {
        var key = new WorldTransferKey(
            SourceAuthority: sourceAuthority,
            TransferId: transferId
        );
        var reason = string.Empty;

        return (m_uncertain.ContainsKey(key: key)
            ? WorldTransferStatus.Uncertain
            : ((m_committed.Contains(item: key) || (m_spent.TryGetValue(key: key, value: out var spent) && spent.Committed))
                ? WorldTransferStatus.Committed
                : (m_leases.ContainsKey(key: key)
                    ? WorldTransferStatus.Reserved
                    : AnswerMissing(
                        key: key,
                        reason: ref reason
                    )
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

    /// <summary>Gets how many reservations this destination accepted that have neither landed nor expired: arrivals it
    /// has promised a commit for.</summary>
    public int AcceptedArrivals => m_leases.Count;
    public WorldTransferTableCounts Counts => new(
        ActiveTransactions: ((m_leases.Count + m_committed.Count) + m_uncertain.Count),
        MobilityCredentials: m_mobilityAdmissions.Count,
        MobilityLeases: m_mobilityLeases.Count,
        TransferKeys: TotalRows
    );
}
