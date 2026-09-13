using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>Operator-facing release preparation and local deployment-group inspection commands.</summary>
internal static class WorldReleaseCommand {
    public static Command Create() {
        var package = new Argument<string>("package-directory") { Description = "Package directory containing composed worlds and release artifacts." };
        var silo = new Option<string>("--silo") { Description = "Validated silo configuration whose owner/world rows name the composed definitions.", Required = true };
        var output = new Option<string>("--output") { Description = "Manifest path (defaults to package-directory/release.json)." };
        var label = new Option<string>("--label") { Required = true };
        var revision = new Option<string>("--source-revision") { Required = true };
        var image = new Option<string>("--engine-image-digest") { Required = true };
        var persistence = new Option<string>("--persistence-contract") { Required = true };
        var peer = new Option<string>("--peer-protocol-contract") { Required = true };
        var prepare = new Command("prepare", "Create and verify an immutable hosted-world release manifest.") { package, silo, output, label, revision, image, persistence, peer };
        prepare.SetAction(parse => Prepare(
            packageDirectory: Path.GetFullPath(parse.GetRequiredValue(package)),
            siloPath: Path.GetFullPath(parse.GetRequiredValue(silo)),
            outputPath: parse.GetValue(output),
            label: parse.GetRequiredValue(label),
            sourceRevision: parse.GetRequiredValue(revision),
            engineImageDigest: parse.GetRequiredValue(image),
            persistenceContract: parse.GetRequiredValue(persistence),
            peerProtocolContract: parse.GetRequiredValue(peer)));

        var status = new Command("status", "Show a validated durable deployment-group state file.");
        var statusPath = new Argument<string>("group-file");
        status.Arguments.Add(statusPath);
        status.SetAction(parse => Status(Path.GetFullPath(parse.GetRequiredValue(statusPath))));

        return new Command("release", "Prepare and inspect hosted-world deployment groups.") { prepare, status };
    }

    private static int Prepare(string packageDirectory, string siloPath, string? outputPath, string label, string sourceRevision, string engineImageDigest, string persistenceContract, string peerProtocolContract) {
        if (!Directory.Exists(packageDirectory)) {
            throw new DirectoryNotFoundException(packageDirectory);
        }
        if (!WorldSiloDefinitionSerialization.TryLoadFile(siloPath, out var silo, out var siloReason)) {
            throw new InvalidDataException(siloReason);
        }
        if (silo!.Worlds.Count == 0) {
            throw new InvalidDataException("release preparation requires a non-empty silo world inventory");
        }
        var packageRoot = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(outputPath ?? Path.Combine(packageRoot, "release.json"));
        var destinationInsidePackage = destination.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(destination, packageRoot, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(destination, packageRoot, StringComparison.OrdinalIgnoreCase)) {
            throw new InvalidDataException("release manifest output must be a file path");
        }
        if (File.Exists(destination) && destinationInsidePackage && !IsReleaseManifest(destination)) {
            throw new InvalidDataException($"refusing to overwrite an existing non-release package file '{destination}'");
        }
        var machines = CliWorldVocabulary.EnsureInstalled();
        var definitions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var definitionFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var artifacts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var definitionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in silo.Worlds.OrderBy(static row => row.Owner).ThenBy(static row => row.World.ToString(), StringComparer.Ordinal)) {
            var definitionPath = FindComposedDefinition(packageRoot, row.World.ToString());
            var relative = Path.GetRelativePath(packageRoot, definitionPath).Replace(Path.DirectorySeparatorChar, '/');
            if (!WorldDefinitionFileSource.TryParseComposed(
                json: File.ReadAllText(definitionPath),
                sourceName: definitionPath,
                neighbours: null,
                validateAdjacencyClaims: false,
                definition: out var definition,
                reason: out var definitionReason,
                catalog: machines
            ) || !WorldDefinitionValidator.TryValidateLocally(definition!, machines, out definitionReason)) {
                throw new InvalidDataException($"composed definition '{relative}' is invalid: {definitionReason}");
            }
            var canonical = WorldDefinitionSerialization.Serialize(definition!);
            if (!canonical.AsSpan().SequenceEqual(File.ReadAllBytes(definitionPath))) {
                throw new InvalidDataException($"definition '{relative}' is not a canonical composed world output; run 'puck world prepare' first");
            }
            var identity = $"{row.Owner:D}/{row.World}";
            if (!definitions.TryAdd(identity, FullHash(canonical))) {
                throw new InvalidDataException($"release definition identity '{identity}' is duplicated");
            }
            definitionFiles.Add(identity, relative);
            definitionPaths.Add(Path.GetFullPath(definitionPath));
        }
        foreach (var path in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)) {
            var fullPath = Path.GetFullPath(path);
            if (destinationInsidePackage && string.Equals(fullPath, destination, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            var relative = Path.GetRelativePath(packageDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
            if (definitionPaths.Contains(fullPath)) {
                continue;
            }
            artifacts[relative] = FullHash(File.ReadAllBytes(path));
        }
        var manifest = new WorldReleaseManifest { Label = label, SourceRevision = sourceRevision, EngineImageDigest = engineImageDigest, Definitions = definitions, DefinitionFiles = definitionFiles, Artifacts = artifacts, PersistenceContract = persistenceContract, PeerProtocolContract = peerProtocolContract };
        if (!WorldReleaseManifest.TryVerify(manifest, packageDirectory, out var manifestReason)) {
            throw new InvalidDataException(manifestReason);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, WorldReleaseManifest.Canonicalize(manifest));
        Console.WriteLine($"Prepared release {manifest.Identity} ({manifest.Label}) with {definitions.Count} definitions and {artifacts.Count} artifacts.");
        return 0;
    }

    private static string FindComposedDefinition(string packageDirectory, string worldName) {
        var candidates = Directory.EnumerateFiles(packageDirectory, "*.world.json", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path)[..^".world.json".Length], worldName, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length == 0) {
            throw new InvalidDataException($"silo world '{worldName}' has no composed .world.json package output");
        }
        if (candidates.Length > 1) {
            throw new InvalidDataException($"silo world '{worldName}' maps to multiple composed package outputs");
        }
        return Path.GetFullPath(candidates[0]);
    }

    private static bool IsReleaseManifest(string path) {
        try {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("schema", out var schema) && string.Equals(schema.GetString(), WorldReleaseManifest.CurrentSchema, StringComparison.Ordinal);
        }
        catch (JsonException) {
            return false;
        }
    }

    private static string FullHash(byte[] bytes) => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static int Status(string path) {
        if (!File.Exists(path)) {
            throw new FileNotFoundException("deployment-group state file was not found", path);
        }
        var record = WorldReleaseGroupStore.DeserializeValidated(File.ReadAllBytes(path));
        Console.WriteLine(JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
