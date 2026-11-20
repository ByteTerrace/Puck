using System.CommandLine;
using System.Text.Json.Nodes;
using Puck.World;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.World.Transpiler;

namespace Puck.Cli.Automation;

public static class WorldPrepareCommand {
    // Hosted storage addresses worlds by document name (WorldDocumentName), independently of repository directories.
    // Composition reads each basis and import from its .puck source where one exists, so the authored worlds
    // directory prepares without a prior build. The primary world's directory is the root no reference may leave; a
    // package's primary is its manifest's entry.
    private static int Run(string output, string primary) {
        if (!TryResolvePrimary(primary: primary, entry: out var entry, reason: out var refusal)) {
            return CliExit.Refuse(verb: "world prepare", what: CliPaths.ToDisplay(fullPath: primary), why: refusal);
        }

        Directory.CreateDirectory(path: output);
        var composed = Compose(primary: entry);

        foreach (var (name, definition, _) in composed.Worlds) {
            File.WriteAllBytes(
                Path.Combine(
                    path1: output,
                    path2: name
                ),
                WorldDefinitionSerialization.Serialize(definition: definition)
            );
        }
        PlayerDerivedProducts.Prepare(output: output, agbBios: composed.Needs.AgbBios, guests: [.. composed.Needs.Guests]);
        PrepareShading(output: output, sets: composed.ShadeSets);
        Console.WriteLine(value: $"Prepared {composed.Worlds.Count} hosted world definitions, with {composed.PrimaryName} as the primary world.");
        return 0;
    }

    /// <summary>A primary world composed with every neighbour it references, as hosted storage holds them.</summary>
    /// <param name="PrimaryName">The primary world's canonical name.</param>
    /// <param name="Worlds">Each world's hosted document name, its validated definition and its document directory, in
    /// the order the references reached them.</param>
    /// <param name="Needs">The derived products the definitions reference.</param>
    /// <param name="ShadeSets">Each world's declared shading program set.</param>
    public sealed record Composition(
        string PrimaryName,
        IReadOnlyList<(string Name, WorldDefinition Definition, string Directory)> Worlds,
        PlayerDerivedProducts.Needs Needs,
        IReadOnlyList<IReadOnlyList<SdfShadeProgram>> ShadeSets
    );

    /// <summary>Resolves a primary world argument to its entry: a package folder's manifest entry, or the source or
    /// document itself.</summary>
    /// <param name="primary">The full path of a package folder, a <c>.puck</c> source or a <c>.world.json</c> document.</param>
    /// <param name="entry">The entry's full path.</param>
    /// <param name="reason">Why the argument names no world, or empty.</param>
    /// <returns><see langword="true"/> when the argument names a world.</returns>
    public static bool TryResolvePrimary(string primary, out string entry, out string reason) {
        entry = primary;
        if (WorldPackageManifest.IsPackagePath(path: primary)) {
            if (!WorldPackageManifest.TryResolveEntry(
                entryPath: out var entryPath,
                manifest: out _,
                path: primary,
                reason: out var packageReason
            )) {
                reason = packageReason;
                return false;
            }

            entry = entryPath;
        }

        if (!WorldDocumentName.IsSourceFile(path: entry) && !WorldDocumentName.IsDocumentFile(path: entry)) {
            reason = $"names no world: the primary world is a {WorldDocumentName.SourceSuffix} source or a {WorldDocumentName.DocumentSuffix} document.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Composes a primary world and its referenced neighbours with the engine's own composer and validates each
    /// as publishable.</summary>
    /// <param name="primary">The primary world's entry (<see cref="TryResolvePrimary"/>).</param>
    /// <returns>The composed worlds.</returns>
    /// <exception cref="InvalidDataException">A reference escapes the primary world's directory, two worlds share a
    /// hosted name, or a world cannot be composed, relocated or validated.</exception>
    public static Composition Compose(string primary) {
        var root = Path.GetDirectoryName(path: primary)!;
        var primaryName = WorldDocumentName.OfCarrierFile(path: Path.GetFileName(path: primary));
        var machines = CliWorldVocabulary.EnsureInstalled();
        var catalogFingerprint = CliWorldVocabulary.Fingerprint(catalog: machines);
        var worlds = new List<(string Name, WorldDefinition Definition, string Directory)>();
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
                documents: WorldCompileSession.Shared,
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
            var directory = WorldDocumentPaths.DirectoryOf(documentPath: path);

            needs.Note(definition: definition!, documentDirectory: directory);
            shadeSets.Add(item: WorldRenderSettings.DeclaredShades(definition: definition!));
            worlds.Add(item: (name, definition!, directory));
        }
        return new Composition(Needs: needs, PrimaryName: primaryName, ShadeSets: shadeSets, Worlds: worlds);
    }

    /// <summary>Specializes the views and glass kernels for each declared program set exactly as a residency requests
    /// them (SdfWorldResidency's PrepareShade), for both targets, from the deployed tree a player compiles against, and
    /// exports them into the shipping cache beside <paramref name="output"/>, so a player's first boot finds them and
    /// interprets only a program its documents did not declare. The kernels are a cache whose miss the runtime derives
    /// again, so a target this machine's toolchain cannot compile (no dxc, a dxc without SPIR-V, an install without the
    /// kernel sources) is skipped and named, never fatal.</summary>
    /// <param name="sets">The declared program sets (<see cref="Composition.ShadeSets"/>).</param>
    /// <param name="output">The directory whose <see cref="Puck.Assets.DerivedBytes.ShippingDirectoryName"/> receives the
    /// kernels: a player's executable directory, or hosted documents' directory.</param>
    public static void PrepareShading(IReadOnlyList<IReadOnlyList<SdfShadeProgram>> sets, string output) {
        var compiler = new ShaderCompiler(cacheDirectory: ShaderCompiler.DefaultCacheDirectory);
        var store = Path.Combine(path1: output, path2: Puck.Assets.DerivedBytes.ShippingDirectoryName);
        var distinct = sets.Where(predicate: static set => (set.Count != 0))
            .DistinctBy(keySelector: static set => string.Join(separator: '\n', values: set.Select(selector: static p => p.Identity)), comparer: StringComparer.Ordinal).ToArray();
        var kernels = 0;

        foreach (var target in (ReadOnlySpan<ShaderTarget>)[ShaderTarget.Dxil, ShaderTarget.Spirv]) {
            try {
                // Read as a runtime's kernel set reads them at load (SdfKernelSet.Load), so the plans key alike.
                var sources = SdfShadeKernels.SourcesOf(tree: SdfKernelSet.DeployedTree);

                foreach (var programs in distinct) {
                    var plans = SdfShadeKernels.Plan(compiler: compiler, programs: programs, sources: sources, target: target);

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
        Console.WriteLine(value: $"Prepared {kernels} specialized shading kernel(s) for {distinct.Length} program set(s) in {Puck.Abstractions.PuckPaths.Slashed(path: store)}.");
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
