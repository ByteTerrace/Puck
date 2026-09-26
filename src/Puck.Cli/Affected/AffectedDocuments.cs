using System.Text;
using Puck.Shaders;
using Puck.World;

namespace Puck.Cli.Affected;

/// <summary>
/// The documents a canary reaches through its manifest, read with the documents' own readers over one
/// <see cref="IAffectedTree"/>, the working tree or the tree a revision recorded, and never the file system: a world
/// document reaches every layer it composes (<see cref="WorldDefinitionFileSource.TryDescribeComposition"/>) and, from its
/// composed and parsed document (<see cref="WorldDefinitionFileSource.TryParseDocument"/>, unvalidated), the neighbour
/// worlds its adjacencies name and the graph documents its <c>views.graphs</c> rows name, each resolved as the host
/// resolves it (<see cref="WorldDocumentPaths.TryResolve"/>), its basis and imports through the tree's own document
/// source (<see cref="IAffectedTree.Documents"/>), and the files of each post-process package its <c>views.post</c> rows
/// name (<see cref="AffectedShaders.FilesOf"/>); a graph document reaches every shader pass source it declares
/// (<see cref="ShaderPipelineLoader.ParseDefinition"/>, resolved beside the document as the loader resolves it) and every
/// include each of those sources reaches (<see cref="ShaderSourceClosure.Collect"/>). A document a reader refuses reaches
/// nothing beyond itself, and a file the tree does not hold is not reached. Paths are repository-relative with forward
/// slashes.
/// </summary>
internal static class AffectedDocuments {
    private const string GraphSuffix = ".graph.json";
    private const string WorldSuffix = ".world.json";

    /// <summary>The shader pass sources a graph document declares.</summary>
    /// <param name="tree">The tree the document is read from.</param>
    /// <param name="graph">The graph document, repository-relative.</param>
    /// <returns>The pass sources, repository-relative, or none when the tree does not hold the document or the reader
    /// refuses it.</returns>
    internal static IReadOnlyList<string> PassSources(IAffectedTree tree, string graph) {
        if (tree.ReadText(path: graph) is not { } text) {
            return [];
        }

        try {
            var full = tree.Full(path: graph);
            var directory = Path.GetDirectoryName(path: full)!;

            return [.. ShaderPipelineLoader.ParseDefinition(name: Path.GetFileName(path: full), path: full, text: text).ShaderPasses
                .Select(selector: pass => tree.Relative(full: Path.GetFullPath(path: pass.Source, basePath: directory)))
                .OfType<string>()];
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or ShaderPipelineCompilationException)) {
            return [];
        }
    }
    /// <summary>The files a shader source's include closure reaches (<see cref="ShaderSourceClosure.Collect"/>), every
    /// include read through the tree.</summary>
    /// <param name="tree">The tree the source and its includes are read from.</param>
    /// <param name="source">The shader source, repository-relative.</param>
    /// <returns>The includes, repository-relative, or none when the tree does not hold the source or the closure refuses
    /// it.</returns>
    internal static IReadOnlyList<string> Includes(IAffectedTree tree, string source) {
        if (tree.ReadText(path: source) is not { } text) {
            return [];
        }

        try {
            var closure = ShaderSourceClosure.Collect(
                limits: ShaderSourceLimits.Default,
                readInclude: full => ((tree.Relative(full: full) is { } include)
                    ? tree.ReadText(path: include)
                    : null),
                sources: [(tree.Full(path: source), text)]
            );

            return [.. closure.Includes.Select(selector: include => tree.Relative(full: include.Path)).OfType<string>()];
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ShaderClosureRefusedException)) {
            return [];
        }
    }
    /// <summary>Every file a document reaches in a tree, itself included.</summary>
    /// <param name="tree">The tree every document and source is read from.</param>
    /// <param name="path">A world or graph document, repository-relative.</param>
    /// <param name="packageFiles">The files of the post-process package an id names (<see cref="AffectedShaders.FilesOf"/>),
    /// which a world reaches through each <c>views.post</c> row, or <see langword="null"/> to reach no package.</param>
    /// <returns>The reached files the tree holds, repository-relative.</returns>
    internal static IReadOnlySet<string> Reach(IAffectedTree tree, string path, Func<string, IReadOnlyList<string>>? packageFiles = null) {
        var reached = new HashSet<string>(comparer: StringComparer.Ordinal);
        var pending = new Queue<string>();

        void Enqueue(string? file) {
            if (
                (file is not null) &&
                !reached.Contains(item: file) &&
                tree.Contains(path: file)
            ) {
                _ = reached.Add(item: file);
                pending.Enqueue(item: file);
            }
        }
        void EnqueueFull(string full) => Enqueue(file: tree.Relative(full: full));

        Enqueue(file: path);

        while (pending.TryDequeue(result: out var file)) {
            if (file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: GraphSuffix)) {
                foreach (var source in PassSources(graph: file, tree: tree)) {
                    Enqueue(file: source);

                    foreach (var include in Includes(source: source, tree: tree)) {
                        Enqueue(file: include);
                    }
                }

                continue;
            }
            if (
                !file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: WorldSuffix) ||
                (tree.ReadText(path: file) is not { } text)
            ) {
                continue;
            }

            var full = tree.Full(path: file);
            var content = Encoding.UTF8.GetBytes(s: text);
            var documents = tree.Documents;

            if (WorldDefinitionFileSource.TryDescribeComposition(content: content, documents: documents, layers: out var layers, path: full, reason: out _)) {
                foreach (var layer in layers) {
                    EnqueueFull(full: layer.Path);
                }
            }
            // Composed and parsed, not validated: a reach needs the rows a document names, and validating an adjacency or
            // an extension would need the host's resolvers, which a canary's document is not refused for lacking here.
            if (
                !WorldDefinitionFileSource.TryComposeDocumentTree(content: content, documents: documents, path: full, reason: out _, tree: out var composed) ||
                !WorldDefinitionFileSource.TryParseDocument(definition: out var definition, json: composed!.ToJsonString(), reason: out _, sourceName: full)
            ) {
                continue;
            }

            var directory = WorldDocumentPaths.DirectoryOf(documentPath: full);

            foreach (var source in (definition!.Views.Graphs ?? []).Select(selector: static row => row.Source).OfType<string>()) {
                if (WorldDocumentPaths.TryResolve(
                    documentDirectory: directory,
                    path: source,
                    reason: out _,
                    resolved: out var resolved
                )) {
                    EnqueueFull(full: resolved);
                }
            }
            foreach (var pass in (definition.Views.Post ?? [])) {
                foreach (var packageFile in (packageFiles?.Invoke(arg: pass.Package) ?? [])) {
                    Enqueue(file: packageFile);
                }
            }
            foreach (var adjacency in (definition.Adjacencies ?? [])) {
                if (WorldDefinitionFileSource.TryResolveDocumentIn(
                    directory: directory,
                    documentPath: out var neighbourDocument,
                    name: adjacency.Destination,
                    reason: out _,
                    sourcePath: out var neighbourSource
                )) {
                    EnqueueFull(full: neighbourDocument);
                    EnqueueFull(full: neighbourSource);
                }
            }
        }

        return reached;
    }
    /// <summary>Maps each file the canaries' manifests reach in a tree to the canaries that reach it.</summary>
    /// <param name="tree">The tree the canaries' documents are read from: the working tree, or the tree the base
    /// recorded, which places a file deleted since then through the documents that named it there.</param>
    /// <param name="canaries">Every canary. Only a canary that exists now can run, so the canaries are the working tree's
    /// even when their documents are read from the base's tree.</param>
    /// <param name="packageFiles">The files of the post-process package an id names in the same tree, which a world
    /// reaches through its <c>views.post</c> rows, or <see langword="null"/> to reach no package.</param>
    /// <returns>Each reached file, repository-relative with forward slashes, with the ids of the canaries reaching
    /// it.</returns>
    internal static IReadOnlyDictionary<string, IReadOnlySet<string>> ReachedBy(IAffectedTree tree, IReadOnlyList<AffectedCanary> canaries, Func<string, IReadOnlyList<string>>? packageFiles = null) {
        var reachedBy = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
        var memo = new Dictionary<string, IReadOnlySet<string>>(comparer: StringComparer.Ordinal);

        foreach (var canary in canaries) {
            foreach (var root in canary.Files.Where(predicate: static file => (file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: WorldSuffix) || file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: GraphSuffix)))) {
                if (!memo.TryGetValue(key: root, value: out var reached)) {
                    reached = Reach(packageFiles: packageFiles, path: root, tree: tree);
                    memo[root] = reached;
                }

                foreach (var file in reached) {
                    if (!reachedBy.TryGetValue(key: file, value: out var ids)) {
                        ids = new HashSet<string>(comparer: StringComparer.Ordinal);
                        reachedBy[file] = ids;
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
