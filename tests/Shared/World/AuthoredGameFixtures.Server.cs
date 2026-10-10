
namespace Puck.World.Testing;

internal static partial class AuthoredGameFixtures {
    public static Puck.World.Server.WorldPopulation PopulationForIdentityChecks(WorldDefinition definition) {
        // Identity and seating depend on the full placement order, kits, and population policy. Navigation's
        // large static collision grids are unrelated to these checks. Retain each named domain for kit
        // compilation, with one cell; no simulation tick is run here.
        var population = new Puck.World.Server.WorldPopulation(definition: definition with {
            NavigationRaw = new WorldNavigationSection(Domains: [.. definition.Navigation.Rows.Select(selector: domain => domain with { Width = 1, Depth = 1, Layers = 1 })]),
        });

        population.ReconcileInhabitants(definition);
        return population;
    }
}
