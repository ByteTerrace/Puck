using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Cli.Host;

namespace Puck.Cli.Affected;

/// <summary>One chosen suite as <c>puck affected --run</c> schedules it.</summary>
/// <param name="Name">The suite's project name, which is also its directory under <c>tests/</c>.</param>
/// <param name="Heavy">Whether its full run is heavy (<see cref="HostProcesses.IsHeavyTestAssembly"/>), so it starts
/// first and is admitted machine-wide before it runs (<see cref="AffectedCommand.AdmitHeavySuite"/>).</param>
internal sealed record AffectedSuite(string Name, bool Heavy);
/// <summary>One suite's outcome.</summary>
/// <param name="ExitCode">The test host's exit code, or <see cref="CliExit.Refused"/> when admission refused it.</param>
/// <param name="Report">The verdict line's summary, then, for a failed suite, its whole report.</param>
internal sealed record AffectedSuiteResult(int ExitCode, IReadOnlyList<string> Report);
/// <summary>
/// Runs the suites <c>puck affected --run</c> chose: builds them once, in one MSBuild invocation over a solution filter
/// of exactly those projects, then runs their test hosts side by side, at most <c>--suite-jobs</c> at once, each on the
/// build that invocation left (<c>dotnet test --project &lt;suite&gt; --no-build</c>, CPU tests only). Heavy suites start
/// first, and each is admitted by the one machine-wide heavy-suite rule before it runs
/// (<see cref="AffectedCommand.AdmitHeavySuite"/>, <see cref="HostAdmission"/>): no other heavy run on the machine, and
/// memory and disk headroom.
/// </summary>
internal static class AffectedSuites {
    // A build or a suite killed at this ceiling has hung; the slowest suite runs for a few minutes.
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(minutes: 30);

    /// <summary>Gets the suites run at once unless <c>--suite-jobs</c> says otherwise: a quarter of the logical
    /// processors, at least one, since every test host already runs its own tests in parallel across the
    /// machine.</summary>
    public static int DefaultJobs => Math.Max(
        val1: 1,
        val2: (Environment.ProcessorCount / 4)
    );

    private static IReadOnlyList<string> Lines(string text) => [.. text.Split(separator: '\n')
        .Select(selector: static line => line.TrimEnd(trimChar: '\r'))
        .Where(predicate: static line => (line.Length > 0))];

    /// <summary>Creates <c>--suite-jobs</c>; <c>puck affected</c> and <c>puck gate</c> share it.</summary>
    /// <returns>The option.</returns>
    public static Option<int> Jobs() {
        var option = new Option<int>(name: "--suite-jobs") {
            DefaultValueFactory = static _ => DefaultJobs,
            Description = "Maximum test suites to run at once (default: a quarter of the logical processors). A heavy suite starts first and waits for host admission before it runs.",
        };

        option.Validators.Add(item: static result => {
            if (result.GetValueOrDefault<int>() < 1) {
                result.AddError(errorMessage: "--suite-jobs must be at least 1.");
            }
        });

        return option;
    }
    /// <summary>The order suites start in: heavy suites first, since they run longest, then the rest in the plan's
    /// order.</summary>
    /// <param name="suites">The chosen suites, in the plan's order.</param>
    /// <returns>The start order.</returns>
    public static List<AffectedSuite> StartOrder(IReadOnlyList<AffectedSuite> suites) => [
        .. suites.Where(predicate: static suite => suite.Heavy),
        .. suites.Where(predicate: static suite => !suite.Heavy),
    ];
    /// <summary>Runs every suite through <paramref name="run"/>, at most <paramref name="jobs"/> at once, starting them in
    /// <see cref="StartOrder"/>, and hands each finished one, with its wall time, to <paramref name="completed"/> on the
    /// calling thread.</summary>
    /// <param name="suites">The suites, in the plan's order.</param>
    /// <param name="jobs">The most suites run at once; at least one runs.</param>
    /// <param name="run">Runs one suite on its own thread, its admission included.</param>
    /// <param name="completed">Receives each suite as it ends.</param>
    /// <param name="started">Receives each dispatched suite on the calling thread before its completion is reported.
    /// The worker may already be running; host admission happens inside <paramref name="run"/>.</param>
    public static void RunConcurrently(IReadOnlyList<AffectedSuite> suites, int jobs, Func<AffectedSuite, AffectedSuiteResult> run, Action<AffectedSuite, AffectedSuiteResult, TimeSpan> completed, Action<AffectedSuite>? started = null) {
        var waiting = new Queue<AffectedSuite>(collection: StartOrder(suites: suites));
        var running = new List<(Task<(AffectedSuiteResult Result, TimeSpan Elapsed)> Task, AffectedSuite Suite)>(capacity: suites.Count);
        var bound = Math.Max(
            val1: 1,
            val2: jobs
        );

        while ((waiting.Count > 0) || (running.Count > 0)) {
            while ((waiting.Count > 0) && (running.Count < bound)) {
                var suite = waiting.Dequeue();

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
                started?.Invoke(obj: suite);
            }

            var finished = Task.WaitAny(tasks: [.. running.Select(selector: static entry => entry.Task)]);

            var (task, done) = running[finished];

            running.RemoveAt(index: finished);

            var (outcome, elapsed) = task.GetAwaiter().GetResult();

            completed(arg1: done, arg2: outcome, arg3: elapsed);
        }
    }
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
            jobs: jobs,
            run: suite => RunSuite(repositoryRoot: repositoryRoot, suite: suite),
            suites: [.. suites.Select(selector: static suite => new AffectedSuite(Heavy: HostProcesses.IsHeavyTestAssembly(assembly: suite), Name: suite))]
        );

        return [.. suites.Where(predicate: failed.Contains)];
    }

    // Microsoft.Testing.Platform hands every option it does not own to the test application, which refuses MSBuild
    // switches, so the run builds nothing and takes none (AffectedCommand.TestArguments). A plain run selects exactly
    // what CI's does, so an explicit tier such as Maths' Deep and Exhaustive stays out, and the Gpu trait keeps device
    // laws out (AffectedCommand.CpuSelection). The platform prints each failure with its message and stack.
    private static AffectedSuiteResult RunSuite(string repositoryRoot, AffectedSuite suite) {
        if (suite.Heavy && !AffectedCommand.AdmitHeavySuite(repositoryRoot: repositoryRoot, suite: suite.Name)) {
            return new AffectedSuiteResult(
                ExitCode: CliExit.Refused,
                Report: [$"REFUSED — host admission did not return within {HostAdmission.HeavyTimeout.TotalHours.ToString(format: "0", provider: CultureInfo.InvariantCulture)} hours"]
            );
        }

        var run = CliProcess.RunCaptured(
            arguments: [.. AffectedCommand.TestArguments(suite: suite.Name), .. AffectedCommand.CpuSelection],
            fileName: "dotnet",
            input: string.Empty,
            timeout: Ceiling,
            workingDirectory: repositoryRoot
        );
        var output = Lines(text: run.Stdout);

        return new AffectedSuiteResult(
            ExitCode: run.ExitCode,
            Report: [CliTestRun.Summary(output: output), .. ((run.ExitCode == 0)
                ? []
                : output.Concat(second: Lines(text: run.Stderr)))]
        );
    }
}
