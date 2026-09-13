using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Cli.Automation;
using Puck.World;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static void PrepareOfficialWorldRelease(string outputDirectory) =>
        PrepareOfficialWorldReleasePackage(Outputs(), CliFiles.ReadJson("artifacts/release-source.json")["commit"]!.GetValue<string>(),
            File.ReadAllText("artifacts/world-silo.digest").Trim(), "artifacts/azure/silo-worlds", outputDirectory);

    /// <summary>Applies deployment-owned endpoint/admission bindings before hashing the official release inventory.
    /// All composed co-hosted worlds become explicit pinned members of the maintenance group.</summary>
    internal static void PrepareOfficialWorldReleasePackage(JsonNode outputs, string revision, string image, string sourceDirectory, string outputDirectory) {
        if (!Regex.IsMatch(revision, CommitPattern) || !Regex.IsMatch(image, "\\A[a-z0-9.-]+/[a-z0-9/_-]+@sha256:[a-f0-9]{64}\\z")) {
            throw new InvalidDataException("official release preparation requires a full source revision and immutable registry image");
        }
        _ = CliWorldVocabulary.EnsureInstalled();
        var configuration = Value(outputs, "worldSiloConfiguration");
        var owner = Text(Value(outputs, "worldSiloOwner"));
        var primary = Text(configuration["worldName"]);
        var host = $"{configuration["dns"]!["recordName"]}.{configuration["dns"]!["zoneName"]}";
        var port = Text(configuration["port"]);
        var files = Directory.EnumerateFiles(sourceDirectory, "*.world.json").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0 || !files.Any(file => Path.GetFileName(file) == primary + ".world.json")) {
            throw new InvalidDataException("official package must contain the configured primary world and its complete composed inventory");
        }
        var temporary = Directory.CreateTempSubdirectory("puck-world-release-prepare-");
        try {
            var package = Path.Combine(temporary.FullName, "package");
            Directory.CreateDirectory(package);
            // This temporary silo is only a validated inventory for manifest preparation; its key and store
            // never become package artifacts or production deployment inputs.
            var silo = SiloDocument("", owner,
                new JsonObject { ["type"] = "directory", ["settings"] = new JsonObject { ["path"] = Path.Combine(temporary.FullName, "store").Replace('\\', '/') } }, primary);
            silo["stateDir"] = Path.Combine(temporary.FullName, "state").Replace('\\', '/');
            silo["doors"]!["budget"] = files.Length;
            var rows = silo["worlds"]!.AsArray();
            var prototype = rows[0]!.DeepClone(); rows.Clear();
            foreach (var file in files) {
                var name = Path.GetFileName(file)[..^".world.json".Length];
                _ = SafeName.Parse(name);
                // A published definition can still contain boot draw sites; it is not a saved runtime document.
                if (!WorldDefinitionFileSource.TryParseComposed(File.ReadAllText(file), file, null, false, out var parsed, out var reason)) {
                    throw new InvalidDataException(reason);
                }
                var definition = parsed!;
                if (name == primary) {
                    if (definition.HostRaw is null) { throw new InvalidDataException("the official primary world must declare its host defaults before endpoint binding"); }
                    definition = definition with { HostRaw = definition.Host with { Authority = $"{host}:{port}", Listen = $"0.0.0.0:{port}" } };
                    if (outputs["worldMcpConfiguration"]?["value"]?["admission"] is JsonArray delegated) {
                        var world = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition))!.AsObject();
                        world["admission"] ??= new JsonArray();
                        foreach (var participant in delegated) { world["admission"]!.AsArray().Add(participant!.DeepClone()); }
                        if (!WorldDefinitionFileSource.TryParseComposed(world.ToJsonString(), file, null, false, out parsed, out reason)) {
                            throw new InvalidDataException(reason);
                        }
                        definition = parsed!;
                    }
                }
                File.WriteAllBytes(Path.Combine(package, Path.GetFileName(file)), WorldDefinitionSerialization.Serialize(definition));
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var keyPath = Path.Combine(temporary.FullName, name + ".pk8").Replace('\\', '/');
                File.WriteAllBytes(keyPath, key.ExportPkcs8PrivateKey());
                var row = prototype.DeepClone(); row["world"] = name; row["federation"]!["keyFile"] = keyPath; rows.Add(row);
            }
            var siloPath = Path.Combine(temporary.FullName, "silo.json");
            CliFiles.WriteJson(siloPath, silo);
            WorldReleaseCommand.Prepare(package, siloPath, null, revision[..12], revision, image[(image.LastIndexOf('@') + 1)..],
                "puck.world.persistence.v1", "puck.world.peer.v1");
            Directory.CreateDirectory(outputDirectory);
            // The manifest is the local publication point; an interrupted copy cannot pass complete file verification.
            foreach (var file in Directory.EnumerateFiles(package).Where(file => Path.GetFileName(file) != "release.json")) {
                File.Copy(file, Path.Combine(outputDirectory, Path.GetFileName(file)), overwrite: true);
            }
            File.Copy(Path.Combine(package, "release.json"), Path.Combine(outputDirectory, "release.json"), overwrite: true);
        } finally { temporary.Delete(recursive: true); }
    }
}
