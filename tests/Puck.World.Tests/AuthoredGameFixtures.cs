using System.Text.Json.Nodes;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Loads the shipped composition once for import checks. Gameplay fixtures retain only the module's
/// state program, so unrelated creatures, rendering assets, and arcade games cannot inflate a rule test.</summary>
internal static class AuthoredGameFixtures {
    public static string Root { get; } = RepositoryPaths.RequireRoot();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<WorldDefinition>> Loaded = new(comparer: StringComparer.Ordinal);

    public static WorldDefinition Nexus => Load(relativePath: "src/Puck.World/Assets/worlds/puck.world.json");

    /// <summary>Returns the world document at <paramref name="relativePath"/>, compiled when it is a <c>.puck</c> source
    /// and loaded, composed, and validated the way the host boots it. A load under the default catalog runs once per
    /// suite; the definition is shared, so a caller derives from it with <c>with</c> and never mutates it.</summary>
    /// <param name="relativePath">The forward-slashed path relative to the repository root.</param>
    /// <param name="catalog">The machine catalog to validate against, or <see langword="null"/> for the test catalog.</param>
    /// <returns>The validated definition.</returns>
    public static WorldDefinition Load(string relativePath, Puck.Abstractions.Machines.IMachineValidationCatalog? catalog = null) => ((catalog is null)
        ? Loaded.GetOrAdd(
            key: relativePath,
            valueFactory: static path => new Lazy<WorldDefinition>(valueFactory: () => LoadUncached(
                catalog: null,
                relativePath: path
            ))
        ).Value
        : LoadUncached(
            catalog: catalog,
            relativePath: relativePath
        ));

    private static WorldDefinition LoadUncached(string relativePath, Puck.Abstractions.Machines.IMachineValidationCatalog? catalog) {
        var path = Path.Combine(
            path1: Root,
            path2: relativePath
        );

        // The document the path carries, compiled when it is a .puck source, composed through the composer the
        // host's .puck boot uses, so a basis or import naming a .puck-sourced document resolves to that source.
        var rootBytes = Puck.Testing.ShippedWorldDocuments.Read(path: path);
        var puckCatalog = (catalog ?? TestHookInstaller.CreateMachineCatalog());

        Assert.True(
            condition: PuckDocumentComposer.TryComposeWorldDocument(
                catalog: puckCatalog,
                rootBytes: rootBytes,
                rootResolvedPath: path,
                chainBytes: out _,
                composed: out var composed,
                reason: out var composeReason
            ),
            userMessage: composeReason
        );

        // The island proves its seams against the shard documents beside it, read the way the host reads them.
        var neighbours = new WorldFileNeighbourResolver(
            baseDirectory: () => (Path.GetDirectoryName(path: path) ?? Root)
        );

        Assert.True(
            condition: WorldDefinitionLoader.TryLoad(
                catalog: puckCatalog,
                definition: out var definition,
                instanceIdentity: WorldDefinitionLoader.BootInstanceName,
                neighbours: neighbours,
                reason: out var reason,
                sourceName: path,
                utf8: ((composed is null)
                    ? rootBytes
                    : System.Text.Encoding.UTF8.GetBytes(s: composed.ToJsonString()))
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
        var source = JsonNode.Parse(utf8Json: Puck.Testing.ShippedWorldDocuments.Read(path: Path.Combine(
            path1: Root,
            path2: $"src/Puck.World/Assets/worlds/games/{module}.puck"
        )))!;
        var host = JsonNode.Parse(Fixtures.DefaultWorldBytes())!.AsObject();

        foreach (var field in new[] { "state", "rules", "patterns", "tables", "search" }) {
            if (source[field] is { } value) { host[field] = value.DeepClone(); }
        }
        return WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: host.ToJsonString()));
    }
}
