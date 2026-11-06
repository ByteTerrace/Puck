using System.Globalization;
using System.Text.Json;
using Puck.Confinement;

namespace Puck.Cli.Test;

public static partial class TestCommand {
    private static string WorldDirectory(string runDirectory, int index) => Path.Combine(
        path1: runDirectory, path2: "worlds", path3: index.ToString(format: "D6", provider: CultureInfo.InvariantCulture));
    // A worker process stays alive across its legs and boots each scenario in a fresh native host. VerifyHash checks
    // every recorded tick's incremental authority hash against a full recompute during the authored run; the first
    // --workers count carries it, since every count runs the same writes, and --reproduce checks its replay instead.
    private static bool RunBatch(IReadOnlyList<TestWorld> worlds, string artifact, string runDirectory, int chunk, int[] indices, bool reproduce, string? cacheDirectory, TestLeg leg, bool record, bool verifyHash, bool heavy) {
        var legs = new List<object>();

        foreach (var index in indices) {
            var world = worlds[index];

            if (!TryReadSchedule(world: world.Path, schedule: out var schedule, reason: out _)) {
                continue; // The normal per-world report names this refusal.
            }
            var legDirectory = Path.Combine(path1: WorldDirectory(index: index, runDirectory: runDirectory), path2: leg.Name);
            // --keep retains evidence, never input state. In particular a crashed worker must not inherit an old
            // successful exit marker or export and turn its missing result into a pass.
            ConfinedDirectory.RemoveTree(path: legDirectory);
            legs.Add(item: new {
                World = world.Path,
                Reproduce = reproduce,
                CacheDirectory = ((cacheDirectory is null) ? null : Path.GetFullPath(path: cacheDirectory)),
                Directory = legDirectory,
                ExitAfterSeconds = ((int)Math.Ceiling(a: LegCeiling(exportTick: schedule!.ExportTick, rateHz: schedule.RateHz).TotalSeconds)),
                leg.Workers,
                Record = record,
                VerifyHash = verifyHash,
            });
        }
        return RunWorker(artifact: artifact, batch: Path.Combine(path1: runDirectory, path2: $"batch-{chunk}-{leg.Name}.json"), heavy: heavy, legs: legs);
    }
    // Writes the legs to `batch` and runs them in one worker process, its streams beside the batch file. A worker that
    // stops advancing (no leg begun or tick reported within the budgets) is cancelled. Each worker process is admitted by
    // the host before it starts, so the replicas of a --workers or --reproduce run never start into another lane's peak.
    private static bool RunWorker(string artifact, string batch, bool heavy, IReadOnlyList<object> legs) {
        File.WriteAllText(path: batch, contents: JsonSerializer.Serialize(value: legs));
        WorldLaunch launch;

        // The wait comes before the watchdog starts: the watchdog bounds a started worker's progress, never the host's headroom.
        try {
            launch = WorldLaunch.Admit(request: new WorldLaunchRequest(Artifact: artifact, Step: "test worker") {
                Arguments = ["--test-batch", batch],
                HeavySuite = heavy,
                Shape = WorldShape.TestBatch,
            });
        } catch (HostAdmissionRefusedException exception) {
            Console.Error.WriteLine(value: $"ERROR: {exception.Message}");

            return false;
        }

        using var admitted = launch;

        using var watchdog = new CancellationTokenSource(delay: StartupBudget);
        using var output = new StreamWriter(path: (batch + ".stdout.log"));
        using var error = new StreamWriter(path: (batch + ".stderr.log"));

        try {
            var process = admitted.Run(
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
