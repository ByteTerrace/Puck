namespace Puck.Cli.Laws;

/// <summary>Builds a law's project with <c>dotnet build</c> and runs the law with <c>dotnet test</c>, reading the
/// outcome from the run's TRX report rather than its console text. Build servers are disabled so no compiler or MSBuild
/// node outlives the proof holding a file in its tree.</summary>
internal sealed class DotnetLawRunner : ILawRunner {
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(minutes: 60);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(minutes: 30);

    private const string ReportName = "law.trx";

    private static string[] Lines(string text) => [.. text.Split(separator: '\n')
        .Select(selector: static line => line.TrimEnd(trimChar: '\r'))
        .Where(predicate: static line => (line.Length > 0))];

    /// <inheritdoc/>
    public LawBuild Build(string tree, string project, CancellationToken cancellationToken) {
        var build = CliProcess.RunCaptured(
            cancellationToken: cancellationToken,
            arguments: ["build", project, "-c", CliOptions.DefaultConfiguration, "-v", "q", "-nologo", "--disable-build-servers", "-p:NuGetAudit=false"],
            fileName: "dotnet",
            input: string.Empty,
            timeout: BuildTimeout,
            workingDirectory: tree
        );

        if (
            !build.TimedOut &&
            (build.ExitCode == 0)
        ) {
            return new LawBuild(Errors: [], Succeeded: true);
        }

        var output = Lines(text: (build.Stdout + build.Stderr));
        string[] errors = [.. output.Where(predicate: static line => line.Contains(comparisonType: StringComparison.Ordinal, value: ": error ")).Distinct(comparer: StringComparer.Ordinal)];

        return new LawBuild(
            Errors: ((errors.Length > 0)
                ? errors
                : [.. output.TakeLast(count: 20), (build.TimedOut ? $"the build did not finish within {BuildTimeout}." : $"dotnet build exited {build.ExitCode}.")]),
            Succeeded: false
        );
    }
    /// <inheritdoc/>
    public LawRun Run(string tree, string project, string law, string results, CancellationToken cancellationToken) {
        var run = CliProcess.RunCaptured(
            cancellationToken: cancellationToken,
            arguments: ["test", project, "-c", CliOptions.DefaultConfiguration, "--no-build", "-nologo", "--filter", $"FullyQualifiedName~{law}", "--logger", $"trx;LogFileName={ReportName}", "--results-directory", results],
            fileName: "dotnet",
            input: string.Empty,
            timeout: TestTimeout,
            workingDirectory: tree
        );
        var report = Path.Combine(
            path1: results,
            path2: ReportName
        );

        return ReadRun(run: run, report: (File.Exists(path: report) ? File.ReadAllText(path: report) : null));
    }
    /// <summary>Judges the report together with process completion; a partial or aborted run is never evidence.</summary>
    /// <param name="run">The test process's outcome.</param>
    /// <param name="report">Its TRX report, or null when none was written.</param>
    /// <returns>The executed tests, or a named refusal reason.</returns>
    public static LawRun ReadRun(CliProcessResult run, string? report) {
        if (run.TimedOut || (report is null)) {
            return new LawRun(Error: $"dotnet test exited {run.ExitCode}{(run.TimedOut ? " after its deadline" : string.Empty)} without a completed run: {string.Join(separator: " | ", values: Lines(text: (run.Stdout + run.Stderr)).TakeLast(count: 5))}", Failures: [], Tests: []);
        }
        var parsed = LawProof.ReadReport(report: report);

        if ((run.ExitCode != 0) && ((run.ExitCode != 1) || (parsed.Failures.Count == 0))) {
            return parsed with { Error = $"dotnet test exited {run.ExitCode} without a matching test-failure verdict." };
        }
        if ((run.ExitCode == 0) && (parsed.Failures.Count > 0)) {
            return parsed with { Error = "dotnet test exited 0 despite reported failures." };
        }
        return parsed;
    }
}
