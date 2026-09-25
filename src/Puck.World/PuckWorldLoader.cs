using Puck.Abstractions;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.World.Server;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;

namespace Puck.World;

/// <summary>Transparent runtime boot loader and compiler for Puck DSL (.puck) files.</summary>
internal static class PuckWorldLoader {
    // A boot reads its compiled world beside its document, else from the per-user compiled-world cache, and writes one
    // there on a miss. The bakes a compiled world names fill the per-user bake cache from the bake pack beside it, and
    // the presentation reads that cache. Like the compile cache, both hold a pure function of the source, the build
    // and the machine catalog, never of a run's state, so every boot on this device shares them whatever its
    // --state-dir; both stores write atomically, so concurrent boots may fill them together.
    private static CompiledWorldCache CompiledWorlds() => new(
        chunks: WorldBakeChunk.Register(
            chunks: CompiledWorldChunks.Standard,
            store: WorldBakeStore.Open(directory: BakeDirectory())
        ),
        directory: Puck.Abstractions.PuckUserDirectory.Resolve(name: "compiled-worlds")
    );

    /// <summary>Returns the per-user directory that bakes made on this device are kept in.</summary>
    internal static string BakeDirectory() => Puck.Abstractions.PuckUserDirectory.Resolve(name: "bakes");

    // A composition source declares several worlds that reach each other across their borders by document name, and
    // a border crossing loads its neighbour from disk. A boot of the source therefore stages every declared world into
    // one boot-private directory under the state root, through the staging `puck test` uses, and boots the declared
    // entry from there. The directory is the source's alone and is restaged whole on every boot, so a world the
    // source no longer declares cannot be reached.
    private static bool TryBootComposition(string path, IReadOnlyList<WorldCompiledWorld> worlds, string? named, out WorldDefinitionSource source, out string failure, string catalogFingerprint,
        IMachineValidationCatalog? catalog, Func<WorldDefinition, WorldDefinition>? overrides, WorldStateRoot stateRoot) {
        source = null!;

        var declared = string.Join(
            separator: ", ",
            values: worlds.Select(selector: static world => world.Name)
        );
        WorldCompiledWorld entry;

        if (named is not null) {
            if (worlds.FirstOrDefault(predicate: world => string.Equals(
                a: world.Name,
                b: named,
                comparisonType: StringComparison.Ordinal
            )) is not { } chosen) {
                failure = $"[world] definition refused: --entry '{named}' names no world '{path}' declares; it declares {declared}.";

                return false;
            }

            entry = chosen;
        } else if (worlds.FirstOrDefault(predicate: static world => world.Entry) is { } declaredEntry) {
            entry = declaredEntry;
        } else {
            failure = $"[world] definition refused: '{path}' declares worlds {declared} and none is its entry — write `entry world <name> = ...` for the world a boot starts in, or name one with --entry.";

            return false;
        }

        var directory = Path.Combine(
            path1: stateRoot.FullPath,
            path2: "compositions",
            path3: Path.GetFileNameWithoutExtension(path: path)
        );
        var sourceDirectory = (Path.GetDirectoryName(path: path) ?? ".");
        var entryPath = string.Empty;

        try {
            if (Directory.Exists(path: directory)) {
                Directory.Delete(
                    path: directory,
                    recursive: true
                );
            }

            foreach (var world in worlds) {
                if (!WorldStaging.TryWrite(
                    directory: directory,
                    name: world.Name,
                    path: out var staged,
                    reason: out var reason,
                    sourceDirectory: sourceDirectory,
                    world: ((JsonObject)JsonNode.Parse(utf8Json: world.Json)!)
                )) {
                    failure = $"[world] definition refused: '{path}' world '{world.Name}' does not compose: {reason}";

                    return false;
                }

                if (world == entry) {
                    entryPath = staged;
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            failure = $"[world] definition refused: '{path}' could not be staged under {directory}: {exception.Message}";

            return false;
        }

        // The booted world's origin is its staged document, so its borders resolve their neighbours beside it; the
        // origin line names the --world path the boot was asked for, as every --world boot does. Its compiled world
        // belongs to the document `puck compile` writes beside the source.
        var neighbours = new WorldFileNeighbourResolver(
            baseDirectory: () => directory,
            catalog: catalog,
            catalogFingerprint: catalogFingerprint
        );
        var compiled = CompiledWorlds().For(
            catalogFingerprint: catalogFingerprint,
            documentPath: Path.Combine(
                path1: sourceDirectory,
                path2: WorldDocumentName.DocumentFile(name: entry.Name)
            )
        );

        if (!WorldDefinitionLoader.TryLoadFileForAdmission(
            admission: out var loaded,
            catalog: catalog,
            contentHash: out _,
            catalogFingerprint: catalogFingerprint,
            compiled: compiled,
            neighbours: neighbours,
            overrides: overrides,
            path: entryPath,
            reason: out var loadReason
        )) {
            failure = $"[world] definition refused: {loadReason}";

            return false;
        }

        Console.Error.WriteLine(value: $"[world] composition: {path} staged {worlds.Count} worlds under {directory}; entry '{entry.Name}'");
        Console.Error.WriteLine(value: $"[world] definition: {path} (--world)");
        if (compiled.Resolution is { } resolution) {
            Console.Error.WriteLine(value: resolution.Describe());
        }
        source = new WorldDefinitionSource(
            Definition: loaded!.Definition,
            SourcePath: entryPath
        ) { Admission = loaded };
        failure = string.Empty;

        return true;
    }

    /// <summary>Resolves a world definition from a .puck or .world.json file, transparently compiling .puck sources in-memory.</summary>
    /// <param name="explicitPath">The authored world path, or null for the shipped default.</param>
    /// <param name="source">The loaded definition and source path.</param>
    /// <param name="failure">The named refusal reason, or empty on success.</param>
    /// <param name="stateRoot">The run's state root, under which a composition source stages its declared
    /// worlds.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for the selected host catalog.</param>
    /// <param name="catalog">The selected host machine catalog used for provider composition and validation.</param>
    /// <param name="overrides">Rewrites the loaded document before its one admission, or null when the host
    /// overrides nothing the document carries.</param>
    /// <param name="entry">The declared world of a composition source to boot in place of its <c>entry world</c>, or
    /// null for the declared entry.</param>
    public static bool TryResolveWorld(string? explicitPath, out WorldDefinitionSource source, out string failure, WorldStateRoot stateRoot, string catalogFingerprint = "", IMachineValidationCatalog? catalog = null,
        Func<WorldDefinition, WorldDefinition>? overrides = null, string? entry = null) {
        var explicitly = !string.IsNullOrWhiteSpace(value: explicitPath);
        string path;

        try {
            path = (explicitly
                ? Path.GetFullPath(path: explicitPath!)
                : PuckPaths.Shipped(relativePath: WorldDefinitionLoader.DefaultRelativePath)
            );
        } catch (Exception ex) when ((ex is ArgumentException or NotSupportedException or PathTooLongException)) {
            source = null!;
            failure = $"[world] definition refused: cannot resolve path '{explicitPath}' ({ex.Message})";
            return false;
        }

        if (!path.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".puck"
        )) {
            if (entry is not null) {
                source = null!;
                failure = $"[world] definition refused: --entry '{entry}' names a world of a composition source, and '{path}' is not a .puck source.";

                return false;
            }

            return WorldDefinitionLoader.TryResolve(
                catalog: catalog,
                catalogFingerprint: catalogFingerprint,
                compiledWorlds: CompiledWorlds(),
                explicitPath: explicitPath,
                failure: out failure,
                overrides: overrides,
                source: out source
            );
        }

        if (!File.Exists(path: path)) {
            source = null!;
            failure = $"[world] definition refused: path '{path}' not found.";
            return false;
        }

        WorldCompiledSource? compiled;
        WorldCompilation? compileFailure;

        try {
            _ = WorldCompileCache.Shared.TryCompile(
                compiled: out compiled,
                failure: out compileFailure,
                path: path
            );
        } catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)) {
            source = null!;
            failure = $"[world] definition refused: cannot read '{path}' ({ex.Message})";
            return false;
        }

        if (compileFailure is not null) {
            source = null!;
            failure = ($"[world] definition refused: '{path}' does not compile:\n" +
                      compileFailure.Diagnostics.FormatReport(
                filePath: path,
                sourceText: File.ReadAllText(path: path)
            ));
            return false;
        }

        if (compiled!.Worlds.Count > 0) {
            return TryBootComposition(
                stateRoot: stateRoot,
                catalog: catalog,
                catalogFingerprint: catalogFingerprint,
                failure: out failure,
                named: entry,
                overrides: overrides,
                path: path,
                source: out source,
                worlds: compiled.Worlds
            );
        }

        if (entry is not null) {
            source = null!;
            failure = $"[world] definition refused: --entry '{entry}' names a world of a composition source, and '{path}' declares no worlds.";

            return false;
        }

        var compiledWorld = CompiledWorlds().For(
            catalogFingerprint: catalogFingerprint,
            documentPath: path
        );

        if (!WorldSourceLoader.TryLoadForAdmission(
            admission: out var loaded,
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            compiled: compiledWorld,
            document: compiled.Document!,
            overrides: overrides,
            path: path,
            reason: out var loadReason
        )) {
            source = null!;
            failure = $"[world] definition refused: {loadReason}";
            return false;
        }

        // The origin reads as a JSON boot's does: the path already says the source is `.puck`, and one spelling is
        // what a transcript reader (the canary runner's origin check) matches for either.
        Console.Error.WriteLine(value: $"[world] definition: {path} ({(explicitly
            ? "--world"
            : "shipped default")})");
        if (compiledWorld.Resolution is { } resolution) {
            Console.Error.WriteLine(value: resolution.Describe());
        }
        source = new WorldDefinitionSource(
            Definition: loaded!.Definition,
            SourcePath: path
        ) { Admission = loaded };
        failure = string.Empty;
        return true;
    }
}
