using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One prospective traveler in a destination reservation.</summary>
/// <param name="Principal">The source-stamped acting principal for a colocated local-seat transfer.</param>
/// <param name="PreferredSlot">The body index the traveler prefers to retain.</param>
/// <param name="Identity">The attested owned-world identity carried by a federated traveler.</param>
public readonly record struct WorldTransferReservationMember(WorldPrincipal Principal, int PreferredSlot, WorldIdentity? Identity = null);

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
    bool RemoteAdmission,
    IReadOnlyList<WorldTransferReservationMember> Members
);

/// <summary>The destination's reservation verdict and assigned body indices.</summary>
public sealed record WorldTransferReservationReply(bool Accepted, string Reason, ulong DeadlineDestinationTick, IReadOnlyList<int> BodyIndices) {
    /// <summary>Creates a named refusal.</summary>
    public static WorldTransferReservationReply Refused(string reason) => new(Accepted: false, Reason: reason, DeadlineDestinationTick: 0, BodyIndices: []);
}

/// <summary>One detached source body carried into a previously reserved destination index.</summary>
public sealed record WorldTransferCommitMember(
    WorldIdentity? Profile,
    bool HasMappedArrival,
    FixedVector3 Position,
    FixedQ4816 YawRadians,
    FixedVector3 PlanarVelocity,
    FixedQ4816 VerticalVelocity
);

/// <summary>The transfer escrow table shared by colocated and TCP authority transports. It owns destination capacity
/// from reserve until commit, explicit abort, or deterministic deadline expiry; it never queues a full request.</summary>
internal sealed class WorldTransferEscrow {
    private sealed record Lease(WorldTransferReservationRequest Request, ulong DeadlineTick, int[] Slots);

    private readonly WorldServer m_server;
    private readonly Dictionary<ulong, Lease> m_leases = new();
    private readonly HashSet<ulong> m_committed = new();
    private readonly Dictionary<ulong, WorldPrincipal[]> m_committedPrincipals = new();
    // A committed body continues to consume its authored border capacity until it leaves that body index.
    // The population remains the source of truth: stale rows are pruned before every capacity decision.
    private readonly Dictionary<int, string> m_borderAdmissions = new();

    public WorldTransferEscrow(WorldServer server) => m_server = server;

    public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);

        if (m_committed.Contains(item: request.TransferId)) {
            return WorldTransferReservationReply.Refused(reason: $"transfer {request.TransferId} already committed");
        }

        if (m_leases.TryGetValue(key: request.TransferId, value: out var existing)) {
            return new WorldTransferReservationReply(Accepted: true, Reason: string.Empty, DeadlineDestinationTick: existing.DeadlineTick, BodyIndices: existing.Slots);
        }

        if (request.Members.Count == 0) {
            return WorldTransferReservationReply.Refused(reason: "reservation carries no travelers");
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
        var firstSlot = (request.RemoteAdmission ? WorldPopulation.LocalSeatCount : 0);
        var consumed = new bool[(request.RemoteAdmission ? m_server.Population.Capacity : WorldPopulation.LocalSeatCount)];

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

            if (!request.RemoteAdmission && m_server.Grants.Allows(principal: member.Principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: slot)) is { IsAllowed: false } standing) {
                return WorldTransferReservationReply.Refused(reason: $"{member.Principal.Describe()} cannot enter body:{slot} — {standing.DescribeDenial()}");
            }

            consumed[slot] = true;
            slots[index] = slot;
        }

        m_leases.Add(key: request.TransferId, value: new Lease(Request: request, DeadlineTick: deadline, Slots: slots));

        return new WorldTransferReservationReply(Accepted: true, Reason: string.Empty, DeadlineDestinationTick: deadline, BodyIndices: slots);
    }

    public bool Commit(ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        if (m_committed.Contains(item: transferId)) {
            reason = string.Empty;

            return true;
        }

        if (!m_leases.Remove(key: transferId, value: out var lease)) {
            reason = $"transfer {transferId} has no live reservation";

            return false;
        }

        if ((m_server.NextInputTick - 1UL) >= lease.DeadlineTick) {
            reason = $"transfer {transferId} reservation expired at destination tick {lease.DeadlineTick}";

            return false;
        }

        if (members.Count != lease.Slots.Length) {
            reason = $"transfer {transferId} commit carries {members.Count} traveler(s), reservation binds {lease.Slots.Length}";

            return false;
        }

        var landed = new List<int>(capacity: members.Count);

        for (var index = 0; (index < members.Count); index++) {
            var slot = lease.Slots[index];
            var reservationMember = lease.Request.Members[index];
            var principal = reservationMember.Principal;
            SessionReply reply;

            if (lease.Request.RemoteAdmission) {
                reply = m_server.AdmitTransferredPeer(slot: slot, identity: reservationMember.Identity);
            } else {
                reply = m_server.ApplySession(request: new SessionRequest.Join(Principal: principal, Slot: slot, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));
            }

            if (!reply.Accepted) {
                foreach (var landedSlot in landed) {
                    _ = m_server.Population.TryDetachSeatForTransfer(slot: landedSlot, profile: out _);
                }

                reason = $"body:{slot} refused reserved commit — {reply.Reason}";

                return false;
            }

            var member = members[index];

            var profile = (member.Profile ?? reservationMember.Identity);

            if (profile is not null) {
                m_server.Population.SetSeatProfile(slot: slot, profile: profile);
            }

            if (member.HasMappedArrival) {
                m_server.Population.ApplyMappedArrival(slot: slot, position: member.Position, yawRadians: member.YawRadians, planarVelocity: member.PlanarVelocity, verticalVelocity: member.VerticalVelocity);
            }

            landed.Add(item: slot);
            m_borderAdmissions[slot] = lease.Request.Border;
        }

        m_committed.Add(item: transferId);
        m_committedPrincipals[transferId] = lease.Slots.Select(selector: slot => lease.Request.RemoteAdmission ? m_server.Population.PeerPrincipal(index: slot) : lease.Request.Members[Array.IndexOf(array: lease.Slots, value: slot)].Principal).ToArray();
        reason = string.Empty;

        return true;
    }

    public void Abort(ulong transferId) => _ = m_leases.Remove(key: transferId);

    public bool TryCommittedPrincipal(ulong transferId, int ordinal, out WorldPrincipal principal) {
        if (m_committedPrincipals.TryGetValue(key: transferId, value: out var principals) && ((uint)ordinal < (uint)principals.Length)) {
            principal = principals[ordinal];
            return true;
        }

        principal = default;
        return false;
    }

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
}
