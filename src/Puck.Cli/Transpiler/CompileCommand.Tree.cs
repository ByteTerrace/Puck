using System.Text;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Assets.Documents;
using Puck.Cli.Shaders;
using Puck.Shaders;
using Puck.World;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Transpiler;

internal static partial class CompileCommand {
    // `puck compile --tree <root> --output <directory> [--written <report>] <sources…>`: the build's one generation
    // step. Each source's documents land at the same relative place under the output directory that the source holds
    // under the tree, so a document's relative basis, import, reference and asset paths resolve there as they do beside
    // its source; each document that draws as a world has its compiled world beside it, the bakes every compiled
    // world names ship once, in one bake pack at the output's root, and every pipeline source a compiled world names
    // ships as a package in the store beside the pack (WritePipelinePackages). Every name resolves first, as the
    // composer resolves it: to the source that emits it, whatever its stem, and to its document otherwise. A
    // hand-authored `.world.json` given as a source ships as it stands, with its compiled world, unless the `.puck`
    // source of its exact name emits that name, which wins; a composition beside it that declares other worlds leaves it shipping, and a source of
    // another stem emitting its name is refused with it before anything is written. The output directory is the tree's
    // alone: after every source compiled, a world document, compiled world or bake pack there that this run did not
    // write belongs to a source that was renamed or deleted, and is removed so nothing can load it. Which documents a
    // source emits is not its file name, so the
    // run reports what it wrote rather than leaving a build to derive it from the sources' names.
    private static int RunTree(string tree, string output, string? report, IReadOnlyList<string> paths, bool strict, bool validate, bool bundle) {
        var root = Path.GetFullPath(path: tree);
        var directory = Path.GetFullPath(path: output);
        // Each file this run wrote and the source that wrote it, so a second source claiming a document is refused.
        var written = new Dictionary<string, string>(comparer: DocumentName.Comparer);
        var outputFromTree = Path.GetRelativePath(
            path: directory,
            relativeTo: root
        );

        // The output is swept of every document this run did not write, so it may never hold the tree's own
        // hand-authored documents.
        if (!outputFromTree.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: ".."
        ) && !Path.IsPathRooted(path: outputFromTree)) {
            Console.Error.WriteLine(value: $"error: the output '{directory}' lies inside the tree '{root}'; a tree run writes outside the sources it compiles.");

            return 2;
        }

        var reportPath = ((report is not null)
            ? Path.GetFullPath(path: report)
            : null
        );

        // A run that fails leaves no report, so a build reading it never ships a partial run as a whole one.
        if (reportPath is not null) {
            try {
                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: reportPath)!);
                File.Delete(path: reportPath);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"error: the report '{reportPath}' could not be removed before the run: {exception.Message}");

                return 2;
            }
        }

        // Every compiled world of the run names its bakes in one pack at the output's root, written once every source
        // has compiled, so a bake the worlds share ships once.
        var pack = new BakePackPlan(
            keepsEarlier: false,
            path: Path.Combine(
                path1: directory,
                path2: WorldBakePack.FileName
            )
        );

        // Every name the tree carries resolves before anything is written, by the one rule the composer resolves a name
        // by (PuckDocumentComposer.TryCarriers over the name index): the source that emits it, whatever its stem, and
        // its document otherwise. Two files carrying one name are refused here, the one refusal this run gives them,
        // except a source beside the document of its own exact name, which wins.
        if (!PuckDocumentComposer.TryCarriers(
            carriers: out var carriers,
            directory: root,
            libraries: out _,
            option: SearchOption.AllDirectories,
            reason: out var collision
        )) {
            Console.Error.WriteLine(value: $"error: {collision} Nothing was written.");

            return 1;
        }

        var carrierOf = new Dictionary<string, string>(comparer: DocumentName.Comparer);

        foreach (var carrier in carriers) {
            carrierOf[carrier.Name] = carrier.Path;
        }

        foreach (var path in paths) {
            var source = Path.GetFullPath(path: path);
            var relative = Path.GetRelativePath(
                path: source,
                relativeTo: root
            );

            if (
                Path.IsPathRooted(path: relative) ||
                relative.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".."
            )
            ) {
                Console.Error.WriteLine(value: $"error: '{source}' does not lie under the tree '{root}'.");

                return 2;
            }

            var exitCode = (WorldDocumentName.IsDocumentFile(path: relative)
                ? ShipDocument(
                    carrierOf: carrierOf,
                    destination: Path.Combine(
                        path1: directory,
                        path2: relative
                    ),
                    pack: pack,
                    relative: relative,
                    source: source,
                    written: written
                )
                : Run(
                    bundle: bundle,
                    output: Path.Combine(
                        path1: directory,
                        path2: Path.ChangeExtension(
                            extension: WorldDocumentName.DocumentSuffix,
                            path: relative
                        )
                    ),
                    pack: pack,
                    path: source,
                    strict: strict,
                    validate: validate,
                    watch: false,
                    written: written
                ));

            if (exitCode != 0) {
                return exitCode;
            }
        }

        var packed = WriteBakePack(
            owner: root,
            pack: pack,
            written: written
        );

        if (packed != 0) {
            return packed;
        }

        var pipelines = WritePipelinePackages(
            directory: directory,
            root: root,
            written: written
        );

        if (pipelines != 0) {
            return pipelines;
        }

        if (Directory.Exists(path: directory)) {
            var kept = new HashSet<string>(
                collection: written.Keys,
                comparer: PuckPaths.Comparer
            );

            foreach (var stale in ((string[])[WorldDocumentName.DocumentSuffix, CompiledWorld.Extension, WorldBakePack.Extension]).SelectMany(selector: suffix => Directory.EnumerateFiles(
                path: directory,
                searchOption: SearchOption.AllDirectories,
                searchPattern: ("*" + suffix)
            )).Where(predicate: file => !kept.Contains(item: Path.GetFullPath(path: file))).ToArray()) {
                try {
                    File.Delete(path: stale);
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    Console.Error.WriteLine(value: $"error: '{stale}' has no source in the tree and could not be removed: {exception.Message}");

                    return 2;
                }

                Console.WriteLine(value: $"Removed '{stale}': no source in the tree compiles to it.");
            }
        }

        if (reportPath is null) {
            return 0;
        }

        // One output-relative, forward-slashed path per line, in ordinal order: exactly the files the output holds.
        var lines = written.Keys.Select(selector: file => (Path.GetRelativePath(
            path: file,
            relativeTo: directory
        ).Replace(
            newChar: '/',
            oldChar: Path.DirectorySeparatorChar
        ) + "\n")).Order(comparer: StringComparer.Ordinal);

        try {
            AtomicFile.WriteAllText(
                contents: string.Concat(values: lines),
                path: reportPath
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"error: Could not write the report '{reportPath}': {exception.Message}");

            return 2;
        }

        Console.WriteLine(value: $"Reported {written.Count:N0} written files in '{reportPath}'.");

        return 0;
    }
    // Every pipeline a compiled world of the run names by source ships compiled, so no device compiles a shipped world's
    // pipeline: the source, resolved from the document's place in the tree as the World resolves it beside the shipped
    // document, is packaged into the store at the output's root under its key (ShaderPackager.StoreAsync), which the
    // World's packager loads in place of compiling it. A row naming a package directory ships that package as it
    // stands. A source that is missing, does not fit a package or does not compile fails the run, and a package in the
    // store that no row of this run names is removed.
    private static int WritePipelinePackages(string root, string directory, Dictionary<string, string> written) {
        var store = Path.Combine(
            path1: directory,
            path2: ShaderPackager.StoreDirectoryName
        );
        var packager = new ShaderPackager(
            compiler: new ShaderCompiler(cacheDirectory: PackageCommand.DefaultCacheDirectory),
            store: store
        );
        var packages = new Dictionary<string, string>(comparer: PuckPaths.Comparer);

        foreach (var (compiledWorld, owner) in written.Where(predicate: static entry => entry.Key.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: CompiledWorld.Extension
        )).OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static entry => entry.Key
        ).ToArray()) {
            if (!TryReadDrawn(
                compiledWorld: compiledWorld,
                definition: out var definition,
                reason: out var reason
            )) {
                Console.Error.WriteLine(value: $"error: the compiled world '{compiledWorld}' of '{owner}' cannot be read for its pipelines: {reason}");

                return 2;
            }

            var sourceDirectory = Path.GetFullPath(path: Path.Combine(
                path1: root,
                path2: Path.GetRelativePath(
                    path: Path.GetDirectoryName(path: compiledWorld)!,
                    relativeTo: directory
                )
            ));

            foreach (var row in (definition.Views.Graphs ?? [])) {
                if (row.Source is null) {
                    continue;
                }
                if (!WorldDocumentPaths.TryResolve(
                    documentDirectory: sourceDirectory,
                    path: row.Source,
                    reason: out var unresolved,
                    resolved: out var source
                )) {
                    Console.Error.WriteLine(value: $"error: '{owner}' names graph instance '{row.Name}' by a source that does not resolve: {unresolved}.");

                    return 1;
                }

                if (ShaderPackager.IsPackage(path: source)) {
                    continue;
                }

                if (!File.Exists(path: source)) {
                    Console.Error.WriteLine(value: $"error: '{owner}' names graph instance '{row.Name}' by the source '{row.Source}', and no file exists at '{source}'.");

                    return 1;
                }

                var (result, package) = packager.StoreAsync(
                    name: row.Name,
                    source: source
                ).GetAwaiter().GetResult();

                if (result.Status != ShaderPipelineLoadStatus.Compiled) {
                    Console.Error.WriteLine(value: $"error: graph instance '{row.Name}' of '{owner}' could not be packaged from '{source}' ({result.Status}): {result.Message.ReplaceLineEndings(replacementText: " ")}");

                    return ((result.Status == ShaderPipelineLoadStatus.Unsupported)
                        ? 2
                        : 1);
                }

                if (packages.TryAdd(
                    key: package,
                    value: owner
                )) {
                    Console.WriteLine(value: $"Packaged pipeline '{row.Name}' from '{source}' into '{package}'.");
                }
            }
        }

        foreach (var (package, owner) in packages) {
            foreach (var file in Directory.EnumerateFiles(
                path: package,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            )) {
                written[Path.GetFullPath(path: file)] = owner;
            }
        }

        if (!Directory.Exists(path: store)) {
            return 0;
        }

        foreach (var stale in Directory.EnumerateDirectories(path: store).Where(predicate: package => !packages.ContainsKey(key: Path.GetFullPath(path: package))).ToArray()) {
            try {
                Directory.Delete(
                    path: stale,
                    recursive: true
                );
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"error: the package '{stale}' has no pipeline in the tree naming it and could not be removed: {exception.Message}");

                return 2;
            }

            Console.WriteLine(value: $"Removed '{stale}': no pipeline in the tree names it.");
        }

        return 0;
    }
    // The drawn definition a compiled world carries in its DEFN chunk: the rows the World runs.
    private static bool TryReadDrawn(string compiledWorld, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? definition, out string reason) {
        definition = null;

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: compiledWorld);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = exception.Message;

            return false;
        }

        if (!CompiledWorld.TryDecode(
            container: out var container,
            content: bytes,
            header: out _,
            reason: out reason
        )) {
            return false;
        }

        if (!container.TryFind(
            chunk: out var chunk,
            code: DefinitionChunk.Instance.Code
        )) {
            reason = "it holds no DEFN chunk.";

            return false;
        }

        return WorldDefinitionFileSource.TryParseDocument(
            definition: out definition,
            json: Encoding.UTF8.GetString(bytes: chunk.Payload.Span),
            reason: out reason,
            sourceName: compiledWorld
        );
    }
    // A hand-authored document ships under the output as it stands, with its compiled world beside it, when it carries
    // its name among the tree's carriers (`carrierOf`, resolved before anything was written): a source emitting the
    // name wins only beside the document of its own exact stem, and carries the document, which is skipped. A document
    // file the run already wrote, given twice, is two claims to one name, refused in the words every door uses.
    private static int ShipDocument(string source, string relative, string destination, IReadOnlyDictionary<string, string> carrierOf, Dictionary<string, string> written, BakePackPlan pack) {
        var target = Path.GetFullPath(path: destination);
        var name = WorldDocumentName.OfDocumentFile(path: relative.Replace(
            newChar: '/',
            oldChar: '\\'
        ));

        if (carrierOf.TryGetValue(
            key: name,
            value: out var carrier
        ) && !PuckPaths.Comparer.Equals(
            x: carrier,
            y: PuckPaths.Normalize(path: source)
        )) {
            Console.WriteLine(value: $"Skipped '{source}': its source '{carrier}' carries the document.");

            return 0;
        }

        if (written.TryGetValue(
            key: target,
            value: out var owner
        )) {
            var collision = DocumentName.Collision(
                heldFile: PuckPaths.Normalize(path: owner),
                heldName: name,
                otherFile: PuckPaths.Normalize(path: source),
                otherName: name
            );

            Console.Error.WriteLine(value: $"error: {collision} Nothing from '{Path.GetFileName(path: source)}' was written.");

            return 1;
        }

        try {
            AtomicFile.WriteAllBytes(
                bytes: File.ReadAllBytes(path: source),
                path: target
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"error: Could not ship document '{source}' as '{target}': {exception.Message}");

            return 2;
        }

        written.Add(
            key: target,
            value: source
        );
        Console.WriteLine(value: $"Shipped '{source}' -> '{target}'.");

        return CompileDocument(
            besidePath: target,
            documentPath: source,
            pack: pack,
            written: written
        );
    }
}
