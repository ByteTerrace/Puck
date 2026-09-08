using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Loads the shipped composition once for import checks. Gameplay fixtures retain only the module's
/// state program, so unrelated creatures, rendering assets, and arcade games cannot inflate a rule test.</summary>
internal static class AuthoredGameFixtures {
    public static string Root { get; } = FindRoot();
    private static readonly Lazy<WorldDefinition> NexusSource = new(() => Load("src/Puck.World/Assets/worlds/puck.world.json"));
    public static WorldDefinition Nexus => NexusSource.Value;

    private static string FindRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) { directory = directory.Parent; }
        Assert.NotNull(directory);
        return directory.FullName;
    }

    public static WorldDefinition Load(string relativePath) {
        var path = Path.Combine(Root, relativePath);
        // The island proves its seams against the shard documents beside it, read the way the host reads them.
        var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => (Path.GetDirectoryName(path: path) ?? Root));

        Assert.True(WorldDefinitionLoader.TryLoadFile(path, out var definition, out var reason, neighbours: neighbours), reason);
        return definition!;
    }

    public static Puck.World.Server.WorldPopulation PopulationForIdentityChecks(WorldDefinition definition) {
        // Identity and seating depend on the full placement order, kits, and population policy. Navigation's
        // large static collision grids are unrelated to these checks. Retain each named domain for kit
        // compilation, with one cell; no simulation tick is run here.
        var population = new Puck.World.Server.WorldPopulation(definition with {
            NavigationRaw = new WorldNavigationSection([.. definition.Navigation.Rows.Select(domain => domain with { Width = 1, Depth = 1, Layers = 1 })]),
        });
        population.ReconcileInhabitants(definition);
        return population;
    }

    public static WorldDefinition Program(string module) {
        var source = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, $"src/Puck.World/Assets/worlds/games/{module}.world.json")))!;
        var host = JsonNode.Parse(Fixtures.DefaultWorldBytes())!.AsObject();
        foreach (var field in new[] { "state", "rules", "patterns", "tables", "search" }) {
            if (source[field] is { } value) { host[field] = value.DeepClone(); }
        }
        return WorldDefinitionSerialization.Deserialize(System.Text.Encoding.UTF8.GetBytes(host.ToJsonString()));
    }
}
