using System.CommandLine;
using System.Text.Json.Nodes;
using Puck.World;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Automation;

public static class WorldPrepareCommand {
    // Hosted storage addresses worlds by document name (WorldDocumentName), independently of repository directories.
    // Composition reads each basis and import from its .puck source where one exists, so the authored worlds
    // directory prepares without a prior build. The primary world's directory is the root no reference may leave.
    private static int Run(string output, string primary) {
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
            File.WriteAllBytes(
                Path.Combine(
                    path1: output,
                    path2: name
                ),
                WorldDefinitionSerialization.Serialize(definition: definition)
            );
        }
        Console.WriteLine(value: $"Prepared {visited.Count} hosted world definitions, with {primaryName} as the primary world.");
        return 0;
    }

    public static Command Create() {
        var primaryArgument = new Argument<string>(name: "primary-world") { Description = "The primary world's .puck source or .world.json document. Its directory holds the neighbours it references and the sources and documents they compose; no reference may leave it." };
        var outputOption = CliOptions.Output(
            description: "The directory the composed hosted documents are written to, one per canonical world name.",
            required: true
        );
        var command = new Command(
            description: "Compose the primary world and its referenced neighbours with the engine's own composer.",
            name: "prepare"
        ) { primaryArgument, outputOption };

        command.SetAction(action: parseResult => Run(
            output: Path.GetFullPath(path: parseResult.GetRequiredValue(option: outputOption)),
            primary: Path.GetFullPath(path: parseResult.GetRequiredValue(argument: primaryArgument))
        ));
        return command;
    }
}
