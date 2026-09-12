using System.CommandLine;
using System.Text.Json.Nodes;
using Puck.World;

namespace Puck.Cli.Automation;

internal static class WorldPrepareCommand {
    public static Command Create() {
        var worldsArgument = new Argument<string>(name: "worlds-directory") { Description = "The directory of authored *.world.json documents." };
        var outputArgument = new Argument<string>(name: "output-directory") { Description = "Where the composed hosted documents are written, one per canonical world name." };
        var command = new Command(description: "Compose the primary world and its referenced neighbours with the engine's own composer.", name: "prepare") { worldsArgument, outputArgument };

        command.SetAction(action: parseResult => Run(output: Path.GetFullPath(path: parseResult.GetRequiredValue(argument: outputArgument)), root: Path.GetFullPath(path: parseResult.GetRequiredValue(argument: worldsArgument))));
        return command;
    }

    // Hosted storage addresses worlds by canonical file name, independently of repository directories.
    private static int Run(string output, string root) {

        var machines = CliWorldVocabulary.EnsureInstalled();
        Directory.CreateDirectory(path: output);
        var pending = new Queue<string>();
        var visited = new HashSet<string>(comparer: StringComparer.Ordinal);
        var names = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        pending.Enqueue(item: Path.Combine(path1: root, path2: "puck.world.json"));
        while (pending.TryDequeue(result: out var path)) {
            if (!visited.Add(item: path)) {
                continue;
            }
            var relative = Path.GetRelativePath(path: path, relativeTo: root);

            if (relative.StartsWith(comparisonType: StringComparison.Ordinal, value: "..") || Path.IsPathRooted(path: relative)) {
                throw new InvalidDataException(message: $"World reference escapes the authored worlds directory: {relative}");
            }
            var name = Path.GetFileName(path: path);

            if (names.TryGetValue(key: name, value: out var previous) && (previous != path)) {
                throw new InvalidDataException(message: $"Hosted world name collision: {previous} and {path}");
            }
            names[name] = path;
            if (!WorldDefinitionFileSource.TryComposeDocumentTree(path: path, reason: out var reason, tree: out var tree)) {
                throw new InvalidDataException(message: $"Cannot compose {relative}: {reason}");
            }
            if (tree!["references"] is JsonArray references) {
                foreach (var reference in references) {
                    if (reference?["document"]?.GetValue<string>() is not { Length: > 0 } document) {
                        continue;
                    }
                    var neighbour = Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: path)!, path2: document));

                    pending.Enqueue(item: neighbour);
                    reference["document"] = Path.GetFileName(path: neighbour);
                }
            }
            if (!WorldDefinitionFileSource.TryParseComposed(
                definition: out var definition, json: tree.ToJsonString(), neighbours: null,
                reason: out reason, sourceName: path, validateAdjacencyClaims: false
            )) {
                throw new InvalidDataException(message: $"Cannot validate {relative}: {reason}");
            }
            if (!WorldDefinitionValidator.TryValidateLocally(definition!, machines, out reason)) {
                throw new InvalidDataException(message: $"Cannot admit machines in {relative}: {reason}");
            }
            File.WriteAllBytes(Path.Combine(path1: output, path2: name), WorldDefinitionSerialization.Serialize(definition: definition!));
        }
        Console.WriteLine(value: $"Prepared {visited.Count} hosted world definitions, with Puck as the primary world.");
        return 0;
    }
}
