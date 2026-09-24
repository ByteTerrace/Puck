using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    /// <summary>Captures every table this class owns.</summary>
    public WorldGrantsCheckpoint Capture() {
        var grantees = new List<WorldGrantsGranteeCheckpoint>(capacity: m_byGrantee.Count);

        foreach (var (grantee, grants) in m_byGrantee) {
            grantees.Add(item: new WorldGrantsGranteeCheckpoint(
                Grantee: grantee,
                Drive: [.. (grants.For(capability: WorldCapability.Drive) ?? [])],
                Observe: [.. (grants.For(capability: WorldCapability.Observe) ?? [])],
                Control: [.. (grants.For(capability: WorldCapability.Control) ?? [])],
                Mutate: [.. (grants.For(capability: WorldCapability.Mutate) ?? [])],
                Edit: [.. (grants.For(capability: WorldCapability.Edit) ?? [])],
                Applications: [.. ((grantee.TryGetPrincipal(principal: out var principal)
                    ? m_applications.GetValueOrDefault(key: principal)
                    : null) ?? [])]
            ));
        }

        // A principal may have composed an application set without holding any capability row of its own, so the
        // application table is swept separately rather than assumed to be a subset of the capability table.
        foreach (var (principal, applications) in m_applications) {
            if (!m_byGrantee.ContainsKey(key: principal)) {
                grantees.Add(item: new WorldGrantsGranteeCheckpoint(
                    Applications: [.. applications],
                    Control: [],
                    Drive: [],
                    Edit: [],
                    Grantee: principal,
                    Mutate: [],
                    Observe: []
                ));
            }
        }

        return new WorldGrantsCheckpoint(
            Grantees: grantees,
            Exclusive: [.. m_exclusive.Select(selector: static pair => (pair.Key.Capability, pair.Key.Subject, pair.Value))],
            Budgets: [.. m_budgets.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, pair.Value))],
            EventBudgets: [.. m_eventBudgets.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, pair.Value))],
            HoldCeilings: [.. m_holdCeilings.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, pair.Value))],
            ChannelReach: [.. m_channelReach.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, pair.Value.Bits))],
            PoolCeilings: [.. m_poolCeilings.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, CaptureCeilings(ceilings: pair.Value)))],
            KindMasks: [.. m_kindMasks.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, pair.Value.Bits))],
            WriteMasks: [.. m_writeMasks.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, pair.Value.Bits))],
            SeededSections: [.. m_seededSections.Select(selector: static key => (key.Grantee, key.Capability, key.Subject))],
            DriveGates: [.. m_driveGates.Select(selector: static pair => (pair.Key, pair.Value))],
            Revision: m_revision
        );
    }
    /// <summary>Restores every table this class owns from a previously captured checkpoint — a wholesale replace,
    /// never a merge onto whatever the boot-seed constructor already installed.</summary>
    public void Restore(WorldGrantsCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        m_applications.Clear();
        m_byGrantee.Clear();
        m_exclusive.Clear();
        m_budgets.Clear();
        m_eventBudgets.Clear();
        m_holdCeilings.Clear();
        m_channelReach.Clear();
        m_poolCeilings.Clear();
        m_kindMasks.Clear();
        m_writeMasks.Clear();
        m_seededSections.Clear();
        m_handleTables.Clear();
        m_groupRoleMembership.Clear();
        m_groupReach.Clear();
        m_ownedGroups.Clear();
        m_ownedGroupRoleReach.Clear();
        m_driveGates.Clear();

        foreach (var row in checkpoint.Grantees) {
            var grants = new GranteeGrants();

            foreach (var subject in row.Drive) {
                grants.Add(
                    capability: WorldCapability.Drive,
                    subject: subject
                );
            }
            foreach (var subject in row.Observe) {
                grants.Add(
                    capability: WorldCapability.Observe,
                    subject: subject
                );
            }
            foreach (var subject in row.Control) {
                grants.Add(
                    capability: WorldCapability.Control,
                    subject: subject
                );
            }
            foreach (var subject in row.Mutate) {
                grants.Add(
                    capability: WorldCapability.Mutate,
                    subject: subject
                );
            }
            foreach (var subject in row.Edit) {
                grants.Add(
                    capability: WorldCapability.Edit,
                    subject: subject
                );
            }

            m_byGrantee[row.Grantee] = grants;

            if (
                (row.Applications.Count > 0) &&
                row.Grantee.TryGetPrincipal(principal: out var principal)
            ) {
                m_applications[principal] = [.. row.Applications];
            }
        }

        foreach (var row in checkpoint.Exclusive) {
            m_exclusive[new ExclusiveKey(
                Capability: row.Capability,
                Subject: row.Subject
            )] = row.Holder;
        }
        foreach (var row in checkpoint.Budgets) {
            m_budgets[(row.Grantee, row.Capability, row.Subject)] = row.Budget;
        }
        foreach (var row in checkpoint.EventBudgets) {
            m_eventBudgets[(row.Grantee, row.Capability, row.Subject)] = row.Budget;
        }
        foreach (var row in checkpoint.HoldCeilings) {
            m_holdCeilings[(row.Grantee, row.Capability, row.Subject)] = row.Ceiling;
        }
        foreach (var row in checkpoint.ChannelReach) {
            m_channelReach[(row.Grantee, row.Capability, row.Subject)] = new ChannelReachMask(Bits: row.Bits);
        }
        foreach (var row in checkpoint.PoolCeilings) {
            m_poolCeilings[(row.Grantee, row.Capability, row.Subject)] = RestoreCeilings(rows: row.Ceilings);
        }
        foreach (var row in checkpoint.KindMasks) {
            m_kindMasks[(row.Grantee, row.Capability, row.Subject)] = new MutationKindMask(Bits: row.Bits);
        }
        foreach (var row in checkpoint.WriteMasks) {
            m_writeMasks[(row.Grantee, row.Capability, row.Subject)] = new DocumentWriteMask(Bits: row.Bits);
        }
        foreach (var row in checkpoint.SeededSections) {
            _ = m_seededSections.Add(item: new SeededKey(
                Capability: row.Capability,
                Grantee: row.Grantee,
                Subject: row.Subject
            ));
        }
        // Group membership, kind reach, and ownership derive from the restored definition: until WorldServer calls
        // RestoreGroups every group-derived check remains deny-by-default.
        foreach (var row in checkpoint.DriveGates) {
            m_driveGates[row.BodyIndex] = row.Reason;
        }

        m_revision = checkpoint.Revision;
    }

    private static IReadOnlyList<(int Ordinal, long Ceiling)> CaptureCeilings(ChannelCeilings ceilings) {
        var rows = new List<(int, long)>();

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if ((ceilings.Support.Bits & (1UL << ordinal)) != 0UL) {
                rows.Add(item: (ordinal, ceilings[ordinal]));
            }
        }

        return rows;
    }
    private static ChannelCeilings RestoreCeilings(IReadOnlyList<(int Ordinal, long Ceiling)> rows) {
        var ceilings = default(ChannelCeilings);

        foreach (var row in rows) {
            ceilings = ceilings.WithCeiling(
                ceiling: row.Ceiling,
                channels: new ChannelConsentMask(Bits: (1UL << row.Ordinal))
            );
        }

        return ceilings;
    }
}
