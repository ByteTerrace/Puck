using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Cli.Automation;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Azure;

public static partial class AzureCommand {
    // The hosted inventory of a directory 'puck world prepare' wrote: its composed documents, in ordinal order. A host
    // release keeps the authored worlds directory beside them (.puck sources, schemas, notes), which is never an inventory
    // member.
    private static string[] HostedWorldFiles(string directory) => [.. Directory.EnumerateFiles(
        path: directory,
        searchPattern: ("*" + WorldDocumentName.DocumentSuffix)
    ).Order(comparer: StringComparer.Ordinal)];
    /// <summary>The file the release preparation records the engine of the release it prepared in, which the production
    /// verification checks the running worker against.</summary>
    private const string WorldHostEnginePath = "artifacts/world-host.engine";
    private static void PrepareOfficialWorldRelease(string outputDirectory) {
        var outputs = Outputs();
        var engine = HostRelease(outputs: outputs).Engine;

        PrepareOfficialWorldReleasePackage(
            outputs,
            CliFiles.ReadJson(path: "artifacts/release-source.json")["commit"]!.GetValue<string>(),
            engine,
            "artifacts/azure/silo-worlds",
            LocalWorldsDirectory,
            outputDirectory
        );
        File.WriteAllText(contents: (engine + "\n"), path: WorldHostEnginePath);
    }

    /// <summary>Applies deployment-owned endpoint/admission bindings before hashing the official release inventory.
    /// All composed co-hosted worlds become explicit pinned members of the maintenance group.</summary>
    /// <param name="outputs">The deployment's outputs.</param>
    /// <param name="revision">The full source commit the release was built from.</param>
    /// <param name="engine">The payload identity of the host release that runs the release.</param>
    /// <param name="sourceDirectory">The directory <c>puck world prepare</c> wrote the composed documents into.</param>
    /// <param name="assetDirectory">The local copy of the asset layout the host release ships the documents into
    /// (<see cref="LocalWorldsDirectory"/>): every document validates beside it, as the deployed silo resolves its
    /// relative paths beside <see cref="WorldHostLayout.WorldsDirectory"/>. Never packaged; the engine's release retains it.</param>
    /// <param name="outputDirectory">The release package directory written.</param>
    /// <exception cref="InvalidDataException">The revision or engine is not a full pin, the inventory lacks the configured
    /// primary world, or a document does not validate beside <paramref name="assetDirectory"/>.</exception>
    public static void PrepareOfficialWorldReleasePackage(JsonNode outputs, string revision, string engine, string sourceDirectory, string assetDirectory, string outputDirectory) {
        if (
            !Regex.IsMatch(
            input: revision,
            pattern: CommitPattern
        ) ||
            !Puck.Assets.ContentPin.TryParse(pin: out _, text: engine)
        ) {
            throw new InvalidDataException(message: "official release preparation requires a full source revision and engine pin");
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
        var files = HostedWorldFiles(directory: sourceDirectory);

        if (
            (files.Length == 0) ||
            !files.Any(predicate: file => (Path.GetFileName(path: file) == WorldDocumentName.DocumentFile(name: primary)))
        ) {
            throw new InvalidDataException(message: "official package must contain the configured primary world and its complete composed inventory");
        }
        // The inventory silo holds a generated key, so it is deleted however the step ends.
        using (var temporary = RunDirectory.Create(
            keepOnFailure: false,
            prefix: "puck-world-release-prepare-"
        )) {
            var package = Path.Combine(
                path1: temporary.Path,
                path2: "package"
            );

            Directory.CreateDirectory(path: package);
            // This temporary silo is only a validated inventory for manifest preparation; its key and store
            // never become package artifacts or production deployment inputs. Its document directory is the local copy
            // of the layout the deployed silo's documentDirectory names, which the documents validate beside.
            var silo = SiloDocument(
                "",
                owner,
                new JsonObject {
                    ["type"] = "directory",
                    ["settings"] = new JsonObject {
                        ["path"] = Puck.Abstractions.PuckPaths.Slashed(path: Path.Combine(
                    path1: temporary.Path,
                    path2: "store"
                )),
                    },
                },
                primary,
                documentDirectory: Puck.Abstractions.PuckPaths.Normalize(path: Path.GetFullPath(path: assetDirectory))
            );

            silo["stateDir"] = Puck.Abstractions.PuckPaths.Slashed(path: Path.Combine(
                path1: temporary.Path,
                path2: "state"
            ));
            silo["doors"]!["budget"] = files.Length;
            var rows = silo["worlds"]!.AsArray();
            var prototype = rows[0]!.DeepClone(); rows.Clear();
            foreach (var file in files) {
                var name = WorldDocumentName.OfDocumentFile(path: Path.GetFileName(path: file));

                _ = SafeName.Parse(candidate: name);
                // Only parsed here and published undrawn, draw sites and all: WorldReleaseCommand.Prepare below reads
                // every definition this writes back through the admission proof.
                if (!WorldDefinitionFileSource.TryParseDocument(
                    definition: out var parsed,
                    json: File.ReadAllText(path: file),
                    reason: out var reason,
                    sourceName: file
                )) {
                    throw new InvalidDataException(message: reason);
                }
                var definition = parsed!;

                if (name == primary) {
                    if (definition.HostRaw is null) { throw new InvalidDataException(message: "the official primary world must declare its host defaults before endpoint binding"); }
                    definition = definition with { HostRaw = definition.Host with { Authority = $"{host}:{port}", Listen = $"{DeploymentListenAddress}:{port}" } };
                    if (outputs["worldMcpConfiguration"]?["value"]?["admission"] is JsonArray delegated) {
                        var world = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: definition))!.AsObject();

                        world["admission"] ??= new JsonArray();
                        foreach (var participant in delegated) { world["admission"]!.AsArray().Add(item: participant!.DeepClone()); }
                        if (!WorldDefinitionFileSource.TryParseDocument(
                            definition: out parsed,
                            json: world.ToJsonString(),
                            reason: out reason,
                            sourceName: file
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
                var keyPath = Puck.Abstractions.PuckPaths.Slashed(path: Path.Combine(
                    path1: temporary.Path,
                    path2: (name + ".pk8")
                ));

                File.WriteAllBytes(
                    keyPath,
                    key.ExportPkcs8PrivateKey()
                );
                var row = prototype.DeepClone(); row["world"] = name; row["federation"]!["keyFile"] = keyPath; rows.Add(item: row);
            }
            var siloPath = Path.Combine(
                path1: temporary.Path,
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
                engine,
                WorldReleaseManifest.CurrentPersistenceContract,
                WorldReleaseManifest.CurrentPeerProtocolContract
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
        }
    }
}
