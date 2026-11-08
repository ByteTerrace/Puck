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
        var shadeSets = new List<IReadOnlyList<SdfShadeProgram>>();

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
            shadeSets.Add(item: WorldRenderSettings.DeclaredShades(definition: definition!));
            File.WriteAllBytes(
                Path.Combine(
                    path1: output,
                    path2: name
                ),
                WorldDefinitionSerialization.Serialize(definition: definition)
            );
        }
        PlayerDerivedProducts.Prepare(output: output, agbBios: needs.AgbBios, guests: [.. needs.Guests]);
        PrepareShading(output: output, sets: shadeSets);
        Console.WriteLine(value: $"Prepared {visited.Count} hosted world definitions, with {primaryName} as the primary world.");
        return 0;
    }

    // Each hosted world's declared program set specializes the views and glass kernels exactly as its residency requests
    // them (SdfWorldResidency's PrepareShade), for both targets, from the deployed tree a player compiles against, so a
    // player's first boot finds them in its shipping cache and interprets only a program its documents did not declare.
    // The kernels are a cache whose miss the runtime derives again, so a target this machine's toolchain cannot compile
    // (no dxc, a dxc without SPIR-V, an install without the kernel sources) is skipped and named, never fatal.
    private static void PrepareShading(IReadOnlyList<IReadOnlyList<SdfShadeProgram>> sets, string output) {
        var compiler = new ShaderCompiler(cacheDirectory: ShaderCompiler.DefaultCacheDirectory);
        var store = Path.Combine(path1: output, path2: Puck.Assets.DerivedBytes.ShippingDirectoryName);
        var distinct = sets.Where(predicate: static set => (set.Count != 0))
            .DistinctBy(keySelector: static set => string.Join(separator: '\n', values: set.Select(selector: static p => p.Identity)), comparer: StringComparer.Ordinal).ToArray();
        var kernels = 0;

        foreach (var target in (ReadOnlySpan<ShaderTarget>)[ShaderTarget.Dxil, ShaderTarget.Spirv]) {
            try {
                foreach (var programs in distinct) {
                    var plans = SdfShadeKernels.Plan(compiler: compiler, programs: programs, target: target, tree: SdfKernelSet.DeployedTree);

                    _ = SdfShadeKernels.CompileAsync(compiler: compiler, plans: plans).GetAwaiter().GetResult();
                    foreach (var (_, plan) in plans) {
                        compiler.ExportPrepared(directory: store, plan: plan);
                        kernels++;
                    }
                }
            } catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ShaderToolMissingException or ShaderClosureRefusedException) {
                Console.Error.WriteLine(value: $"world prepare: specialized shading kernels for {target} skipped, the runtime compiles them on a miss: {exception.Message}");
            }
        }
        Console.WriteLine(value: $"Prepared {kernels} specialized shading kernel(s) for {distinct.Length} program set(s) in {store.Replace(oldChar: '\\', newChar: '/')}.");
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
