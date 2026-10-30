using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // The population revision and definition the role bindings were last reconciled against: neither moved, nothing a
    // role derives could have.
    private int m_rolesPopulationRevision = int.MinValue;
    private WorldDefinition? m_rolesDefinition;
    private readonly List<(WorldCapability Capability, int Body, ushort Budget)> m_roleDesired = [];

    /// <summary>Gets the requests a visiting driver mounts with: Drive and Observe over whatever this world grants it,
    /// which is the companions of its role and the players they follow, never more.</summary>
    public static IReadOnlyList<WorldCapabilityRequest> VisitingDriverRequests { get; } = [
        new(Capability: WorldCapability.Drive, Subject: GrantSubject.All),
        new(Capability: WorldCapability.Observe, Subject: GrantSubject.All),
    ];

    /// <summary>Reconciles every declared role with the companions that hold it here: the role's driver holds Drive
    /// and Observe over each companion of the role and Observe over the player each follows, and nothing else of the
    /// role's; an <c>accept</c> role mounts its visiting driver, by pin from the process content store, while a visitor
    /// of the role is present and unmounts it when none is. A function of the population and the document, run at one
    /// fixed point of the tick, so a replay derives the same grants and mounts.</summary>
    /// <param name="tick">The tick being stepped.</param>
    internal void ReconcileRoles(ulong tick) {
        var definition = Definition;

        if (
            ReferenceEquals(objA: definition, objB: m_rolesDefinition) &&
            (m_population.Revision == m_rolesPopulationRevision)
        ) {
            return;
        }

        m_rolesDefinition = definition;
        m_rolesPopulationRevision = m_population.Revision;

        foreach (var role in definition.Roles) {
            var principal = Principal.Addon(name: role.DriverAddonName);
            string? visitingDriver = null;

            m_roleDesired.Clear();
            for (var slot = 0; (slot < m_population.Capacity); slot++) {
                if (
                    (m_population.Companion(index: slot) is not { } binding) ||
                    !string.Equals(a: binding.Role, b: role.Name, comparisonType: StringComparison.Ordinal)
                ) {
                    continue;
                }

                AddDesired(capability: WorldCapability.Drive, body: slot, budget: role.Drive);
                AddDesired(capability: WorldCapability.Observe, body: slot, budget: role.Observe);
                if ((binding.Follows >= 0) && m_population.IsActive(index: binding.Follows)) {
                    AddDesired(capability: WorldCapability.Observe, body: binding.Follows, budget: role.Observe);
                }
                if (role.Accept && (m_population.InhabitantPlacementId(index: slot) is null)) {
                    visitingDriver ??= binding.Driver;
                }
            }

            // A role's driver holds body authority only through its role: every body hold it has that the role no
            // longer derives is released, whatever wrote it.
            foreach (var (capability, subject) in m_grants.Held(grantee: principal).ToArray()) {
                if (
                    (subject.Kind == GrantSubjectKind.Body) &&
                    (capability is WorldCapability.Drive or WorldCapability.Observe) &&
                    !m_roleDesired.Exists(match: desired => (desired.Capability == capability) && (desired.Body == subject.Value))
                ) {
                    _ = m_grants.Revoke(capability: capability, grantee: principal, subject: subject);
                }
            }
            foreach (var (capability, body, budget) in m_roleDesired) {
                var subject = GrantSubject.Body(index: body);

                if (m_grants.Holds(capability: capability, grantee: principal, subject: subject).Rule == GrantRule.ConcreteHold) {
                    continue;
                }
                if (!m_grants.TryGrant(
                    grant: new WorldGrant(Budget: budget, Capability: capability, Exclusive: false, Grantee: principal, Subject: subject),
                    reason: out var refusal
                ) && Output.HasNarrationSink) {
                    Output.Narrate(channel: "world.companion", text: $"[world.companion: role '{role.Name}' could not grant {principal.Describe()} {capability} over body:{body} — {refusal}]");
                }
            }

            if (role.Accept) {
                MountVisitingDriver(pin: visitingDriver, role: role, tick: tick);
            }
        }
    }
    private void AddDesired(WorldCapability capability, int body, ushort budget) {
        if (!m_roleDesired.Exists(match: desired => (desired.Capability == capability) && (desired.Body == body))) {
            m_roleDesired.Add(item: (capability, body, budget));
        }
    }
    // Mounts an accept role's visiting driver while a visitor of the role is here, by its pin from the content store,
    // and removes it once none is: the world's own program writing its own addons section.
    private void MountVisitingDriver(WorldRole role, string? pin, ulong tick) {
        var name = role.DriverAddonName;
        var existing = Definition.Addons.FirstOrDefault(predicate: addon => string.Equals(a: addon.Name, b: name, comparisonType: StringComparison.Ordinal));
        WorldMutation mutation;

        if (pin is null) {
            if (existing is null) {
                return;
            }
            mutation = new WorldMutation.RemoveAddon(Name: name, Principal: Principal.World);
        } else if ((existing is not null) && string.Equals(a: existing.Hash, b: pin, comparisonType: StringComparison.Ordinal)) {
            return;
        } else {
            mutation = new WorldMutation.UpsertAddon(
                Addon: new WorldAddonRow(
                    Enabled: true,
                    Fuel: role.Fuel,
                    Hash: pin,
                    ModulePath: string.Empty,
                    Name: name,
                    Requests: VisitingDriverRequests
                ),
                Principal: Principal.World
            );
        }

        if (!TryApplyMutation(
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            engineTick: CompletedEngineTicks,
            mutation: mutation,
            preMetered: false,
            tick: tick
        ) && Output.HasNarrationSink) {
            Output.Narrate(channel: "world.companion", text: $"[world.companion: role '{role.Name}' could not {((pin is null) ? "unmount" : $"mount visiting driver {pin}")}]");
        }
    }
}
