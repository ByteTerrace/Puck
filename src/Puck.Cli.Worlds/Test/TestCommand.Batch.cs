using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;

namespace Puck.Cli.Test;

public static partial class TestCommand {
    // A boot owns a cohort, not just its root document. Include every staged companion and asset copied
    // into the worker, so two scenarios cannot share a companion's state or rules from another package.
    private static string BatchShape(TestWorld world) => ((Path.GetFileName(path: world.Path) + "\n") + string.Join(separator: "\n",
        values: Directory.EnumerateFiles(path: Path.GetDirectoryName(path: world.Path)!, searchPattern: "*", searchOption: SearchOption.AllDirectories).Order(comparer: StringComparer.Ordinal).Select(selector: path => {
            var bytes = File.ReadAllBytes(path: path);
            var shape = (((path == world.Path) || path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".world.json"))
                ? Puck.World.Server.WorldPersistence.ScenarioBootShape(json: bytes)
                : Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)));
            var name = Path.GetRelativePath(relativeTo: Path.GetDirectoryName(path: world.Path)!, path: path).Replace(newChar: '/', oldChar: '\\');

            return $"{name.Length}:{name}:{shape}";
        })));
    private static string WorldDirectory(string runDirectory, int index) => Path.Combine(
        path1: runDirectory, path2: "worlds", path3: index.ToString(format: "D6", provider: CultureInfo.InvariantCulture));
    // A worker retains its process across boots, and restores checkpoints only while the complete boot shape matches.
    private static bool RunBatch(IReadOnlyList<TestWorld> worlds, string artifact, string runDirectory, int chunk, int[] indices, bool reproduce, string? cacheDirectory) {
        var legs = new List<object>();

        foreach (var index in indices) {
            var world = worlds[index];

            if (!TryReadSchedule(world: world.Path, schedule: out var schedule, reason: out _)) {
                continue; // The normal per-world report names this refusal.
            }
            var legDirectory = Path.Combine(path1: WorldDirectory(index: index, runDirectory: runDirectory), path2: "run1");
            // --keep retains evidence, never input state. In particular a crashed worker must not inherit an old
            // successful exit marker or export and turn its missing result into a pass.
            if (Directory.Exists(path: legDirectory)) {
                Directory.Delete(path: legDirectory, recursive: true);
            }
            legs.Add(item: new {
                World = world.Path,
                BootShape = BatchShape(world: world),
                Reproduce = reproduce,
                CacheDirectory = ((cacheDirectory is null) ? null : Path.GetFullPath(path: cacheDirectory)),
                Directory = legDirectory,
                ExitAfterSeconds = ((int)Math.Ceiling(a: LegCeiling(exportTick: schedule!.ExportTick, rateHz: schedule.RateHz).TotalSeconds)),
            });
        }
        var batch = Path.Combine(path1: runDirectory, path2: $"batch-{chunk}-1.json");

        File.WriteAllText(path: batch, contents: JsonSerializer.Serialize(value: legs));
        using var watchdog = new CancellationTokenSource(delay: StartupBudget);
        using var output = new StreamWriter(path: (batch + ".stdout.log"));
        using var error = new StreamWriter(path: (batch + ".stderr.log"));

        try {
            var process = CliProcess.RunCaptured(fileName: "dotnet",
                arguments: [artifact, .. WorldDevices.NoneArguments, "--test-batch", batch],
                input: string.Empty, cancellationToken: watchdog.Token, timeout: Timeout.InfiniteTimeSpan,
                retainOutput: false,
                onOutput: line => {
                    ((line.Stream == CliProcessOutputStream.Stdout) ? output : error).WriteLine(value: line.Line);
                    if (line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[test.batch: begin ")) {
                        watchdog.CancelAfter(delay: StartupBudget);
                    } else if (line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: WaitAnswer) ||
                        line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[test.batch: tick ")) {
                        watchdog.CancelAfter(delay: ProgressBudget);
                    }
                });

            output.Flush();
            error.Flush();
            if (process.ExitCode != 0) {
                Console.Error.WriteLine(value: $"ERROR: test batch {batch} exited {process.ExitCode}; transcripts under {batch}.*.log");
            }
            return (process.ExitCode == 0);
        } catch (OperationCanceledException) when (watchdog.IsCancellationRequested) {
            error.WriteLine(value: "The test batch stopped advancing; unfinished legs are refused.");
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            error.WriteLine(value: exception.Message);
        }
        return false;
    }
    private static bool TryReadBatchLeg(string world, string legDirectory, TextWriter error, out TestReading? reading) {
        reading = null;
        var exit = Path.Combine(path1: legDirectory, path2: "exit-code");

        if (!File.Exists(path: exit) || (File.ReadAllText(path: exit) != "0")) {
            error.WriteLine(value: $"ERROR: the leg for {world} did not finish successfully; transcripts under {legDirectory}.");
            var stderr = Path.Combine(path1: legDirectory, path2: "stderr.log");

            if (File.Exists(path: stderr)) {
                error.WriteLine(value: Tail(text: File.ReadAllText(path: stderr)));
            }
            return false;
        }
        return TryRead(world: world, scheduleDirectory: Path.Combine(path1: legDirectory, path2: "out"), error: error, reading: out reading);
    }
}
