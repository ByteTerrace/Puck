using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One prospective traveler in a destination reservation.</summary>
/// <param name="Principal">The source-stamped acting principal for a colocated transfer.</param>
/// <param name="PreferredSlot">The body index the traveler prefers to retain.</param>
/// <param name="Identity">The attested owned-world identity carried by a federated traveler.</param>
/// <param name="Source">The traveler's authored intent source, preserved across the authority boundary.</param>
/// <param name="BodyColor">The source body's exact rendered material color, preserved across ownership.</param>
/// <param name="CatalogRig">The source body's entity-owned procedural rig, preserved across ownership. Destination
/// look authoring may deliberately override it; ordinary admission may not.</param>
public readonly record struct WorldTransferReservationMember(WorldPrincipal Principal, int PreferredSlot, WorldIdentity? Identity, IntentSource Source, Vector3 BodyColor, byte CatalogRig);

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

/// <summary>The destination's idempotent answer for an ambiguous commit.</summary>
public enum WorldTransferStatus : byte { Missing = 0, Reserved = 1, Committed = 2 }

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

/// <summary>The destination's reservation verdict and assigned body indices.</summary>
public sealed record WorldTransferReservationReply(bool Accepted, string Reason, ulong DeadlineDestinationTick, IReadOnlyList<int> BodyIndices, WorldDefinition? DestinationDefinition) {
    /// <summary>Creates a named refusal.</summary>
    public static WorldTransferReservationReply Refused(string reason) => new(Accepted: false, Reason: reason, DeadlineDestinationTick: 0, BodyIndices: [], DestinationDefinition: null);
}

/// <summary>One detached source body carried into a previously reserved destination index.</summary>
public sealed record WorldTransferCommitMember(
    WorldIdentity? Profile,
    bool HasMappedArrival,
    string BodyMotionProgramName,
    FixedVector3 Position,
    FixedQ4816 YawRadians,
    FixedVector3 PlanarVelocity,
    FixedQ4816 VerticalVelocity,
    WorldTransferActionContinuity? ActionContinuity = null
);

/// <summary>The transfer escrow table shared by colocated and TCP authority transports. It owns destination capacity
/// from reserve until commit, explicit abort, or deterministic deadline expiry; it never queues a full request.</summary>
internal sealed class WorldTransferEscrow {
    private sealed record Lease(WorldTransferReservationRequest Request, ulong DeadlineTick, int[] Slots, WorldDefinition DestinationDefinition);

    private readonly WorldServer m_server;
    private readonly Dictionary<WorldTransferKey, Lease> m_leases = new();
    private readonly HashSet<WorldTransferKey> m_committed = new();
    private readonly Dictionary<WorldTransferKey, WorldTransferCommitMember[]> m_committedMembers = new();
    private readonly Dictionary<WorldTransferKey, WorldPrincipal[]> m_committedPrincipals = new();
    // A committed body continues to consume its authored border capacity until it leaves that body index.
    // The population remains the source of truth: stale rows are pruned before every capacity decision.
    private readonly Dictionary<int, string> m_borderAdmissions = new();

    public WorldTransferEscrow(WorldServer server) => m_server = server;

    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);
        var key = new WorldTransferKey(SourceAuthority: request.SourceAuthority, TransferId: request.TransferId);

        if (m_committed.Contains(item: key)) {
            return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} already committed");
        }

        if (m_leases.TryGetValue(key: key, value: out var existing)) {
            if (!ReservationMatches(left: existing.Request, right: request)) {
                return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} reuses an existing source-scoped id with a different reservation");
            }

            return new WorldTransferReservationReply(Accepted: true, Reason: string.Empty, DeadlineDestinationTick: existing.DeadlineTick, BodyIndices: existing.Slots, DestinationDefinition: existing.DestinationDefinition);
        }

        if (request.Members.Count == 0) {
            return WorldTransferReservationReply.Refused(reason: "reservation carries no travelers");
        }

        for (var index = 0; index < request.Members.Count; index++) {
            if (request.Members[index].CatalogRig >= WorldLookSource.Catalog.RigCount) {
                return WorldTransferReservationReply.Refused(reason: $"traveler {index + 1} catalog rig {request.Members[index].CatalogRig} is outside 0..{WorldLookSource.Catalog.RigCount - 1}");
            }
        }

        if ((request.SourceRateHz <= 0) || (request.DeadlineSourceTick <= request.SourceTick)) {
            return WorldTransferReservationReply.Refused(reason: "source lease deadline does not advance on a positive simulation rate");
        }

        if ((FixedTickConversion.TicksPerSecond % checked((ulong)request.SourceRateHz)) != 0UL) {
            return WorldTransferReservationReply.Refused(reason: $"source simulation rate {request.SourceRateHz}Hz does not divide the exact {FixedTickConversion.TicksPerSecond}-tick bridge");
        }

        var destinationRate = m_server.Definition.SimulationRateHz;

        if (destinationRate <= 0) {
            return WorldTransferReservationReply.Refused(reason: "destination simulation rate is stopped, so no binding lease can expire there");
        }

        if ((FixedTickConversion.TicksPerSecond % checked((ulong)destinationRate)) != 0UL) {
            return WorldTransferReservationReply.Refused(reason: $"destination simulation rate {destinationRate}Hz does not divide the exact {FixedTickConversion.TicksPerSecond}-tick bridge");
        }

        var sourceStepTicks = (FixedTickConversion.TicksPerSecond / checked((ulong)request.SourceRateHz));
        var destinationStepTicks = (FixedTickConversion.TicksPerSecond / checked((ulong)destinationRate));
        var remainingSourceSteps = (request.DeadlineSourceTick - request.SourceTick);
        var remainingEngineTicks = checked(remainingSourceSteps * sourceStepTicks);
        var destinationSteps = checked((remainingEngineTicks + destinationStepTicks - 1UL) / destinationStepTicks);
        var deadline = checked((m_server.NextInputTick - 1UL) + destinationSteps);
        PruneDepartedAdmissions();
        var firstSlot = (request.PeerAdmission ? WorldPopulation.LocalSeatCount : 0);
        var consumed = new bool[(request.PeerAdmission ? m_server.Population.Capacity : WorldPopulation.LocalSeatCount)];

        for (var slot = 0; (slot < consumed.Length); slot++) {
            consumed[slot] = m_server.Population.IsActive(index: slot);
        }

        foreach (var lease in m_leases.Values) {
            foreach (var slot in lease.Slots) {
                if ((uint)slot < (uint)consumed.Length) {
                    consumed[slot] = true;
                }
            }
        }

        if (request.BorderCapacity is { } borderCapacity) {
            var heldAtBorder = m_leases.Values.Where(predicate: lease => string.Equals(a: lease.Request.Border, b: request.Border, comparisonType: StringComparison.Ordinal)).Sum(selector: lease => lease.Slots.Length)
                + m_borderAdmissions.Count(predicate: admission => string.Equals(a: admission.Value, b: request.Border, comparisonType: StringComparison.Ordinal));

            if ((heldAtBorder + request.Members.Count) > borderCapacity) {
                return WorldTransferReservationReply.Refused(reason: $"border '{request.Border}' is full ({heldAtBorder}/{borderCapacity} reserved); no queue was created");
            }
        }

        var slots = new int[request.Members.Count];

        for (var index = 0; (index < request.Members.Count); index++) {
            var member = request.Members[index];
            var slot = PreferredOrLowestFree(consumed: consumed, preferred: member.PreferredSlot, first: firstSlot);

            if (slot < 0) {
                return WorldTransferReservationReply.Refused(reason: $"destination has no free body index for traveler {index + 1}; no queue was created");
            }

            if (!request.PeerAdmission && m_server.Grants.Allows(principal: member.Principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: slot)) is { IsAllowed: false } standing) {
                return WorldTransferReservationReply.Refused(reason: $"{member.Principal.Describe()} cannot enter body:{slot} — {standing.DescribeDenial()}");
            }
            if (request.PeerAdmission && !member.Source.IsLive && !m_server.Population.SupportsSource(index: slot, source: member.Source, refusal: out var sourceRefusal)) {
                return WorldTransferReservationReply.Refused(reason: $"traveler {index + 1} cannot enter body:{slot} — {sourceRefusal}");
            }

            consumed[slot] = true;
            slots[index] = slot;
        }

        var destinationDefinition = m_server.Definition;

        m_leases.Add(key: key, value: new Lease(Request: request, DeadlineTick: deadline, Slots: slots, DestinationDefinition: destinationDefinition));

        return new WorldTransferReservationReply(Accepted: true, Reason: string.Empty, DeadlineDestinationTick: deadline, BodyIndices: slots, DestinationDefinition: destinationDefinition);
    }

    public bool Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        var key = new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: transferId);

        if (m_committed.Contains(item: key)) {
            if (m_committedMembers.TryGetValue(key: key, value: out var committedMembers) && CommitMatches(left: committedMembers, right: members)) {
                reason = string.Empty;
                return true;
            }

            reason = $"transfer {transferId} reuses a committed source-scoped id with a different commit";
            return false;
        }

        if (!m_leases.Remove(key: key, value: out var lease)) {
            reason = $"transfer {transferId} has no live reservation";

            return false;
        }

        if ((m_server.NextInputTick - 1UL) >= lease.DeadlineTick) {
            reason = $"transfer {transferId} reservation expired at destination tick {lease.DeadlineTick}";

            return false;
        }

        if (!ReferenceEquals(objA: m_server.Definition, objB: lease.DestinationDefinition)) {
            reason = $"transfer {transferId} destination definition moved after reservation; reserve again against the current revision";

            return false;
        }

        if (members.Count != lease.Slots.Length) {
            reason = $"transfer {transferId} commit carries {members.Count} traveler(s), reservation binds {lease.Slots.Length}";

            return false;
        }

        for (var index = 0; index < members.Count; index++) {
            var member = members[index];

            if (member.HasMappedArrival && (string.IsNullOrWhiteSpace(value: member.BodyMotionProgramName)
                || !lease.DestinationDefinition.BodyMotionPrograms.Any(program => (program.Kind == BodyProgramKind.Motion)
                    && string.Equals(a: program.Name, b: member.BodyMotionProgramName, comparisonType: StringComparison.Ordinal)))) {
                reason = $"transfer {transferId} traveler {index + 1} names unavailable destination motion program '{member.BodyMotionProgramName}'";
                return false;
            }
        }

        var landed = new List<int>(capacity: members.Count);

        for (var index = 0; (index < members.Count); index++) {
            var slot = lease.Slots[index];
            var reservationMember = lease.Request.Members[index];
            var principal = reservationMember.Principal;
            SessionReply reply;

            if (lease.Request.PeerAdmission) {
                reply = reservationMember.Source.IsLive
                    ? m_server.AdmitTransferredPeer(slot: slot, identity: reservationMember.Identity)
                    : m_server.AdmitTransferredEntity(slot: slot, source: reservationMember.Source, identity: reservationMember.Identity);
            } else {
                reply = m_server.ApplySession(request: new SessionRequest.Join(Principal: principal, Slot: slot, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));
            }

            if (!reply.Accepted) {
                foreach (var landedSlot in landed) {
                    if (lease.Request.PeerAdmission) {
                        m_server.RollbackTransferredEntity(slot: landedSlot);
                    } else {
                        _ = m_server.Population.TryDetachSeatForTransfer(slot: landedSlot, profile: out _);
                    }
                    _ = m_borderAdmissions.Remove(key: landedSlot);
                }

                reason = $"body:{slot} refused reserved commit — {reply.Reason}";

                return false;
            }

            var member = members[index];

            var profile = (member.Profile ?? reservationMember.Identity);

            if (profile is not null) {
                m_server.Population.SetSeatProfile(slot: slot, profile: profile);
            }
            m_server.Population.SetBodyColor(slot: slot, color: reservationMember.BodyColor);
            m_server.Population.SetCatalogRig(slot: slot, catalogRig: reservationMember.CatalogRig);

            if (member.HasMappedArrival) {
                m_server.Population.ApplyMappedArrival(slot: slot, motionProgramName: member.BodyMotionProgramName, position: member.Position, yawRadians: member.YawRadians, planarVelocity: member.PlanarVelocity, verticalVelocity: member.VerticalVelocity, actionContinuity: member.ActionContinuity ?? new WorldTransferActionContinuity(Channels: [], Registers: []));
            }

            landed.Add(item: slot);
            m_borderAdmissions[slot] = lease.Request.Border;
        }

        m_committed.Add(item: key);
        m_committedMembers[key] = [.. members];
        m_committedPrincipals[key] = lease.Slots.Select(selector: slot => lease.Request.PeerAdmission ? m_server.Population.PeerPrincipal(index: slot) : lease.Request.Members[Array.IndexOf(array: lease.Slots, value: slot)].Principal).ToArray();
        reason = string.Empty;

        return true;
    }

    public void Abort(string sourceAuthority, ulong transferId) => _ = m_leases.Remove(key: new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: transferId));

    public bool TryCommittedPrincipal(string sourceAuthority, ulong transferId, int ordinal, out WorldPrincipal principal) {
        if (m_committedPrincipals.TryGetValue(key: new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: transferId), value: out var principals) && ((uint)ordinal < (uint)principals.Length)) {
            principal = principals[ordinal];
            return true;
        }

        principal = default;
        return false;
    }

    public WorldTransferStatus Status(string sourceAuthority, ulong transferId) {
        var key = new WorldTransferKey(SourceAuthority: sourceAuthority, TransferId: transferId);

        return (m_committed.Contains(item: key) ? WorldTransferStatus.Committed : (m_leases.ContainsKey(key: key) ? WorldTransferStatus.Reserved : WorldTransferStatus.Missing));
    }

    /// <summary>Reads the authenticated source-border identity retained for one active arrived body.</summary>
    public bool TryArrivalBorder(int bodyIndex, out string border) =>
        m_borderAdmissions.TryGetValue(key: bodyIndex, value: out border!);

    /// <summary>Re-arms a committed arrival's reciprocal edge after the body has been observed fully inside its new
    /// owner's half-space. The expected identity makes slot reuse or a later arrival unable to clear another
    /// transfer's latch.</summary>
    public bool ClearArrivalBorder(int bodyIndex, string expectedBorder) =>
        m_borderAdmissions.TryGetValue(key: bodyIndex, value: out var border) &&
        string.Equals(a: border, b: expectedBorder, comparisonType: StringComparison.Ordinal) &&
        m_borderAdmissions.Remove(key: bodyIndex);

    public void ReclaimExpired(ulong tick) {
        PruneDepartedAdmissions();

        foreach (var transferId in m_leases.Where(predicate: pair => tick >= pair.Value.DeadlineTick).Select(selector: pair => pair.Key).ToArray()) {
            _ = m_leases.Remove(key: transferId);
        }
    }

    private void PruneDepartedAdmissions() {
        foreach (var slot in m_borderAdmissions.Keys.Where(predicate: slot => !m_server.Population.IsActive(index: slot)).ToArray()) {
            _ = m_borderAdmissions.Remove(key: slot);
        }
    }

    private static int PreferredOrLowestFree(bool[] consumed, int preferred, int first) {
        if ((preferred >= first) && ((uint)preferred < (uint)consumed.Length) && !consumed[preferred]) {
            return preferred;
        }

        for (var slot = first; (slot < consumed.Length); slot++) {
            if (!consumed[slot]) {
                return slot;
            }
        }

        return -1;
    }

    private static bool ReservationMatches(WorldTransferReservationRequest left, WorldTransferReservationRequest right) {
        if ((left.TransferId != right.TransferId)
            || !string.Equals(a: left.SourceAuthority, b: right.SourceAuthority, comparisonType: StringComparison.Ordinal)
            || (left.SourceRateHz != right.SourceRateHz)
            || (left.SourceTick != right.SourceTick)
            || (left.DeadlineSourceTick != right.DeadlineSourceTick)
            || !string.Equals(a: left.Border, b: right.Border, comparisonType: StringComparison.Ordinal)
            || (left.BorderCapacity != right.BorderCapacity)
            || (left.PartyAllOrNothing != right.PartyAllOrNothing)
            || (left.PeerAdmission != right.PeerAdmission)
            || (left.Members.Count != right.Members.Count)) {
            return false;
        }

        for (var index = 0; index < left.Members.Count; index++) {
            var a = left.Members[index];
            var b = right.Members[index];

            if ((a.Principal != b.Principal) || (a.PreferredSlot != b.PreferredSlot) || (a.Source != b.Source) || (a.BodyColor != b.BodyColor) || (a.CatalogRig != b.CatalogRig) || !IdentityMatches(left: a.Identity, right: b.Identity)) {
                return false;
            }
        }

        return true;
    }

    private static bool IdentityMatches(WorldIdentity? left, WorldIdentity? right) {
        if (ReferenceEquals(objA: left, objB: right)) {
            return true;
        }

        if ((left is null) || (right is null)) {
            return false;
        }

        if ((left.Document is not { } leftDocument) || (right.Document is not { } rightDocument)) {
            return (left.Document is null) && (right.Document is null);
        }

        var leftBytes = WorldDefinitionSerialization.Serialize(definition: leftDocument);
        var rightBytes = WorldDefinitionSerialization.Serialize(definition: rightDocument);

        return leftBytes.AsSpan().SequenceEqual(other: rightBytes);
    }

    private static bool CommitMatches(IReadOnlyList<WorldTransferCommitMember> left, IReadOnlyList<WorldTransferCommitMember> right) {
        if (left.Count != right.Count) {
            return false;
        }

        for (var index = 0; index < left.Count; index++) {
            var a = left[index];
            var b = right[index];

            if (!IdentityMatches(left: a.Profile, right: b.Profile)
                || (a.HasMappedArrival != b.HasMappedArrival)
                || !string.Equals(a: a.BodyMotionProgramName, b: b.BodyMotionProgramName, comparisonType: StringComparison.Ordinal)
                || (a.Position != b.Position)
                || (a.YawRadians != b.YawRadians)
                || (a.PlanarVelocity != b.PlanarVelocity)
                || (a.VerticalVelocity != b.VerticalVelocity)) {
                return false;
            }
        }

        return true;
    }
}
