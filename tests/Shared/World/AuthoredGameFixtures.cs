using System.Text.Json.Nodes;

namespace Puck.World.Testing;

/// <summary>Loads the shipped composition once for import checks. Gameplay fixtures retain only the module's
/// state program, so unrelated creatures, rendering assets, and arcade games cannot inflate a rule test.</summary>
internal static partial class AuthoredGameFixtures {
    public static string Root { get; } = RepositoryPaths.RequireRoot();

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
