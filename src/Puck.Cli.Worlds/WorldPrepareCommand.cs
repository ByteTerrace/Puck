using System.CommandLine;
using System.Text.Json.Nodes;
using Puck.World;
using Puck.World.Transpiler.Composition;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.Cli.Automation;

public static class WorldPrepareCommand {
    // Hosted storage addresses worlds by document name (WorldDocumentName), independently of repository directories.
    // Composition reads each basis and import from its .puck source where one exists, so the authored worlds
    // directory prepares without a prior build. The primary world's directory is the root no reference may leave; a
    // package's primary is its manifest's entry.
    private static int Run(string output, string primary) {
        if (WorldPackageManifest.IsPackagePath(path: primary)) {
            if (!WorldPackageManifest.TryResolveEntry(
                entryPath: out var entryPath,
                manifest: out _,
                path: primary,
                reason: out var packageReason
            )) {
                return CliExit.Refuse(verb: "world prepare", what: CliPaths.ToDisplay(fullPath: primary), why: packageReason);
            }

            primary = entryPath;
        }

        if (!WorldDocumentName.IsSourceFile(path: primary) && !WorldDocumentName.IsDocumentFile(path: primary)) {
            return CliExit.Refuse(verb: "world prepare", what: CliPaths.ToDisplay(fullPath: primary), why: $"names no world: the primary world is a {WorldDocumentName.SourceSuffix} source or a {WorldDocumentName.DocumentSuffix} document.");
        }

        var root = Path.GetDirectoryName(path: primary)!;
        var primaryName = WorldDocumentName.OfCarrierFile(path: Path.GetFileName(path: primary));
        var machines = CliWorldVocabulary.EnsureInstalled();
        var catalogFingerprint = CliWorldVocabulary.Fingerprint(catalog: machines);

        Directory.CreateDirectory(path: output);
        var pending = new Queue<string>();
        var visited = new HashSet<string>(comparer: StringComparer.Ordinal);
        var names = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);
        var needs = new PlayerDerivedProducts.Needs();
        var shadeSets = new List<SdfShadeProgram[]>();

        pending.Enqueue(item: Path.Combine(
            path1: root,
            path2: WorldDocumentName.DocumentFile(name: primaryName)
        ));
        while (pending.TryDequeue(result: out var path)) {
            if (!visited.Add(item: path)) {
                continue;
            }
            var relative = Path.GetRelativePath(
                path: path,
                relativeTo: root
            );

            if (
                relative.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".."
            ) ||
                Path.IsPathRooted(path: relative)
            ) {
                throw new InvalidDataException(message: $"World reference escapes the authored worlds directory: {relative}");
            }
            var name = Path.GetFileName(path: path);

            if (
                names.TryGetValue(
                key: name,
                value: out var previous
            ) &&
                (previous != path)
            ) {
                throw new InvalidDataException(message: $"Hosted world name collision: {previous} and {path}");
            }
            names[name] = path;
            if (!WorldDefinitionFileSource.TryComposeDocumentTree(
                catalog: machines,
                catalogFingerprint: catalogFingerprint,
                documents: PuckDocumentComposer.Instance,
                path: path,
                reason: out var reason,
                tree: out var tree
            )) {
                throw new InvalidDataException(message: $"Cannot compose {relative}: {reason}");
            }
            if (tree!["references"] is JsonArray references) {
                foreach (var reference in references) {
                    if (reference?["document"]?.GetValue<string>() is not { Length: > 0 } document) {
                        continue;
                    }
                    if (!WorldDefinitionFileSource.TryResolveDocumentBeside(
                        documentPath: out var neighbour,
                        name: document,
                        reason: out var referenceReason,
                        referrerName: path,
                        sourcePath: out _
                    )) {
                        throw new InvalidDataException(message: $"Cannot resolve a reference of {relative}: {referenceReason}");
                    }

                    pending.Enqueue(item: Path.GetFullPath(path: neighbour));
                    reference["document"] = WorldDocumentName.OfDocumentFile(path: Path.GetFileName(path: neighbour));
                }
            }
            // Every hosted document moves to the root of the world's asset layout. Rebase provider-declared
            // asset paths from nested source directories before discarding their document origins.
            if (!WorldModuleNamespace.TryRelocateConfigurationAssets(
                tree,
                machines,
                path,
                Path.Combine(
                    path1: root,
                    path2: name
                ),
                out reason
            )) {
                throw new InvalidDataException(message: $"Cannot relocate {relative}: {reason}");
            }
            if (!WorldDefinitionLoader.TryReadPublishable(
                catalog: machines,
                definition: out var definition,
                documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: path),
                json: tree.ToJsonString(),
                reason: out reason,
                sourceName: path
            )) {
                throw new InvalidDataException(message: $"Cannot validate {relative}: {reason}");
            }
            needs.Note(definition: definition!, documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: path));
            var shades = definition!.Creations.SelectMany(p => p.Document.Palette ?? [])
                .Concat(definition.FieldRegions.SelectMany(r => r.Fields).Where(f => f.Material is not null).Select(f => f.Material!))
                .Select(m => m.Shade).Append(definition.Render.Shade).Where(s => s is not null)
                .Select(s => SdfShadeProgram.Compile(s!)).DistinctBy(s => s.Identity).ToArray();
            shadeSets.Add(shades);
            File.WriteAllBytes(
                Path.Combine(
                    path1: output,
                    path2: name
                ),
                WorldDefinitionSerialization.Serialize(definition: definition)
            );
        }
        PlayerDerivedProducts.Prepare(output: output, agbBios: needs.AgbBios, guests: [.. needs.Guests]);
        PrepareShading(shadeSets, output);
        Console.WriteLine(value: $"Prepared {visited.Count} hosted world definitions, with {primaryName} as the primary world.");
        return 0;
    }

    private static void PrepareShading(IReadOnlyList<SdfShadeProgram[]> sets, string output) {
        var compiler = new ShaderCompiler(ShaderCompiler.DefaultCacheDirectory);
        foreach (var target in new[] { ShaderTarget.Dxil, ShaderTarget.Spirv }) {
            var kernels = SdfKernelSet.Load(target == ShaderTarget.Dxil ? ".dxil" : ".spv");
            var requests = sets.Select(s => new SdfShadeKernels(s, kernels))
                .Concat(sets.SelectMany(s => s).Select(s => new SdfShadeKernels([s], kernels, fallback: false)))
                .Append(new SdfShadeKernels([], kernels, fallback: false)).DistinctBy(r => r.Identity);
            foreach (var request in requests) {
                _ = request.CompileAsync(compiler, target, SdfKernelSet.DeployedTree).GetAwaiter().GetResult();
                foreach (var (_, plan) in request.Plan(compiler, target, SdfKernelSet.DeployedTree)) {
                    compiler.ExportPrepared(plan, Path.Combine(output, "derived"));
                }
            }
        }
    }

    public static Command Create() {
        var primaryArgument = new Argument<string>(name: "primary-world") { Description = "The primary world: an asset package folder (its manifest names the entry), or a .puck source or .world.json document. Its directory holds the neighbours it references and the sources and documents they compose; no reference may leave it." };
        var outputOption = CliOptions.Output(
            description: "The directory the composed hosted documents are written to, one per canonical world name.",
            required: true
        );
        var command = new Command(
            description: "Compose the primary world and its referenced neighbours with the engine's own composer, and prewarm the derived products they reference.",
            name: "prepare"
        ) { primaryArgument, outputOption };

        command.SetAction(action: parseResult => Run(
            output: Path.GetFullPath(path: parseResult.GetRequiredValue(option: outputOption)),
            primary: Path.GetFullPath(path: parseResult.GetRequiredValue(argument: primaryArgument))
        ));
        return command;
    }
}
