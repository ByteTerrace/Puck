using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    private static void AddRoleScopedGroup(
        Dictionary<WorldPrincipal, List<GroupRoleReach>> target,
        WorldPrincipal principal,
        string groupId,
        HashSet<WorldCapability> reach
    ) {
        if (reach.Count == 0) {
            return;
        }

        if (!target.TryGetValue(
            key: principal,
            value: out var groups
        )) {
            groups = new List<GroupRoleReach>();
            target[principal] = groups;
        }

        groups.Add(item: new GroupRoleReach(GroupId: groupId, Reach: reach));
    }

    // Role-scoped expansion: a group row is considered only when the member's exact role reaches the queried
    // capability, then the group's own grant row is resolved fresh. The same helper serves ordinary membership and
    // group-owner reach so both doors enforce the same null/unknown-role refusal.
    private bool TryRoleGroupExpansion(
        Dictionary<WorldPrincipal, List<GroupRoleReach>> groups,
        WorldPrincipal principal,
        WorldCapability capability,
        GrantSubject subject,
        GrantRule rule,
        out GrantVerdict verdict
    ) {
        if (groups.TryGetValue(
            key: principal,
            value: out var groupRows
        )) {
            foreach (var groupRow in groupRows) {
                if (!groupRow.Reach.Contains(item: capability)) {
                    continue;
                }

                var groupPrincipal = WorldPrincipal.Group(id: groupRow.GroupId);

                if (
                    !m_byPrincipal.TryGetValue(
                    key: groupPrincipal,
                    value: out var groupGrants
                ) ||
                    (groupGrants.For(capability: capability) is not { } groupSubjects)
                ) {
                    continue;
                }

                if (
                    groupSubjects.Contains(item: subject) ||
                    groupSubjects.Contains(item: GrantSubject.All)
                ) {
                    verdict = new GrantVerdict(
                        Rule: rule,
                        Group: groupRow.GroupId
                    );

                    return true;
                }
            }
        }

        verdict = default;

        return false;
    }

    // Resolves the grant row that supplied an already-allowed verdict. Payload callers (budget, reach, ceiling, and
    // masks) must read that same row, including when the verdict came from a role-scoped group fallback; reading
    // only the acting principal's dictionaries would silently turn a granted group row into an unmetered/defaulted
    // authority path.
    private bool TryResolveDecidingGrant(
        WorldPrincipal principal,
        WorldCapability capability,
        GrantSubject subject,
        out WorldPrincipal grantPrincipal,
        out GrantSubject grantSubject
    ) {
        var verdict = Allows(
            principal: principal,
            capability: capability,
            subject: subject
        );

        if (!verdict.IsAllowed) {
            grantPrincipal = default;
            grantSubject = default;

            return false;
        }

        if (verdict.Rule is GrantRule.GroupHold or GrantRule.OwnershipHold) {
            if (
                (verdict.Group is not { } groupId) ||
                !TryGroupGrantSubject(
                groupId: groupId,
                capability: capability,
                subject: subject,
                grantSubject: out grantSubject
            )) {
                grantPrincipal = default;
                grantSubject = default;

                return false;
            }

            grantPrincipal = WorldPrincipal.Group(id: groupId);

            return true;
        }

        grantPrincipal = principal;
        grantSubject = ((verdict.Rule == GrantRule.WildcardHold)
            ? GrantSubject.All
            : subject
        );

        return true;
    }

    private bool TryGroupGrantSubject(string groupId, WorldCapability capability, GrantSubject subject, out GrantSubject grantSubject) {
        if (
            m_byPrincipal.TryGetValue(
            key: WorldPrincipal.Group(id: groupId),
            value: out var grants
        ) &&
            (grants.For(capability: capability) is { } subjects)
        ) {
            if (subjects.Contains(item: subject)) {
                grantSubject = subject;

                return true;
            }

            if (subjects.Contains(item: GrantSubject.All)) {
                grantSubject = GrantSubject.All;

                return true;
            }
        }

        grantSubject = default;

        return false;
    }

    /// <summary>Resyncs the group+membership+ownership index wholesale from the live document's <c>groups</c>
    /// section. The role projection is derived state rather than checkpoint payload: null and unknown roles create no
    /// authority entry, direct principal ownership remains unrestricted, and group-owned reach uses the OWNER group's
    /// current member role. WorldServer calls this at construction and every install; checkpoint restoration uses
    /// <see cref="RestoreGroups"/> to retain the captured revision.</summary>
    /// <param name="groups">The live document's group roster rows.</param>
    /// <param name="kinds">The live document's declared group-kind catalog.</param>
    /// <param name="ownership">The live document's ownership bindings.</param>
    public void SyncGroups(IReadOnlyList<WorldGroup> groups, IReadOnlyList<WorldGroupKind> kinds, IReadOnlyList<WorldOwnership> ownership) =>
        RebuildGroups(groups, kinds, ownership, advanceRevision: true);

    /// <summary>Rehydrates the restored document's derived group authority without changing the checkpoint's
    /// revision. Restore has already discarded cached handle projections.</summary>
    internal void RestoreGroups(IReadOnlyList<WorldGroup> groups, IReadOnlyList<WorldGroupKind> kinds, IReadOnlyList<WorldOwnership> ownership) =>
        RebuildGroups(groups, kinds, ownership, advanceRevision: false);

    private void RebuildGroups(IReadOnlyList<WorldGroup> groups, IReadOnlyList<WorldGroupKind> kinds, IReadOnlyList<WorldOwnership> ownership, bool advanceRevision) {
        m_groupMembership.Clear();
        m_groupRoleMembership.Clear();
        m_groupReach.Clear();
        m_ownedGroups.Clear();
        m_ownedGroupRoleReach.Clear();

        var reachByKindName = new Dictionary<string, HashSet<WorldCapability>>(comparer: StringComparer.Ordinal);
        var roleReachByKindName = new Dictionary<string, Dictionary<string, HashSet<WorldCapability>>>(comparer: StringComparer.Ordinal);

        foreach (var kind in kinds) {
            var reach = new HashSet<WorldCapability>();

            foreach (var role in kind.Roles) {
                var roleReach = new HashSet<WorldCapability>();

                foreach (var capability in role.Capabilities) {
                    _ = reach.Add(item: capability);
                    _ = roleReach.Add(item: capability);
                }

                if (roleReach.Count > 0) {
                    if (!roleReachByKindName.TryGetValue(
                        key: kind.Name,
                        value: out var roleReaches
                    )) {
                        roleReaches = new Dictionary<string, HashSet<WorldCapability>>(comparer: StringComparer.Ordinal);
                        roleReachByKindName[kind.Name] = roleReaches;
                    }

                    roleReaches[role.Name] = roleReach;
                }
            }

            reachByKindName[kind.Name] = reach;
        }

        var groupsById = new Dictionary<string, WorldGroup>(comparer: StringComparer.Ordinal);

        foreach (var group in groups) {
            m_groupReach[group.Id] = (reachByKindName.TryGetValue(
                key: group.KindName,
                value: out var reach
            )
                ? reach
                : new HashSet<WorldCapability>()
            );
            groupsById[group.Id] = group;

            foreach (var memberRow in group.Members) {
                if ((memberRow.Ref.Kind != MemberRefKind.Local) || (memberRow.Ref.Principal is not { } member)) {
                    // Verified identities are retained in the document roster but cannot impersonate a local
                    // WorldPrincipal in this local grant index; the federation/consent lane owns that projection.
                    continue;
                }

                if (!m_groupMembership.TryGetValue(
                    key: member,
                    value: out var memberOf
                )) {
                    memberOf = new List<string>();
                    m_groupMembership[member] = memberOf;
                }

                memberOf.Add(item: group.Id);

                if (
                    (memberRow.Role is { } roleName) &&
                    roleReachByKindName.TryGetValue(
                    key: group.KindName,
                    value: out var roleReaches
                ) &&
                    roleReaches.TryGetValue(
                    key: roleName,
                    value: out var roleReach
                )
                ) {
                    AddRoleScopedGroup(
                        target: m_groupRoleMembership,
                        principal: member,
                        groupId: group.Id,
                        reach: roleReach
                    );
                }
            }
        }

        // Ownership is not a grant — a fact this door consults (GrantRule.OwnershipHold). Only Subject.Kind Group
        // exists today; a later subject-kind widening adds its own case here.
        foreach (var row in ownership) {
            if (row.Subject.Kind != OwnershipSubjectKind.Group) {
                continue;
            }

            switch (row.Owner.Kind) {
                case OwnershipOwnerKind.Principal:
                    if (row.Owner.Principal is { } ownerPrincipal) {
                        AddOwnedGroup(
                            owner: ownerPrincipal,
                            groupId: row.Subject.Id
                        );
                    }

                    break;
                case OwnershipOwnerKind.Group:
                    // A group owns a group: a CURRENT member reaches the SUBJECT group's own rows only when the
                    // member's exact role in the OWNER group reaches the queried capability — one level, resolved
                    // here against this same pass's roster, never recursively.
                    if (
                        (row.Owner.GroupId is { } ownerGroupId) &&
                        groupsById.TryGetValue(
                        key: ownerGroupId,
                        value: out var ownerGroup
                    )
                    ) {
                        var ownerRoleReaches = (roleReachByKindName.TryGetValue(
                            key: ownerGroup.KindName,
                            value: out var reaches
                        )
                            ? reaches
                            : null
                        );

                        foreach (var memberRow in ownerGroup.Members) {
                            if (
                                (memberRow.Ref.Kind != MemberRefKind.Local) ||
                                (memberRow.Ref.Principal is not { } member) ||
                                (memberRow.Role is not { } roleName) ||
                                (ownerRoleReaches is null) ||
                                !ownerRoleReaches.TryGetValue(
                                key: roleName,
                                value: out var roleReach
                            )
                            ) {
                                continue;
                            }

                            AddRoleScopedGroup(
                                target: m_ownedGroupRoleReach,
                                principal: member,
                                groupId: row.Subject.Id,
                                reach: roleReach
                            );
                        }
                    }

                    break;
            }
        }

        // Group membership and ownership are part of the effective authority document even though their projection
        // is derived rather than checkpointed. Advance the same revision used by handle tables so a changed roster or
        // role cannot leave an existing projection serving the previous authority view.
        if (advanceRevision) {
            m_revision++;
        }
    }
}
