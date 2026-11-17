using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    private WorldDefinition? m_authorityDefinition;
    private bool m_proposal;
    private HashSet<string> m_operatorSources = new(comparer: StringComparer.Ordinal);

    private readonly int m_localSeatCount;

    /// <inheritdoc/>
    public bool IsOperator(Principal principal) {
        if (principal.Kind is PrincipalKind.Console or PrincipalKind.World) { return true; }
        return (HasOperatorHold(principal: principal) && EligibleOperator(principal: principal));
    }

    // Deliberately does not call Allows: Allows itself expands operator authority.
    private bool HasOperatorHold(Principal principal) =>
        (Holds(principal, WorldCapability.Operate, GrantSubject.All).IsAllowed ||
        TryRoleGroupExpansion(m_groupRoleMembership, principal, WorldCapability.Operate, GrantSubject.All, GrantRule.GroupHold, out _) ||
        TryGroupExpansion(m_ownedGroups, principal, WorldCapability.Operate, GrantSubject.All, GrantRule.OwnershipHold, out _) ||
        TryRoleGroupExpansion(m_ownedGroupRoleReach, principal, WorldCapability.Operate, GrantSubject.All, GrantRule.OwnershipHold, out _));
    private bool IsOperatorGrantee(Grantee grantee) => (grantee.TryGetPrincipal(principal: out var principal)
        ? IsOperator(principal: principal) : ((grantee.Kind == GranteeKind.Group) && Holds(grantee, WorldCapability.Operate, GrantSubject.All).IsAllowed));
    private bool EligibleOperator(Principal principal) {
        if (principal.Kind == PrincipalKind.Seat) { return (((uint)principal.Index) < ((uint)m_localSeatCount)); }
        // Federated sessions identify a namespace, never one authenticated subject.
        if ((principal.Kind != PrincipalKind.Peer) || (m_host is null) || !Host.IsLivePeer(principal: principal) ||
            (Host.Population.PeerAdmissionTier(bodyIndex: principal.Index) != WorldDisclosureTier.Replica)) { return false; }
        var identity = Host.Population.PeerIdentity(bodyIndex: principal.Index);

        return (WorldAdmissionDoor.TryMatchEntry((m_authorityDefinition ?? Host.Definition).Admission, identity.Domain,
            identity.Subject, Host.Population.PeerAdmissionTrust(bodyIndex: principal.Index), out var verdict) &&
            (verdict.Tier == WorldDisclosureTier.Replica));
    }

    /// <summary>Admits and installs a direct grant using the actor's authority before the change.</summary>
    public bool TryGrant(WorldGrant grant, Principal actor, out string reason) => TryGrantSet(actor: actor, grants: [grant], reason: out reason);

    internal bool TryGrantSet(IReadOnlyList<WorldGrant> grants, Principal actor, out string reason) {
        if (!TryPrepareAuthority(actor: actor, adds: grants, candidate: m_authorityDefinition, current: m_authorityDefinition, grantsWrite: false, prepared: out var prepared, reason: out reason, removes: [])) { return false; }
        prepared!.Install();
        return true;
    }

    /// <summary>Admits and removes a direct grant using the actor's authority before the change.</summary>
    public bool Revoke(Grantee grantee, WorldCapability capability, GrantSubject subject, Principal actor) {
        var row = new WorldGrant(grantee, capability, subject, false);
        var held = Held(grantee: grantee).Contains(value: (capability, subject));

        if (!TryPrepareAuthority(actor: actor, adds: [], candidate: m_authorityDefinition, current: m_authorityDefinition, grantsWrite: false, prepared: out var prepared, reason: out _, removes: [row])) { return false; }
        prepared!.Install();
        return held;
    }

    internal bool TryPrepareDocument(Principal actor, WorldDefinition? current, WorldDefinition candidate,
        bool grantsWrite, out PreparedAuthorityTransition? prepared, out string reason) {
        var before = (current?.Grants ?? []);

        if (ReferenceEquals(objA: before, objB: candidate.Grants)) {
            return TryPrepareAuthority(actor: actor, adds: [], candidate: candidate, current: current, grantsWrite: grantsWrite, prepared: out prepared, reason: out reason, removes: []);
        }
        var removes = before.Where(predicate: row => (!candidate.Grants.Contains(value: row) && !WorldTick.IsDocumentChannelRow(grant: row))).ToArray();
        var adds = candidate.Grants.Where(predicate: row => (!before.Contains(value: row) && !WorldTick.IsDocumentChannelRow(grant: row)))
            .Select(selector: WithoutAuthoredConsent).ToArray();

        return TryPrepareAuthority(actor, current, candidate, adds, removes, (grantsWrite || !before.SequenceEqual(second: candidate.Grants)), out prepared, out reason);
    }

    private bool TryPrepareAuthority(Principal actor, WorldDefinition? current, WorldDefinition? candidate,
        IReadOnlyList<WorldGrant> adds, IReadOnlyList<WorldGrant> removes, bool grantsWrite,
        out PreparedAuthorityTransition? prepared, out string reason) =>
        PreparedAuthorityTransition.TryPrepare(this, actor, current, candidate, adds, removes, grantsWrite, out prepared, out reason);
    private bool TryValidateAuthority(Principal actor, WorldDefinition? current, WorldDefinition? candidate,
        IReadOnlyList<WorldGrant> adds, IReadOnlyList<WorldGrant> removes, bool grantsWrite,
        out WorldGrant[] ordered, out string reason, bool restore = false, bool reset = false, WorldGrantsCheckpoint? checkpoint = null) {
        ordered = [];
        reason = string.Empty;
        var operates = IsOperator(principal: actor);

        if (!operates && (grantsWrite || !CanAdminister(rows: adds) || !CanAdminister(rows: removes))) {
            reason = "the authority transition requires an operator or an existing delegated capability";
            return false;
        }
        bool CanAdminister(IReadOnlyList<WorldGrant> rows) {
            for (var i = 0; (i < rows.Count); i++) {
                var row = rows[i];

                if ((row.Capability == WorldCapability.Operate) || !HoldsForAdministration(actor, row.Capability, row.Subject)) { return false; }
            }
            return true;
        }

        if (!restore && !reset && (adds.Count == 0) && (removes.Count == 0) && ReferenceEquals(objA: current?.Groups, objB: candidate?.Groups) && ReferenceEquals(objA: current?.Admission, objB: candidate?.Admission)) { return true; }

        // A single direct row changes no membership graph. Validate against the live indexes; revocation
        // can only subtract authority. Neither operation needs a checkpoint of unrelated grants or sessions.
        if (!restore && !reset && ReferenceEquals(objA: current?.Groups, objB: candidate?.Groups) && ReferenceEquals(objA: current?.Admission, objB: candidate?.Admission) &&
            ((adds.Count == 0) || ((adds.Count == 1) && (removes.Count == 0)))) {
            if (adds.Count == 1) {
                var row = adds[0];

                if (Conflicts(grant: row, reason: out reason)) { return false; }
                if ((row.Capability == WorldCapability.Operate) && !EligibleOperatorGrantee(grantee: row.Grantee)) {
                    reason = $"{row.Grantee.Describe()} cannot hold Operate: addons are excluded and remote holders require a live single-subject Replica entitlement";
                    return false;
                }
                ordered = [row];
            }
            return true;
        }

        // Detached scratch uses the very same role and grant resolution as the live table. It cannot publish.
        var proposed = new WorldGrants(host: m_host, population: m_population, routeTransition: static (_, _, _) => { }, seatCount: m_localSeatCount) { m_proposal = true };

        if (!reset) { proposed.RestoreRaw((checkpoint ?? Capture())); }
        proposed.m_authorityDefinition = candidate;
        var groups = (candidate?.Groups ?? WorldGroupsSection.Empty);

        proposed.RebuildGroups(groups.Groups, groups.Kinds, groups.Ownership, false);
        foreach (var row in removes) { proposed.RemoveGrant(row.Grantee, row.Capability, row.Subject); }
        // The entire Operate set is installed in scratch before checking any dependent program grants.
        ordered = adds.OrderByDescending(keySelector: row => (row.Capability == WorldCapability.Operate)).ToArray();
        foreach (var row in ordered) {
            if (!proposed.InstallGrant(grant: row, reason: out reason)) { return false; }
        }

        var protectedGroups = OperatorGroups(definition: current);

        protectedGroups.UnionWith(other: proposed.OperatorGroups(definition: candidate));
        var oldGroups = (current?.Groups ?? WorldGroupsSection.Empty);

        if (!operates) {
            foreach (var id in protectedGroups) {
                var before = oldGroups.Groups.FirstOrDefault(predicate: row => (row.Id == id));
                var after = groups.Groups.FirstOrDefault(predicate: row => (row.Id == id));

                if (!Equals(objA: before, objB: after) ||
                    !Equals(objA: oldGroups.Kinds.FirstOrDefault(predicate: row => (row.Name == before?.KindName)), objB: groups.Kinds.FirstOrDefault(predicate: row => (row.Name == after?.KindName))) ||
                    !oldGroups.Ownership.Where(predicate: row => (row.Subject.Id == id)).SequenceEqual(second: groups.Ownership.Where(predicate: row => (row.Subject.Id == id)))) {
                    reason = "changing an operator group's membership, role, kind or ownership requires an operator";
                    return false;
                }
            }
        }

        foreach (var principal in proposed.AuthorityPrincipals()) {
            if (proposed.HasOperatorHold(principal: principal) && !proposed.EligibleOperator(principal: principal) &&
                (principal.Kind is not (PrincipalKind.Console or PrincipalKind.World)) &&
                (!(restore && !proposed.Holds(principal, WorldCapability.Operate, GrantSubject.All).IsAllowed) &&
                (restore || reset || adds.Any(predicate: row => (row.Capability == WorldCapability.Operate)) || !ReferenceEquals(objA: current?.Groups, objB: candidate?.Groups) || !ReferenceEquals(objA: current?.Admission, objB: candidate?.Admission)))) {
                reason = $"{principal.Describe()} cannot hold Operate: addons are excluded and remote holders require a live Replica entitlement";
                return false;
            }
        }
        if (candidate is not null) {
            foreach (var dependency in proposed.OperatorDependencies(definition: candidate)) {
                foreach (var principal in proposed.AuthorityPrincipals()) {
                    if (!proposed.IsOperator(principal: principal) && proposed.AllowsRecipient(principal, WorldCapability.Edit, GrantSubject.State(name: dependency)).IsAllowed) {
                        reason = $"operator membership source '{dependency}' is writable by {principal.Describe()}";
                        return false;
                    }
                }
            }
        }
        return true;
    }
    private bool EligibleOperatorGrantee(Grantee grantee) {
        if (grantee.TryGetPrincipal(principal: out var principal)) {
            return ((principal.Kind is PrincipalKind.Console or PrincipalKind.World) || EligibleOperator(principal: principal));
        }
        if (grantee.Kind != GranteeKind.Group) { return false; }
        foreach (var (member, groups) in m_groupRoleMembership) {
            if (!CheckRoles(groups: groups, member: member)) { return false; }
        }
        foreach (var (member, groups) in m_ownedGroupRoleReach) {
            if (!CheckRoles(groups: groups, member: member)) { return false; }
        }
        foreach (var (owner, groups) in m_ownedGroups) {
            if (groups.Contains(item: grantee.Name!) && (owner.Kind is not (PrincipalKind.Console or PrincipalKind.World)) && !EligibleOperator(principal: owner)) { return false; }
        }
        return true;

        bool CheckRoles(Principal member, List<GroupRoleReach> groups) {
            foreach (var group in groups) {
                if ((group.GroupId == grantee.Name) && group.Reach.Contains(item: WorldCapability.Operate) &&
                    (member.Kind is not (PrincipalKind.Console or PrincipalKind.World)) && !EligibleOperator(principal: member)) { return false; }
            }
            return true;
        }
    }

    internal PreparedAuthorityTransition PrepareDocument(Principal actor, WorldDefinition? current, WorldDefinition candidate) =>
        (TryPrepareDocument(actor: actor, candidate: candidate, current: current, grantsWrite: false, prepared: out var prepared, reason: out var reason) ? prepared! : throw new InvalidOperationException(message: reason));
    internal bool TryPrepareReset(Principal actor, WorldDefinition current, WorldDefinition candidate,
        out PreparedAuthorityTransition? prepared, out string reason) =>
        PreparedAuthorityTransition.TryPrepare(this, actor, current, candidate,
            candidate.Grants.Where(predicate: row => !WorldTick.IsDocumentChannelRow(grant: row)).Select(selector: WithoutAuthoredConsent).ToArray(), [], true,
            out prepared, out reason, reset: true);
    internal PreparedAuthorityTransition PrepareRestore(WorldGrantsCheckpoint checkpoint, WorldDefinition definition, Principal actor, bool resetSessionEpochs = false) {
        if (!IsOperator(principal: actor)) { throw new InvalidOperationException(message: "Restoring authority requires an operator."); }
        var proposed = new WorldGrants(host: m_host, population: m_population, routeTransition: static (_, _, _) => { }, seatCount: m_localSeatCount) { m_proposal = true };

        proposed.RestoreRaw(checkpoint: checkpoint, resetSessionEpochs: resetSessionEpochs);
        // A captured transport is not a restored connection. Remove retired generations before validating the graph.
        foreach (var peer in proposed.AuthorityPrincipals().Where(predicate: p => ((p.Kind == PrincipalKind.Peer) && (m_host is not null) && !Host.IsLivePeer(principal: p))).ToArray()) {
            foreach (var row in proposed.Rows(principal: peer)) { proposed.RemoveGrant(row.Grantee, row.Capability, row.Subject); }
        }
        if (!PreparedAuthorityTransition.TryPrepare(this, actor, m_authorityDefinition, definition, [], [], true,
            out var prepared, out var reason, checkpoint: proposed.Capture(), resetSessionEpochs: resetSessionEpochs)) {
            throw new InvalidOperationException(message: reason);
        }
        return prepared!;
    }

    private HashSet<string> OperatorGroups(WorldDefinition? definition) {
        var result = m_byGrantee.Where(predicate: pair => ((pair.Key.Kind == GranteeKind.Group) &&
            (pair.Value.For(capability: WorldCapability.Operate)?.Contains(item: GrantSubject.All) == true))).Select(selector: pair => pair.Key.Name!).ToHashSet(comparer: StringComparer.Ordinal);

        for (var count = -1; (count != result.Count);) {
            count = result.Count;
            foreach (var ownership in (definition?.Groups?.Ownership ?? [])) {
                if ((ownership.Subject.Kind == OwnershipSubjectKind.Group) && result.Contains(item: ownership.Subject.Id) && (ownership.Owner.GroupId is { } owner)) { result.Add(item: owner); }
            }
        }
        return result;
    }
    private IEnumerable<Principal> AuthorityPrincipals() => m_byGrantee.Keys.Where(predicate: row => (row.Kind == GranteeKind.Principal)).Select(selector: row => row.Principal)
        .Concat(second: m_groupRoleMembership.Keys).Concat(second: m_ownedGroups.Keys).Concat(second: m_ownedGroupRoleReach.Keys).Distinct();
    private HashSet<string> OperatorDependencies(WorldDefinition definition) {
        var rows = new HashSet<string>(comparer: StringComparer.Ordinal);
        var groups = (definition.Groups ?? WorldGroupsSection.Empty);
        var protectedGroups = OperatorGroups(definition: definition);

        foreach (var group in groups.Groups.Where(predicate: row => protectedGroups.Contains(item: row.Id))) {
            WorldStateDocumentValues.CollectReferencedRows(graph: group, rows: rows);
            if (groups.Kinds.FirstOrDefault(predicate: row => (row.Name == group.KindName)) is { } kind) { WorldStateDocumentValues.CollectReferencedRows(graph: kind, rows: rows); }
        }
        for (var count = -1; (count != rows.Count);) {
            count = rows.Count;
            foreach (var row in definition.State.Where(predicate: row => rows.Contains(item: row.Name.Value)).ToArray()) {
                WorldStateDocumentValues.CollectReferencedRows(graph: row, rows: rows);
            }
        }
        return rows;
    }

    // The token carries the exact admitted proposal. Its factory is private to WorldGrants; callers can only
    // consume it, never construct one or reach the raw installers. One token is consumed once on the tick thread.
    internal sealed class PreparedAuthorityTransition {
        private readonly WorldGrants m_owner;
        private readonly WorldDefinition? m_candidate;
        private readonly IReadOnlyList<WorldGrant> m_adds;
        private readonly IReadOnlyList<WorldGrant> m_removes;
        private readonly bool m_reset;
        private readonly WorldGrantsCheckpoint? m_checkpoint;
        private readonly bool m_resetSessionEpochs;

        private bool m_installed;

        private PreparedAuthorityTransition(WorldGrants owner, WorldDefinition? candidate, IReadOnlyList<WorldGrant> adds, IReadOnlyList<WorldGrant> removes, bool reset, WorldGrantsCheckpoint? checkpoint, bool resetSessionEpochs) {
            m_owner = owner; m_candidate = candidate; m_adds = adds; m_removes = removes; m_reset = reset; m_checkpoint = checkpoint;
            m_resetSessionEpochs = resetSessionEpochs;
        }

        internal static bool TryPrepare(WorldGrants owner, Principal actor, WorldDefinition? current, WorldDefinition? candidate,
            IReadOnlyList<WorldGrant> adds, IReadOnlyList<WorldGrant> removes, bool grantsWrite,
            out PreparedAuthorityTransition? prepared, out string reason, bool reset = false, WorldGrantsCheckpoint? checkpoint = null, bool resetSessionEpochs = false) {
            prepared = null;
            if (!owner.TryValidateAuthority(actor, current, candidate, adds, removes, grantsWrite, out var ordered, out reason,
                reset: reset, restore: (checkpoint is not null), checkpoint: checkpoint)) { return false; }
            prepared = new(adds: ordered, candidate: candidate, checkpoint: checkpoint, owner: owner, removes: removes, reset: reset, resetSessionEpochs: resetSessionEpochs);
            return true;
        }
        internal void Install() {
            if ((m_owner.m_host is { } host) && ((m_adds.Count != 0) || (m_removes.Count != 0) || !ReferenceEquals(objA: m_owner.m_authorityDefinition?.Groups, objB: m_candidate?.Groups))) { host.Document.MarkDefinitionDeliveryPending(); }
            if (m_installed) { throw new InvalidOperationException(message: "Authority transition already consumed."); }
            m_installed = true;
            var groupsChanged = !ReferenceEquals(objA: m_owner.m_authorityDefinition?.Groups, objB: m_candidate?.Groups);
            var dependenciesChanged = (groupsChanged || m_reset || (m_checkpoint is not null) || m_adds.Any(predicate: row => (row.Capability == WorldCapability.Operate)) || m_removes.Any(predicate: row => (row.Capability == WorldCapability.Operate)) ||
                ((m_owner.m_operatorSources.Count != 0) && !ReferenceEquals(objA: m_owner.m_authorityDefinition?.State, objB: m_candidate?.State)));

            if (m_checkpoint is not null) { m_owner.RestoreRaw(checkpoint: m_checkpoint, resetSessionEpochs: m_resetSessionEpochs); }
            if (m_reset) { m_owner.ResetRaw(seatCount: m_owner.m_localSeatCount); }
            if (m_candidate is not null) {
                var groups = (m_candidate.Groups ?? WorldGroupsSection.Empty);

                if (groupsChanged || m_reset || (m_checkpoint is not null)) { m_owner.RebuildGroups(groups.Groups, groups.Kinds, groups.Ownership, (m_checkpoint is null)); }
                m_owner.m_authorityDefinition = m_candidate;
            }
            foreach (var row in m_removes) { m_owner.RemoveGrant(row.Grantee, row.Capability, row.Subject); }
            foreach (var row in m_adds) { m_owner.InstallAdmittedGrant(grant: row); }
            if (dependenciesChanged) { m_owner.m_operatorSources = ((m_candidate is null) ? new(comparer: StringComparer.Ordinal) : m_owner.OperatorDependencies(definition: m_candidate)); }
        }
    }
}
