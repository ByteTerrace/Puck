#!/usr/bin/env dotnet
#:property PublishAot=false
#:project ../src/Puck.World.Schema/Puck.World.Schema.csproj
#:project ../src/Puck.World.Client/Puck.World.Client.csproj
#:project ../src/Puck.World.Addons/Puck.World.Addons.csproj

using System.Text.Json.Nodes;
using Puck.World;

// Compose the primary world and its referenced neighbours with the engine's own composer.
// Hosted storage addresses worlds by canonical file name, independently of repository directories.
if (args.Length != 2) {
    Console.Error.WriteLine("Usage: dotnet run build/Prepare-WorldSilo.cs -- <worlds-directory> <output-directory>");
    return 1;
}
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Puck.World.Client.WorldSchemaVocabularyHooks.Install(
    postRenderExtensionCheck: WorldPostRenderExtensions.IsShipped,
    probeKindCheck: WorldProbeKinds.IsShipped,
    screenMachineCartridgeCheck: WorldScreenMachineEngines.CompilesCartridges,
    screenMachineEngineCheck: WorldScreenMachineEngines.IsRegistered
);
Directory.CreateDirectory(output);
var pending = new Queue<string>();
var visited = new HashSet<string>(StringComparer.Ordinal);
var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
pending.Enqueue(Path.Combine(root, "puck.world.json"));
while (pending.TryDequeue(out var path)) {
    if (!visited.Add(path)) {
        continue;
    }
    var relative = Path.GetRelativePath(root, path);
    if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) {
        throw new InvalidDataException($"World reference escapes the authored worlds directory: {relative}");
    }
    var name = Path.GetFileName(path);
    if (names.TryGetValue(name, out var previous) && previous != path) {
        throw new InvalidDataException($"Hosted world name collision: {previous} and {path}");
    }
    names[name] = path;
    if (!WorldDefinitionFileSource.TryComposeDocumentTree(path: path, tree: out var tree, reason: out var reason)) {
        throw new InvalidDataException($"Cannot compose {relative}: {reason}");
    }
    if (tree!["references"] is JsonArray references) {
        foreach (var reference in references) {
            if (reference?["document"]?.GetValue<string>() is not { Length: > 0 } document) {
                continue;
            }
            var neighbour = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, document));
            pending.Enqueue(neighbour);
            reference["document"] = Path.GetFileName(neighbour);
        }
    }
    if (!WorldDefinitionFileSource.TryParseComposed(
        definition: out var definition, json: tree.ToJsonString(), neighbours: null,
        reason: out reason, sourceName: path, validateAdjacencyClaims: false
    )) {
        throw new InvalidDataException($"Cannot validate {relative}: {reason}");
    }
    File.WriteAllBytes(Path.Combine(output, name), WorldDefinitionSerialization.Serialize(definition: definition!));
}
Console.WriteLine($"Prepared {visited.Count} hosted world definitions, with Puck as the primary world.");
return 0;
