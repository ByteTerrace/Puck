using Puck.Abstractions.Machines;
using Puck.World.Server;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;

namespace Puck.World;

/// <summary>Transparent runtime boot loader and compiler for Puck DSL (.puck) files.</summary>
internal static class PuckWorldLoader {
    // A boot reads its compiled world beside its document, else from the compiled-world cache, and writes one there
    // on a miss. Like the compile cache, it holds a pure function of the source, the build and the machine catalog,
    // never of a run's state, so every boot handed the same roots shares it whatever its --state-dir; it writes
    // atomically, so concurrent boots may fill it together.
    private static CompiledWorldCache CompiledWorlds(WorldCacheRoots caches) => new(
        chunks: CompiledWorldChunks.Standard,
        directory: caches.CompiledWorlds
    );
    // A composition source declares several worlds that reach each other across their borders by document name, and
    // a border crossing loads its neighbour from disk. A boot of the source therefore stages every declared world into
    // one boot-private directory under the state root, through the staging `puck test` uses, and boots the declared
    // entry from there. The directory is the source's alone and is restaged whole on every boot, so a world the
    // source no longer declares cannot be reached.
    private static bool TryBootComposition(string path, IReadOnlyList<WorldCompiledWorld> worlds, string? named, out WorldDefinitionSource source, out string failure, string catalogFingerprint,
        IMachineValidationCatalog catalog, Func<WorldDefinition, WorldDefinition>? overrides, WorldStateRoot stateRoot, WorldCacheRoots caches, bool authority, bool preview) {
        source = null!;

        var directory = Path.Combine(
            path1: stateRoot.FullPath,
            path2: "compositions",
            path3: WorldDrawSave.WorldIdentity(path)
        );
        var sourceDirectory = (Path.GetDirectoryName(path: path) ?? ".");

        if (!WorldStaging.TryStageComposition(
            catalog: catalog,
            directory: Path.Combine(directory, "staged"),
            entry: named,
            entryName: out var entryName,
            entryPath: out var entryPath,
            path: path,
            reason: out var stageReason,
            worlds: worlds
        )) {
            failure = $"[world] definition refused: {stageReason}";

            return false;
        }

        // The booted world's origin is its staged document, so its borders resolve their neighbours beside it; the
        // origin line names the --world path the boot was asked for, as every --world boot does. Its compiled world
        // belongs to the document `puck compile` writes beside the source.
        var staged = Path.GetDirectoryName(entryPath)!;
        var entryIdentity = worlds.Any(world => world.Entry && world.Name == entryName) ? null : entryName;
        var savedDocument = Path.Combine(directory, Path.GetFileName(entryPath));
        // Stage a fresh authored basis each boot. The durable document is an ordinary author-editable delta.
        var basisName = entryName + "-basis";
        while (File.Exists(Path.Combine(staged, WorldDocumentName.DocumentFile(basisName)))) { basisName += "-basis"; }
        File.Move(entryPath, Path.Combine(staged, WorldDocumentName.DocumentFile(basisName)));
        var overlay = File.Exists(savedDocument)
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(savedDocument))!.AsObject()
            : new System.Text.Json.Nodes.JsonObject();
        overlay[WorldDocumentBasis.BasisMemberName] = basisName;
        Puck.Confinement.AtomicFile.WriteAllBytes(entryPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(overlay));
        var neighbours = new WorldFileNeighbourResolver(
            baseDirectory: () => staged,
            catalog: catalog,
            catalogFingerprint: catalogFingerprint
        );
        var compiled = CompiledWorlds(caches: caches).For(
            catalogFingerprint: catalogFingerprint,
            documentPath: Path.Combine(
                path1: sourceDirectory,
                path2: WorldDocumentName.DocumentFile(name: entryName)
            )
        );

        var keys = authority ? WorldDrawSave.Open(stateRoot, path, entryIdentity) : preview ? WorldPrivateDrawKeys.Preview : WorldPrivateDrawKeys.Missing;
        if (!WorldDefinitionLoader.TryLoadFileForAdmission(
            keys: keys,
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

        Console.Error.WriteLine(value: $"[world] composition: {path} staged {worlds.Count} worlds under {directory}; entry '{entryName}'");
        Console.Error.WriteLine(value: $"[world] definition: {path} (--world)");
        if (compiled.Resolution is { } resolution) {
            Console.Error.WriteLine(value: resolution.Describe());
        }
        source = new WorldDefinitionSource(
            Definition: loaded!.Definition,
            SourcePath: entryPath
        ) { Admission = loaded, DrawKeys = keys, CompositionOrigin = (entryPath, path, entryIdentity, savedDocument) };
        failure = string.Empty;

        return true;
    }

    /// <summary>Resolves a world definition from an asset package folder (its <see cref="WorldPackageManifest"/>'s entry),
    /// a .puck source compiled in memory, or a .world.json document.</summary>
    /// <param name="worldPath">The <c>--world</c> path.</param>
    /// <param name="source">The loaded definition and source path.</param>
    /// <param name="failure">The named refusal reason, or empty on success.</param>
    /// <param name="stateRoot">The run's state root, under which a composition source stages its declared
    /// worlds.</param>
    /// <param name="caches">The compiled-world and bake caches the boot reads and fills.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for the selected host catalog.</param>
    /// <param name="catalog">The selected host machine catalog used for provider composition and validation.</param>
    /// <param name="overrides">Rewrites the loaded document before its one admission, or null when the host
    /// overrides nothing the document carries.</param>
    /// <param name="entry">The declared world of a composition source to boot in place of its <c>entry world</c>, or
    /// null for the declared entry.</param>
    /// <param name="preview">Whether this boot is an isolated deterministic test run.</param>
    /// <param name="authority">Whether this boot owns the local world authority save.</param>
    public static bool TryResolveWorld(string worldPath, out WorldDefinitionSource source, out string failure, WorldStateRoot stateRoot, WorldCacheRoots caches, IMachineValidationCatalog catalog, string catalogFingerprint = "",
        Func<WorldDefinition, WorldDefinition>? overrides = null, string? entry = null, bool authority = false, bool preview = false) {
        string path;

        try {
            path = Path.GetFullPath(path: worldPath);
        } catch (Exception ex) when ((ex is ArgumentException or NotSupportedException or PathTooLongException)) {
            source = null!;
            failure = $"[world] definition refused: cannot resolve path '{worldPath}' ({ex.Message})";
            return false;
        }

        if (WorldPackageManifest.IsPackagePath(path: path)) {
            if (!WorldPackageManifest.TryResolveEntry(
                entryPath: out var entryPath,
                manifest: out var package,
                path: path,
                reason: out var packageReason
            )) {
                source = null!;
                failure = $"[world] definition refused: {packageReason}";
                return false;
            }

            Console.Error.WriteLine(value: $"[world] package: '{package.Id}' ({package.Name}) at {path}; entry '{package.Entry}'");
            path = entryPath;
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

            var documentKeys = authority ? WorldDrawSave.Open(stateRoot, path, null) : preview ? WorldPrivateDrawKeys.Preview : WorldPrivateDrawKeys.Missing;
            return WorldDefinitionLoader.TryResolve(
                keys: documentKeys,
                catalog: catalog,
                catalogFingerprint: catalogFingerprint,
                compiledWorlds: CompiledWorlds(caches: caches),
                failure: out failure,
                path: path,
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
                authority: authority,
                preview: preview,
                caches: caches,
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

        var compiledWorld = CompiledWorlds(caches: caches).For(
            catalogFingerprint: catalogFingerprint,
            documentPath: path
        );

        var keys = authority ? WorldDrawSave.Open(stateRoot, path, null) : preview ? WorldPrivateDrawKeys.Preview : WorldPrivateDrawKeys.Missing;
        if (!WorldSourceLoader.TryLoadForAdmission(
            keys: keys,
            admission: out var loaded,
            catalog: catalog,
            catalogFingerprint: catalogFingerprint,
            compiled: compiledWorld,
            document: compiled.Document!,
            sourceCompilation: compiled,
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
        Console.Error.WriteLine(value: $"[world] definition: {path} (--world)");
        if (compiledWorld.Resolution is { } resolution) {
            Console.Error.WriteLine(value: resolution.Describe());
        }
        source = new WorldDefinitionSource(
            Definition: loaded!.Definition,
            SourcePath: path
        ) { Admission = loaded, DrawKeys = keys };
        failure = string.Empty;
        return true;
    }
}
