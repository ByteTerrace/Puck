using System.Globalization;
using System.Text;

namespace Puck.Cli.Motion;

public static partial class MotionCommand {
    private const string ManifestFileName = "manifest.json";

    // Everything that boots a World: the build, the run or the two runs of --reproduce, and the report of each.
    private static int RunWorld(string repositoryRoot, string scenarioFile, string world, string directory, MotionScenarioSet set, IReadOnlyList<MotionScenario> selected, bool reproduce, string? worldArtifact, string? backend, string? cacheDirectory, int columns, int cellWidth, int timeoutMinutes) {
        _ = Directory.CreateDirectory(path: directory);

        var deadline = TimeSpan.FromMinutes(value: timeoutMinutes);
        var suite = System.Diagnostics.Stopwatch.StartNew();

        if (!WorldArtifactBuild.TryResolveNamed(
            error: out var buildError,
            lease: out var lease,
            logDirectory: directory,
            named: worldArtifact,
            path: out var artifact,
            repositoryRoot: repositoryRoot,
            timeout: TimeSpan.FromMinutes(value: 30),
            verb: Verb
        )) {
            return CliExit.Refuse(verb: Verb, what: "the World build", why: buildError.ReplaceLineEndings(replacementText: " "));
        }

        using var leased = lease;

        var commit = (CliGit.TryResolveCommit(repository: repositoryRoot, resolved: out var resolved, revision: "HEAD") ? resolved : "unknown");

        Console.Error.WriteLine(value: $"{Verb}: output {CliPaths.ToDisplay(fullPath: directory)}");

        MotionRunRequest RequestFor(string runDirectory, ulong? start) => new(
            Artifact: artifact,
            Backend: backend,
            CellWidth: cellWidth,
            Columns: columns,
            Deadline: (deadline - suite.Elapsed),
            Directory: runDirectory,
            Selected: selected,
            Set: set,
            StartTick: start,
            StateDirectory: Path.Combine(path1: runDirectory, path2: "state"),
            WorkingDirectory: repositoryRoot,
            World: world
        ) { CacheDirectory = cacheDirectory };

        var worldDisplay = CliPaths.ToDisplay(fullPath: world, relativeTo: repositoryRoot);
        var scenariosDisplay = CliPaths.ToDisplay(fullPath: scenarioFile, relativeTo: repositoryRoot);

        if (!reproduce) {
            var result = MotionRun.Execute(progress: Console.Error, request: RequestFor(runDirectory: directory, start: null));

            return Conclude(commit: commit, directory: directory, result: result, scenarios: scenariosDisplay, set: set, world: worldDisplay);
        }

        var first = Path.Combine(path1: directory, path2: "run-a");
        var second = Path.Combine(path1: directory, path2: "run-b");
        MotionRunResult? a = null;
        MotionRunResult? b = null;
        ulong? start = null;

        // A second World that boots after the first's start tick cannot pin to it: both run again from a later start.
        for (var attempt = 0; (attempt < 3); attempt++) {
            if (Directory.Exists(path: first)) {
                Directory.Delete(path: first, recursive: true);
            }
            if (Directory.Exists(path: second)) {
                Directory.Delete(path: second, recursive: true);
            }

            a = MotionRun.Execute(progress: Console.Error, request: RequestFor(runDirectory: first, start: start));

            if (a.Error.Length != 0) {
                break;
            }

            b = MotionRun.Execute(progress: Console.Error, request: RequestFor(runDirectory: second, start: a.StartTick));

            if (b.TooLateStart is not { } later) {
                break;
            }

            start = Math.Max(val1: later, val2: (a.StartTick + 600UL));
            Console.Error.WriteLine(value: $"{Verb}: the second World was ready after the first's start tick; running both again from tick {start.Value.ToString(provider: CultureInfo.InvariantCulture)}.");
        }

        var exitA = Conclude(commit: commit, directory: first, result: a!, scenarios: scenariosDisplay, set: set, world: worldDisplay);

        if ((b is null) || (b.Error.Length != 0)) {
            Console.Error.WriteLine(value: $"{Verb}: the second run did not complete: {b?.Error}");

            return CliExit.Refused;
        }

        var exitB = Conclude(commit: commit, directory: second, result: b, scenarios: scenariosDisplay, set: set, world: worldDisplay);
        var rows = MotionReport.Compare(first: a!, second: b);

        MotionReport.WriteReproduce(first: "run-a", path: Path.Combine(path1: directory, path2: "reproduce.json"), rows: rows, second: "run-b");
        Console.Out.WriteLine(value: MotionReport.FormatTable(rows: rows));
        Console.Out.WriteLine(value: $"{Verb}: reproduce {CliPaths.ToDisplay(fullPath: Path.Combine(path1: directory, path2: "reproduce.json"))}");

        if ((rows.Count == 0) || rows.Any(predicate: static row => !row.Reproduced)) {
            Console.Out.WriteLine(value: $"{Verb}: reproduction failed: every still must have matching pose and capture hashes and comparable camera images within the pixel bound.");

            return CliExit.Failed;
        }

        return (((exitA == CliExit.Success) && (exitB == CliExit.Success)) ? CliExit.Success : Math.Max(val1: exitA, val2: exitB));
    }
    private static int Conclude(string directory, MotionRunResult result, string commit, string scenarios, string world, MotionScenarioSet set) {
        if (result.Error.Length != 0) {
            return CliExit.Refuse(verb: Verb, what: "the run", why: result.Error);
        }

        MotionReport.WriteManifest(
            commit: commit,
            layout: set.Layout,
            path: Path.Combine(path1: directory, path2: ManifestFileName),
            result: result,
            scenariosFile: scenarios,
            world: world
        );

        var text = new StringBuilder();

        foreach (var scenario in result.Scenarios) {
            _ = text.AppendLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{scenario.Planned.Scenario.Name}: {scenario.Stills.Count} still(s), ticks {scenario.Planned.ZeroTick}..{(scenario.Planned.ZeroTick + ((ulong)scenario.Planned.Scenario.LastTick))}{(scenario.Sheets.TryGetValue(key: "combined", value: out var sheet) ? $", sheet {CliPaths.ToDisplay(fullPath: Path.Combine(path1: directory, path2: sheet))}" : string.Empty)}"));

            foreach (var problem in scenario.Problems) {
                _ = text.AppendLine(value: $"  PROBLEM {problem}");
            }
        }
        foreach (var note in result.Notes) {
            _ = text.AppendLine(value: $"note: {note}");
        }

        Console.Out.Write(value: text.ToString());
        Console.Out.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{Verb}: {result.Scenarios.Count} scenario(s) in {result.Seconds:0} s (World ready at tick {result.BootTick}, pinned from tick {result.StartTick}); manifest {CliPaths.ToDisplay(fullPath: Path.Combine(path1: directory, path2: ManifestFileName))}"));

        return (result.Clean ? CliExit.Success : CliExit.Failed);
    }
}
