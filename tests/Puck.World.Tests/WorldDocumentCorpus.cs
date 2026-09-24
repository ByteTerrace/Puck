using System.Text;
using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;

namespace Puck.World.Tests;

/// <summary>The world documents the game ships, the fixture documents its laws boot, and the worlds its canary
/// manifests boot, enumerated rather than listed, and the door each boots through: a JSON document through <see cref="WorldDefinitionLoader.TryResolve"/>, a
/// <c>.puck</c> source compiled and admitted through <see cref="WorldSourceLoader"/>, and a composition source staged
/// world by world as a composition boot stages one. A shipped document that does not boot on its own is a module
/// library, which emits no document, or a fragment (<see cref="IsFragment"/>).</summary>
internal static class WorldDocumentCorpus {
    /// <summary>The repository-relative directory of the canary manifests.</summary>
    public const string CanaryDirectory = "tests/Puck.World.Canaries";
    /// <summary>The repository-relative directory of the world documents this suite's laws boot as fixtures.</summary>
    public const string FixtureDirectory = "tests/Puck.World.Tests/Fixtures";
    /// <summary>The refusal <see cref="TryBoot"/> reports for a module library.</summary>
    public const string ModuleLibrary = "a module library emits no document";

    // The trees of the game's shipped world documents.
    private static readonly string[] ShippedTrees = [
        ShippedWorldDocuments.WorldDirectory,
        "worlds",
    ];
    // Every shipped document another shipped document names as its basis or an import, repository-relative.
    private static readonly Lazy<HashSet<string>> ComposedDocuments = new(valueFactory: static () => {
        var composed = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var relativePath in ShippedDocuments()) {
            var path = RepositoryPaths.Resolve(relativePath: relativePath);

            if (ReadObject(path: path) is not { Count: > 0 } document) {
                continue;
            }

            foreach (var name in ComposedNames(document: document)) {
                _ = composed.Add(item: RelativePath(path: ShippedWorldDocuments.Carrier(
                    name: name,
                    referrer: path
                )));
            }
        }

        return composed;
    });

    /// <summary>Returns every world a <c>canary.json</c> names under any <c>world</c> member that is checked in,
    /// repository-relative.</summary>
    /// <returns>The canary worlds, in manifest order.</returns>
    public static IEnumerable<string> CanaryWorlds() {
        var root = RepositoryPaths.Resolve(relativePath: CanaryDirectory);

        foreach (var manifest in Directory.EnumerateFiles(
            path: root,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "canary.json"
        )) {
            foreach (var world in WorldMembers(node: JsonNode.Parse(utf8Json: File.ReadAllBytes(path: manifest)))) {
                // A manifest also names the documents a leg writes during its run; only a checked-in world boots.
                if (File.Exists(path: RepositoryPaths.Resolve(relativePath: world))) {
                    yield return world;
                }
            }
        }
    }
    /// <summary>Returns the carrying file of every document under <see cref="FixtureDirectory"/>, repository-relative.</summary>
    /// <returns>The fixture documents.</returns>
    public static IEnumerable<string> FixtureDocuments() => ShippedWorldDocuments.Files(directory: RepositoryPaths.Resolve(relativePath: FixtureDirectory)).Select(selector: RelativePath);
    /// <summary>Determines whether a shipped document is a fragment: it declares no schema of its own, exports a
    /// surface to the host that imports it, or another shipped document composes it as a basis or an import.</summary>
    /// <param name="relativePath">The repository-relative path of the document's carrying file.</param>
    /// <returns><see langword="true"/> when the document is a fragment.</returns>
    public static bool IsFragment(string relativePath) => (
        ComposedDocuments.Value.Contains(item: relativePath) ||
        (
            (ReadObject(path: RepositoryPaths.Resolve(relativePath: relativePath)) is { } document) &&
            (!document.ContainsKey(propertyName: "schema") || document.ContainsKey(propertyName: "exports"))
        )
    );
    /// <summary>Reads the document a carrying file holds as a JSON object, compiling a <c>.puck</c> source.</summary>
    /// <param name="path">The full path of the carrying file.</param>
    /// <returns>The document, or <see langword="null"/> when the file holds no JSON object.</returns>
    public static JsonObject? ReadObject(string path) {
        try {
            return (JsonNode.Parse(utf8Json: ShippedWorldDocuments.Read(path: path)) as JsonObject);
        } catch (Exception exception) when ((exception is InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)) {
            return null;
        }
    }
    /// <summary>Returns a full path relative to the repository root, forward-slashed.</summary>
    /// <param name="path">The full path.</param>
    /// <returns>The repository-relative path.</returns>
    public static string RelativePath(string path) => Path.GetRelativePath(
        path: path,
        relativeTo: RepositoryPaths.RequireRoot()
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    );
    /// <summary>Returns the carrying file of every document under the shipped world trees, repository-relative.</summary>
    /// <returns>The shipped documents.</returns>
    public static IEnumerable<string> ShippedDocuments() {
        foreach (var tree in ShippedTrees) {
            foreach (var file in ShippedWorldDocuments.Files(directory: RepositoryPaths.Resolve(relativePath: tree))) {
                yield return RelativePath(path: file);
            }
        }
    }
    /// <summary>Loads the worlds a carrying file boots, through the door the game boots it through.</summary>
    /// <param name="path">The full path of the carrying file.</param>
    /// <param name="stagingDirectory">The directory a composition source's worlds are staged into; the caller owns
    /// and deletes it.</param>
    /// <param name="worlds">The file each world booted from, with its loaded definition.</param>
    /// <param name="reason">Why nothing booted, <see cref="ModuleLibrary"/> for a module library.</param>
    /// <returns><see langword="true"/> when every world the file declares loaded.</returns>
    public static bool TryBoot(string path, string stagingDirectory, out List<(string Source, WorldDefinition Definition)> worlds, out string reason) {
        worlds = [];

        var catalog = TestHookInstaller.CreateMachineCatalog();

        if (!WorldDocumentName.IsSourceFile(path: path)) {
            if (!WorldDefinitionLoader.TryResolve(
                catalog: catalog,
                explicitPath: path,
                failure: out reason,
                source: out var resolved
            )) {
                return false;
            }

            worlds.Add(item: (path, resolved.Definition));

            return true;
        }

        var compilation = WorldCompiler.CompileFile(
            allowMultiple: true,
            path: path
        );

        if (!compilation.Success) {
            reason = compilation.Diagnostics.FormatReport(
                filePath: path,
                sourceText: File.ReadAllText(path: path)
            );

            return false;
        }

        if (compilation.Worlds.Count > 0) {
            var stagedPaths = new List<string>(capacity: compilation.Worlds.Count);

            // Every declared world is staged before any boots, since each proves its borders against the others.
            foreach (var world in compilation.Worlds) {
                if (!WorldStaging.TryWrite(
                    directory: stagingDirectory,
                    name: world.Name,
                    path: out var staged,
                    reason: out reason,
                    sourceDirectory: Path.GetDirectoryName(path: path)!,
                    world: ((JsonObject)world.Json.DeepClone())
                )) {
                    return false;
                }

                stagedPaths.Add(item: staged);
            }

            foreach (var staged in stagedPaths) {
                if (!WorldDefinitionLoader.TryResolve(
                    catalog: catalog,
                    explicitPath: staged,
                    failure: out reason,
                    source: out var resolved
                )) {
                    return false;
                }

                worlds.Add(item: (staged, resolved.Definition));
            }

            reason = string.Empty;

            return true;
        }

        if (!compilation.EmitsDocument) {
            reason = ModuleLibrary;

            return false;
        }

        if (!WorldSourceLoader.TryLoadForAdmission(
            admission: out var admission,
            catalog: catalog,
            document: Encoding.UTF8.GetBytes(s: compilation.RequireJson().ToJsonString()),
            path: path,
            reason: out reason
        )) {
            return false;
        }

        worlds.Add(item: (path, admission!.Definition));

        return true;
    }

    private static IEnumerable<string> ComposedNames(JsonObject document) {
        if ((document[WorldDocumentBasis.BasisMemberName] is JsonValue basis) && basis.TryGetValue<string>(value: out var basisName)) {
            yield return basisName;
        }

        if (document[WorldDocumentBasis.ImportsMemberName] is JsonArray imports) {
            foreach (var import in imports) {
                if ((import?[WorldImport.DocumentMemberName] is JsonValue value) && value.TryGetValue<string>(value: out var importName)) {
                    yield return importName;
                }
            }
        }
    }
    // Every string a manifest carries under a "world" member, at any depth: a leg's world and each federated
    // authority's.
    private static IEnumerable<string> WorldMembers(JsonNode? node) {
        switch (node) {
            case JsonObject obj:
                foreach (var (name, value) in obj) {
                    if ((name == "world") && (value is JsonValue leaf) && leaf.TryGetValue<string>(value: out var world)) {
                        yield return world;
                    } else {
                        foreach (var nested in WorldMembers(node: value)) {
                            yield return nested;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array) {
                    foreach (var nested in WorldMembers(node: item)) {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
