using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Loads the shipped composition once for import checks. Gameplay fixtures retain only the module's
/// state program, so unrelated creatures, rendering assets, and arcade games cannot inflate a rule test.</summary>
internal static class AuthoredGameFixtures {
    public static string Root { get; } = FindRoot();

    private static readonly Lazy<WorldDefinition> NexusSource = new(valueFactory: () => Load(relativePath: "src/Puck.World/Assets/worlds/puck.world.json"));

    public static WorldDefinition Nexus => NexusSource.Value;

    private static string FindRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (
            (directory is not null) &&
            !File.Exists(path: Path.Combine(
            path1: directory.FullName,
            path2: "Puck.slnx"
        ))
        ) { directory = directory.Parent; }
        Assert.NotNull(@object: directory);
        return directory.FullName;
    }

    public static WorldDefinition Load(string relativePath, Puck.Abstractions.Machines.IMachineValidationCatalog? catalog = null) {
        var path = Path.Combine(
            path1: Root,
            path2: relativePath
        );
        // The island proves its seams against the shard documents beside it, read the way the host reads them.
        // A supplied catalog also relocates imported machine assets through the real content-provider metadata.
        var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => (Path.GetDirectoryName(path: path) ?? Root));

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path,
                out var definition,
                out var reason,
                neighbours: neighbours,
                catalog: (catalog ?? TestHookInstaller.CreateMachineCatalog())
            ),
            userMessage: reason
        );
        return definition!;
    }
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
    public static WorldDefinition Program(string module) {
        var source = JsonNode.Parse(File.ReadAllText(path: Path.Combine(
            path1: Root,
            path2: $"src/Puck.World/Assets/worlds/games/{module}.world.json"
        )))!;
        var host = JsonNode.Parse(Fixtures.DefaultWorldBytes())!.AsObject();

        foreach (var field in new[] { "state", "rules", "patterns", "tables", "search" }) {
            if (source[field] is { } value) { host[field] = value.DeepClone(); }
        }
        return WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: host.ToJsonString()));
    }
}
