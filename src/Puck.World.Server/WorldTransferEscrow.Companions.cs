using Puck.Assets;

namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    /// <summary>Returns why this world refuses a traveller on its companion terms, or <see langword="null"/> when it
    /// takes it. No traveller claims an away companion of this world without the custody token this world issued when
    /// it left; with the token, home always takes its own back. Any other companion is taken only for a role this world
    /// declares: a role its own addon drives, or an <c>accept</c> role whose visiting driver is the companion's preferred
    /// pin, held in the process content store and matching it, within this world's addon budget once every visiting
    /// pin present, held by a pending reservation and carried by this request has its own driver.</summary>
    /// <param name="request">The reservation the traveller belongs to.</param>
    /// <param name="member">The arriving traveller.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? CompanionRefusal(WorldTransferReservationRequest request, in WorldTransferReservationMember member) {
        ArgumentNullException.ThrowIfNull(argument: request);

        var world = (m_server.Definition.Metadata?.Title ?? m_server.AuthorityIdentity);

        if ((member.Mobility is { } mobility) && m_server.Population.IsAwayIncarnation(incarnation: mobility.Incarnation)) {
            return (m_server.Population.TryFindAway(custody: member.Companion?.Custody, incarnation: mobility.Incarnation, slot: out _)
                ? null
                : $"'{world}' holds the home of {mobility.Incarnation}, and this traveller does not carry the custody token '{world}' issued it");
        }
        if (member.Companion is not { } companion) {
            return null;
        }
        if (m_server.Definition.FindRole(name: companion.Role) is not { } role) {
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

        return VisitingDriverBudgetRefusal(request: request, world: world);
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

    // Refuses a request whose visiting drivers this world could not mount: every distinct visiting pin that a companion
    // here prefers, that a pending reservation holds, and that this request carries mounts a driver of its own, and
    // together with the authored addons they must fit the document's addon count and fuel ceilings.
    private string? VisitingDriverBudgetRefusal(WorldTransferReservationRequest request, string world) {
        var definition = m_server.Definition;
        var drivers = new HashSet<string>(comparer: StringComparer.Ordinal);

        void Count(string role, string? pin) {
            if ((pin is not null) && (definition.FindRole(name: role) is { Accept: true } accept)) {
                _ = drivers.Add(item: WorldRole.VisitorAddonName(pin: pin, role: accept.Name));
            }
        }

        for (var index = 0; (index < m_server.Population.Capacity); index++) {
            if ((m_server.Population.Companion(index: index) is { } present) && (m_server.Population.InhabitantPlacementId(index: index) is null)) {
                Count(pin: present.Driver, role: present.Role);
            }
        }
        foreach (var lease in m_leases.Values) {
            foreach (var held in lease.Request.Members) {
                if (held.Companion is { } pending) {
                    Count(pin: pending.Driver, role: pending.Role);
                }
            }
        }
        foreach (var carried in request.Members) {
            if (carried.Companion is { } arriving) {
                Count(pin: arriving.Driver, role: arriving.Role);
            }
        }

        var authored = definition.Addons.Where(predicate: addon => !drivers.Contains(item: addon.Name) && !definition.Roles.Any(predicate: role => WorldRole.IsVisitorAddonName(addonName: addon.Name, role: role.Name))).ToArray();
        var fuel = authored.Aggregate(seed: 0UL, func: static (sum, addon) => (sum + ((addon.Fuel == 0) ? WorldAddonLimits.DefaultFuel : addon.Fuel)));

        foreach (var name in drivers) {
            var role = definition.Roles.First(predicate: candidate => WorldRole.IsVisitorAddonName(addonName: name, role: candidate.Name));

            fuel += ((role.Fuel == 0) ? WorldAddonLimits.DefaultFuel : role.Fuel);
        }

        return (((authored.Length + drivers.Count) > WorldAddonLimits.Addons) || (fuel > WorldAddonLimits.FuelPerDocument))
            ? $"'{world}' cannot run {drivers.Count} visiting driver(s) beside its own addons within its addon count and fuel ceilings"
            : null;
    }
    // Binds a landed companion: home re-adopts its own into the placement it left, against the custody token it carries,
    // and any other world dresses it in its role's kit and look. Its player binds only when this world can verify it:
    // the incarnation it names is a traveller of this same request, or one this same source authority already landed
    // here and that is still here; any other claim leaves it following no one, which sends it home.
    private void BindArrivingCompanion(WorldCrossingArrival arrival, int index, in WorldTransferCompanion companion, in WorldMobilityIdentity mobility) {
        var definition = m_server.Definition;
        var slot = arrival.Slots[index];

        if (m_server.Population.TryReadoptAway(custody: companion.Custody, definition: definition, incarnation: mobility.Incarnation, slot: slot)) {
            if (arrival.Members[index].Arrival != WorldTransferArrival.Mapped) {
                m_server.Population.PoseAtPlacement(definition: definition, slot: slot);
            }
            return;
        }

        var followed = -1;

        if (companion.Follows is { } claimed) {
            var inCohort = false;

            for (var other = 0; (other < arrival.Request.Members.Count); other++) {
                if ((other != index) && (arrival.Request.Members[other].Mobility?.Incarnation == claimed)) {
                    inCohort = true;
                }
            }
            // The source's own delivery of that player is its mobility credential here: the admission row this source
            // holds for the incarnation, at the epoch the body holding it now carries.
            if (
                (m_server.Population.FindIncarnation(incarnation: claimed) is var at and >= 0) &&
                (inCohort ||
                (m_mobilityAdmissions.TryGetValue(key: (arrival.Request.SourceAuthority, claimed), value: out var delivered) &&
                (m_server.Population.MobilityEpoch(index: at) == delivered.Epoch)))
            ) {
                followed = at;
            }
        }

        var role = definition.FindRole(name: companion.Role);

        m_server.Population.BindCompanion(
            binding: new WorldCompanionBinding(
                Custody: companion.Custody,
                Driver: companion.Driver,
                Follows: followed,
                FollowsIncarnation: ((followed >= 0) ? companion.Follows : null),
                Home: companion.Home,
                Role: companion.Role
            ),
            kit: role?.Kit,
            look: role?.Look,
            slot: slot
        );
    }
}
