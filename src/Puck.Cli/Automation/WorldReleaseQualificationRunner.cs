using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Automation;

/// <summary>Executes an ordered package pair against copied state in isolated Docker containers. The fixture
/// must be a coherent, offline export; the runner never mounts or changes the source store.</summary>
internal sealed class WorldReleaseQualificationRunner(string fixture, string outputDirectory, string sourceImage, string targetImage, int steps = 60)
    : IWorldReleaseQualificationRunner, IWorldReleaseBootstrapQualificationRunner {
    private const string Marker = "puck.world.qualification.v1";
    private const int MaximumFileBytes = WorldReleaseFixtureArchive.MaximumCheckpointBytes;

    public Task<WorldReleaseQualificationReceipt?> RunAsync(WorldReleaseManifest target, CancellationToken cancellationToken = default) =>
        RunPairAsync(null, target, cancellationToken);

    public Task<WorldReleaseQualificationReceipt?> RunAsync(WorldReleaseManifest source, WorldReleaseManifest target, CancellationToken cancellationToken = default) =>
        RunPairAsync(source, target, cancellationToken);

    private async Task<WorldReleaseQualificationReceipt?> RunPairAsync(WorldReleaseManifest? source, WorldReleaseManifest target, CancellationToken cancellationToken) {
        if (steps is < 1 or > 1024) { throw new ArgumentOutOfRangeException(nameof(steps)); }
        if (!WorldReleaseManifest.TryValidate(target, out var reason) ||
            (source is not null && !WorldReleaseTransitionPolicy.TryPrepare(source, target, out _, out reason))) {
            throw new InvalidDataException(reason);
        }
        if (Encoding.UTF8.GetString(ConfinedFile.ReadAllBytes(Path.Combine(fixture, "qualification.fixture"), 128)).Trim() != Marker) {
            throw new InvalidDataException("qualification requires a marked offline fixture export");
        }
        if (!WorldSiloDefinitionSerialization.TryLoadFile(Path.Combine(fixture, "silo.json"), out var definition, out reason)) {
            throw new InvalidDataException(reason);
        }
        if (definition!.Release is not null || definition.Store.Type != "directory" || definition.Worlds.Select(row => row.Owner).Distinct().Count() != 1 || definition.Worlds.Any(row => !row.Pinned || row.Federation.Authentication is not null) ||
            !definition.Worlds.Select(row => $"{row.Owner:D}/{row.World}").ToHashSet(StringComparer.Ordinal).SetEquals(target.Definitions.Keys)) {
            throw new InvalidDataException("qualification fixture must exactly match the release's local pinned inventory without production credentials");
        }
        if (source is null && definition.Worlds.Any(row => File.Exists(Path.Combine(fixture, "store", row.Owner.ToString("D"), "private", "puck", "hosted", row.World.Value, "authority", "root")))) {
            throw new InvalidDataException("bootstrap qualification requires a fixture without existing authority state");
        }
        // A unique evidence directory prevents a previous successful report from satisfying a failed retry.
        var run = Path.Combine(Path.GetFullPath(outputDirectory), Guid.NewGuid().ToString("N"));
        if (run.Contains(',') || run.Contains('\n') || run.Contains('\r')) { throw new InvalidDataException("qualification output path cannot contain Docker mount separators"); }
        Directory.CreateDirectory(run);
        var seed = Path.Combine(run, "seed");
        CopyFixture(fixture, seed, definition);
        var seedHash = HashTree(seed);
        var aImage = await ResolveImageAsync(source is null ? targetImage : sourceImage, (source ?? target).EngineImageDigest, cancellationToken).ConfigureAwait(false);
        var bImage = await ResolveImageAsync(targetImage, target.EngineImageDigest, cancellationToken).ConfigureAwait(false);

        // Compare two imports of the same state, not a capture with a fresh import: restoration deliberately
        // parks sessions and resolves host bookkeeping. Then make both packages import state actually written by B.
        var a = await LegAsync(seed, "source-import", aImage, definition, run, cancellationToken).ConfigureAwait(false);
        var b = await LegAsync(seed, "target-import", bImage, definition, run, cancellationToken).ConfigureAwait(false);
        if (a.Result.ImportedStateHash != b.Result.ImportedStateHash) {
            throw new InvalidDataException("candidate import changed the complete source state");
        }
        var reverse = await LegAsync(b.Directory, "source-reverse-import", aImage, definition, run, cancellationToken).ConfigureAwait(false);
        var reference = await LegAsync(b.Directory, "target-reverse-reference", bImage, definition, run, cancellationToken).ConfigureAwait(false);
        if (reverse.Result.ImportedStateHash != reference.Result.ImportedStateHash) {
            throw new InvalidDataException("source cannot preserve the candidate-written continuation state");
        }
        var evidence = JsonSerializer.SerializeToUtf8Bytes(new {
            Schema = "puck.world.qualification.v1", SourceRelease = source?.Identity, TargetRelease = target.Identity,
            SourceImage = aImage, TargetImage = bImage, SeedHash = seedHash, Steps = steps,
            SourceImport = a.Result, TargetImport = b.Result, ReverseImport = reverse.Result, ReverseReference = reference.Result
        });
        await File.WriteAllBytesAsync(Path.Combine(run, "evidence.json"), evidence, cancellationToken).ConfigureAwait(false);
        var receipt = new WorldReleaseQualificationReceipt {
            SourceRelease = source?.Identity, TargetRelease = target.Identity, EvidenceId = Hash(evidence),
            SourceStateHash = a.Result.ImportedStateHash, TargetStateHash = b.Result.ImportedStateHash,
            ReverseStateHash = reverse.Result.ImportedStateHash, ReverseReferenceStateHash = reference.Result.ImportedStateHash
        };
        await File.WriteAllBytesAsync(Path.Combine(run, "receipt.json"), JsonSerializer.SerializeToUtf8Bytes(receipt), cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Packaged qualification evidence: {Path.Combine(run, "evidence.json")}");
        return receipt;
    }

    private async Task<(string Directory, WorldReleaseExerciseResult Result)> LegAsync(string seed, string name, string image,
        WorldSiloDefinition definition, string run, CancellationToken cancellationToken) {
        var leg = Path.Combine(run, name);
        CopyFixture(seed, leg, definition);
        var container = "puck-qualification-" + Guid.NewGuid().ToString("N");
        Console.WriteLine($"Qualification {name}: {image}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try {
            await DockerAsync([
                "run", "--name", container, "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges", "--pids-limit", "256", "--memory", "4g", "--cpus", "2",
                "--tmpfs", "/tmp:rw,nosuid,nodev,size=256m", "--mount", $"type=bind,source={leg},target=/fixture",
                "--entrypoint", "dotnet", image, "/puck-cli/Puck.Cli.dll", "world", "release", "exercise", "/fixture",
                "--steps", steps.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ], deadline.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            throw new TimeoutException($"qualification leg '{name}' exceeded its five-minute limit; no receipt was produced");
        } finally {
            // Killing a Docker client does not stop its container. Always remove this runner's unique container,
            // including after cancellation, before another leg can use its completed state.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await DockerAsync(["rm", "--force", container], cleanup.Token, allowFailure: true).ConfigureAwait(false);
        }
        var result = JsonSerializer.Deserialize<WorldReleaseExerciseResult>(ConfinedFile.ReadAllBytes(Path.Combine(leg, "exercise-result.json"), 1024 * 1024))
            ?? throw new InvalidDataException("qualification leg produced no state report");
        var worlds = definition.Worlds.Select(row => row.World.Value).ToHashSet(StringComparer.Ordinal);
        if (result.Schema != "puck.world.qualification-exercise.v1" || result.Steps != steps ||
            result.ImportedTicks is null || result.ContinuedTicks is null ||
            !worlds.SetEquals(result.ImportedTicks.Keys) || !worlds.SetEquals(result.ContinuedTicks.Keys) ||
            !FullPin(result.ImportedStateHash) || !FullPin(result.ContinuedStateHash) ||
            worlds.Any(world => result.ContinuedTicks[world] < result.ImportedTicks[world]) ||
            !worlds.Any(world => result.ContinuedTicks[world] > result.ImportedTicks[world])) {
            throw new InvalidDataException("qualification leg did not report a complete advancing inventory");
        }
        return (leg, result);
    }

    private static async Task<string> ResolveImageAsync(string reference, string expected, CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('-')) { throw new InvalidDataException("qualification requires an image reference"); }
        var json = await DockerAsync(["image", "inspect", reference], cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var image = document.RootElement.EnumerateArray().Single();
        var id = image.GetProperty("Id").GetString()!;
        if (id != expected && !image.GetProperty("RepoDigests").EnumerateArray().Any(value => value.GetString()?.EndsWith("@" + expected, StringComparison.Ordinal) == true)) {
            throw new InvalidDataException("qualification image does not match the release's immutable digest");
        }
        return id;
    }

    private static void CopyFixture(string source, string destination, WorldSiloDefinition definition) {
        Directory.CreateDirectory(destination);
        foreach (var owner in definition.Worlds.Select(row => row.Owner).Distinct()) {
            var relative = Path.Combine("store", owner.ToString("D"), "private", "puck", "hosted");
            // The offline export includes neighbour definitions required for validation, even when those worlds
            // are not hosted rows. Release archives and group records are controller state and never enter a leg.
            foreach (var world in Directory.EnumerateDirectories(Path.Combine(source, relative))) {
                var name = Path.GetFileName(world);
                if (name is "releases" or "release-groups") { continue; }
                CopyTree(world, Path.Combine(destination, relative, name));
            }
        }
        // The row-owned store is simulation state too; the seed's stable machine identity must survive every leg.
        var state = Path.Combine(source, "state");
        if (Directory.Exists(state)) { CopyTree(state, Path.Combine(destination, "state")); }
        Directory.CreateDirectory(Path.Combine(destination, "state"));
        var machine = Path.Combine(destination, "state", "silo-machine.id");
        if (!File.Exists(machine)) { File.WriteAllText(machine, "33333333-3333-3333-3333-333333333333"); }
        Directory.CreateDirectory(Path.Combine(destination, "keys"));
        foreach (var row in definition.Worlds) {
            var name = $"{row.Owner:D}-{row.World}.pk8";
            var key = Path.Combine(source, "keys", name);
            if (!File.Exists(key)) {
                key = Path.GetFullPath(row.Federation.KeyFile, Path.GetFullPath(source));
                if (!key.StartsWith(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                    throw new InvalidDataException("qualification signing keys must be inside the disposable fixture");
                }
            }
            File.WriteAllBytes(Path.Combine(destination, "keys", name), ConfinedFile.ReadAllBytes(key, 4096));
        }
        File.WriteAllText(Path.Combine(destination, "qualification.fixture"), Marker);
        var config = JsonNode.Parse(WorldSiloDefinitionSerialization.Serialize(definition))!.AsObject();
        config["stateDir"] = "/fixture/state";
        config["store"]!["settings"]!["path"] = "/fixture/store";
        foreach (var row in config["worlds"]!.AsArray()) { row!["federation"]!["keyFile"] = $"/fixture/keys/{row["owner"]}-{row["world"]}.pk8"; }
        File.WriteAllText(Path.Combine(destination, "silo.json"), config.ToJsonString());
    }

    private static void CopyTree(string source, string destination) {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("qualification fixture contains a linked directory"); }
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source)) {
            // Directory-store lock and staging files are implementation metadata, never published objects.
            if (Path.GetFileName(entry).StartsWith(".puck-", StringComparison.OrdinalIgnoreCase)) { continue; }
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("qualification fixture contains a linked entry"); }
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) { CopyTree(entry, target); }
            else { File.WriteAllBytes(target, ConfinedFile.ReadAllBytes(entry, MaximumFileBytes)); }
        }
    }

    private static string HashTree(string directory) {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) {
            files.Add(Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/'), Hash(ConfinedFile.ReadAllBytes(file, MaximumFileBytes)));
        }
        return Hash(JsonSerializer.SerializeToUtf8Bytes(files));
    }

    private static string Hash(byte[] bytes) => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool FullPin(string? value) => value is { Length: 71 } && value.StartsWith("sha256/", StringComparison.Ordinal) && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static async Task<string> DockerAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool allowFailure = false) {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new IOException("cannot start Docker qualification process");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var text = await output.ConfigureAwait(false);
        var error = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0 && !allowFailure) { throw new InvalidOperationException($"packaged qualification failed: {error}"); }
        return text;
    }
}
