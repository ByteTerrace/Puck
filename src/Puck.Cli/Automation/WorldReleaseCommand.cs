using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Puck.Assets;
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

            if (!WorldDefinitionLoader.TryReadPublishable(
                catalog: machines,
                definition: out var definition,
                documentDirectory: WorldDocumentPaths.DirectoryOf(documentPath: definitionPath),
                json: File.ReadAllText(path: definitionPath),
                reason: out var definitionReason,
                sourceName: definitionPath
            )) {
                throw new InvalidDataException(message: $"composed definition '{relative}' is invalid: {definitionReason}");
            }
            var canonical = WorldDefinitionSerialization.Serialize(definition: definition);

            if (!canonical.AsSpan().SequenceEqual(other: File.ReadAllBytes(path: definitionPath))) {
                throw new InvalidDataException(message: $"definition '{relative}' is not a canonical composed world output; run 'puck world prepare' first");
            }
            var identity = $"{row.Owner:D}/{row.World}";

            if (!definitions.TryAdd(
                key: identity,
                value: ContentPin.Compute(content: canonical).ToString()
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
            artifacts[relative] = ContentPin.OfFile(path: path).ToString();
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

    // A durable operation that fails or is cancelled leaves a record behind, so its refusal also says where to look.
    // CliExit maps the exception to the exit code; this only adds the hint.
    private static async Task<int> WithRecoveryHintAsync(Func<Task<int>> action) {
        const string Hint = "Run 'puck world release status' to inspect the durable result; use 'puck world release resume' if an operation is unfinished.";

        try {
            return await action().ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) {
            Console.Error.WriteLine(value: Hint);

            throw;
        } catch (Exception error) {
            throw new InvalidOperationException(
                innerException: error,
                message: $"{error.Message} {Hint}"
            );
        }
    }
    private static string FindComposedDefinition(string packageDirectory, string worldName) {
        var candidates = Directory.EnumerateFiles(
            path: packageDirectory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: ("*" + WorldDocumentName.DocumentSuffix)
        )
            .Where(predicate: path => string.Equals(
            a: WorldDocumentName.OfDocumentFile(path: Path.GetFileName(path: path)),
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
                return CliExit.Refuse(
                    verb: "world release status",
                    what: "the configured world deployment",
                    why: "it has no managed release record; establish its release identity before a managed deployment"
                );
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
    // The stable ID of a durable operation: repeating a command with the same ID resumes that operation rather than
    // starting another.
    private static Option<Guid?> Operation() => new("--operation") { Description = "A stable operation ID; repeating the same target with it resumes its pending operation." };

    /// <summary>Creates the <c>world release</c> verb; <paramref name="clock"/> bounds its leases and qualification
    /// runs.</summary>
    /// <param name="clock">The CLI host's clock.</param>
    /// <param name="containers">The container engine <c>qualify</c> runs its legs in, or <see langword="null"/> for
    /// Docker.</param>
    /// <returns>The verb.</returns>
    public static Command Create(TimeProvider clock, IWorldReleaseQualificationContainers? containers = null) {
        var package = new Argument<string>(name: "package-directory") { Description = "Package directory containing composed worlds and release artifacts." };
        var silo = new Option<string>("--silo") { Description = "Validated silo configuration whose owner/world rows name the composed definitions.", Required = true };
        var output = CliOptions.Output(description: "The manifest's path; absent, release.json in the package directory.");
        var label = new Option<string>("--label") { Description = "The release's human-readable label.", Required = true };
        var revision = new Option<string>("--source-revision") { Description = "The full source commit the release was built from.", Required = true };
        var image = new Option<string>("--engine-image-digest") { Description = "The sha256 digest of the engine image that runs the release.", Required = true };
        var persistence = new Option<string>("--persistence-contract") { Description = "The persistence contract the release reads and writes, such as puck.world.persistence.v1.", Required = true };
        var peer = new Option<string>("--peer-protocol-contract") { Description = "The peer protocol contract the release speaks, such as puck.world.peer.v1.", Required = true };
        var prepare = new Command(
            description: "Create and verify an immutable hosted-world release manifest.",
            name: "prepare"
        ) { package, silo, output, label, revision, image, persistence, peer };

        prepare.SetAction(action: parse => Prepare(
            packageDirectory: Path.GetFullPath(path: parse.GetRequiredValue(argument: package)),
            siloPath: Path.GetFullPath(path: parse.GetRequiredValue(option: silo)),
            outputPath: parse.GetValue(option: output),
            label: parse.GetRequiredValue(option: label),
            sourceRevision: parse.GetRequiredValue(option: revision),
            engineImageDigest: parse.GetRequiredValue(option: image),
            persistenceContract: parse.GetRequiredValue(option: persistence),
            peerProtocolContract: parse.GetRequiredValue(option: peer)
        ));

        var status = new Command(
            description: "Inspect the configured Azure world deployment, or a saved deployment-group file.",
            name: "status"
        );
        var statusPath = new Argument<string?>(name: "group-file") { Arity = ArgumentArity.ZeroOrOne, Description = "A saved deployment-group record to read instead of the configured deployment." };
        var json = CliOptions.Json(description: "Write the full validated state, with named phases and the next operator action, as JSON.");

        status.Arguments.Add(item: statusPath);
        status.Options.Add(item: json);
        status.SetAction(action: (parse, cancellationToken) => StatusAsync(
            parse.GetValue(argument: statusPath),
            parse.GetValue(option: json),
            cancellationToken
        ));

        var finalize = new Command(
            description: "Close the admitted release's rollback window, retaining recovery history and artifacts.",
            name: "finalize"
        );

        finalize.SetAction(action: (_, token) => WithRecoveryHintAsync(
            action: async () => {
                var finalized = await AzureCommand.FinalizeWorldReleaseAsync(
                    cancellationToken: token,
                    clock: clock
                ).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: $"Finalized {finalized.Record.ActiveRelease}. The previous release is no longer eligible for ordinary rollback; recovery history and artifacts are retained.");
                return CliExit.Success;
            }
        ));

        var sourceManifest = new Argument<string>(name: "source-manifest") { Description = "The release.json of the release being left." };
        var targetManifest = new Argument<string>(name: "target-manifest") { Description = "The release.json of the release being entered." };
        var fixture = new Argument<string>(name: "fixture-directory") { Description = "The offline fixture whose saved worlds each leg imports." };
        var sourceImage = new Option<string>("--source-image") { Description = "The engine image of the source release.", Required = true };
        var targetImage = new Option<string>("--target-image") { Description = "The engine image of the target release.", Required = true };
        var evidenceDirectory = CliOptions.Output(
            description: "The directory that retains the isolated legs and their qualification evidence.",
            required: true
        );
        var qualificationSteps = new Option<int>("--steps") { DefaultValueFactory = _ => 60, Description = "Exact continuation steps per packaged leg (1–1024)." };
        var qualify = new Command(
            description: "Exercise exact packaged releases in both directions against an offline fixture.",
            name: "qualify"
        ) {
            sourceManifest, targetManifest, fixture, sourceImage, targetImage, evidenceDirectory, qualificationSteps,
        };

        qualify.Detail(detail: """
            Exit codes: 0 the pair qualified and its receipt is under --output; 1 the pair was exercised and a leg failed
            its claim (a packaged engine exited nonzero or overran its limit, a report was incomplete, or two imports
            disagreed); 2 a refusal (unreadable manifests, an unsupported pair, an unmarked or mismatched fixture, an
            image that is not the release's, or a container engine that cannot run a leg).
            """);

        qualify.SetAction(action: async (parse, token) => {
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
                archive,
                clock,
                containers
            );
            var result = await runner.RunAsync(
                cancellationToken: token,
                source: source,
                target: target
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (result.Failure is { } failure) {
                Console.Error.WriteLine(value: $"puck world release qualify: the pair did not qualify: {failure}");

                return CliExit.Failed;
            }

            return CliExit.Success;
        });

        var resume = new Command(
            description: "Resume the configured group's durable operation using its retained release inputs.",
            name: "resume"
        );

        resume.SetAction(action: (_, token) => WithRecoveryHintAsync(
            action: async () => {
                var result = await AzureCommand.ResumeWorldReleaseAsync(
                    cancellationToken: token,
                    clock: clock
                ).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: result.Detail);
                return ((result.Completed && !result.SourceRecovered)
                    ? CliExit.Success
                    : CliExit.Failed
                );
            }
        ));
        var deployPackage = new Argument<string>(name: "package-directory") { Description = "The prepared package: composed worlds, artifacts, and their release.json." };
        var deployOperation = Operation();
        var deploy = new Command(
            description: "Export current state, qualify, and deploy an exact package while preserving authoritative progress.",
            name: "deploy"
        ) { deployPackage, deployOperation };

        deploy.SetAction(action: (parse, token) => WithRecoveryHintAsync(
            action: async () => {
                var result = await AzureCommand.DeployWorldReleaseAsync(
                    parse.GetRequiredValue(argument: deployPackage),
                    parse.GetValue(option: deployOperation),
                    clock,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: result.Detail);
                return ((result.Completed && !result.SourceRecovered)
                    ? CliExit.Success
                    : CliExit.Failed
                );
            }
        ));
        var rollbackOperation = Operation();
        var rollback = new Command(
            description: "Return to the retained previous release using current player progress.",
            name: "rollback"
        ) { rollbackOperation };

        rollback.SetAction(action: (parse, token) => WithRecoveryHintAsync(
            action: async () => {
                var result = await AzureCommand.RollbackWorldReleaseAsync(
                    parse.GetValue(option: rollbackOperation),
                    clock,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: result.Detail);
                return ((result.Completed && !result.SourceRecovered)
                    ? CliExit.Success
                    : CliExit.Failed
                );
            }
        ));
        var checkpointId = new Option<Guid?>("--request") { Description = "Stable recovery-point request ID for retrying an interrupted capture." };
        var checkpoint = new Command(
            description: "Retain a coherent closed-group recovery point without stopping gameplay.",
            name: "checkpoint"
        ) { checkpointId };

        checkpoint.SetAction(action: async (parse, token) => {
            var point = await AzureCommand.CaptureWorldReleasePointAsync(
                clock: clock,
                requestId: (parse.GetValue(option: checkpointId) ?? Guid.NewGuid()),
                token: token
            ).ConfigureAwait(continueOnCapturedContext: false);

            Console.WriteLine(value: JsonSerializer.Serialize(
                point,
                new JsonSerializerOptions { WriteIndented = true }
            ));
            Console.WriteLine(value: $"Recovery point: {point.RequestId:D}; identity: {point.Identity}");
            return CliExit.Success;
        });
        var restorePoint = new Argument<Guid>(name: "recovery-point") { Description = "The recovery point's request ID, as world release checkpoint printed it." };
        var restoreOperation = Operation();
        var discardProgress = new Option<bool>("--discard-progress") { Description = "Explicitly acknowledge discarding this entire group's progress after the selected point." };
        var restore = new Command(
            description: "Preview a recovery point, or explicitly rewind the complete deployment group.",
            name: "restore"
        ) { restorePoint, restoreOperation, discardProgress };

        restore.SetAction(action: (parse, token) => WithRecoveryHintAsync(
            action: async () => {
                var result = await AzureCommand.RestoreWorldReleaseAsync(
                    parse.GetRequiredValue(argument: restorePoint),
                    parse.GetValue(option: restoreOperation),
                    parse.GetValue(option: discardProgress),
                    clock,
                    token
                ).ConfigureAwait(continueOnCapturedContext: false);

                Console.WriteLine(value: result.Detail);
                return ((!parse.GetValue(option: discardProgress) || (result.Completed && !result.SourceRecovered))
                    ? CliExit.Success
                    : CliExit.Failed
                );
            }
        ));
        return new Command(
            description: "Prepare, deploy, roll back, inspect, and recover hosted-world deployment groups.",
            name: "release"
        ) { prepare, status, finalize, qualify, resume, deploy, rollback, checkpoint, restore, WorldReleaseExerciseCommand.Create() };
    }
}
