using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Cli.Automation;
using Puck.World;

namespace Puck.Cli.Azure;

internal static partial class AzureCommand {
    private static void PrepareOfficialWorldRelease(string outputDirectory) =>
        PrepareOfficialWorldReleasePackage(
            Outputs(),
            CliFiles.ReadJson(path: "artifacts/release-source.json")["commit"]!.GetValue<string>(),
            File.ReadAllText(path: "artifacts/world-silo.digest").Trim(),
            "artifacts/azure/silo-worlds",
            outputDirectory
        );

    /// <summary>Applies deployment-owned endpoint/admission bindings before hashing the official release inventory.
    /// All composed co-hosted worlds become explicit pinned members of the maintenance group.</summary>
    internal static void PrepareOfficialWorldReleasePackage(JsonNode outputs, string revision, string image, string sourceDirectory, string outputDirectory) {
        if (
            !Regex.IsMatch(
            input: revision,
            pattern: CommitPattern
        ) ||
            !Regex.IsMatch(
            input: image,
            pattern: "\\A[a-z0-9.-]+/[a-z0-9/_-]+@sha256:[a-f0-9]{64}\\z"
        )
        ) {
            throw new InvalidDataException(message: "official release preparation requires a full source revision and immutable registry image");
        }
        _ = CliWorldVocabulary.EnsureInstalled();
        var configuration = Value(
            key: "worldSiloConfiguration",
            outputs: outputs
        );
        var owner = Text(value: Value(
            key: "worldSiloOwner",
            outputs: outputs
        ));
        var primary = Text(value: configuration["worldName"]);
        var host = $"{configuration["dns"]!["recordName"]}.{configuration["dns"]!["zoneName"]}";
        var port = Text(value: configuration["port"]);
        var files = Directory.EnumerateFiles(
            path: sourceDirectory,
            searchPattern: "*.world.json"
        ).Order(comparer: StringComparer.Ordinal).ToArray();

        if (
            (files.Length == 0) ||
            !files.Any(predicate: file => (Path.GetFileName(path: file) == (primary + ".world.json")))
        ) {
            throw new InvalidDataException(message: "official package must contain the configured primary world and its complete composed inventory");
        }
        var temporary = Directory.CreateTempSubdirectory(prefix: "puck-world-release-prepare-");

        try {
            var package = Path.Combine(
                path1: temporary.FullName,
                path2: "package"
            );

            Directory.CreateDirectory(path: package);
            // This temporary silo is only a validated inventory for manifest preparation; its key and store
            // never become package artifacts or production deployment inputs.
            var silo = SiloDocument(
                "",
                owner,
                new JsonObject {
                    ["type"] = "directory",
                    ["settings"] = new JsonObject {
                        ["path"] = Path.Combine(
                    path1: temporary.FullName,
                    path2: "store"
                ).Replace(
                    newChar: '/',
                    oldChar: '\\'
                ),
                    },
                },
                primary
            );

            silo["stateDir"] = Path.Combine(
                path1: temporary.FullName,
                path2: "state"
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );
            silo["doors"]!["budget"] = files.Length;
            var rows = silo["worlds"]!.AsArray();
            var prototype = rows[0]!.DeepClone(); rows.Clear();
            foreach (var file in files) {
                var name = Path.GetFileName(path: file)[..^".world.json".Length];

                _ = SafeName.Parse(candidate: name);
                // A published definition can still contain boot draw sites; it is not a saved runtime document.
                if (!WorldDefinitionFileSource.TryParseComposed(
                    File.ReadAllText(path: file),
                    file,
                    null,
                    false,
                    out var parsed,
                    out var reason
                )) {
                    throw new InvalidDataException(message: reason);
                }
                var definition = parsed!;

                if (name == primary) {
                    if (definition.HostRaw is null) { throw new InvalidDataException(message: "the official primary world must declare its host defaults before endpoint binding"); }
                    definition = definition with { HostRaw = definition.Host with { Authority = $"{host}:{port}", Listen = $"0.0.0.0:{port}" } };
                    if (outputs["worldMcpConfiguration"]?["value"]?["admission"] is JsonArray delegated) {
                        var world = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: definition))!.AsObject();

                        world["admission"] ??= new JsonArray();
                        foreach (var participant in delegated) { world["admission"]!.AsArray().Add(item: participant!.DeepClone()); }
                        if (!WorldDefinitionFileSource.TryParseComposed(
                            world.ToJsonString(),
                            file,
                            null,
                            false,
                            out parsed,
                            out reason
                        )) {
                            throw new InvalidDataException(message: reason);
                        }
                        definition = parsed!;
                    }
                }
                File.WriteAllBytes(
                    Path.Combine(
                        path1: package,
                        path2: Path.GetFileName(path: file)
                    ),
                    WorldDefinitionSerialization.Serialize(definition: definition)
                );
                using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
                var keyPath = Path.Combine(
                    path1: temporary.FullName,
                    path2: (name + ".pk8")
                ).Replace(
                    newChar: '/',
                    oldChar: '\\'
                );

                File.WriteAllBytes(
                    keyPath,
                    key.ExportPkcs8PrivateKey()
                );
                var row = prototype.DeepClone(); row["world"] = name; row["federation"]!["keyFile"] = keyPath; rows.Add(item: row);
            }
            var siloPath = Path.Combine(
                path1: temporary.FullName,
                path2: "silo.json"
            );

            CliFiles.WriteJson(
                path: siloPath,
                value: silo
            );
            WorldReleaseCommand.Prepare(
                package,
                siloPath,
                null,
                revision[..12],
                revision,
                image[(image.LastIndexOf(value: '@') + 1)..],
                "puck.world.persistence.v1",
                "puck.world.peer.v1"
            );
            Directory.CreateDirectory(path: outputDirectory);
            // The manifest is the local publication point; an interrupted copy cannot pass complete file verification.
            foreach (var file in Directory.EnumerateFiles(path: package).Where(predicate: file => (Path.GetFileName(path: file) != "release.json"))) {
                File.Copy(
                    file,
                    Path.Combine(
                        path1: outputDirectory,
                        path2: Path.GetFileName(path: file)
                    ),
                    overwrite: true
                );
            }
            File.Copy(
                Path.Combine(
                    path1: package,
                    path2: "release.json"
                ),
                Path.Combine(
                    path1: outputDirectory,
                    path2: "release.json"
                ),
                overwrite: true
            );
        } finally { temporary.Delete(recursive: true); }
    }
}
