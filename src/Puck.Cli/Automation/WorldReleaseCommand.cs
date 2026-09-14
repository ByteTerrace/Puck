using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Puck.Cli.Azure;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>Operator-facing release preparation and durable deployment-group inspection commands.</summary>
internal static class WorldReleaseCommand {
    internal static int Prepare(string packageDirectory, string siloPath, string? outputPath, string label, string sourceRevision, string engineImageDigest, string persistenceContract, string peerProtocolContract) {
        if (!Directory.Exists(path: packageDirectory)) {
            throw new DirectoryNotFoundException(message: packageDirectory);
        }
        if (!WorldSiloDefinitionSerialization.TryLoadFile(
            siloPath,
            out var silo,
            out var siloReason
        )) {
            throw new InvalidDataException(message: siloReason);
        }
        if (silo!.Worlds.Count == 0) {
            throw new InvalidDataException(message: "release preparation requires a non-empty silo world inventory");
        }
        var packageRoot = Path.GetFullPath(path: packageDirectory).TrimEnd(trimChar: Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(path: (outputPath ?? Path.Combine(
            path1: packageRoot,
            path2: "release.json"
        )));
        var destinationInsidePackage = (destination.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: (packageRoot + Path.DirectorySeparatorChar)
        ) || string.Equals(
            a: destination,
            b: packageRoot,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ));

        if (string.Equals(
            a: destination,
            b: packageRoot,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            throw new InvalidDataException(message: "release manifest output must be a file path");
        }
        if (
            File.Exists(path: destination) &&
            destinationInsidePackage &&
            !IsReleaseManifest(path: destination)
        ) {
            throw new InvalidDataException(message: $"refusing to overwrite an existing non-release package file '{destination}'");
        }
        var machines = CliWorldVocabulary.EnsureInstalled();
        var definitions = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var definitionFiles = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var artifacts = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var definitionPaths = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var row in silo.Worlds.OrderBy(keySelector: static row => row.Owner).ThenBy(
            static row => row.World.ToString(),
            StringComparer.Ordinal
        )) {
            var definitionPath = FindComposedDefinition(
                packageDirectory: packageRoot,
                worldName: row.World.ToString()
            );
            var relative = Path.GetRelativePath(
                path: definitionPath,
                relativeTo: packageRoot
            ).Replace(
                newChar: '/',
                oldChar: Path.DirectorySeparatorChar
            );

            if (
                !WorldDefinitionFileSource.TryParseComposed(
                json: File.ReadAllText(path: definitionPath),
                sourceName: definitionPath,
                neighbours: null,
                validateAdjacencyClaims: false,
                definition: out var definition,
                reason: out var definitionReason,
                catalog: machines
            ) ||
                !WorldDefinitionValidator.TryValidateLocally(
                definition: definition!,
                machines: machines,
                reason: out definitionReason
            )
            ) {
                throw new InvalidDataException(message: $"composed definition '{relative}' is invalid: {definitionReason}");
            }
            var canonical = WorldDefinitionSerialization.Serialize(definition: definition!);

            if (!canonical.AsSpan().SequenceEqual(File.ReadAllBytes(path: definitionPath))) {
                throw new InvalidDataException(message: $"definition '{relative}' is not a canonical composed world output; run 'puck world prepare' first");
            }
            var identity = $"{row.Owner:D}/{row.World}";

            if (!definitions.TryAdd(
                key: identity,
                value: FullHash(bytes: canonical)
            )) {
                throw new InvalidDataException(message: $"release definition identity '{identity}' is duplicated");
            }
            definitionFiles.Add(
                key: identity,
                value: relative
            );
            definitionPaths.Add(item: Path.GetFullPath(path: definitionPath));
        }
        foreach (var path in Directory.EnumerateFiles(
            path: packageRoot,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )) {
            var fullPath = Path.GetFullPath(path: path);

            if (
                destinationInsidePackage &&
                string.Equals(
                a: fullPath,
                b: destination,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )
            ) {
                continue;
            }
            var relative = Path.GetRelativePath(
                path: path,
                relativeTo: packageDirectory
            ).Replace(
                newChar: '/',
                oldChar: Path.DirectorySeparatorChar
            );

            if (definitionPaths.Contains(item: fullPath)) {
                continue;
            }
            artifacts[relative] = FullHash(bytes: File.ReadAllBytes(path: path));
        }
        var manifest = new WorldReleaseManifest {
            Artifacts = artifacts,
            CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
            DefinitionFiles = definitionFiles,
            Definitions = definitions,
            EngineImageDigest = engineImageDigest,
            Label = label,
            PeerProtocolContract = peerProtocolContract,
            PersistenceContract = persistenceContract,
            SourceRevision = sourceRevision,
        };

        if (!WorldReleaseManifest.TryVerify(
            manifest: manifest,
            packageDirectory: packageDirectory,
            reason: out var manifestReason
        )) {
            throw new InvalidDataException(message: manifestReason);
        }
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
        File.WriteAllBytes(
            destination,
            WorldReleaseManifest.Canonicalize(manifest: manifest)
        );
        Console.WriteLine(value: $"Prepared release {manifest.Identity} ({manifest.Label}) with {definitions.Count} definitions and {artifacts.Count} artifacts.");
        return 0;
    }
    internal static async Task<int> RunAsync(Func<Task<int>> action, bool recovery = false) {
        try { return await action().ConfigureAwait(continueOnCapturedContext: false); } catch (Exception error) {
            Console.Error.WriteLine(value: ((error is OperationCanceledException)
                ? "world release: canceled."
                : $"world release: {error.Message}"));
            if (recovery) { Console.Error.WriteLine(value: "Run 'puck world release status' to inspect the durable result; use 'puck world release resume' if an operation is unfinished."); }
            return ((error is OperationCanceledException)
                ? 130
                : 1
            );
        }
    }

    private static string FindComposedDefinition(string packageDirectory, string worldName) {
        var candidates = Directory.EnumerateFiles(
            path: packageDirectory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.world.json"
        )
            .Where(predicate: path => string.Equals(
            a: Path.GetFileName(path: path)[..^".world.json".Length],
            b: worldName,
            comparisonType: StringComparison.Ordinal
        ))
            .ToArray();

        if (candidates.Length == 0) {
            throw new InvalidDataException(message: $"silo world '{worldName}' has no composed .world.json package output");
        }
        if (candidates.Length > 1) {
            throw new InvalidDataException(message: $"silo world '{worldName}' maps to multiple composed package outputs");
        }
        return Path.GetFullPath(path: candidates[0]);
    }
    private static string FullHash(byte[] bytes) => ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)));
    private static bool IsReleaseManifest(string path) {
        try {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path: path));

            return (
                (document.RootElement.ValueKind == JsonValueKind.Object) &&
                document.RootElement.TryGetProperty(
                propertyName: "schema",
                value: out var schema
            ) &&
                string.Equals(
                a: schema.GetString(),
                b: WorldReleaseManifest.CurrentSchema,
                comparisonType: StringComparison.Ordinal
            )
            );
        } catch (JsonException) {
            return false;
        }
    }
    private static string NextAction(WorldReleaseGroupRecord record) => record.PendingPhase switch {
        WorldReleaseOperationPhase.Prepare => "Resume the pending operation; preparation has not drained the source.",
        WorldReleaseOperationPhase.Drain => "Resume drain and protected-root capture. Keep the source available for a failed-save retry.",
        WorldReleaseOperationPhase.Activate or WorldReleaseOperationPhase.Verify => "Resume the pending operation and verify the private candidate.",
        WorldReleaseOperationPhase.Recover => "Resume source recovery from the protected roots. Keep admission closed.",
        WorldReleaseOperationPhase.RecoverActivate => "Resume the restored source's private activation and admission publication.",
        WorldReleaseOperationPhase.Commit when (record.Admission == WorldReleaseAdmissionState.Closed) => "Resume the committed target and its admission publication. The pre-deployment save is no longer an automatic fallback.",
        _ when ((record.ActiveRelease is not null) && (record.Admission == WorldReleaseAdmissionState.Closed)) => "Resume the active release under fresh authority fences and publish its admission.",
        _ when record.RollbackEligible => "Roll back to the retained previous release or finalize this rollback window before deploying another release.",
        WorldReleaseOperationPhase.Commit => "Finalize the first deployment before preparing another release.",
        _ when (record.ActiveRelease is null) => "Prepare the first managed deployment.",
        _ => "Prepare the next release."
    };
    private static async Task<int> StatusAsync(string? path, bool json, CancellationToken cancellationToken) {
        WorldReleaseGroupRecord record;

        if (path is not null) {
            record = WorldReleaseGroupStore.DeserializeValidated(bytes: await File.ReadAllBytesAsync(
                Path.GetFullPath(path: path),
                cancellationToken
            ));
        } else {
            var snapshot = await AzureCommand.ReadWorldReleaseStatusAsync(cancellationToken: cancellationToken);

            if (snapshot is null) {
                Console.Error.WriteLine(value: "The configured world deployment has no managed release record. Establish its release identity before a managed deployment.");
                return 2;
            }
            record = snapshot.Value.Record;
        }
        var action = NextAction(record: record);

        if (json) {
            var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
            var document = JsonSerializer.SerializeToNode(
                options: options,
                value: record
            )!.AsObject();

            document["nextAction"] = action;
            Console.WriteLine(value: document.ToJsonString(options: options));
        } else {
            Console.WriteLine(value: $"Group: {record.DeploymentGroup}");
            Console.WriteLine(value: $"Active: {(record.ActiveRelease ?? "none (first deployment)")}");
            Console.WriteLine(value: $"Previous: {(record.PreviousRelease ?? "none")}");
            Console.WriteLine(value: $"Admission: {record.Admission}");
            Console.WriteLine(value: $"Rollback: {(record.RollbackEligible
                ? "eligible"
                : "unavailable")}");
            Console.WriteLine(value: $"Operation: {(record.PendingOperationId?.ToString(format: "D") ?? "none")}");
            Console.WriteLine(value: $"Phase: {(record.PendingPhase?.ToString() ?? "idle")}");
            if (record.RestorePoint is { } point) { Console.WriteLine(value: $"Intentional rewind: {point.PointId:D} ({point.Identity})"); }
            Console.WriteLine(value: $"Protected worlds: {record.RecoveryRoots.Count}; retained operations: {record.History.Count}");
            if (record.PendingFailure is { } failure) { Console.WriteLine(value: $"Failure: {failure}"); }
            Console.WriteLine(value: $"Next: {action}");
        }
        return 0;
    }

    public static Command Create() {
        var package = new Argument<string>(name: "package-directory") { Description = "Package directory containing composed worlds and release artifacts." };
        var silo = new Option<string>("--silo") { Description = "Validated silo configuration whose owner/world rows name the composed definitions.", Required = true };
        var output = new Option<string>("--output") { Description = "Manifest path (defaults to package-directory/release.json)." };
        var label = new Option<string>("--label") { Required = true };
        var revision = new Option<string>("--source-revision") { Required = true };
        var image = new Option<string>("--engine-image-digest") { Required = true };
        var persistence = new Option<string>("--persistence-contract") { Required = true };
        var peer = new Option<string>("--peer-protocol-contract") { Required = true };
        var prepare = new Command(
            description: "Create and verify an immutable hosted-world release manifest.",
            name: "prepare"
        ) { package, silo, output, label, revision, image, persistence, peer };

        prepare.SetAction(action: (parse, _) => RunAsync(() => Task.FromResult(result: Prepare(
            packageDirectory: Path.GetFullPath(path: parse.GetRequiredValue(argument: package)),
            siloPath: Path.GetFullPath(path: parse.GetRequiredValue(option: silo)),
            outputPath: parse.GetValue(option: output),
            label: parse.GetRequiredValue(option: label),
            sourceRevision: parse.GetRequiredValue(option: revision),
            engineImageDigest: parse.GetRequiredValue(option: image),
            persistenceContract: parse.GetRequiredValue(option: persistence),
            peerProtocolContract: parse.GetRequiredValue(option: peer)
        ))));

        var status = new Command(
            description: "Inspect the configured Azure world deployment, or a saved deployment-group file.",
            name: "status"
        );
        var statusPath = new Argument<string?>(name: "group-file") { Arity = ArgumentArity.ZeroOrOne };
        var json = new Option<bool>("--json") { Description = "Write the full validated state with named phases and the next operator action." };

        status.Arguments.Add(item: statusPath);
        status.Options.Add(item: json);
        status.SetAction(action: (parse, cancellationToken) => RunAsync(() => StatusAsync(
            parse.GetValue(argument: statusPath),
            parse.GetValue(option: json),
            cancellationToken
        )));

        var finalize = new Command(
            description: "Close the admitted release's rollback window, retaining recovery history and artifacts.",
            name: "finalize"
        );

        finalize.SetAction(action: (_, token) => RunAsync(
            async () => {
                var finalized = await AzureCommand.FinalizeWorldReleaseAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: $"Finalized {finalized.Record.ActiveRelease}. The previous release is no longer eligible for ordinary rollback; recovery history and artifacts are retained.");
                return 0;
            },
            recovery: true
        ));

        var sourceManifest = new Argument<string>(name: "source-manifest");
        var targetManifest = new Argument<string>(name: "target-manifest");
        var fixture = new Argument<string>(name: "fixture-directory");
        var sourceImage = new Option<string>("--source-image") { Required = true };
        var targetImage = new Option<string>("--target-image") { Required = true };
        var evidenceDirectory = new Option<string>("--output") { Description = "Directory retaining isolated legs and qualification evidence.", Required = true };
        var qualificationSteps = new Option<int>("--steps") { DefaultValueFactory = _ => 60, Description = "Exact continuation steps per packaged leg (1–1024)." };
        var qualify = new Command(
            description: "Exercise exact packaged releases in both directions against an offline fixture.",
            name: "qualify"
        ) {
            sourceManifest, targetManifest, fixture, sourceImage, targetImage, evidenceDirectory, qualificationSteps,
        };

        qualify.SetAction(action: (parse, token) => RunAsync(async () => {
            var sourcePath = Path.GetFullPath(path: parse.GetRequiredValue(argument: sourceManifest));
            var targetPath = Path.GetFullPath(path: parse.GetRequiredValue(argument: targetManifest));
            var source = (JsonSerializer.Deserialize<WorldReleaseManifest>(ConfinedFile.ReadAllBytes(
                maximumBytes: (1024 * 1024),
                path: sourcePath
            )) ?? throw new InvalidDataException(message: "missing source manifest"));
            var target = (JsonSerializer.Deserialize<WorldReleaseManifest>(ConfinedFile.ReadAllBytes(
                maximumBytes: (1024 * 1024),
                path: targetPath
            )) ?? throw new InvalidDataException(message: "missing target manifest"));

            if (!WorldReleaseTransitionPolicy.TryPrepare(
                changes: out var changes,
                reason: out var reason,
                source: source,
                target: target
            )) { throw new InvalidDataException(message: reason); }
            var evidence = Path.GetFullPath(path: parse.GetRequiredValue(option: evidenceDirectory));
            var services = new ServiceCollection();

            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
            using var provider = services.BuildServiceProvider();
            WorldReleaseArchive? archive = null;

            if (changes.Count != 0) {
                archive = new(
                    provider.GetRequiredService<IObjectBlobStore>(),
                    new DirectoryObjectStorageTarget(Path.Combine(
                        path1: evidence,
                        path2: "packages",
                        path3: Guid.NewGuid().ToString(format: "N")
                    )),
                    Guid.NewGuid()
                );
                await archive.SaveAsync(
                    source,
                    Path.GetDirectoryName(path: sourcePath)!,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);
                await archive.SaveAsync(
                    target,
                    Path.GetDirectoryName(path: targetPath)!,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
            var runner = new WorldReleaseQualificationRunner(
                Path.GetFullPath(path: parse.GetRequiredValue(argument: fixture)),
                evidence,
                parse.GetRequiredValue(option: sourceImage),
                parse.GetRequiredValue(option: targetImage),
                parse.GetValue(option: qualificationSteps),
                archive
            );

            _ = await runner.RunAsync(
                cancellationToken: token,
                source: source,
                target: target
            ).ConfigureAwait(continueOnCapturedContext: false);
            return 0;
        }));

        var resume = new Command(
            description: "Resume the configured group's durable operation using its retained release inputs.",
            name: "resume"
        );

        resume.SetAction(action: (_, token) => RunAsync(
            async () => {
                var result = await AzureCommand.ResumeWorldReleaseAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: result.Detail);
                return ((result.Completed && !result.SourceRecovered)
                    ? 0
                    : 1
                );
            },
            recovery: true
        ));
        var deployPackage = new Argument<string>(name: "package-directory");
        var deployOperation = new Option<Guid?>("--operation");
        var deploy = new Command(
            description: "Export current state, qualify, and deploy an exact package while preserving authoritative progress.",
            name: "deploy"
        ) { deployPackage, deployOperation };

        deploy.SetAction(action: (parse, token) => RunAsync(
            async () => {
                var result = await AzureCommand.DeployWorldReleaseAsync(
                    parse.GetRequiredValue(argument: deployPackage),
                    parse.GetValue(option: deployOperation),
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: result.Detail);
                return ((result.Completed && !result.SourceRecovered)
                    ? 0
                    : 1
                );
            },
            recovery: true
        ));
        var rollbackOperation = new Option<Guid?>("--operation");
        var rollback = new Command(
            description: "Return to the retained previous release using current player progress.",
            name: "rollback"
        ) { rollbackOperation };

        rollback.SetAction(action: (parse, token) => RunAsync(
            async () => {
                var result = await AzureCommand.RollbackWorldReleaseAsync(
                    parse.GetValue(option: rollbackOperation),
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: result.Detail);
                return ((result.Completed && !result.SourceRecovered)
                    ? 0
                    : 1
                );
            },
            recovery: true
        ));
        var checkpointId = new Option<Guid?>("--request") { Description = "Stable recovery-point request ID for retrying an interrupted capture." };
        var checkpoint = new Command(
            description: "Retain a coherent closed-group recovery point without stopping gameplay.",
            name: "checkpoint"
        ) { checkpointId };

        checkpoint.SetAction(action: (parse, token) => RunAsync(async () => {
            var point = await AzureCommand.CaptureWorldReleasePointAsync(
                requestId: (parse.GetValue(option: checkpointId) ?? Guid.NewGuid()),
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false);

            Console.WriteLine(value: JsonSerializer.Serialize(
                point,
                new JsonSerializerOptions { WriteIndented = true }
            ));
            Console.WriteLine(value: $"Recovery point: {point.RequestId:D}; identity: {point.Identity}");
            return 0;
        }));
        var restorePoint = new Argument<Guid>(name: "recovery-point");
        var restoreOperation = new Option<Guid?>("--operation");
        var discardProgress = new Option<bool>("--discard-progress") { Description = "Explicitly acknowledge discarding this entire group's progress after the selected point." };
        var restore = new Command(
            description: "Preview a recovery point, or explicitly rewind the complete deployment group.",
            name: "restore"
        ) { restorePoint, restoreOperation, discardProgress };

        restore.SetAction(action: (parse, token) => RunAsync(
            async () => {
                var result = await AzureCommand.RestoreWorldReleaseAsync(
                    parse.GetRequiredValue(argument: restorePoint),
                    parse.GetValue(option: restoreOperation),
                    parse.GetValue(option: discardProgress),
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: result.Detail);
                return ((!parse.GetValue(option: discardProgress) || (result.Completed && !result.SourceRecovered))
                    ? 0
                    : 1
                );
            },
            recovery: true
        ));
        return new Command(
            description: "Prepare, deploy, roll back, inspect, and recover hosted-world deployment groups.",
            name: "release"
        ) { prepare, status, finalize, qualify, resume, deploy, rollback, checkpoint, restore, WorldReleaseExerciseCommand.Create() };
    }
}
