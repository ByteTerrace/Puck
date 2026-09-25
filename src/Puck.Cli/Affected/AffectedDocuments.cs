using Puck.Shaders;
using Puck.World;

namespace Puck.Cli.Affected;

/// <summary>
/// The documents a canary reaches through its manifest, read with the documents' own readers: a world document reaches
/// every layer it composes (<see cref="WorldDefinitionFileSource.TryDescribeComposition"/>) and, from its composed and
/// parsed document (<see cref="WorldDefinitionFileSource.TryParseDocument"/>, unvalidated), the neighbour worlds its
/// adjacencies name and the graph documents its <c>views.pipelines</c> and <c>views.graphs</c> rows name, each
/// resolved as the host resolves it (<see cref="WorldDocumentPaths.TryResolve"/>); a graph document reaches every shader
/// pass source it declares (<see cref="ShaderPipelineLoader.ReadDefinition"/>, resolved beside the document as the
/// loader resolves it). A document a reader refuses reaches nothing beyond itself.
/// </summary>
internal static class AffectedDocuments {
    private const string GraphSuffix = ".graph.json";
    private const string WorldSuffix = ".world.json";

    /// <summary>The shader pass sources a graph document declares, as full paths.</summary>
    /// <param name="graphPath">The graph document's full path.</param>
    /// <returns>The pass sources, or none when the reader refuses the document.</returns>
    internal static IReadOnlyList<string> PassSources(string graphPath) {
        try {
            var directory = Path.GetDirectoryName(path: graphPath)!;

            return [.. ShaderPipelineLoader.ReadDefinition(name: Path.GetFileName(path: graphPath), path: graphPath).ShaderPasses
                .Select(selector: pass => Path.GetFullPath(path: pass.Source, basePath: directory))];
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or ShaderPipelineCompilationException)) {
            return [];
        }
    }
    /// <summary>Every file a document reaches, itself included, as full paths.</summary>
    /// <param name="path">A world or graph document's full path.</param>
    /// <returns>The reached files.</returns>
    internal static IReadOnlySet<string> Reach(string path) {
        var reached = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();

        void Enqueue(string file) {
            var full = Path.GetFullPath(path: file);

            if (
                File.Exists(path: full) &&
                reached.Add(item: full)
            ) {
                pending.Enqueue(item: full);
            }
        }

        Enqueue(file: path);

        while (pending.TryDequeue(result: out var file)) {
            if (file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: GraphSuffix)) {
                foreach (var source in PassSources(graphPath: file)) {
                    Enqueue(file: source);
                }

                continue;
            }
            if (!file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: WorldSuffix)) {
                continue;
            }

            if (WorldDefinitionFileSource.TryDescribeComposition(layers: out var layers, path: file, reason: out _)) {
                foreach (var layer in layers) {
                    Enqueue(file: layer.Path);
                }
            }
            // Composed and parsed, not validated: a reach needs the rows a document names, and validating an adjacency or
            // an extension would need the host's resolvers, which a canary's document is not refused for lacking here.
            if (
                !WorldDefinitionFileSource.TryComposeDocumentTree(path: file, reason: out _, tree: out var tree) ||
                !WorldDefinitionFileSource.TryParseDocument(definition: out var definition, json: tree!.ToJsonString(), reason: out _, sourceName: file)
            ) {
                continue;
            }

            var directory = WorldDocumentPaths.DirectoryOf(documentPath: file);

            foreach (var source in definition!.Views.Pipelines.Select(selector: static row => row.Source).Concat(second: (definition.Views.Graphs ?? []).Select(selector: static row => row.Source).OfType<string>())) {
                if (WorldDocumentPaths.TryResolve(
                    documentDirectory: directory,
                    path: source,
                    reason: out _,
                    resolved: out var resolved
                )) {
                    Enqueue(file: resolved);
                }
            }
            foreach (var adjacency in (definition.Adjacencies ?? [])) {
                if (WorldDefinitionFileSource.TryResolveDocumentIn(
                    directory: WorldDocumentPaths.DirectoryOf(documentPath: file),
                    documentPath: out var neighbourDocument,
                    name: adjacency.Destination,
                    reason: out _,
                    sourcePath: out var neighbourSource
                )) {
                    Enqueue(file: neighbourDocument);
                    Enqueue(file: neighbourSource);
                }
            }
        }

        return reached;
    }
    /// <summary>Maps each file the canaries' manifests reach to the canaries that reach it.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="canaries">Every canary.</param>
    /// <returns>Each reached file, repository-relative with forward slashes, with the ids of the canaries reaching
    /// it.</returns>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> ReachedBy(string repositoryRoot, IReadOnlyList<AffectedCanary> canaries) {
        var reachedBy = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
        var memo = new Dictionary<string, IReadOnlySet<string>>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var canary in canaries) {
            foreach (var root in canary.Files.Where(predicate: static file => (file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: WorldSuffix) || file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: GraphSuffix)))) {
                var full = Path.GetFullPath(path: Path.Combine(path1: repositoryRoot, path2: root));

                if (!memo.TryGetValue(key: full, value: out var reached)) {
                    reached = Reach(path: full);
                    memo[full] = reached;
                }

                foreach (var file in reached) {
                    var relative = Path.GetRelativePath(path: file, relativeTo: repositoryRoot).Replace(newChar: '/', oldChar: '\\');

                    if (relative.StartsWith(comparisonType: StringComparison.Ordinal, value: "..")) {
                        continue;
                    }
                    if (!reachedBy.TryGetValue(key: relative, value: out var ids)) {
                        ids = new HashSet<string>(comparer: StringComparer.Ordinal);
                        reachedBy[relative] = ids;
                    }

                    _ = ids.Add(item: canary.Id);
                }
            }
        }

        return reachedBy.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static pair => ((IReadOnlySet<string>)pair.Value),
            keySelector: static pair => pair.Key
        );
    }
}
