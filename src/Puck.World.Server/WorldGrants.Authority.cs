using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    private WorldDefinition? m_authorityDefinition;
    private bool m_proposal;
    private HashSet<string> m_operatorSources = new(StringComparer.Ordinal);
    private readonly int m_localSeatCount;

    /// <inheritdoc/>
    public bool IsOperator(Principal principal) {
        if (principal.Kind is PrincipalKind.Console or PrincipalKind.World) { return true; }
        if (!EligibleOperator(principal)) { return false; }
        return HasOperatorHold(principal);
    }

    // Deliberately does not call Allows: Allows itself expands operator authority.
    private bool HasOperatorHold(Principal principal) =>
        Holds(principal, WorldCapability.Operate, GrantSubject.All).IsAllowed ||
        TryRoleGroupExpansion(m_groupRoleMembership, principal, WorldCapability.Operate, GrantSubject.All, GrantRule.GroupHold, out _) ||
        TryGroupExpansion(m_ownedGroups, principal, WorldCapability.Operate, GrantSubject.All, GrantRule.OwnershipHold, out _) ||
        TryRoleGroupExpansion(m_ownedGroupRoleReach, principal, WorldCapability.Operate, GrantSubject.All, GrantRule.OwnershipHold, out _);

    private bool IsOperatorGrantee(Grantee grantee) => grantee.TryGetPrincipal(out var principal)
        ? IsOperator(principal) : grantee.Kind == GranteeKind.Group && Holds(grantee, WorldCapability.Operate, GrantSubject.All).IsAllowed;

    private bool EligibleOperator(Principal principal) {
        if (principal.Kind == PrincipalKind.Seat) { return (uint)principal.Index < (uint)m_localSeatCount; }
        if (principal.Kind == PrincipalKind.Session) {
            return IsLiveSession(principal) && m_sessions[principal.Index].Tier == WorldDisclosureTier.Replica;
        }
        if (principal.Kind != PrincipalKind.Peer || m_host is null ||
            !Host.Population.IsAdmittedPeer(principal.Index) || Host.Population.IsParked(principal.Index) ||
            Host.Population.PeerPrincipal(principal.Index) != principal || Host.Population.PeerAdmissionTier(principal.Index) != WorldDisclosureTier.Replica) { return false; }
        var identity = Host.Population.PeerIdentity(principal.Index);
        return WorldAdmissionDoor.TryMatchEntry((m_authorityDefinition ?? Host.Definition).Admission, identity.Domain,
            identity.Subject, Host.Population.PeerAdmissionTrust(principal.Index), out var verdict) &&
            verdict.Tier == WorldDisclosureTier.Replica;
    }

    /// <summary>Admits and installs a direct grant using the actor's authority before the change.</summary>
    public bool TryGrant(WorldGrant grant, Principal actor, out string reason) {
        if (!TryPrepareAuthority(actor, m_authorityDefinition, m_authorityDefinition, [grant], [], false, out var prepared, out reason)) { return false; }
        prepared!.Install();
        return true;
    }

    internal bool TryGrantSet(IReadOnlyList<WorldGrant> grants, Principal actor, out string reason) {
        if (!TryPrepareAuthority(actor, m_authorityDefinition, m_authorityDefinition, grants, [], false, out var prepared, out reason)) { return false; }
        prepared!.Install();
        return true;
    }

    /// <summary>Admits and removes a direct grant using the actor's authority before the change.</summary>
    public bool Revoke(Grantee grantee, WorldCapability capability, GrantSubject subject, Principal actor) {
        var row = new WorldGrant(grantee, capability, subject, false);
        var held = Held(grantee).Contains((capability, subject));
        if (!TryPrepareAuthority(actor, m_authorityDefinition, m_authorityDefinition, [], [row], false, out var prepared, out _)) { return false; }
        prepared!.Install();
        return held;
    }

    internal bool TryPrepareDocument(Principal actor, WorldDefinition? current, WorldDefinition candidate,
        bool grantsWrite, out PreparedAuthorityTransition? prepared, out string reason) {
        var before = current?.Grants ?? [];
        var removes = before.Where(row => !candidate.Grants.Contains(row) && !WorldTick.IsDocumentChannelRow(row)).ToArray();
        var adds = candidate.Grants.Where(row => !before.Contains(row) && !WorldTick.IsDocumentChannelRow(row))
            .Select(WithoutAuthoredConsent).ToArray();
        return TryPrepareAuthority(actor, current, candidate, adds, removes, grantsWrite || !before.SequenceEqual(candidate.Grants), out prepared, out reason);
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
        var operates = IsOperator(actor);
        if ((!operates && grantsWrite) || adds.Concat(removes).Any(row =>
            !operates && (row.Capability == WorldCapability.Operate || !HoldsForAdministration(actor, row.Capability, row.Subject)))) {
            reason = "the authority transition requires an operator or an existing delegated capability";
            return false;
        }

        if (!restore && !reset && adds.Count == 0 && removes.Count == 0 && ReferenceEquals(current?.Groups, candidate?.Groups) && ReferenceEquals(current?.Admission, candidate?.Admission)) { return true; }

        // Detached scratch uses the very same role and grant resolution as the live table. It cannot publish.
        var proposed = new WorldGrants(m_localSeatCount, m_population, static (_, _, _) => { }, m_host) { m_proposal = true };
        if (!reset) { proposed.RestoreRaw(checkpoint ?? Capture()); }
        proposed.m_authorityDefinition = candidate;
        var groups = candidate?.Groups ?? WorldGroupsSection.Empty;
        proposed.RebuildGroups(groups.Groups, groups.Kinds, groups.Ownership, false);
        foreach (var row in removes) { proposed.RemoveGrant(row.Grantee, row.Capability, row.Subject); }
        // The entire Operate set is installed in scratch before checking any dependent program grants.
        ordered = adds.OrderByDescending(row => row.Capability == WorldCapability.Operate).ToArray();
        foreach (var row in ordered) {
            if (!proposed.InstallGrant(row, out reason)) { return false; }
        }

        var protectedGroups = OperatorGroups(current);
        protectedGroups.UnionWith(proposed.OperatorGroups(candidate));
        var oldGroups = current?.Groups ?? WorldGroupsSection.Empty;
        if (!operates) {
            foreach (var id in protectedGroups) {
                var before = oldGroups.Groups.FirstOrDefault(row => row.Id == id);
                var after = groups.Groups.FirstOrDefault(row => row.Id == id);
                if (!Equals(before, after) ||
                    !Equals(oldGroups.Kinds.FirstOrDefault(row => row.Name == before?.KindName), groups.Kinds.FirstOrDefault(row => row.Name == after?.KindName)) ||
                    !oldGroups.Ownership.Where(row => row.Subject.Id == id).SequenceEqual(groups.Ownership.Where(row => row.Subject.Id == id))) {
                    reason = "changing an operator group's membership, role, kind or ownership requires an operator";
                    return false;
                }
            }
        }

        foreach (var principal in proposed.AuthorityPrincipals()) {
            if (proposed.HasOperatorHold(principal) && !proposed.EligibleOperator(principal) &&
                principal.Kind is not (PrincipalKind.Console or PrincipalKind.World) &&
                (!(restore && !proposed.Holds(principal, WorldCapability.Operate, GrantSubject.All).IsAllowed) &&
                (restore || reset || adds.Any(row => row.Capability == WorldCapability.Operate) || !ReferenceEquals(current?.Groups, candidate?.Groups) || !ReferenceEquals(current?.Admission, candidate?.Admission)))) {
                reason = $"{principal.Describe()} cannot hold Operate: addons are excluded and remote holders require a live Replica entitlement";
                return false;
            }
        }
        if (candidate is not null) {
            foreach (var dependency in proposed.OperatorDependencies(candidate)) {
                foreach (var principal in proposed.AuthorityPrincipals()) {
                    if (!proposed.IsOperator(principal) && proposed.AllowsRecipient(principal, WorldCapability.Edit, GrantSubject.State(dependency)).IsAllowed) {
                        reason = $"operator membership source '{dependency}' is writable by {principal.Describe()}";
                        return false;
                    }
                }
            }
        }
        return true;
    }

    internal PreparedAuthorityTransition PrepareDocument(Principal actor, WorldDefinition? current, WorldDefinition candidate) =>
        TryPrepareDocument(actor, current, candidate, false, out var prepared, out var reason) ? prepared! : throw new InvalidOperationException(reason);

    internal PreparedAuthorityTransition PrepareReset(Principal actor, WorldDefinition current, WorldDefinition candidate) =>
        PreparedAuthorityTransition.TryPrepare(this, actor, current, candidate,
            candidate.Grants.Where(row => !WorldTick.IsDocumentChannelRow(row)).Select(WithoutAuthoredConsent).ToArray(), [], true,
            out var prepared, out var reason, reset: true) ? prepared! : throw new InvalidOperationException(reason);

    internal void Restore(WorldGrantsCheckpoint checkpoint, WorldDefinition definition, Principal actor, bool resetSessionEpochs = false) {
        if (!IsOperator(actor)) { throw new InvalidOperationException("Restoring authority requires an operator."); }
        var proposed = new WorldGrants(m_localSeatCount, m_population, static (_, _, _) => { }, m_host) { m_proposal = true };
        proposed.RestoreRaw(checkpoint, resetSessionEpochs);
        // A captured transport is not a restored connection. Remove retired generations before validating the graph.
        foreach (var peer in proposed.AuthorityPrincipals().Where(p => p.Kind == PrincipalKind.Peer && m_host is not null && !Host.IsLivePeer(p)).ToArray()) {
            foreach (var row in proposed.Rows(peer)) { proposed.RemoveGrant(row.Grantee, row.Capability, row.Subject); }
        }
        if (!PreparedAuthorityTransition.TryPrepare(this, actor, m_authorityDefinition, definition, [], [], true,
            out var prepared, out var reason, checkpoint: proposed.Capture())) {
            throw new InvalidOperationException(reason);
        }
        prepared!.Install();
    }

    private HashSet<string> OperatorGroups(WorldDefinition? definition) {
        var result = m_byGrantee.Where(pair => pair.Key.Kind == GranteeKind.Group &&
            pair.Value.For(WorldCapability.Operate)?.Contains(GrantSubject.All) == true).Select(pair => pair.Key.Name!).ToHashSet(StringComparer.Ordinal);
        for (var count = -1; count != result.Count;) {
            count = result.Count;
            foreach (var ownership in definition?.Groups?.Ownership ?? []) {
                if (ownership.Subject.Kind == OwnershipSubjectKind.Group && result.Contains(ownership.Subject.Id) && ownership.Owner.GroupId is { } owner) { result.Add(owner); }
            }
        }
        return result;
    }

    private IEnumerable<Principal> AuthorityPrincipals() => m_byGrantee.Keys.Where(row => row.Kind == GranteeKind.Principal).Select(row => row.Principal)
        .Concat(m_groupRoleMembership.Keys).Concat(m_ownedGroups.Keys).Concat(m_ownedGroupRoleReach.Keys).Distinct();

    private HashSet<string> OperatorDependencies(WorldDefinition definition) {
        var rows = new HashSet<string>(StringComparer.Ordinal);
        var groups = definition.Groups ?? WorldGroupsSection.Empty;
        var protectedGroups = OperatorGroups(definition);
        foreach (var group in groups.Groups.Where(row => protectedGroups.Contains(row.Id))) {
            WorldStateDocumentValues.CollectReferencedRows(group, rows);
            if (groups.Kinds.FirstOrDefault(row => row.Name == group.KindName) is { } kind) { WorldStateDocumentValues.CollectReferencedRows(kind, rows); }
        }
        for (var count = -1; count != rows.Count;) {
            count = rows.Count;
            foreach (var row in definition.State.Where(row => rows.Contains(row.Name.Value)).ToArray()) {
                WorldStateDocumentValues.CollectReferencedRows(row, rows);
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
        private bool m_installed;
        private PreparedAuthorityTransition(WorldGrants owner, WorldDefinition? candidate, IReadOnlyList<WorldGrant> adds, IReadOnlyList<WorldGrant> removes, bool reset, WorldGrantsCheckpoint? checkpoint) {
            m_owner = owner; m_candidate = candidate; m_adds = adds; m_removes = removes; m_reset = reset; m_checkpoint = checkpoint;
        }
        internal static bool TryPrepare(WorldGrants owner, Principal actor, WorldDefinition? current, WorldDefinition? candidate,
            IReadOnlyList<WorldGrant> adds, IReadOnlyList<WorldGrant> removes, bool grantsWrite,
            out PreparedAuthorityTransition? prepared, out string reason, bool reset = false, WorldGrantsCheckpoint? checkpoint = null) {
            prepared = null;
            if (!owner.TryValidateAuthority(actor, current, candidate, adds, removes, grantsWrite, out var ordered, out reason,
                reset: reset, restore: checkpoint is not null, checkpoint: checkpoint)) { return false; }
            prepared = new(owner, candidate, ordered, removes, reset, checkpoint);
            return true;
        }
        internal void Install() {
            if (m_owner.m_host is { } host && (m_adds.Count != 0 || m_removes.Count != 0 || !ReferenceEquals(m_owner.m_authorityDefinition?.Groups, m_candidate?.Groups))) { host.Document.MarkDefinitionDeliveryPending(); }
            if (m_installed) { throw new InvalidOperationException("Authority transition already consumed."); }
            m_installed = true;
            if (m_checkpoint is not null) { m_owner.RestoreRaw(m_checkpoint); }
            if (m_reset) { m_owner.ResetRaw(m_owner.m_localSeatCount); }
            if (m_candidate is not null) {
                var groups = m_candidate.Groups ?? WorldGroupsSection.Empty;
                m_owner.RebuildGroups(groups.Groups, groups.Kinds, groups.Ownership, m_checkpoint is null);
                m_owner.m_authorityDefinition = m_candidate;
            }
            foreach (var row in m_removes) { m_owner.RemoveGrant(row.Grantee, row.Capability, row.Subject); }
            foreach (var row in m_adds) {
                if (!m_owner.InstallGrant(row, out var reason)) { throw new InvalidOperationException(reason); }
            }
            m_owner.m_operatorSources = m_candidate is null ? new(StringComparer.Ordinal) : m_owner.OperatorDependencies(m_candidate);
        }
    }
}
