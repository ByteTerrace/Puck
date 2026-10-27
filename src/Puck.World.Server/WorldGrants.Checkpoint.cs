using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    /// <summary>Captures every table this class owns: every grantee's rows, the sessions it admitted with the rows they
    /// hold and the input their viewers last forwarded, and the epoch each session ordinal last carried, so a restored
    /// world never reissues a retired session principal. A restored session has no observer of its own: a replay re-drive
    /// keeps it until the tape ends it, and a live restore ends what nothing observes
    /// (<see cref="WorldServer.EndUnobservedSessions"/>).</summary>
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
            ChannelReach: [.. m_channelReach.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, pair.Value))],
            PoolCeilings: [.. m_poolCeilings.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, CaptureCeilings(ceilings: pair.Value)))],
            KindMasks: [.. m_kindMasks.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, pair.Value.Bits))],
            WriteMasks: [.. m_writeMasks.Select(selector: static pair => (pair.Key.Grantee, pair.Key.Capability, pair.Key.Subject, pair.Value.Bits))],
            SeededSections: [.. m_seededSections.Select(selector: static key => (key.Grantee, key.Capability, key.Subject))],
            DriveGates: [.. m_driveGates.Select(selector: static pair => (pair.Key, pair.Value))],
            SessionEpochs: [.. m_sessionEpochs.OrderBy(keySelector: static pair => pair.Key).Select(selector: static pair => (pair.Key, pair.Value))],
            Sessions: CaptureSessions(),
            Revision: m_revision
        );
    }
    /// <summary>Restores every table this class owns from a previously captured checkpoint — a wholesale replace,
    /// never a merge onto whatever the boot-seed constructor already installed. The sessions it admits afterwards are
    /// the checkpoint's, each as it stood, with the retired epochs kept; a live session the checkpoint does not hold
    /// ends here, and its screen admits a new one.</summary>
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
            m_channelReach[(row.Grantee, row.Capability, row.Subject)] = row.Reach;
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

        // An epoch never moves backward: the later of the checkpoint's epoch and this world's own stays retired.
        foreach (var (ordinal, epoch) in checkpoint.SessionEpochs) {
            m_sessionEpochs[ordinal] = Math.Max(
                val1: epoch,
                val2: m_sessionEpochs.GetValueOrDefault(key: ordinal)
            );
        }

        RestoreSessions(sessions: checkpoint.Sessions);
        m_revision = checkpoint.Revision;
    }

    private static IReadOnlyList<(string Channel, long Ceiling)> CaptureCeilings(Dictionary<string, long> ceilings) =>
        ceilings.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => (pair.Key, pair.Value)).ToArray();

    private static Dictionary<string, long> RestoreCeilings(IReadOnlyList<(string Channel, long Ceiling)> rows) {
        var ceilings = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows) {
            if (string.IsNullOrWhiteSpace(row.Channel) || row.Ceiling <= 0 || !ceilings.TryAdd(row.Channel, row.Ceiling)) {
                throw new ArgumentException("checkpoint contains an invalid or duplicate named channel ceiling", nameof(rows));
            }
        }
        return ceilings;
    }
}
