namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The roles section and every inhabit facet that holds a role: a role names its driver once (this world's own addon
    // row, or `accept` for a visitor's pinned driver), dresses arrivals in rows this document declares, and is followed
    // only from a local seat this world has.
    private static void ValidateRoles(WorldDefinition definition, HashSet<string> addonNames, HashSet<string> kitNames, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var lookNames = new HashSet<string>(collection: definition.Looks.Select(selector: static look => look.Name.Value),comparer: StringComparer.Ordinal);

        for (var index = 0; (index < definition.Roles.Count); index++) {
            var role = definition.Roles[index];
            var path = $"roles[{index}]";

            if (role is null || string.IsNullOrWhiteSpace(value: role.Name)) {
                errors.Add(item: $"{path} requires a name.");
                continue;
            }
            if (!names.Add(item: role.Name)) {
                errors.Add(item: $"{path} duplicates role '{role.Name}'.");
            }
            if (Puck.State.GeneratedName.IsReserved(name: role.Name)) {
                errors.Add(item: $"{path} name '{role.Name}' uses a generated-name spelling.");
            }
            if ((role.Driver is null) == !role.Accept) {
                errors.Add(item: $"{path} '{role.Name}' names exactly one of driver (this world's own addon) and accept (a visitor's own pinned driver).");
            }
            if ((role.Driver is { } driver) && !addonNames.Contains(item: driver)) {
                errors.Add(item: $"{path} '{role.Name}' driver '{driver}' names no addon row.");
            }
            if ((role.Kit is { } kit) && !kitNames.Contains(item: kit)) {
                errors.Add(item: $"{path} '{role.Name}' kit '{kit}' names no kit row.");
            }
            if ((role.Look is { } look) && !lookNames.Contains(item: look)) {
                errors.Add(item: $"{path} '{role.Name}' look '{look}' names no look row.");
            }
            if (role.Fuel > WorldAddonLimits.FuelPerAddon) {
                errors.Add(item: $"{path} '{role.Name}' fuel {role.Fuel} exceeds the maximum of {WorldAddonLimits.FuelPerAddon}.");
            }
            if (!double.IsFinite(d: role.FollowReach) || (role.FollowReach < 0) || (role.FollowReach > 64)) {
                errors.Add(item: $"{path} '{role.Name}' followReach {role.FollowReach} is outside 0..64 metres.");
            }
            if ((role.Drive == 0) || (role.Observe == 0)) {
                errors.Add(item: $"{path} '{role.Name}' drive and observe budgets must be at least 1.");
            }
        }

        foreach (var placement in definition.Placements) {
            if (placement.Inhabit is not { } inhabit) {
                continue;
            }

            var path = $"placements '{placement.Id}'.inhabit";

            if (inhabit.Role is { } roleName) {
                if (!names.Contains(item: roleName)) {
                    errors.Add(item: $"{path}.role '{roleName}' names no roles row.");
                }
                if (inhabit.Follows is not { } follows) {
                    errors.Add(item: $"{path}.role '{roleName}' requires follows, the local seat whose player it follows.");
                } else if ((follows < 0) || (follows >= definition.Population.LocalSeats)) {
                    errors.Add(item: $"{path}.follows {follows} is outside 0..{(definition.Population.LocalSeats - 1)}, this world's local seats.");
                }
                if (inhabit.ResolvedCount is not { Literal: 1, Row: null }) {
                    errors.Add(item: $"{path}.role '{roleName}' inhabits exactly one body: a companion is one body its home re-adopts by incarnation.");
                }
            } else if (inhabit.Follows is not null) {
                errors.Add(item: $"{path}.follows requires role.");
            }
        }
    }
}
