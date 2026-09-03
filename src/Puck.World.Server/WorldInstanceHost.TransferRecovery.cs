using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    private static WorldTransferContinuationCheckpoint CaptureTransferContinuation(in PendingTransfer transfer, List<LandedMember> landed) => new(
        CohortSlots: transfer.FrozenCohortSlots is { } slots ? [.. slots] : landed.ConvertAll(static member => member.SourceSlot),
        SourceSlot: transfer.SourceSlot,
        Border: transfer.Border,
        AdjacencyCounterpart: transfer.AdjacencyCounterpart,
        SourceCrossingPoint: transfer.SourceCrossingPoint,
        SourceFrame: transfer.SourceFrame,
        DestinationName: transfer.RecoveryDestinationName ?? transfer.ResolvedDestinationRow?.Name.Value,
        ScopeKey: transfer.FrozenScopeKey,
        GenerationId: transfer.FrozenGenerationId);

    // Durable destination identity is independent of a live handle. A missing local row may be admitted after the
    // source; a same-named replacement must never answer another authority's transaction.
    private WorldInstance? FindRecoveryDestination(string authority) {
        if (string.IsNullOrEmpty(authority)) { return null; }
        foreach (var candidate in m_instances.Values) {
            if (string.Equals(candidate.Server.AuthorityIdentity, authority, StringComparison.Ordinal)) { return candidate; }
        }
        return null;
    }

    private bool TryBindRecoveryDestination(ref InDoubtTransfer pending) {
        if (pending.TargetAuthority is not null) { return true; }
        if (pending.RecoveryAuthority is not { } authority) { return false; }
        if (FindRecoveryDestination(authority) is { } target) {
            pending = pending with { TargetAuthority = LocalPeerCall(target), TargetName = target.Name, RecoveryEndpoint = null, RecoveryDefinition = null };
            return true;
        }
        if (pending.RecoveryEndpoint is not { } endpoint || pending.RecoveryDefinition is not { } definition ||
            !m_instances.TryGetValue(pending.Transfer.SourceInstance, out var source)) { return false; }
        var remote = RecoveryRemoteAuthority(source, pending.SourceAuthority, authority, endpoint, definition);
        pending = pending with { TargetAuthority = new WorldPeerCall(null, remote) };
        return true;
    }

    // Complete this preflight before installing any host schedule or table. A malformed later row must not leave
    // the valid prefix installed, erase an existing recovery, or call a peer with a changed commit payload.
    private List<InDoubtTransfer> PrepareInDoubtTransfers(WorldInstance row, IReadOnlyList<WorldInDoubtTransferCheckpoint> records) {
        var restored = new List<InDoubtTransfer>(records.Count);
        var ids = new HashSet<ulong>();
        var observers = new HashSet<WorldEntityAddress>();
        foreach (var pending in records) {
            void Refuse(string reason) => throw new ArgumentException($"in-doubt transfer={pending.TransferId} for '{row.Name}': {reason}", nameof(records));
            if (!string.Equals(pending.SourceInstance, row.Name, StringComparison.Ordinal) || !ids.Add(pending.TransferId)) {
                Refuse("source ownership or transfer identity is inconsistent");
            }
            if (pending.RollbackOnly && pending.CommitConfirmed) { Refuse("commit and rollback cannot both be confirmed"); }
            if ((pending.MemberCount <= 0) || (pending.MemberCount > WorldBodiesLimits.CapacityCeiling) ||
                pending.CommitMembers.Count != pending.Landed.Count ||
                (pending.RollbackOnly ? pending.Landed.Count > pending.MemberCount : pending.Landed.Count != pending.MemberCount)) {
                Refuse("commit member count does not match the retained cohort");
            }
            if (pending.TargetEndpoint is { } endpoint && !System.Net.IPEndPoint.TryParse(endpoint, out _)) {
                Refuse("target endpoint is invalid");
            }
            if (string.IsNullOrEmpty(pending.TargetAuthority)) {
                Refuse("target authority is missing");
            }
            WorldDefinition? targetDefinition = null;
            if (pending.TargetEndpoint is not null) {
                if (pending.TargetDefinitionJson is null) { Refuse("remote target definition is missing"); }
                try { targetDefinition = WorldDefinitionSerialization.Deserialize(pending.TargetDefinitionJson!); }
                catch (InvalidDataException exception) { throw new ArgumentException("remote target definition is invalid", nameof(records), exception); }
            } else if (pending.TargetDefinitionJson is not null) { Refuse("a local target cannot carry remote connection state"); }
            var sourceSlots = new HashSet<int>();
            var targetSlots = new HashSet<int>();
            var landed = new List<LandedMember>(pending.Landed.Count);
            var followedSeats = 0;
            for (var ordinal = 0; ordinal < pending.Landed.Count; ordinal++) {
                var member = pending.Landed[ordinal];
                if ((member.FollowedSeatMask >> WorldBodiesLimits.LocalSeatCount) != 0 ||
                    (followedSeats & member.FollowedSeatMask) != 0) { Refuse("followed roster slots are invalid or duplicated"); }
                followedSeats |= member.FollowedSeatMask;
                if ((uint)member.SourceSlot >= (uint)row.Server.Population.Capacity ||
                    (uint)member.TargetSlot >= WorldBodiesLimits.CapacityCeiling ||
                    !sourceSlots.Add(member.SourceSlot) || !targetSlots.Add(member.TargetSlot) || !observers.Add(member.Mobility.Incarnation)) {
                    Refuse("member slots or mobility identities are invalid or duplicated");
                }
                landed.Add(new(
                    AdmissionGrants: [.. member.AdmissionGrants], BodyColor: member.BodyColor,
                    Designations: [.. member.Designations], DynamicState: member.DynamicState, Mobility: member.Mobility,
                    Peer: member.Peer, Position: member.Position, Profile: pending.CommitMembers[ordinal].Profile,
                    FollowedSeatMask: member.FollowedSeatMask,
                    SourceGrants: [.. member.SourceGrants], SourcePrincipal: WorldPrincipal.Console,
                    SourceSlot: member.SourceSlot, TargetSlot: member.TargetSlot, Yaw: member.Yaw));
            }
            var continuation = pending.Continuation;
            if (continuation is not null) {
                var cohort = new HashSet<int>();
                foreach (var slot in continuation.CohortSlots) {
                    if ((uint)slot >= (uint)row.Server.Population.Capacity || !cohort.Add(slot)) { Refuse("continuation cohort is invalid"); }
                }
                if (cohort.Count != pending.MemberCount || !sourceSlots.IsSubsetOf(cohort) ||
                    (uint)continuation.SourceSlot >= (uint)row.Server.Population.Capacity ||
                    (continuation.AdjacencyCounterpart is not null && continuation.SourceFrame is null) ||
                    (continuation.DestinationName is null) != (continuation.ScopeKey is null) ||
                    (continuation.DestinationName is null) != (continuation.GenerationId is null)) {
                    Refuse("continuation does not match the retained crossing");
                }
            }
            var target = FindRecoveryDestination(pending.TargetAuthority);
            restored.Add(new(
                RollbackOnly: pending.RollbackOnly, CommitMembers: [.. pending.CommitMembers], Landed: landed,
                CommitConfirmed: pending.CommitConfirmed,
                MemberCount: pending.MemberCount, SourceAuthority: row.Server.AuthorityIdentity,
                SourceDeadlineTick: pending.SourceDeadlineTick, Spawned: pending.Spawned,
                TargetAuthority: target is null ? null : LocalPeerCall(target), TargetName: target?.Name ?? pending.TargetName ?? pending.TargetAuthority,
                RecoveryAuthority: pending.TargetAuthority, RecoveryEndpoint: target is null ? pending.TargetEndpoint : null,
                RecoveryDefinition: target is null ? targetDefinition : null,
                // Resolver admission and mapped arrival are already decided. Reconciliation uses only the exact
                // commit payload and this source-side completion context; it never reruns destination resolution.
                Transfer: new(
                    ActingPrincipal: WorldPrincipal.Console, AdjacencyCounterpart: continuation?.AdjacencyCounterpart,
                    Arrival: WorldPortalArrival.Spawn, Border: continuation?.Border ?? string.Empty, BorderCapacity: null,
                    Continuum: null, Counterpart: null, Destination: TransferDestination.Existing(pending.TargetName ?? pending.TargetAuthority),
                    FrozenCohortSlots: continuation is null ? landed.ConvertAll(static member => member.SourceSlot) : [.. continuation.CohortSlots],
                    FrozenGenerationId: continuation?.GenerationId, FrozenScopeKey: continuation?.ScopeKey,
                    FullPolicy: WorldTransferFullPolicy.Refuse, HoldSeconds: 0, PartyAllOrNothing: false,
                    ResolvedDestinationRow: null, RecoveryDestinationName: continuation?.DestinationName, Scope: TransferScope.Body,
                    SourceCrossingPoint: continuation?.SourceCrossingPoint ?? default, SourceFrame: continuation?.SourceFrame,
                    SourceInstance: row.Name, SourceSlot: continuation?.SourceSlot ?? (landed.Count > 0 ? landed[0].SourceSlot : 0),
                    TestForceJoinRefusalOrdinal: null, TransferId: pending.TransferId)));
        }
        return restored;
    }
}
