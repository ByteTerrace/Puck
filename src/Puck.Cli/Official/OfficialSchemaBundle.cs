using System.Text.Json.Nodes;

using Puck.Shaders;
using Puck.World;

namespace Puck.Cli.Official;

// Builds the worldSchemaBundle exactly the way `puck schema --bundle` does (WorldSchema.Export then
// WorldSchema.Bundle, never by shelling out), and reads the same x-puck identity block that call stamps into it
// back out — so official build's build.commit/build.generator/build.worldSchema agree with what `puck schema`
// itself reports, with no second hash-resolution path to drift from it.
//
// LoadPostRenderExtensions mirrors Puck.Cli.Schema.SchemaCommand's own scan over every src/*/Assets/Shaders
// manifest tree, which is a private implementation detail of that verb and not reachable from here; `puck schema
// --check` (run as a precondition of official build, before this runs) already proves that scan's result matches
// what is checked in, so this second, equally small scan cannot silently diverge from it.
internal static class OfficialSchemaBundle {
    public static (JsonObject Bundle, string Commit, string Generator, string WorldSchemaId) Build(string repositoryRoot) {
        var extensions = LoadPostRenderExtensions(repositoryRoot: repositoryRoot);
        var split = WorldSchema.Export(postRenderExtensions: extensions);
        var bundle = WorldSchema.Bundle(split: split);
        var identity = (bundle["x-puck"] as JsonObject)!;
        var commit = (identity["commit"]?.GetValue<string>() ?? "unknown");
        var generator = (identity["generator"]?.GetValue<string>() ?? WorldSchema.SchemaId);
        var worldSchemaId = (identity["schemaVersion"]?.GetValue<string>() ?? WorldSchema.SchemaId);

        return (bundle, commit, generator, worldSchemaId);
    }
    private static List<WorldSchema.PostRenderExtensionSchema> LoadPostRenderExtensions(string repositoryRoot) {
        var extensions = new List<WorldSchema.PostRenderExtensionSchema>();
        var seen = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var project in Directory.EnumerateDirectories(path: Path.Combine(path1: repositoryRoot, path2: "src")).Order(comparer: StringComparer.Ordinal)) {
            var catalog = ShaderSetCatalog.Scan(rootDirectory: Path.Combine(path1: project, path2: "Assets", path3: "Shaders"));

            foreach (var id in catalog.Ids) {
                if (!seen.TryAdd(key: id, value: project)) {
                    throw new InvalidDataException(message: $"Shader set '{id}' is shipped by both '{seen[id]}' and '{project}'.");
                }

                extensions.Add(item: new WorldSchema.PostRenderExtensionSchema(Id: id, ConfigSchema: catalog.Load(id: id).ConfigJsonSchema()));
            }
        }

        extensions.Sort(comparison: static (a, b) => string.CompareOrdinal(strA: a.Id, strB: b.Id));

        return extensions;
    }
}
