using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Puck.Cli.Host;

namespace Puck.Cli.Affected;

/// <summary>One chosen suite as <c>puck affected --run</c> schedules it.</summary>
/// <param name="Name">The suite's project name, which is also its directory under <c>tests/</c>.</param>
/// <param name="Heavy">Whether its project declares <c>&lt;PuckSuiteLoad&gt;heavy&lt;/PuckSuiteLoad&gt;</c>: a run whose
/// peak alone is a heavy job on the host (<see cref="AffectedSuites.Admits"/>).</param>
internal sealed record AffectedSuite(string Name, bool Heavy);
/// <summary>One suite's outcome.</summary>
/// <param name="ExitCode">The test host's exit code.</param>
/// <param name="Report">The verdict line's summary, then, for a failed suite, its whole report.</param>
internal sealed record AffectedSuiteResult(int ExitCode, IReadOnlyList<string> Report);
/// <summary>
/// Runs the suites <c>puck affected --run</c> chose: builds them once, in one MSBuild invocation over a solution filter
/// of exactly those projects, then runs their test hosts concurrently, each on the build that invocation left
/// (<c>dotnet test --project &lt;suite&gt; --no-build</c>, CPU tests only). How many run at once follows the host-load capacity rules: at most
/// <c>--suite-jobs</c> hosts, never two heavy suites at once, and a suite starts beside others only while the machine
/// has the free memory its weight needs.
/// </summary>
internal static class AffectedSuites {
    /// <summary>The project property that declares a suite heavy.</summary>
    public const string LoadProperty = "PuckSuiteLoad";

    // How long a suite waiting on free memory sleeps before the machine is read again, when no running suite ends first.
    private static readonly TimeSpan MemoryPoll = TimeSpan.FromSeconds(value: 2);
    // A build or a suite killed at this ceiling has hung; the slowest suite runs for a few minutes.
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(minutes: 30);

    /// <summary>Gets the suites run at once unless <c>--suite-jobs</c> says otherwise: a quarter of the logical
    /// processors, at least one, since every xUnit host already runs its own tests in parallel across the
    /// machine.</summary>
    public static int DefaultJobs => Math.Max(
        val1: 1,
        val2: (Environment.ProcessorCount / 4)
    );

    private static IReadOnlyList<string> Lines(string text) => [.. text.Split(separator: '\n')
        .Select(selector: static line => line.TrimEnd(trimChar: '\r'))
        .Where(predicate: static line => (line.Length > 0))];
    private static string ProjectPath(string repositoryRoot, string suite) => Path.Combine(
        path1: repositoryRoot,
        path2: "tests",
        path3: suite,
        path4: $"{suite}.csproj"
    );

    /// <summary>Creates <c>--suite-jobs</c>; <c>puck affected</c> and <c>puck gate</c> share it.</summary>
    /// <returns>The option.</returns>
    public static Option<int> Jobs() {
        var option = new Option<int>(name: "--suite-jobs") {
            DefaultValueFactory = static _ => DefaultJobs,
            Description = "Maximum test suites to run at once (default: a quarter of the logical processors). Never two heavy suites at once, and none beside another while free memory is short.",
        };

        option.Validators.Add(item: static result => {
            if (result.GetValueOrDefault<int>() < 1) {
                result.AddError(errorMessage: "--suite-jobs must be at least 1.");
            }
        });

        return option;
    }
    /// <summary>Answers whether <paramref name="suite"/> may start now. With nothing running any suite starts, so a run
    /// always progresses on a loaded machine. Beside others, a suite needs a free <paramref name="jobs"/> slot, a heavy
    /// one needs no other heavy suite running, and each needs free memory above its floor: an eighth of the machine's
    /// memory for a light suite (the pressure line, 4 GB on a 32 GB machine), a quarter for a heavy one (8 GB on a 32 GB
    /// machine, the free memory a heavy step waits for).</summary>
    /// <param name="suite">The suite waiting to start.</param>
    /// <param name="running">The suites running.</param>
    /// <param name="heavyRunning">Whether a heavy suite is running.</param>
    /// <param name="jobs">The most suites run at once.</param>
    /// <param name="freeRamGb">Free physical memory now, or NaN when it cannot be read.</param>
    /// <param name="totalRamGb">The machine's physical memory.</param>
    /// <returns>Whether it starts.</returns>
    public static bool Admits(AffectedSuite suite, int running, bool heavyRunning, int jobs, double freeRamGb, double totalRamGb) {
        if (running == 0) {
            return true;
        }
        if (
            (running >= jobs) ||
            (suite.Heavy && heavyRunning)
        ) {
            return false;
        }

        var floor = (totalRamGb / (suite.Heavy
            ? 4
            : 8));

        return (double.IsNaN(d: freeRamGb) || (freeRamGb > floor));
    }
    /// <summary>The order suites are offered to <see cref="Admits"/> in: heavy suites first, since they run longest,
    /// then the rest in the plan's order.</summary>
    /// <param name="suites">The chosen suites, in the plan's order.</param>
    /// <returns>The start order.</returns>
    public static List<AffectedSuite> StartOrder(IReadOnlyList<AffectedSuite> suites) => [
        .. suites.Where(predicate: static suite => suite.Heavy),
        .. suites.Where(predicate: static suite => !suite.Heavy),
    ];
    /// <summary>Runs every suite through <paramref name="run"/>, starting each as soon as <see cref="Admits"/> lets it,
    /// and hands each finished one, with its wall time, to <paramref name="completed"/> on the calling thread.</summary>
    /// <param name="suites">The suites, in the plan's order.</param>
    /// <param name="jobs">The most suites run at once.</param>
    /// <param name="run">Runs one suite on its own thread.</param>
    /// <param name="freeRamGb">Reads free physical memory.</param>
    /// <param name="totalRamGb">The machine's physical memory.</param>
    /// <param name="completed">Receives each suite as it ends.</param>
    public static void RunConcurrently(IReadOnlyList<AffectedSuite> suites, int jobs, Func<AffectedSuite, AffectedSuiteResult> run, Func<double> freeRamGb, double totalRamGb, Action<AffectedSuite, AffectedSuiteResult, TimeSpan> completed) {
        var waiting = StartOrder(suites: suites);
        var running = new List<(Task<(AffectedSuiteResult Result, TimeSpan Elapsed)> Task, AffectedSuite Suite)>(capacity: suites.Count);

        while ((waiting.Count > 0) || (running.Count > 0)) {
            while (waiting.Count > 0) {
                var heavyRunning = running.Any(predicate: static entry => entry.Suite.Heavy);
                var free = freeRamGb();
                var next = waiting.FindIndex(match: suite => Admits(
                    freeRamGb: free,
                    heavyRunning: heavyRunning,
                    jobs: jobs,
                    running: running.Count,
                    suite: suite,
                    totalRamGb: totalRamGb
                ));

                if (next < 0) {
                    break;
                }

                var suite = waiting[next];

                waiting.RemoveAt(index: next);
                running.Add(item: (Task.Factory.StartNew(
                    cancellationToken: CancellationToken.None,
                    creationOptions: TaskCreationOptions.LongRunning,
                    function: () => {
                        var clock = Stopwatch.StartNew();
                        var result = run(arg: suite);

                        return (result, clock.Elapsed);
                    },
                    scheduler: TaskScheduler.Default
                ), suite));
            }

            // A suite held back by memory alone waits for the machine as well as for a running suite to end.
            var finished = Task.WaitAny(
                tasks: [.. running.Select(selector: static entry => entry.Task)],
                timeout: ((waiting.Count > 0)
                    ? MemoryPoll
                    : Timeout.InfiniteTimeSpan)
            );

            if (finished < 0) {
                continue;
            }

            var (task, done) = running[finished];

            running.RemoveAt(index: finished);

            var (outcome, elapsed) = task.GetAwaiter().GetResult();

            completed(arg1: done, arg2: outcome, arg3: elapsed);
        }
    }
    /// <summary>Reads whether a suite's project declares itself heavy.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="suite">The suite's name.</param>
    /// <returns>The suite.</returns>
    public static AffectedSuite Read(string repositoryRoot, string suite) => new(
        Heavy: string.Equals(
            a: XDocument.Load(uri: ProjectPath(repositoryRoot: repositoryRoot, suite: suite))
                .Descendants()
                .FirstOrDefault(predicate: static element => (element.Name.LocalName == LoadProperty))?.Value.Trim(),
            b: "heavy",
            comparisonType: StringComparison.Ordinal
        ),
        Name: suite
    );
    /// <summary>Builds and runs <paramref name="suites"/>, printing each suite's verdict line, with its wall time, as it
    /// ends; a failed suite's whole report follows its line.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="suites">The chosen suites, in the plan's order.</param>
    /// <param name="jobs">The most suites run at once.</param>
    /// <returns>The failed suites, in the plan's order.</returns>
    public static IReadOnlyList<string> Run(string repositoryRoot, IReadOnlyList<string> suites, int jobs) {
        if (suites.Count == 0) {
            return [];
        }

        // The directory holds only the solution filter, so it goes whatever the outcome.
        using var scratch = RunDirectory.Create(
            keepOnFailure: false,
            prefix: "puck-affected-suites-"
        );
        var filter = Path.Combine(
            path1: scratch.Path,
            path2: "suites.slnf"
        );

        using (var stream = File.Create(path: filter)) {
            using var writer = new Utf8JsonWriter(utf8Json: stream);

            writer.WriteStartObject();
            writer.WriteStartObject(propertyName: "solution");
            writer.WriteString(propertyName: "path", value: Path.Combine(path1: repositoryRoot, path2: "Puck.slnx").Replace(newChar: '/', oldChar: '\\'));
            writer.WriteStartArray(propertyName: "projects");

            foreach (var suite in suites) {
                writer.WriteStringValue(value: $"tests/{suite}/{suite}.csproj");
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        // One build over every chosen suite lets MSBuild build their shared projects once and in parallel; the hosts
        // below then only run tests, so no two of them ever write the same project's output.
        var clock = Stopwatch.StartNew();
        var build = CliProcess.RunCaptured(
            arguments: ["build", filter, "-c", CliOptions.DefaultConfiguration, CliOptions.NoNodeReuse, "-v", "q", "-nologo"],
            fileName: "dotnet",
            input: string.Empty,
            timeout: Ceiling,
            workingDirectory: repositoryRoot
        );

        if (build.ExitCode != 0) {
            Console.Out.WriteLine(value: $"affected: building the {suites.Count} suite(s) FAILED (exit {build.ExitCode}); none ran.");

            foreach (var line in Lines(text: build.Stdout).Concat(second: Lines(text: build.Stderr))) {
                Console.Out.WriteLine(value: $"  {line}");
            }

            return suites;
        }

        Console.Out.WriteLine(value: $"affected: built {suites.Count} suite(s) in {clock.Elapsed.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}s; running up to {jobs} at once.");

        var failed = new HashSet<string>(comparer: StringComparer.Ordinal);

        RunConcurrently(
            completed: (suite, result, elapsed) => {
                var report = new StringBuilder();

                _ = report.Append(value: $"affected: {suite.Name} {((result.ExitCode == 0) ? "passed" : "FAILED")} — {result.Report[0]} in {elapsed.TotalSeconds.ToString(format: "0", provider: CultureInfo.InvariantCulture)}s");

                // A failed suite's whole report follows its verdict line: every failure with its message and stack.
                foreach (var line in result.Report.Skip(count: 1)) {
                    _ = report.AppendLine().Append(value: "  ").Append(value: line);
                }

                Console.Out.WriteLine(value: report.ToString());

                if (result.ExitCode != 0) {
                    _ = failed.Add(item: suite.Name);
                }
            },
            freeRamGb: HostProbe.FreeRamGb,
            jobs: jobs,
            run: suite => RunSuite(repositoryRoot: repositoryRoot, suite: suite.Name),
            suites: [.. suites.Select(selector: suite => Read(repositoryRoot: repositoryRoot, suite: suite))],
            totalRamGb: (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0)
        );

        return [.. suites.Where(predicate: failed.Contains)];
    }

    // Microsoft.Testing.Platform hands every option it does not own to the test application, which refuses MSBuild
    // switches, so the run builds nothing and takes none (AffectedCommand.TestArguments). A plain run selects exactly
    // what CI's does, so an explicit tier such as Maths' Deep and Exhaustive stays out, and the Gpu trait keeps device
    // laws out (AffectedCommand.CpuSelection). The platform prints each failure with its message and stack.
    private static AffectedSuiteResult RunSuite(string repositoryRoot, string suite) {
        var run = CliProcess.RunCaptured(
            arguments: [.. AffectedCommand.TestArguments(suite: suite), .. AffectedCommand.CpuSelection],
            fileName: "dotnet",
            input: string.Empty,
            timeout: Ceiling,
            workingDirectory: repositoryRoot
        );
        var output = Lines(text: run.Stdout);
        var total = CliTestRun.Summary(output: output);

        return new AffectedSuiteResult(
            ExitCode: run.ExitCode,
            Report: [total, .. ((run.ExitCode == 0)
                ? []
                : output.Concat(second: Lines(text: run.Stderr)))]
        );
    }
}
