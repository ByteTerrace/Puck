using System.Globalization;
using System.Text.Json;

namespace Puck.Cli.Test;

public static partial class TestCommand {
    private static string WorldDirectory(string runDirectory, int index) => Path.Combine(
        path1: runDirectory, path2: "worlds", path3: index.ToString(format: "D6", provider: CultureInfo.InvariantCulture));
    // Replicas ALWAYS live in separate processes, even with --jobs 1. Each process boots a sequence of unrelated
    // test documents through the real composition root, reusing JIT/immutable caches but no host or mutable state.
    private static bool RunBatch(IReadOnlyList<TestWorld> worlds, string artifact, string runDirectory, int chunk, int chunks, int run) {
        var legs = new List<object>();

        for (var index = chunk; (index < worlds.Count); index += chunks) {
            var world = worlds[index];

            if (!TryReadSchedule(world: world.Path, schedule: out var schedule, reason: out _)) {
                continue; // The normal per-world report names this refusal.
            }
            var legDirectory = Path.Combine(path1: WorldDirectory(index: index, runDirectory: runDirectory), path2: $"run{run}");
            // --keep retains evidence, never input state. In particular a crashed worker must not inherit an old
            // successful exit marker or export and turn its missing result into a pass.
            if (Directory.Exists(path: legDirectory)) {
                Directory.Delete(path: legDirectory, recursive: true);
            }
            legs.Add(item: new {
                World = world.Path,
                Directory = legDirectory,
                Script = LegScript(exportTick: schedule!.ExportTick, rateHz: schedule.RateHz),
                ExitAfterSeconds = ((int)Math.Ceiling(a: LegCeiling(exportTick: schedule.ExportTick, rateHz: schedule.RateHz).TotalSeconds)),
            });
        }
        var batch = Path.Combine(path1: runDirectory, path2: $"batch-{chunk}-{run}.json");

        File.WriteAllText(path: batch, contents: JsonSerializer.Serialize(value: legs));
        using var watchdog = new CancellationTokenSource(delay: StartupBudget);

        try {
            var process = CliProcess.RunCaptured(fileName: "dotnet",
                arguments: [artifact, .. WorldDevices.NoneArguments, "--test-batch", batch],
                input: string.Empty, cancellationToken: watchdog.Token, timeout: Timeout.InfiniteTimeSpan,
                onOutput: line => {
                    if (line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[test.batch: begin ")) {
                        watchdog.CancelAfter(delay: StartupBudget);
                    } else if (line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: WaitAnswer)) {
                        watchdog.CancelAfter(delay: ProgressBudget);
                    }
                });

            File.WriteAllText(path: (batch + ".stdout.log"), contents: process.Stdout);
            File.WriteAllText(path: (batch + ".stderr.log"), contents: process.Stderr);
            if (process.ExitCode != 0) {
                Console.Error.WriteLine(value: $"ERROR: test batch {batch} exited {process.ExitCode}: {Tail(text: process.Stderr)}");
            }
            return (process.ExitCode == 0);
        } catch (OperationCanceledException) when (watchdog.IsCancellationRequested) {
            File.WriteAllText(contents: "The test batch stopped advancing; unfinished legs are refused.", path: (batch + ".stderr.log"));
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            File.WriteAllText(path: (batch + ".stderr.log"), contents: exception.Message);
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
