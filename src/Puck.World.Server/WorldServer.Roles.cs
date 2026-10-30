using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // A role driver's derived body authority: the bodies (with the generation that holds each) it may Drive and
    // Observe, and the budgets its role grants. Derived from the live companion bindings, never held as grant rows.
    private sealed class RoleAuthority {
        public List<(int Body, int Generation)> Drive { get; } = [];
        public List<(int Body, int Generation)> Observe { get; } = [];
        public ushort DriveBudget { get; set; }
        public ushort ObserveBudget { get; set; }
    }

    private readonly Dictionary<string, RoleAuthority> m_roleAuthority = new(comparer: StringComparer.Ordinal);
    private int m_roleAuthorityRevision = int.MinValue;
    private WorldDefinition? m_roleAuthorityDefinition;
    // The population revision and definition the visiting drivers were last reconciled against.
    private int m_rolesPopulationRevision = int.MinValue;
    private WorldDefinition? m_rolesDefinition;

    /// <summary>Gets the requests a visiting driver mounts with: Drive and Observe over whatever its role derives,
    /// which is the companions that prefer its pin and the players they follow, never more.</summary>
    public static IReadOnlyList<WorldCapabilityRequest> VisitingDriverRequests { get; } = [
        new(Capability: WorldCapability.Drive, Subject: GrantSubject.All),
        new(Capability: WorldCapability.Observe, Subject: GrantSubject.All),
    ];

    /// <summary>Returns whether <paramref name="principal"/> drives a role this world declares: a role row's named
    /// driver addon, or a visiting driver of an accept role. Such a principal's Drive and Observe over bodies are
    /// derived from the role and it holds no other body, control, mutation or edit authority.</summary>
    /// <param name="principal">The principal.</param>
    /// <returns><see langword="true"/> for a role driver.</returns>
    public bool IsRoleDriver(Principal principal) {
        if ((principal.Kind != PrincipalKind.Addon) || (principal.Name is not { } name) || (m_document is null)) {
            return false;
        }

        foreach (var role in Definition.Roles) {
            if (
                string.Equals(a: role.Driver, b: name, comparisonType: StringComparison.Ordinal) ||
                (role.Accept && WorldRole.IsVisitorAddonName(addonName: name, role: role.Name))
            ) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Decides a role driver's authority from its role: Drive over exactly the companions bound to it and
    /// Observe over those and the players they follow, at the generation each holds now; no Drive, Control, Mutate or
    /// Edit over anything else and no Observe over any other body or the whole domain. Answers
    /// <see langword="false"/> for a principal that drives no role, and for a role driver's Observe over a non-body
    /// subject, which its own grant rows decide.</summary>
    /// <param name="principal">The acting principal.</param>
    /// <param name="capability">The capability.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="verdict">The derived verdict.</param>
    /// <returns><see langword="true"/> when the role decided.</returns>
    internal bool TryRoleVerdict(Principal principal, WorldCapability capability, GrantSubject subject, out GrantVerdict verdict) {
        verdict = new GrantVerdict(Rule: GrantRule.NoHold);
        if (!IsRoleDriver(principal: principal)) {
            return false;
        }

        if (
            (capability is WorldCapability.Drive or WorldCapability.Observe) &&
            (subject.Kind == GrantSubjectKind.Body)
        ) {
            if (RoleBodies(capability: capability, principal: principal) is { } bodies) {
                foreach (var (body, generation) in bodies) {
                    if ((body == subject.Value) && m_population.IsActive(index: body) && (m_population.Generation(index: body) == generation)) {
                        verdict = new GrantVerdict(Rule: GrantRule.ConcreteHold);
                        break;
                    }
                }
            }
            return true;
        }

        return ((capability != WorldCapability.Observe) || (subject.Kind is GrantSubjectKind.All or GrantSubjectKind.Body));
    }
    /// <summary>Returns the per-tick budget a role grants its driver over a body it may act on.</summary>
    /// <param name="principal">The role driver.</param>
    /// <param name="capability">Drive or Observe.</param>
    /// <param name="subject">The body.</param>
    /// <param name="budget">The role's budget.</param>
    /// <returns><see langword="true"/> when the role derives this authority.</returns>
    internal bool TryRoleBudget(Principal principal, WorldCapability capability, GrantSubject subject, out ushort budget) {
        budget = 0;
        if (
            !TryRoleVerdict(capability: capability, principal: principal, subject: subject, verdict: out var verdict) ||
            !verdict.IsAllowed ||
            !m_roleAuthority.TryGetValue(key: principal.Name!, value: out var authority)
        ) {
            return false;
        }

        budget = ((capability == WorldCapability.Drive) ? authority.DriveBudget : authority.ObserveBudget);
        return true;
    }
    /// <summary>Returns the bodies a role derives for its driver's projection (handle tables and disclosures), in body
    /// order, or <see langword="null"/> for a principal that drives no role.</summary>
    /// <param name="principal">The principal.</param>
    /// <param name="capability">Drive or Observe.</param>
    /// <returns>The derived body subjects, or <see langword="null"/>.</returns>
    internal IEnumerable<GrantSubject>? RoleProjection(Principal principal, WorldCapability capability) {
        if (!IsRoleDriver(principal: principal)) {
            return null;
        }

        return ((RoleBodies(capability: capability, principal: principal) is { } bodies)
            ? bodies.Select(selector: static row => GrantSubject.Body(index: row.Body)).Distinct().Order(comparer: Comparer<GrantSubject>.Create(comparison: WorldGrants.CompareSubjects))
            : []);
    }
    /// <summary>Re-derives every role's authority when the population or the document moved since the last
    /// derivation, and moves the grant table's revision when any driver's bodies changed, so handle tables and
    /// disclosures re-project. Cheap when nothing moved.</summary>
    internal void RefreshRoleAuthority() {
        if ((m_population is null) || (m_document is null)) {
            return;
        }

        var definition = Definition;

        if (ReferenceEquals(objA: definition, objB: m_roleAuthorityDefinition) && (m_population.Revision == m_roleAuthorityRevision)) {
            return;
        }

        m_roleAuthorityDefinition = definition;
        m_roleAuthorityRevision = m_population.Revision;

        var fresh = new Dictionary<string, RoleAuthority>(comparer: StringComparer.Ordinal);

        for (var slot = 0; (slot < m_population.Capacity); slot++) {
            if (
                (m_population.Companion(index: slot) is not { } binding) ||
                (definition.FindRole(name: binding.Role) is not { } role) ||
                (role.DriverFor(pin: ((m_population.InhabitantPlacementId(index: slot) is null) ? binding.Driver : null)) is not { } driver)
            ) {
                continue;
            }
            if (!fresh.TryGetValue(key: driver, value: out var authority)) {
                authority = new RoleAuthority { DriveBudget = role.Drive, ObserveBudget = role.Observe };
                fresh[driver] = authority;
            }

            authority.Drive.Add(item: (slot, m_population.Generation(index: slot)));
            authority.Observe.Add(item: (slot, m_population.Generation(index: slot)));
            if (m_population.FollowedBody(binding: in binding) is var followed and >= 0) {
                authority.Observe.Add(item: (followed, m_population.Generation(index: followed)));
            }
        }

        if (!SameAuthority(left: m_roleAuthority, right: fresh)) {
            m_roleAuthority.Clear();
            foreach (var (driver, authority) in fresh) {
                m_roleAuthority[driver] = authority;
            }
            m_grants.NoteDerivedAuthorityMoved();
        }
    }
    /// <summary>Reconciles the visiting drivers with the companions present: each verified pin an accept role's
    /// visitors prefer mounts its own driver, by pin from the process content store, and a driver whose pin no
    /// visitor prefers any more unmounts. A function of the population and the document, run at one fixed point of
    /// the tick, so a replay mounts the same drivers.</summary>
    /// <param name="tick">The tick being stepped.</param>
    internal void ReconcileRoles(ulong tick) {
        RefreshRoleAuthority();

        var definition = Definition;

        if (ReferenceEquals(objA: definition, objB: m_rolesDefinition) && (m_population.Revision == m_rolesPopulationRevision)) {
            return;
        }

        m_rolesDefinition = definition;
        m_rolesPopulationRevision = m_population.Revision;

        foreach (var role in definition.Roles) {
            if (!role.Accept) {
                continue;
            }

            var pins = new SortedSet<string>(comparer: StringComparer.Ordinal);

            for (var slot = 0; (slot < m_population.Capacity); slot++) {
                if (
                    (m_population.Companion(index: slot) is { Driver: { } pin } binding) &&
                    string.Equals(a: binding.Role, b: role.Name, comparisonType: StringComparison.Ordinal) &&
                    (m_population.InhabitantPlacementId(index: slot) is null)
                ) {
                    _ = pins.Add(item: pin);
                }
            }

            foreach (var addon in definition.Addons.ToArray()) {
                if (
                    WorldRole.IsVisitorAddonName(addonName: addon.Name, role: role.Name) &&
                    !pins.Any(predicate: pin => string.Equals(a: WorldRole.VisitorAddonName(pin: pin, role: role.Name), b: addon.Name, comparisonType: StringComparison.Ordinal))
                ) {
                    ApplyRoleMutation(mutation: new WorldMutation.RemoveAddon(Name: addon.Name, Principal: Principal.World), role: role, tick: tick);
                }
            }
            foreach (var pin in pins) {
                var name = WorldRole.VisitorAddonName(pin: pin, role: role.Name);

                if (Definition.Addons.Any(predicate: addon => string.Equals(a: addon.Name, b: name, comparisonType: StringComparison.Ordinal))) {
                    continue;
                }

                ApplyRoleMutation(
                    mutation: new WorldMutation.UpsertAddon(
                        Addon: new WorldAddonRow(
                            Enabled: true,
                            Fuel: role.Fuel,
                            Hash: pin,
                            ModulePath: string.Empty,
                            Name: name,
                            Requests: VisitingDriverRequests
                        ),
                        Principal: Principal.World
                    ),
                    role: role,
                    tick: tick
                );
            }
        }
    }
    private List<(int Body, int Generation)>? RoleBodies(Principal principal, WorldCapability capability) {
        RefreshRoleAuthority();

        return (m_roleAuthority.TryGetValue(key: principal.Name!, value: out var authority)
            ? ((capability == WorldCapability.Drive) ? authority.Drive : authority.Observe)
            : null);
    }
    private static bool SameAuthority(Dictionary<string, RoleAuthority> left, Dictionary<string, RoleAuthority> right) {
        if (left.Count != right.Count) {
            return false;
        }

        foreach (var (driver, authority) in right) {
            if (
                !left.TryGetValue(key: driver, value: out var prior) ||
                !prior.Drive.SequenceEqual(second: authority.Drive) ||
                !prior.Observe.SequenceEqual(second: authority.Observe) ||
                (prior.DriveBudget != authority.DriveBudget) ||
                (prior.ObserveBudget != authority.ObserveBudget)
            ) {
                return false;
            }
        }

        return true;
    }
    private void ApplyRoleMutation(WorldMutation mutation, WorldRole role, ulong tick) {
        if (!TryApplyMutation(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            engineTick: CompletedEngineTicks,
            mutation: mutation,
            preMetered: false,
            tick: tick
        ) && Output.HasNarrationSink) {
            Output.Narrate(channel: "world.companion", text: $"[world.companion: role '{role.Name}' could not apply {mutation.GetType().Name} for its visiting driver]");
        }
    }
}
