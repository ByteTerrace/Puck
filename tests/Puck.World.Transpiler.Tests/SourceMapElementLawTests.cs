using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A compiled document's source map names each element an author wrote at its own pointer, spanning the
/// construct that wrote it, so a trace or a refusal about one rule, placement, prototype or state row lands on that
/// construct rather than on the whole section.</summary>
public sealed class SourceMapElementLawTests {
    private static IEnumerable<(string Name, JsonObject Json, SourceMap Map)> Outputs(string relativePath) {
        var path = RepositoryPaths.Resolve(relativePath: relativePath);
        var compilation = WorldCompiler.Compile(
            allowMultiple: true,
            cancellationToken: TestContext.Current.CancellationToken,
            source: File.ReadAllText(path: path),
            sourcePath: path
        );

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport());

        return ((compilation.Worlds.Count > 0)
            ? compilation.Worlds.Select(selector: static world => (world.Name, world.Json, world.SourceMap))
            : [("(document)", compilation.Json!, compilation.SourceMap)]
        );
    }
    private static IEnumerable<string> Unmapped(JsonObject json, SourceMap map, IEnumerable<string> arrays) {
        var entries = map.Snapshot();

        foreach (var pointer in arrays) {
            var node = pointer.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '/').Aggregate(
                func: static (current, segment) => (current as JsonObject)?[segment],
                seed: ((JsonNode?)json)
            );

            if (node is not JsonArray elements) {
                continue;
            }

            for (var index = 0; (index < elements.Count); ++index) {
                var element = $"{pointer}/{index}";

                if (!entries.TryGetValue(key: element, value: out var origin) || (origin.Span.Line <= 0)) {
                    yield return element;
                }
            }
        }
    }

    public static TheoryData<string> WorldSources() {
        var data = new TheoryData<string>();

        foreach (var tree in new[] { "worlds", "src/Puck.World/Assets/worlds", "src/Puck.World.Transpiler/Samples" }) {
            foreach (var path in Directory.EnumerateFiles(path: RepositoryPaths.Resolve(relativePath: tree), searchOption: SearchOption.AllDirectories, searchPattern: "*.puck")) {
                var source = File.ReadAllText(path: path);

                // A module source compiles to no rules of its own; its rules are mapped where a root instantiates it.
                if (source.Contains(comparisonType: StringComparison.Ordinal, value: "schema:")) {
                    data.Add(row: Path.GetRelativePath(path: path, relativeTo: RepositoryPaths.RequireRoot()).Replace(newChar: '/', oldChar: '\\'));
                }
            }
        }

        return data;
    }
    public static TheoryData<string> Samples() => [.. Directory.EnumerateFiles(
        path: RepositoryPaths.Resolve(relativePath: "src/Puck.World.Transpiler/Samples"),
        searchOption: SearchOption.AllDirectories,
        searchPattern: "*.puck"
    ).Select(selector: static path => Path.GetRelativePath(path: path, relativeTo: RepositoryPaths.RequireRoot()).Replace(newChar: '/', oldChar: '\\'))];
    [MemberData(nameof(WorldSources))]
    [Theory]
    public void EveryRuleIsMappedToTheConstructThatAuthoredIt(string relativePath) {
        foreach (var (name, json, map) in Outputs(relativePath: relativePath)) {
            Assert.Empty(collection: Unmapped(arrays: ["/rules"], json: json, map: map).Select(selector: pointer => $"{name}: {pointer}"));
        }
    }
    [MemberData(nameof(Samples))]
    [Theory]
    public void EveryAuthoredElementOfASampleIsMappedToItsConstruct(string relativePath) {
        foreach (var (name, json, map) in Outputs(relativePath: relativePath)) {
            string[] arrays = [
                "/rules", "/prototypes", "/placements/rows", "/decisions", "/grants", "/channels", "/materials", "/addons",
                "/ruleGroups", "/patterns", "/sets", "/spawnPoints",
                .. ((json["state"] as JsonObject)?.Select(selector: static member => $"/state/{member.Key}") ?? []),
            ];

            Assert.Empty(collection: Unmapped(arrays: arrays, json: json, map: map).Select(selector: pointer => $"{name}: {pointer}"));
        }
    }
}
