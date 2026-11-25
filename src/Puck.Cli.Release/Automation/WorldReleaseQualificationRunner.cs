using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>
/// Executes an ordered package pair against copied state in isolated containers. The fixture must be a coherent,
/// offline export; the runner never mounts or changes the source store.
/// <para>
/// A run that exercised the pair returns its verdict: a receipt, or the claim a leg failed (a packaged engine that
/// exited nonzero, overran <see cref="LegTimeout"/>, reported an incomplete or non-advancing inventory, or lost
/// receipts, or two imports that disagree). A run that cannot exercise the pair throws: bad input, an unmarked or
/// mismatched fixture, an unsupported pair, an install that does not hold the release's engine, or an engine that cannot
/// start.
/// </para>
/// </summary>
/// <param name="fixture">The marked offline fixture directory each leg imports.</param>
/// <param name="outputDirectory">The directory that retains the isolated legs and their evidence.</param>
/// <param name="sourceEngine">The install root holding the engine of the release being left.</param>
/// <param name="targetEngine">The install root holding the engine of the release being entered.</param>
/// <param name="steps">The exact continuation steps per packaged leg.</param>
/// <param name="archive">The archive that retains both releases' files; required when their definitions differ.</param>
/// <param name="clock">The clock every deadline runs on; <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
/// <param name="engines">What runs the legs' installed engines; <see langword="null"/> runs them as processes.</param>
/// <param name="documentDirectory">The directory the fixture's captured definitions resolve their relative paths beside
/// when the release's metadata transition decodes them on this machine (<see cref="WorldSiloDefinition.DocumentDirectory"/>
/// as the hosting silo lays it out); <see langword="null"/> for documents that author none.</param>
public sealed class WorldReleaseQualificationRunner(string fixture, string outputDirectory, string sourceEngine, string targetEngine, int steps = 60,
    WorldReleaseArchive? archive = null, TimeProvider? clock = null, IWorldReleaseQualificationEngines? engines = null, string? documentDirectory = null)
    : IWorldReleaseQualificationRunner, IWorldReleaseBootstrapQualificationRunner {
    /// <summary>Gets how long one packaged leg may run, on the runner's clock, before it is abandoned.</summary>
    public static TimeSpan LegTimeout { get; } = TimeSpan.FromMinutes(minutes: 5);

    private const string Marker = "puck.world.qualification.v1";

    // The whole content of a fixture's marker file: the token naming the fixture layout it marks.
    public const string MarkerFileContent = Marker;

    private readonly TimeProvider m_clock = (clock ?? TimeProvider.System);
    private readonly IWorldReleaseQualificationEngines m_engines = (engines ?? new WorldReleaseQualificationProcesses());

    private const int MaximumFileBytes = WorldAuthorityBlobStore.MaximumBlobBytes;

    // A leg's copy names only paths inside itself; engine is the installed version the leg runs, whose asset layout the
    // release's definitions resolve their relative paths beside, as the deployed silo does (null for a seed, which no
    // engine runs).
    private static void CopyFixture(string source, string destination, WorldSiloDefinition definition, string? engine = null) {
        Directory.CreateDirectory(path: destination);
        foreach (var owner in definition.Worlds.Select(selector: row => row.Owner).Distinct()) {
            var relative = Path.Combine(
                "store",
                owner.ToString(format: "D"),
                "private",
                "puck",
                "hosted"
            );
            // The offline export includes neighbour definitions required for validation, even when those worlds
            // are not hosted rows. Release archives and group records are controller state and never enter a leg.
            foreach (var world in Directory.EnumerateDirectories(path: Path.Combine(
                path1: source,
                path2: relative
            ))) {
                var name = Path.GetFileName(path: world);

                if (name is "releases" or "release-groups") { continue; }
                CopyTree(
                    world,
                    Path.Combine(
                        path1: destination,
                        path2: relative,
                        path3: name
                    )
                );
            }
        }
        // The row-owned store is simulation state too; the seed's stable machine identity must survive every leg.
        var state = Path.Combine(
            path1: source,
            path2: "state"
        );

        if (Directory.Exists(path: state)) {
            CopyTree(
                state,
                Path.Combine(
                    path1: destination,
                    path2: "state"
                )
            );
        }
        Directory.CreateDirectory(path: Path.Combine(
            path1: destination,
            path2: "state"
        ));
        var machine = Path.Combine(
            path1: destination,
            path2: "state",
            path3: "silo-machine.id"
        );

        if (!File.Exists(path: machine)) {
            File.WriteAllText(
                contents: "33333333-3333-3333-3333-333333333333",
                path: machine
            );
        }
        Directory.CreateDirectory(path: Path.Combine(
            path1: destination,
            path2: "keys"
        ));
        foreach (var row in definition.Worlds) {
            var name = $"{row.Owner:D}-{row.World}.pk8";
            var key = Path.Combine(
                path1: source,
                path2: "keys",
                path3: name
            );

            if (!File.Exists(path: key)) {
                key = Path.GetFullPath(
                    row.Federation.KeyFile,
                    Path.GetFullPath(path: source)
                );
                if (!key.StartsWith(
                    (Path.GetFullPath(path: source).TrimEnd(trimChar: Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar),
                    PuckPaths.Comparison
                )) {
                    throw new InvalidDataException(message: "qualification signing keys must be inside the disposable fixture");
                }
            }
            File.WriteAllBytes(
                Path.Combine(
                    path1: destination,
                    path2: "keys",
                    path3: name
                ),
                ConfinedFile.ReadAllBytes(
                    maximumBytes: 4096,
                    path: key
                )
            );
        }
        File.WriteAllText(
            Path.Combine(
                path1: destination,
                path2: "qualification.fixture"
            ),
            MarkerFileContent
        );
        var config = JsonNode.Parse(WorldSiloDefinitionSerialization.Serialize(definition: definition))!.AsObject();

        config["stateDir"] = Path.Combine(path1: destination, path2: "state");
        config["store"]!["settings"]!["path"] = Path.Combine(path1: destination, path2: "store");
        if (engine is not null) { config["documentDirectory"] = Path.Combine(path1: engine, path2: WorldHostLayout.WorldsDirectory); }
        foreach (var row in config["worlds"]!.AsArray()) { row!["federation"]!["keyFile"] = Path.Combine(path1: destination, path2: "keys", path3: $"{row["owner"]}-{row["world"]}.pk8"); }
        File.WriteAllText(
            Path.Combine(
                path1: destination,
                path2: "silo.json"
            ),
            config.ToJsonString()
        );
    }
    private static void CopyTree(string source, string destination) {
        if ((File.GetAttributes(path: source) & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException(message: "qualification fixture contains a linked directory"); }
        Directory.CreateDirectory(path: destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path: source)) {
            // Directory-store lock and staging files are implementation metadata, never published objects.
            if (Path.GetFileName(path: entry).StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: ".puck-"
            )) { continue; }
            var attributes = File.GetAttributes(path: entry);

            if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException(message: "qualification fixture contains a linked entry"); }
            var target = Path.Combine(
                path1: destination,
                path2: Path.GetFileName(path: entry)
            );

            if ((attributes & FileAttributes.Directory) != 0) {
                CopyTree(
                    destination: target,
                    source: entry
                );
            } else {
                File.WriteAllBytes(
                    target,
                    ConfinedFile.ReadAllBytes(
                        maximumBytes: MaximumFileBytes,
                        path: entry
                    )
                );
            }
        }
    }
    private static string HashTree(string directory) {
        var files = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(
            path: directory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )) {
            var relative = Puck.Abstractions.PuckPaths.Relative(path: file, relativeTo: directory);
            // Match CopyTree: locks and staging files are not published fixture objects.
            if (relative.Split('/').Any(predicate: segment => segment.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: ".puck-"
            ))) { continue; }
            files.Add(
                key: relative,
                value: ContentPin.Compute(content: ConfinedFile.ReadAllBytes(
                    maximumBytes: MaximumFileBytes,
                    path: file
                )).ToString()
            );
        }
        return ContentPin.Compute(content: JsonSerializer.SerializeToUtf8Bytes(files)).ToString();
    }
    // The end of a failed packaged engine's output, which the failure carries because the container is gone once the
    // leg returns and only the runner saw what it wrote.
    private static string Output(ChildProcessResult exercise) {
        var error = CliText.Tail(count: 12, text: exercise.Stderr);
        var output = CliText.Tail(count: 4, text: exercise.Stdout);

        return (((error.Length == 0) ? "; it wrote nothing to standard error" : $"; its standard error ends: {error}") +
            ((output.Length == 0) ? string.Empty : $"; its standard output ends: {output}"));
    }
    // One packaged leg: its verdict is a report, or the claim the packaged engine failed. Only a leg that cannot start
    // throws.
    private async Task<LegOutcome> LegAsync(string seed, string name, string engine, WorldSiloDefinition definition, string run,
        CancellationToken cancellationToken, bool bootstrap = false) {
        var leg = Path.Combine(
            path1: run,
            path2: name
        );

        CopyFixture(
            definition: definition,
            destination: leg,
            engine: engine,
            source: seed
        );
        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using var provider = services.BuildServiceProvider();
        var authority = new WorldAuthorityBlobStore(
            documentDirectory: documentDirectory,
            store: provider.GetRequiredService<IObjectBlobStore>(),
            target: WorldAuthorityBlobStore.DirectoryTarget(rootPath: Path.Combine(
                path1: leg,
                path2: "store"
            )),
            timeProvider: m_clock
        );
        var expectedReceipts = WorldReleaseReceiptProof.Hash(inventory: await WorldReleaseReceiptProof.ReadAsync(
            allowMissingRoots: bootstrap,
            definition: definition,
            store: authority,
            token: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false));
        ChildProcessResult exercise;

        Console.WriteLine(value: $"Qualification {name}: {engine}");
        using var deadline = new OperationDeadline(
            caller: cancellationToken,
            timeout: LegTimeout,
            timeProvider: m_clock
        );

        try {
            exercise = await m_engines.ExerciseAsync(
                cancellationToken: deadline.Token,
                engine: engine,
                fixture: leg,
                steps: steps
            ).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return LegOutcome.Failed(failure: $"qualification leg '{name}' exceeded its five-minute limit; no receipt was produced");
        }
        if (exercise.ExitCode != 0) {
            return LegOutcome.Failed(failure: $"qualification leg '{name}': the packaged engine exited with code {exercise.ExitCode}{Output(exercise: exercise)}");
        }
        var report = Path.Combine(
            path1: leg,
            path2: "exercise-result.json"
        );
        WorldReleaseExerciseResult? result;

        try {
            result = (File.Exists(path: report)
                ? JsonSerializer.Deserialize<WorldReleaseExerciseResult>(ConfinedFile.ReadAllBytes(
                    maximumBytes: (1024 * 1024),
                    path: report
                ))
                : null
            );
        } catch (JsonException) {
            result = null;
        }
        if (result is null) {
            return LegOutcome.Failed(failure: $"qualification leg '{name}' produced no readable state report");
        }
        var worlds = definition.Worlds.Select(selector: row => row.World.Value).ToHashSet(comparer: StringComparer.Ordinal);

        if (
            (result.Schema != WorldReleaseExerciseResult.CurrentSchema) ||
            (result.ReceiptSeedHash != expectedReceipts) ||
            (result.ImportedReceiptHash != expectedReceipts) ||
            (result.ContinuedReceiptHash != expectedReceipts)
        ) {
            return LegOutcome.Failed(failure: $"qualification leg '{name}': the packaged engine report has another schema or does not prove receipt preservation and duplicate handling");
        }
        if (
            (result.Steps != steps) ||
            (result.ImportedTicks is null) ||
            (result.ContinuedTicks is null) ||
            !worlds.SetEquals(other: result.ImportedTicks.Keys) ||
            !worlds.SetEquals(other: result.ContinuedTicks.Keys) ||
            !ContentPin.TryParse(
                pin: out _,
                text: result.ImportedStateHash
            ) ||
            !ContentPin.TryParse(
                pin: out _,
                text: result.ContinuedStateHash
            ) ||
            worlds.Any(predicate: world => (result.ContinuedTicks[world] < result.ImportedTicks[world])) ||
            !worlds.Any(predicate: world => (result.ContinuedTicks[world] > result.ImportedTicks[world]))
        ) {
            return LegOutcome.Failed(failure: $"qualification leg '{name}' did not report a complete advancing inventory");
        }
        return new(
            Directory: leg,
            Failure: null,
            Result: result
        );
    }
    private async Task<WorldReleaseQualificationResult> RunPairAsync(WorldReleaseManifest? source, WorldReleaseManifest target, CancellationToken cancellationToken) {
        if (steps is < 1 or > 1024) { throw new ArgumentOutOfRangeException(paramName: nameof(steps)); }
        IReadOnlyList<WorldReleaseDefinitionChange> changes = [];

        if (
            !WorldReleaseManifest.TryValidate(
            manifest: target,
            reason: out var reason
        ) ||
            ((source is not null) && !WorldReleaseTransitionPolicy.TryPrepare(
            changes: out changes,
            reason: out reason,
            source: source,
            target: target
        ))
        ) {
            throw new InvalidDataException(message: reason);
        }
        var changesDefinitions = (changes.Count != 0);

        if (
            changesDefinitions &&
            (archive is null)
        ) {
            throw new InvalidDataException(message: "metadata qualification requires both verified release packages");
        }
        if (Encoding.UTF8.GetString(bytes: ConfinedFile.ReadAllBytes(
            Path.Combine(
                path1: fixture,
                path2: "qualification.fixture"
            ),
            128
        )).Trim() != MarkerFileContent) {
            throw new InvalidDataException(message: "qualification requires a marked offline fixture export");
        }
        if (!WorldSiloDefinitionSerialization.TryLoadFile(
            Path.Combine(
                path1: fixture,
                path2: "silo.json"
            ),
            out var definition,
            out reason
        )) {
            throw new InvalidDataException(message: reason);
        }
        if (
            (definition!.Release is not null) ||
            (definition.Store.Type != "directory") ||
            (definition.Worlds.Select(selector: row => row.Owner).Distinct().Count() != 1) ||
            (definition.Issuer is not null) ||
            definition.Worlds.Any(predicate: static row => !row.Pinned) ||
            !definition.Worlds.Select(selector: row => row.RootKey).ToHashSet(comparer: StringComparer.Ordinal).SetEquals(other: target.Definitions.Keys)
        ) {
            throw new InvalidDataException(message: "qualification fixture must exactly match the release's local pinned inventory without production credentials");
        }
        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using var provider = services.BuildServiceProvider();

        // A bootstrap fixture holds each world's published definition under an unowned epoch-zero root and no gameplay,
        // as the bootstrap publishes it (WorldReleaseFixtureBuilder, WorldReleaseBootstrap.PublishAsync).
        if (source is null) {
            var fixtureAuthority = new WorldAuthorityBlobStore(
                documentDirectory: documentDirectory,
                store: provider.GetRequiredService<IObjectBlobStore>(),
                target: WorldAuthorityBlobStore.DirectoryTarget(rootPath: Path.Combine(
                    path1: fixture,
                    path2: "store"
                )),
                timeProvider: m_clock
            );

            foreach (var row in definition.Worlds) {
                var root = await fixtureAuthority.LoadRootAsync(
                    cancellationToken: cancellationToken,
                    identity: new WorldAuthorityIdentity(
                        Owner: row.Owner,
                        World: row.World
                    )
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (root is { Root.HoldsGameplay: true }) {
                    throw new InvalidDataException(message: $"bootstrap qualification requires a fixture without gameplay state; '{row.World}' holds some");
                }
            }
        }
        // A unique evidence directory prevents a previous successful report from satisfying a failed retry.
        var run = Path.Combine(
            path1: Path.GetFullPath(path: outputDirectory),
            path2: Guid.NewGuid().ToString(format: "N")
        );

        Directory.CreateDirectory(path: run);
        var seed = Path.Combine(
            path1: run,
            path2: "seed"
        );

        CopyFixture(
            definition: definition,
            destination: seed,
            source: fixture
        );
        var seedHash = HashTree(directory: seed);
        var transition = (changesDefinitions
            ? new WorldReleaseQualificationTransition(
                provider.GetRequiredService<IObjectBlobStore>(),
                archive!,
                documentDirectory
            )
            : null
        );
        var forwardSeed = seed;

        if (transition is not null) {
            forwardSeed = Path.Combine(
                path1: run,
                path2: "forward-seed"
            );
            CopyFixture(
                definition: definition,
                destination: forwardSeed,
                source: seed
            );
            await transition.ApplyAsync(
                definition: definition,
                directory: forwardSeed,
                source: source!,
                target: target,
                token: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        var forwardSeedHash = HashTree(directory: forwardSeed);
        var aEngine = m_engines.Resolve(
            expected: (source ?? target).Engine,
            installRoot: ((source is null) ? targetEngine : sourceEngine)
        );
        var bEngine = m_engines.Resolve(
            expected: target.Engine,
            installRoot: targetEngine
        );

        // Compare two imports of the same state, not a capture with a fresh import: restoration deliberately
        // parks sessions and resolves host bookkeeping. Then make both packages import state actually written by B.
        // The two legs of each pair import the same seed into their own copies and containers, so they run side by side.
        var forward = await Task.WhenAll(
            LegAsync(forwardSeed, "source-import", aEngine, definition, run, cancellationToken, bootstrap: (source is null)),
            LegAsync(forwardSeed, "target-import", bEngine, definition, run, cancellationToken, bootstrap: (source is null))
        ).ConfigureAwait(continueOnCapturedContext: false);
        var (a, b) = (forward[0], forward[1]);

        if (a.Failure is { } sourceImportFailure) {
            return WorldReleaseQualificationResult.Failed(failure: sourceImportFailure);
        }
        if (b.Failure is { } targetImportFailure) {
            return WorldReleaseQualificationResult.Failed(failure: targetImportFailure);
        }
        if (a.Result!.ImportedStateHash != b.Result!.ImportedStateHash) {
            return WorldReleaseQualificationResult.Failed(failure: "candidate import changed the complete source state");
        }
        var reverseSeed = b.Directory!;

        if (transition is not null) {
            reverseSeed = Path.Combine(
                path1: run,
                path2: "reverse-seed"
            );
            CopyFixture(
                definition: definition,
                destination: reverseSeed,
                source: b.Directory!
            );
            await transition.ApplyAsync(
                definition: definition,
                directory: reverseSeed,
                source: target,
                target: source!,
                token: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        var reverseSeedHash = HashTree(directory: reverseSeed);
        var backward = await Task.WhenAll(
            LegAsync(reverseSeed, "source-reverse-import", aEngine, definition, run, cancellationToken),
            LegAsync(reverseSeed, "target-reverse-reference", bEngine, definition, run, cancellationToken)
        ).ConfigureAwait(continueOnCapturedContext: false);
        var (reverse, reference) = (backward[0], backward[1]);

        if (reverse.Failure is { } reverseFailure) {
            return WorldReleaseQualificationResult.Failed(failure: reverseFailure);
        }
        if (reference.Failure is { } referenceFailure) {
            return WorldReleaseQualificationResult.Failed(failure: referenceFailure);
        }
        if (reverse.Result!.ImportedStateHash != reference.Result!.ImportedStateHash) {
            return WorldReleaseQualificationResult.Failed(failure: "source cannot preserve the candidate-written continuation state");
        }
        var evidence = JsonSerializer.SerializeToUtf8Bytes(new {
            Schema = Marker,
            SourceRelease = source?.Identity,
            TargetRelease = target.Identity,
            SourceEngine = (source ?? target).Engine,
            TargetEngine = target.Engine,
            SeedHash = seedHash,
            ForwardSeedHash = forwardSeedHash,
            ReverseSeedHash = reverseSeedHash,
            MetadataTransition = changesDefinitions,
            Steps = steps,
            SourceImport = a.Result,
            TargetImport = b.Result,
            ReverseImport = reverse.Result,
            ReverseReference = reference.Result
        });

        await File.WriteAllBytesAsync(
            Path.Combine(
                path1: run,
                path2: "evidence.json"
            ),
            evidence,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var receipt = new WorldReleaseQualificationReceipt {
            SourceRelease = source?.Identity,
            TargetRelease = target.Identity,
            EvidenceId = ContentPin.Compute(content: evidence).ToString(),
            SourceStateHash = a.Result.ImportedStateHash,
            TargetStateHash = b.Result.ImportedStateHash,
            ReverseStateHash = reverse.Result.ImportedStateHash,
            ReverseReferenceStateHash = reference.Result.ImportedStateHash,
        };

        await File.WriteAllBytesAsync(
            Path.Combine(
                path1: run,
                path2: "receipt.json"
            ),
            JsonSerializer.SerializeToUtf8Bytes(receipt),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        Console.WriteLine(value: $"Packaged qualification evidence: {Path.Combine(
            path1: run,
            path2: "evidence.json"
        )}");
        return WorldReleaseQualificationResult.Qualified(receipt: receipt);
    }

    public Task<WorldReleaseQualificationResult> RunAsync(WorldReleaseManifest target, CancellationToken cancellationToken = default) =>
        RunPairAsync(
            cancellationToken: cancellationToken,
            source: null,
            target: target
        );
    public Task<WorldReleaseQualificationResult> RunAsync(WorldReleaseManifest source, WorldReleaseManifest target, CancellationToken cancellationToken = default) =>
        RunPairAsync(
            cancellationToken: cancellationToken,
            source: source,
            target: target
        );

    // A leg's verdict: its directory and report, or the claim it failed.
    private sealed record LegOutcome(string? Directory, WorldReleaseExerciseResult? Result, string? Failure) {
        public static LegOutcome Failed(string failure) => new(
            Directory: null,
            Failure: failure,
            Result: null
        );
    }
}
