using Puck.Assets;

namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    /// <summary>Returns why this world's role policy refuses a crossing companion, or <see langword="null"/> when it takes
    /// it. A world always takes its own companion home. Otherwise it takes a companion only for a role it declares: a role
    /// its own addon drives, or an <c>accept</c> role whose visiting driver is the companion's preferred pin, held in the
    /// process content store and matching it, with no other visiting driver already running for the role.</summary>
    /// <param name="member">The arriving traveller.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? CompanionRefusal(in WorldTransferReservationMember member) {
        if (member.Companion is not { } companion) {
            return null;
        }
        if ((member.Mobility is { } mobility) && m_server.Population.TryFindAway(incarnation: mobility.Incarnation, slot: out _)) {
            return null;
        }

        var definition = m_server.Definition;
        var world = (definition.Metadata?.Title ?? m_server.AuthorityIdentity);

        if (definition.FindRole(name: companion.Role) is not { } role) {
            return $"'{world}' declares no '{companion.Role}' role, so it takes no companion of that role";
        }
        if (!role.Accept) {
            return null;
        }
        if (companion.Driver is not { } driver) {
            return $"'{world}' runs a visitor's own driver for '{companion.Role}', and this companion prefers none";
        }
        if (!VisitingDriverResolves(pin: driver)) {
            return $"'{world}' runs a visitor's own driver for '{companion.Role}', and {driver} is not in its content store or does not match its pin";
        }

        foreach (var addon in definition.Addons) {
            if (
                string.Equals(a: addon.Name, b: role.DriverAddonName, comparisonType: StringComparison.Ordinal) &&
                !string.Equals(a: addon.Hash, b: driver, comparisonType: StringComparison.Ordinal)
            ) {
                return $"'{world}' already runs visiting driver {addon.Hash} for '{companion.Role}'; one visiting driver runs per role";
            }
        }
        for (var index = 0; (index < m_server.Population.Capacity); index++) {
            if (
                (m_server.Population.Companion(index: index) is { Driver: { } present } binding) &&
                string.Equals(a: binding.Role, b: companion.Role, comparisonType: StringComparison.Ordinal) &&
                (m_server.Population.InhabitantPlacementId(index: index) is null) &&
                !string.Equals(a: present, b: driver, comparisonType: StringComparison.Ordinal)
            ) {
                return $"'{world}' already runs visiting driver {present} for '{companion.Role}'; one visiting driver runs per role";
            }
        }

        return null;
    }
    /// <summary>Returns whether a visiting driver's pin names module bytes the process content store holds and that hash
    /// to that pin: the verification an <c>accept</c> world asks before it mounts code it never authored.</summary>
    /// <param name="pin">The driver's pin text.</param>
    /// <returns><see langword="true"/> when the bytes are held and match.</returns>
    public static bool VisitingDriverResolves(string pin) => (
        ContentPin.TryParse(text: pin, pin: out var parsed) &&
        WorldProjectionContent.Shared.TryGet(content: out var bytes, pin: parsed) &&
        (ContentPin.Compute(content: bytes) == parsed)
    );

    // Binds a landed companion: home re-adopts its own into the placement it left, and any other world dresses it in its
    // role's kit and look and resolves the player it follows from the incarnation that player travels under.
    private void BindArrivingCompanion(int slot, in WorldTransferCompanion companion, in WorldMobilityIdentity mobility, WorldTransferArrival arrival) {
        var definition = m_server.Definition;

        if (m_server.Population.TryReadoptAway(definition: definition, incarnation: mobility.Incarnation, slot: slot)) {
            if (arrival != WorldTransferArrival.Mapped) {
                m_server.Population.PoseAtPlacement(definition: definition, slot: slot);
            }
            return;
        }

        var role = definition.FindRole(name: companion.Role);

        m_server.Population.BindCompanion(
            binding: new WorldCompanionBinding(
                Driver: companion.Driver,
                Follows: ((companion.Follows is { } follows) ? m_server.Population.FindIncarnation(incarnation: follows) : -1),
                Home: companion.Home,
                Role: companion.Role
            ),
            kit: role?.Kit,
            look: role?.Look,
            slot: slot
        );
    }
}
