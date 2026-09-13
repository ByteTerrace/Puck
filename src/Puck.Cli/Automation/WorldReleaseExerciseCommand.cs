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
    public static Command Create() {
        var fixture = new Argument<string>("fixture-directory");
        var steps = new Option<int>("--steps") { DefaultValueFactory = _ => 60, Description = "Exact simulation steps in this isolated qualification leg (1–1024)." };
        var command = new Command("exercise", "Run one packaged release qualification leg in a marked disposable fixture.") { fixture, steps };
        command.SetAction((parse, token) => RunAsync(Path.GetFullPath(parse.GetRequiredValue(fixture)), parse.GetValue(steps), token));
        return command;
    }

    private static async Task<int> RunAsync(string fixture, int steps, CancellationToken cancellationToken) {
        if (steps is < 1 or > 1024) { throw new ArgumentOutOfRangeException(nameof(steps)); }
        if (Encoding.UTF8.GetString(ConfinedFile.ReadAllBytes(Path.Combine(fixture, "qualification.fixture"), 128)).Trim() != "puck.world.qualification.v1") {
            throw new InvalidDataException("release exercise requires a marked disposable qualification fixture");
        }
        var resultPath = Path.Combine(fixture, "exercise-result.json");
        if (File.Exists(resultPath)) { throw new InvalidDataException("qualification result already exists; use a fresh leg directory"); }
        var machines = CliWorldVocabulary.EnsureInstalled();
        if (!WorldSiloDefinitionSerialization.TryLoadFile(Path.Combine(fixture, "silo.json"), out var loaded, out var reason)) { throw new InvalidDataException(reason); }
        var definition = loaded!;
        if (definition.Release is not null || definition.Store.Type != "directory" || definition.Worlds.Any(row => !row.Pinned || row.Federation.Authentication is not null) ||
            definition.Worlds.Select(row => row.Owner).Distinct().Count() != 1) {
            throw new InvalidDataException("qualification requires a local single-owner pinned inventory without production release or authentication configuration");
        }
        var owner = definition.Worlds[0].Owner;
        var target = new DirectoryObjectStorageTarget(Path.Combine(fixture, "store"));
        definition = definition with { StateDir = Path.Combine(fixture, "state"), Release = new("qualification", owner, "exercise") };
        var services = new ServiceCollection();
        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services);
        using var provider = services.BuildServiceProvider();
        var blobs = provider.GetRequiredService<IObjectBlobStore>();
        var groups = new WorldReleaseGroupStore(blobs, target, owner);
        var created = await groups.CreateAsync("qualification", "exercise", cancellationToken).ConfigureAwait(false);
        if (!created.Ok) { throw new InvalidOperationException("qualification fixture already contains a release group; use a fresh leg directory"); }
        using var output = new BufferedConsoleOutput();
        var commands = new TextCommandSource(new CommandRegistry(modules: []));
        var routing = new SiloConsoleRouting(() => commands, new SiloConsoleTagging(output));
        var host = new WorldSiloHost(definition, blobs, routing, target, machines);
        using var instances = host.Instances;
        foreach (var row in definition.Worlds) {
            var activated = host.ActivateAsync(new(row.Owner, row.World), cancellationToken);
            await PumpAsync(host, activated, cancellationToken).ConfigureAwait(false);
            if (!await activated.ConfigureAwait(false)) { throw new InvalidDataException($"qualification could not restore '{row.World}'"); }
        }
        // The outer runner isolates this disposable store and network. Open its own admission gate so that
        // continuation follows the same simulation path as a published worker, including held adjacent rows.
        var publication = host.PublishManagedReleaseAdmissionAsync(cancellationToken);
        await PumpAsync(host, publication, cancellationToken).ConfigureAwait(false);
        if (await publication.ConfigureAwait(false) != WorldReleaseAdmissionPublication.Opened) {
            throw new InvalidDataException("qualification fixture could not publish its isolated admission gate");
        }
        var imported = Capture(host, definition);
        var initialTicks = definition.Worlds.ToDictionary(row => row.World.Value, row => RequireRow(host, row.World.Value).CompletedTicks);
        var rate = host.MasterRateHz;
        if (rate == 0) { throw new InvalidDataException("qualification fixture has no advancing world"); }
        var delta = EngineTicks.PerRate(rate);
        var simulation = new WorldSiloSimulation(host);
        for (var index = 0; index < steps; index++) {
            cancellationToken.ThrowIfCancellationRequested();
            simulation.Step(new FixedStepContext((ulong)index, ((ulong)index + 1) * delta, delta), default);
            // Fail promptly when the first real step enters state the persistence contract cannot represent.
            // A boot-only capture must not make a long-running machine/addon fixture appear eligible.
            if (index == 0) { _ = Capture(host, definition); }
        }
        var finalTicks = definition.Worlds.ToDictionary(row => row.World.Value, row => RequireRow(host, row.World.Value).CompletedTicks);
        if (!initialTicks.Any(row => finalTicks[row.Key] > row.Value)) { throw new InvalidDataException("qualification did not exercise continuation"); }
        var continued = Capture(host, definition);
        await PumpAsync(host, host.DrainAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        var report = new WorldReleaseExerciseResult("puck.world.qualification-exercise.v1", Hash(imported), Hash(continued), initialTicks, finalTicks, steps);
        await File.WriteAllBytesAsync(resultPath, JsonSerializer.SerializeToUtf8Bytes(report), cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Qualification leg restored {definition.Worlds.Count} worlds and advanced {steps} exact steps.");
        return 0;
    }

    private static SortedDictionary<string, string> Capture(WorldSiloHost host, WorldSiloDefinition definition) {
        var state = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in definition.Worlds) {
            var instance = RequireRow(host, row.World.Value);
            if (!instance.Server.TryCaptureCheckpoint(hostRow: host.Instances.CaptureRow(instance), checkpoint: out var checkpoint, reason: out var reason)) {
                throw new InvalidDataException($"qualification capture refused for '{row.World}': {reason}");
            }
            state[$"{row.Owner:D}/{row.World}"] = "sha256/" + Convert.ToHexStringLower(SHA256.HashData(WorldAuthorityCheckpointCodec.Encode(checkpoint!)));
        }
        return state;
    }

    private static string Hash(SortedDictionary<string, string> hashes) => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hashes)));

    private static WorldInstance RequireRow(WorldSiloHost host, string name) => host.Instances.TryGet(name, out var instance) && instance is not null
        ? instance : throw new InvalidDataException($"qualification lost hosted row '{name}'");

    private static async Task PumpAsync(WorldSiloHost host, Task operation, CancellationToken cancellationToken) {
        while (!operation.IsCompleted) {
            cancellationToken.ThrowIfCancellationRequested();
            host.DrainActivationMailbox();
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
        await operation.ConfigureAwait(false);
        host.DrainActivationMailbox();
    }
}

/// <summary>Evidence from one independently executed packaged engine; hashes cover its complete encoded checkpoint inventory.</summary>
public sealed record WorldReleaseExerciseResult(string Schema, string ImportedStateHash, string ContinuedStateHash,
    IReadOnlyDictionary<string, ulong> ImportedTicks, IReadOnlyDictionary<string, ulong> ContinuedTicks, int Steps);
