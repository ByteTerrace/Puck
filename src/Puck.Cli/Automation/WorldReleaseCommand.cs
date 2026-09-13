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
        prepare.SetAction((parse, _) => RunAsync(() => Task.FromResult(Prepare(
            packageDirectory: Path.GetFullPath(parse.GetRequiredValue(package)),
            siloPath: Path.GetFullPath(parse.GetRequiredValue(silo)),
            outputPath: parse.GetValue(output),
            label: parse.GetRequiredValue(label),
            sourceRevision: parse.GetRequiredValue(revision),
            engineImageDigest: parse.GetRequiredValue(image),
            persistenceContract: parse.GetRequiredValue(persistence),
            peerProtocolContract: parse.GetRequiredValue(peer)))));

        var status = new Command("status", "Inspect the configured Azure world deployment, or a saved deployment-group file.");
        var statusPath = new Argument<string?>("group-file") { Arity = ArgumentArity.ZeroOrOne };
        var json = new Option<bool>("--json") { Description = "Write the full validated state with named phases and the next operator action." };
        status.Arguments.Add(statusPath);
        status.Options.Add(json);
        status.SetAction((parse, cancellationToken) => RunAsync(() => StatusAsync(parse.GetValue(statusPath), parse.GetValue(json), cancellationToken)));

        var finalize = new Command("finalize", "Close the admitted release's rollback window, retaining recovery history and artifacts.");
        finalize.SetAction((_, token) => RunAsync(async () => {
            var finalized = await AzureCommand.FinalizeWorldReleaseAsync(token).ConfigureAwait(false);
            Console.WriteLine($"Finalized {finalized.Record.ActiveRelease}. The previous release is no longer eligible for ordinary rollback; recovery history and artifacts are retained.");
            return 0;
        }, recovery: true));

        var sourceManifest = new Argument<string>("source-manifest");
        var targetManifest = new Argument<string>("target-manifest");
        var fixture = new Argument<string>("fixture-directory");
        var sourceImage = new Option<string>("--source-image") { Required = true };
        var targetImage = new Option<string>("--target-image") { Required = true };
        var evidenceDirectory = new Option<string>("--output") { Required = true, Description = "Directory retaining isolated legs and qualification evidence." };
        var qualificationSteps = new Option<int>("--steps") { DefaultValueFactory = _ => 60, Description = "Exact continuation steps per packaged leg (1–1024)." };
        var qualify = new Command("qualify", "Exercise exact packaged releases in both directions against an offline fixture.") {
            sourceManifest, targetManifest, fixture, sourceImage, targetImage, evidenceDirectory, qualificationSteps
        };
        qualify.SetAction((parse, token) => RunAsync(async () => {
            var sourcePath = Path.GetFullPath(parse.GetRequiredValue(sourceManifest));
            var targetPath = Path.GetFullPath(parse.GetRequiredValue(targetManifest));
            var source = JsonSerializer.Deserialize<WorldReleaseManifest>(ConfinedFile.ReadAllBytes(sourcePath, 1024 * 1024)) ?? throw new InvalidDataException("missing source manifest");
            var target = JsonSerializer.Deserialize<WorldReleaseManifest>(ConfinedFile.ReadAllBytes(targetPath, 1024 * 1024)) ?? throw new InvalidDataException("missing target manifest");
            if (!WorldReleaseTransitionPolicy.TryPrepare(source, target, out var changes, out var reason)) { throw new InvalidDataException(reason); }
            var evidence = Path.GetFullPath(parse.GetRequiredValue(evidenceDirectory));
            var services = new ServiceCollection();
            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services);
            using var provider = services.BuildServiceProvider();
            WorldReleaseArchive? archive = null;
            if (changes.Count != 0) {
                archive = new(provider.GetRequiredService<IObjectBlobStore>(),
                    new DirectoryObjectStorageTarget(Path.Combine(evidence, "packages", Guid.NewGuid().ToString("N"))), Guid.NewGuid());
                await archive.SaveAsync(source, Path.GetDirectoryName(sourcePath)!, token).ConfigureAwait(false);
                await archive.SaveAsync(target, Path.GetDirectoryName(targetPath)!, token).ConfigureAwait(false);
            }
            var runner = new WorldReleaseQualificationRunner(Path.GetFullPath(parse.GetRequiredValue(fixture)), evidence,
                parse.GetRequiredValue(sourceImage), parse.GetRequiredValue(targetImage), parse.GetValue(qualificationSteps), archive);
            _ = await runner.RunAsync(source, target, token).ConfigureAwait(false);
            return 0;
        }));

        var resume = new Command("resume", "Resume the configured group's durable operation using its retained release inputs.");
        resume.SetAction((_, token) => RunAsync(async () => {
            var result = await AzureCommand.ResumeWorldReleaseAsync(token).ConfigureAwait(false);
            Console.WriteLine(result.Detail);
            return result.Completed && !result.SourceRecovered ? 0 : 1;
        }, recovery: true));
        var deployPackage = new Argument<string>("package-directory");
        var deployOperation = new Option<Guid?>("--operation");
        var deploy = new Command("deploy", "Export current state, qualify, and deploy an exact package while preserving authoritative progress.") { deployPackage, deployOperation };
        deploy.SetAction((parse, token) => RunAsync(async () => {
            var result = await AzureCommand.DeployWorldReleaseAsync(parse.GetRequiredValue(deployPackage), parse.GetValue(deployOperation), token).ConfigureAwait(false);
            Console.WriteLine(result.Detail);
            return result.Completed && !result.SourceRecovered ? 0 : 1;
        }, recovery: true));
        var rollbackOperation = new Option<Guid?>("--operation");
        var rollback = new Command("rollback", "Return to the retained previous release using current player progress.") { rollbackOperation };
        rollback.SetAction((parse, token) => RunAsync(async () => {
            var result = await AzureCommand.RollbackWorldReleaseAsync(parse.GetValue(rollbackOperation), token).ConfigureAwait(false);
            Console.WriteLine(result.Detail);
            return result.Completed && !result.SourceRecovered ? 0 : 1;
        }, recovery: true));
        return new Command("release", "Prepare, deploy, roll back, inspect, and recover hosted-world deployment groups.") { prepare, status, finalize, qualify, resume, deploy, rollback, WorldReleaseExerciseCommand.Create() };
    }

    internal static async Task<int> RunAsync(Func<Task<int>> action, bool recovery = false) {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) {
            Console.Error.WriteLine(error is OperationCanceledException ? "world release: canceled." : $"world release: {error.Message}");
            if (recovery) { Console.Error.WriteLine("Run 'puck world release status' to inspect the durable result; use 'puck world release resume' if an operation is unfinished."); }
            return error is OperationCanceledException ? 130 : 1;
        }
    }

    internal static int Prepare(string packageDirectory, string siloPath, string? outputPath, string label, string sourceRevision, string engineImageDigest, string persistenceContract, string peerProtocolContract) {
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
        var manifest = new WorldReleaseManifest { Label = label, SourceRevision = sourceRevision, EngineImageDigest = engineImageDigest, Definitions = definitions, DefinitionFiles = definitionFiles, Artifacts = artifacts, PersistenceContract = persistenceContract, PeerProtocolContract = peerProtocolContract,
            CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract };
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

    private static async Task<int> StatusAsync(string? path, bool json, CancellationToken cancellationToken) {
        WorldReleaseGroupRecord record;
        if (path is not null) {
            record = WorldReleaseGroupStore.DeserializeValidated(await File.ReadAllBytesAsync(Path.GetFullPath(path), cancellationToken));
        } else {
            var snapshot = await AzureCommand.ReadWorldReleaseStatusAsync(cancellationToken);
            if (snapshot is null) {
                Console.Error.WriteLine("The configured world deployment has no managed release record. Establish its release identity before a managed deployment.");
                return 2;
            }
            record = snapshot.Value.Record;
        }
        var action = NextAction(record);
        if (json) {
            var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
            var document = JsonSerializer.SerializeToNode(record, options)!.AsObject();
            document["nextAction"] = action;
            Console.WriteLine(document.ToJsonString(options));
        } else {
            Console.WriteLine($"Group: {record.DeploymentGroup}");
            Console.WriteLine($"Active: {record.ActiveRelease ?? "none (first deployment)"}");
            Console.WriteLine($"Previous: {record.PreviousRelease ?? "none"}");
            Console.WriteLine($"Admission: {record.Admission}");
            Console.WriteLine($"Rollback: {(record.RollbackEligible ? "eligible" : "unavailable")}");
            Console.WriteLine($"Operation: {record.PendingOperationId?.ToString("D") ?? "none"}");
            Console.WriteLine($"Phase: {record.PendingPhase?.ToString() ?? "idle"}");
            Console.WriteLine($"Protected worlds: {record.RecoveryRoots.Count}; retained operations: {record.History.Count}");
            if (record.PendingFailure is { } failure) { Console.WriteLine($"Failure: {failure}"); }
            Console.WriteLine($"Next: {action}");
        }
        return 0;
    }

    private static string NextAction(WorldReleaseGroupRecord record) => record.PendingPhase switch {
        WorldReleaseOperationPhase.Prepare => "Resume the pending operation; preparation has not drained the source.",
        WorldReleaseOperationPhase.Drain => "Resume drain and protected-root capture. Keep the source available for a failed-save retry.",
        WorldReleaseOperationPhase.Activate or WorldReleaseOperationPhase.Verify => "Resume the pending operation and verify the private candidate.",
        WorldReleaseOperationPhase.Recover => "Resume source recovery from the protected roots. Keep admission closed.",
        WorldReleaseOperationPhase.RecoverActivate => "Resume the restored source's private activation and admission publication.",
        WorldReleaseOperationPhase.Commit when record.Admission == WorldReleaseAdmissionState.Closed => "Resume the committed target and its admission publication. The pre-deployment save is no longer an automatic fallback.",
        _ when record.ActiveRelease is not null && record.Admission == WorldReleaseAdmissionState.Closed => "Resume the active release under fresh authority fences and publish its admission.",
        _ when record.RollbackEligible => "Roll back to the retained previous release or finalize this rollback window before deploying another release.",
        WorldReleaseOperationPhase.Commit => "Finalize the first deployment before preparing another release.",
        _ when record.ActiveRelease is null => "Prepare the first managed deployment.",
        _ => "Prepare the next release."
    };
}
