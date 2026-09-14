using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;
using Puck.World.Silo;

namespace Puck.Cli.Automation;

/// <summary>One packaged-engine qualification leg over a disposable local fixture. The outer runner provides
/// process/network isolation; the leg restores and captures actual hosted state, not merely a codec transcode.</summary>
internal static class WorldReleaseExerciseCommand {
    private static SortedDictionary<string, string> Capture(WorldSiloHost host, WorldSiloDefinition definition) {
        var state = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var row in definition.Worlds) {
            var instance = RequireRow(
                host: host,
                name: row.World.Value
            );

            if (!instance.Server.TryCaptureCheckpoint(
                hostRow: host.Instances.CaptureRow(row: instance),
                checkpoint: out var checkpoint,
                reason: out var reason
            )) {
                throw new InvalidDataException(message: $"qualification capture refused for '{row.World}': {reason}");
            }
            state[$"{row.Owner:D}/{row.World}"] = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!))));
        }
        return state;
    }
    private static string Hash(SortedDictionary<string, string> hashes) => ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: JsonSerializer.SerializeToUtf8Bytes(hashes))));
    private static async Task PumpAsync(WorldSiloHost host, Task operation, CancellationToken cancellationToken) {
        while (!operation.IsCompleted) {
            cancellationToken.ThrowIfCancellationRequested();
            host.DrainActivationMailbox();
            await Task.Delay(
                cancellationToken: cancellationToken,
                millisecondsDelay: 1
            ).ConfigureAwait(continueOnCapturedContext: false);
        }
        await operation.ConfigureAwait(continueOnCapturedContext: false);
        host.DrainActivationMailbox();
    }
    private static WorldInstance RequireRow(WorldSiloHost host, string name) => ((host.Instances.TryGet(
        instance: out var instance,
        name: name
    ) && (instance is not null))
        ? instance
        : throw new InvalidDataException(message: $"qualification lost hosted row '{name}'")
    );
    private static async Task<int> RunAsync(string fixture, int steps, CancellationToken cancellationToken) {
        if (steps is < 1 or > 1024) { throw new ArgumentOutOfRangeException(paramName: nameof(steps)); }
        if (Encoding.UTF8.GetString(bytes: ConfinedFile.ReadAllBytes(
            Path.Combine(
                path1: fixture,
                path2: "qualification.fixture"
            ),
            128
        )).Trim() != "puck.world.qualification.v1") {
            throw new InvalidDataException(message: "release exercise requires a marked disposable qualification fixture");
        }
        var resultPath = Path.Combine(
            path1: fixture,
            path2: "exercise-result.json"
        );

        if (File.Exists(path: resultPath)) { throw new InvalidDataException(message: "qualification result already exists; use a fresh leg directory"); }
        var machines = CliWorldVocabulary.EnsureInstalled();

        if (!WorldSiloDefinitionSerialization.TryLoadFile(
            Path.Combine(
                path1: fixture,
                path2: "silo.json"
            ),
            out var loaded,
            out var reason
        )) { throw new InvalidDataException(message: reason); }
        var definition = loaded!;

        if (
            (definition.Release is not null) ||
            (definition.Store.Type != "directory") ||
            definition.Worlds.Any(predicate: row => (!row.Pinned || (row.Federation.Authentication is not null))) ||
            (definition.Worlds.Select(selector: row => row.Owner).Distinct().Count() != 1)
        ) {
            throw new InvalidDataException(message: "qualification requires a local single-owner pinned inventory without production release or authentication configuration");
        }
        var owner = definition.Worlds[0].Owner;
        var target = new DirectoryObjectStorageTarget(
            Path.Combine(
                path1: fixture,
                path2: "store"
            ),
            maximumBlobBytes: WorldReleaseFixtureArchive.MaximumCheckpointBytes
        );

        definition = definition with {
            StateDir = Path.Combine(
            path1: fixture,
            path2: "state"
        ),
            Release = new(
            ExpectedRelease: "exercise",
            Group: "qualification",
            Owner: owner
        ),
        };
        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        using var provider = services.BuildServiceProvider();
        var blobs = provider.GetRequiredService<IObjectBlobStore>();
        var authority = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var receiptInventory = await WorldReleaseReceiptProof.ReadAsync(
            authority,
            definition,
            allowMissingRoots: true,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var receiptSeedHash = WorldReleaseReceiptProof.Hash(inventory: receiptInventory);
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: blobs,
            target: target
        );
        var created = await groups.CreateAsync(
            activeRelease: "exercise",
            cancellationToken: cancellationToken,
            deploymentGroup: "qualification"
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!created.Ok) { throw new InvalidOperationException(message: "qualification fixture already contains a release group; use a fresh leg directory"); }
        using var output = new BufferedConsoleOutput();
        var commands = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(
            source: () => commands,
            tagging: new SiloConsoleTagging(output: output)
        );
        var host = new WorldSiloHost(
            definition,
            blobs,
            routing,
            target,
            machines
        );
        using var instances = host.Instances;

        foreach (var row in definition.Worlds) {
            var activated = host.ActivateAsync(
                new(
                    Owner: row.Owner,
                    World: row.World
                ),
                cancellationToken
            );

            await PumpAsync(
                cancellationToken: cancellationToken,
                host: host,
                operation: activated
            ).ConfigureAwait(continueOnCapturedContext: false);
            if (!await activated.ConfigureAwait(continueOnCapturedContext: false)) { throw new InvalidDataException(message: $"qualification could not restore '{row.World}'"); }
        }
        // The outer runner isolates this disposable store and network. Open its own admission gate so that
        // continuation follows the same simulation path as a published worker, including held adjacent rows.
        var publication = host.PublishManagedReleaseAdmissionAsync(cancellationToken);

        await PumpAsync(
            cancellationToken: cancellationToken,
            host: host,
            operation: publication
        ).ConfigureAwait(continueOnCapturedContext: false);
        if (await publication.ConfigureAwait(continueOnCapturedContext: false) != WorldReleaseAdmissionPublication.Opened) {
            throw new InvalidDataException(message: "qualification fixture could not publish its isolated admission gate");
        }
        var imported = Capture(
            definition: definition,
            host: host
        );
        var importedReceiptHash = await WorldReleaseReceiptProof.VerifyAsync(
            authority,
            definition,
            receiptInventory,
            testDuplicates: false,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var initialTicks = definition.Worlds.ToDictionary(
            row => row.World.Value,
            row => RequireRow(
                host: host,
                name: row.World.Value
            ).CompletedTicks
        );
        var rate = host.MasterRateHz;

        if (rate == 0) { throw new InvalidDataException(message: "qualification fixture has no advancing world"); }
        var delta = EngineTicks.PerRate(ratePerSecond: rate);
        var simulation = new WorldSiloSimulation(host: host);

        for (var index = 0; (index < steps); index++) {
            cancellationToken.ThrowIfCancellationRequested();
            simulation.Step(
                new FixedStepContext(
                    ElapsedTicks: ((((ulong)index) + 1) * delta),
                    StepTicks: delta,
                    Tick: ((ulong)index)
                ),
                default
            );
            // Fail promptly when the first real step enters state the persistence contract cannot represent.
            // A boot-only capture must not make a long-running machine/addon fixture appear eligible.
            if (index == 0) {
                _ = Capture(
                    definition: definition,
                    host: host
                );
            }
        }
        var finalTicks = definition.Worlds.ToDictionary(
            row => row.World.Value,
            row => RequireRow(
                host: host,
                name: row.World.Value
            ).CompletedTicks
        );

        if (!initialTicks.Any(predicate: row => (finalTicks[row.Key] > row.Value))) { throw new InvalidDataException(message: "qualification did not exercise continuation"); }
        var continued = Capture(
            definition: definition,
            host: host
        );

        await PumpAsync(
            host,
            host.DrainAsync(ct: cancellationToken),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var continuedReceiptHash = await WorldReleaseReceiptProof.VerifyAsync(
            authority,
            definition,
            receiptInventory,
            testDuplicates: true,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        var report = new WorldReleaseExerciseResult(
            "puck.world.qualification-exercise.v2",
            Hash(hashes: imported),
            Hash(hashes: continued),
            initialTicks,
            finalTicks,
            steps
        ) {
            ContinuedReceiptHash = continuedReceiptHash,
            ImportedReceiptHash = importedReceiptHash,
            ReceiptSeedHash = receiptSeedHash,
        };

        await File.WriteAllBytesAsync(
            resultPath,
            JsonSerializer.SerializeToUtf8Bytes(report),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
        Console.WriteLine(value: $"Qualification leg restored {definition.Worlds.Count} worlds and advanced {steps} exact steps.");
        return 0;
    }

    public static Command Create() {
        var fixture = new Argument<string>(name: "fixture-directory");
        var steps = new Option<int>("--steps") { DefaultValueFactory = _ => 60, Description = "Exact simulation steps in this isolated qualification leg (1–1024)." };
        var command = new Command(
            description: "Run one packaged release qualification leg in a marked disposable fixture.",
            name: "exercise"
        ) { fixture, steps };

        command.SetAction(action: (parse, token) => WorldReleaseCommand.RunAsync(() => RunAsync(
            Path.GetFullPath(path: parse.GetRequiredValue(argument: fixture)),
            parse.GetValue(option: steps),
            token
        )));
        return command;
    }
}

/// <summary>Evidence from one independently executed packaged engine; separate hashes cover its complete encoded
/// checkpoint inventory and the original operation receipts checked through its storage API.</summary>
public sealed record WorldReleaseExerciseResult(string Schema, string ImportedStateHash, string ContinuedStateHash,
    IReadOnlyDictionary<string, ulong> ImportedTicks, IReadOnlyDictionary<string, ulong> ContinuedTicks, int Steps) {
    /// <summary>Original receipts after continued gameplay, with duplicate and conflicting retries checked.</summary>
    public string? ContinuedReceiptHash { get; init; }
    /// <summary>Exact original receipts found through the packaged lookup API after import.</summary>
    public string? ImportedReceiptHash { get; init; }
    /// <summary>The packaged store's complete receipt inventory before activation.</summary>
    public string? ReceiptSeedHash { get; init; }
}
