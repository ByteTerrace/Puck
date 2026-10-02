using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Puck.World;

namespace Puck.Cli.Counters;

/// <summary><c>puck counters</c> — the work-counter collector. It boots the authored counters workload
/// (<c>tests/Puck.Counters/counters.world.json</c>, driven by <c>counters.script.txt</c> beside it, unless
/// <c>--world</c> and <c>--script</c> select another workload) offscreen once per
/// backend through the shared leg machinery, reads the one <c>world.counters --json</c> response the script asks for,
/// and writes a <c>puck.counters.report.v1</c> report: per backend the device identity, the offscreen resolution, the
/// shader toolchain, the GC mode, and every count tagged with its class. The deterministic counts and the passes'
/// states must agree across the two backends; <c>--check</c> holds the counts to the counted-cost ceilings and
/// <c>--record</c> writes them (<see cref="CountersCeilings"/>). <c>puck counters compare</c> holds two reports to each
/// other.</summary>
internal static class CountersCommand {
    /// <summary>The workload's console script, repository-relative.</summary>
    public const string ScriptPath = "tests/Puck.Counters/counters.script.txt";
    /// <summary>The workload's world document, repository-relative.</summary>
    public const string WorldPath = "tests/Puck.Counters/counters.world.json";

    private const string ReportFileName = "counters.report.json";
    private const string ScratchPrefix = "puck-counters-";
    private const string Verb = "counters";

    private static readonly TimeSpan SuiteBudget = TimeSpan.FromSeconds(value: 600);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads a report, refusing one that is not a well-formed <c>puck.counters.report.v1</c>
    /// document.</summary>
    /// <param name="path">The report's path.</param>
    /// <param name="report">The report, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the file is not a report, or empty.</param>
    /// <returns><see langword="true"/> when the file is a report.</returns>
    public static bool TryReadReport(string path, [NotNullWhen(returnValue: true)] out WorldCountersReport? report, out string reason) {
        report = null;

        try {
            report = JsonSerializer.Deserialize(
                jsonTypeInfo: WorldJsonContext.Default.WorldCountersReport,
                utf8Json: File.ReadAllBytes(path: path)
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"unreadable: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        } catch (JsonException exception) {
            reason = $"not a {WorldCountersReport.SchemaVersion} report: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (report is null) {
            reason = $"not a {WorldCountersReport.SchemaVersion} report: the document is null";

            return false;
        }
        if (!string.Equals(
            a: report.Schema,
            b: WorldCountersReport.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"a foreign report: its schema is '{report.Schema}', not '{WorldCountersReport.SchemaVersion}'";
            report = null;

            return false;
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Writes a report as indented UTF-8 JSON ending in one line feed.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="report">The report.</param>
    public static void WriteReport(string path, WorldCountersReport report) {
        var directory = Path.GetDirectoryName(path: path);

        if (directory is { Length: > 0 }) {
            _ = Directory.CreateDirectory(path: directory);
        }

        File.WriteAllText(
            contents: $"{JsonSerializer.Serialize(value: report, jsonTypeInfo: WorldJsonContext.Default.WorldCountersReport)}\n",
            encoding: Utf8,
            path: path
        );
    }
    /// <summary>Compares two reports and prints one line per difference on standard output.</summary>
    /// <param name="left">The first report's path.</param>
    /// <param name="right">The second report's path.</param>
    /// <returns>0 when every comparable count agrees, 1 on any difference, 2 when either file is not a report.</returns>
    public static int Compare(string left, string right) {
        if (!TryReadReport(
            path: Path.GetFullPath(path: left),
            reason: out var leftReason,
            report: out var leftReport
        )) {
            return CliExit.Refuse(verb: $"{Verb} compare", what: left, why: leftReason);
        }
        if (!TryReadReport(
            path: Path.GetFullPath(path: right),
            reason: out var rightReason,
            report: out var rightReport
        )) {
            return CliExit.Refuse(verb: $"{Verb} compare", what: right, why: rightReason);
        }

        if (leftReport.Revision != rightReport.Revision) {
            Console.Error.WriteLine(value: $"{Verb} compare: the reports ran different sources (left {leftReport.Revision.SourceState}, right {rightReport.Revision.SourceState}).");
        }

        return Report(differences: CountersComparison.Reports(
            left: leftReport,
            right: rightReport
        ));
    }

    /// <summary>Records a report's counts as the ceilings in a file, writing nothing unless the whole record is sound: the
    /// backends agree on every deterministic count and pass state, and the recorded ceilings hold the report they were
    /// recorded from. A refused record leaves an existing file as it was.</summary>
    /// <param name="report">The report.</param>
    /// <param name="path">The ceilings file to write.</param>
    /// <param name="reason">Why nothing was written, or empty.</param>
    /// <returns><see langword="true"/> when the ceilings were written.</returns>
    internal static bool TryRecord(WorldCountersReport report, string path, out string reason) {
        var disagreements = CountersComparison.AcrossBackends(
            left: report.Runs[0],
            right: report.Runs[1]
        );

        if (disagreements.Count > 0) {
            reason = $"the backends disagree on {disagreements.Count} deterministic count(s) or pass state(s), so no ceilings are recorded";

            return false;
        }

        var ceilings = CountersCeilings.Record(report: report);
        var verdict = CountersCeilings.Check(
            ceilings: ceilings,
            report: report
        );

        if (verdict.Failures.Count > 0) {
            reason = $"the recorded ceilings fail their own run ({verdict.Failures[0]}), so no ceilings are recorded";

            return false;
        }

        CountersCeilings.Write(
            ceilings: ceilings,
            path: path
        );
        reason = string.Empty;

        return true;
    }
    private static int Report(IReadOnlyList<string> differences) {
        foreach (var difference in differences) {
            Console.Out.WriteLine(value: $"{Verb}: {difference}");
        }

        return ((differences.Count == 0)
            ? CliExit.Success
            : CliExit.Failed
        );
    }
    private static int Run(string? output, bool check, bool record, string? ceilingsPath, string? world, string? scriptPath) {
        if (check && record) {
            return CliExit.Refuse(verb: Verb, what: "--check and --record", why: "a run either holds the report to the ceilings or records them, not both");
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        var ceilingsFile = ((ceilingsPath is null)
            ? Path.Combine(
                path1: repositoryRoot,
                path2: CountersCeilings.CeilingsPath
            )
            : Path.GetFullPath(path: ceilingsPath)
        );
        WorldCountersCeilings? ceilings = null;

        // The ceilings are read before the workload runs, so a missing or damaged file refuses before any GPU work.
        if (
            check &&
            !CountersCeilings.TryRead(
                ceilings: out ceilings,
                path: ceilingsFile,
                reason: out var ceilingsReason
            )
        ) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: ceilingsFile), why: ceilingsReason);
        }

        var worldPath = Path.GetFullPath(path: (world ?? Path.Combine(path1: repositoryRoot, path2: WorldPath)));
        var workloadScriptPath = Path.GetFullPath(path: (scriptPath ?? Path.Combine(path1: repositoryRoot, path2: ScriptPath)));
        string script;
        int width;
        int height;

        try {
            script = File.ReadAllText(path: workloadScriptPath).ReplaceLineEndings(replacementText: "\n");

            using var document = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: worldPath));
            var host = document.RootElement.GetProperty(propertyName: "host");

            width = host.GetProperty(propertyName: "width").GetInt32();
            height = host.GetProperty(propertyName: "height").GetInt32();
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)) {
            return CliExit.Refuse(verb: Verb, what: "the counters workload", why: exception.Message.ReplaceLineEndings(replacementText: " "));
        }

        if (!script.EndsWith(value: '\n')) {
            script += "\n";
        }

        var commit = (CliGit.TryResolveCommit(
            repository: repositoryRoot,
            resolved: out var resolved,
            revision: "HEAD"
        )
            ? resolved
            : "unknown"
        );
        var compiler = new Puck.Shaders.ShaderToolchain().Identity;
        var suiteClock = Stopwatch.StartNew();

        CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);

        var runDirectory = Directory.CreateTempSubdirectory(prefix: ScratchPrefix).FullName;

        Console.Error.WriteLine(value: $"{Verb}: artifacts {CliPaths.ToDisplay(fullPath: runDirectory)}");

        if (!WorldOffscreenLeg.TryResolveWorld(
            artifact: out var artifact,
            repositoryRoot: repositoryRoot,
            runDirectory: runDirectory,
            timeout: CliProcess.RemainingBudget(
                budget: SuiteBudget,
                clock: suiteClock
            ),
            verb: Verb
        )) {
            return CliExit.Refused;
        }

        using var lease = artifact;
        var runs = new List<WorldCountersRun>(capacity: WorldOffscreenLeg.Backends.Count);

        foreach (var backend in WorldOffscreenLeg.Backends) {
            var leg = WorldOffscreenLeg.Run(
                arguments: [],
                artifact: artifact.Path,
                backend: backend,
                budget: SuiteBudget,
                // A safety net, not the leg length: it outlasts the script's 180-second world.wait ready deadline and
                // the ticks after it, so a build that never finishes is named by that wait rather than cut off.
                exitAfterSeconds: 240,
                process: out var process,
                runDirectory: runDirectory,
                script: script,
                suiteClock: suiteClock,
                verb: Verb,
                world: worldPath
            );

            if (leg != CliExit.Success) {
                return leg;
            }
            if (!CountersReading.TryRead(
                backend: backend,
                compiler: compiler,
                height: height,
                reason: out var reason,
                run: out var run,
                stdout: process!.OutputLines.Where(predicate: static line => (line.Stream == CliProcessOutputStream.Stdout)).Select(selector: static line => line.Line),
                width: width
            )) {
                return CliExit.Refuse(verb: Verb, what: $"the {backend} leg's counters", why: reason);
            }

            runs.Add(item: run);
        }

        var report = new WorldCountersReport(
            Revision: new WorldCountersRevision(
                Commit: commit,
                SourceState: artifact.Key
            ),
            Runs: runs,
            Script: CliPaths.ToDisplay(fullPath: workloadScriptPath, relativeTo: repositoryRoot),
            Workload: CliPaths.ToDisplay(fullPath: worldPath, relativeTo: repositoryRoot)
        );
        var reportPath = ((output is null)
            ? Path.Combine(
                path1: runDirectory,
                path2: ReportFileName
            )
            : Path.GetFullPath(path: output)
        );

        WriteReport(
            path: reportPath,
            report: report
        );
        Console.Out.WriteLine(value: $"{Verb}: report {CliPaths.ToDisplay(fullPath: reportPath)}");

        var differences = new List<string>(collection: CountersComparison.AcrossBackends(
            left: runs[0],
            right: runs[1]
        ));

        if (record) {
            if (TryRecord(
                path: ceilingsFile,
                reason: out var refusal,
                report: report
            )) {
                Console.Out.WriteLine(value: $"{Verb}: ceilings {CliPaths.ToDisplay(fullPath: ceilingsFile)}");
            } else {
                Console.Out.WriteLine(value: $"{Verb}: ceilings {CliPaths.ToDisplay(fullPath: ceilingsFile)} not written: {refusal}");

                if (differences.Count == 0) {
                    differences.Add(item: refusal);
                }
            }
        }
        if (ceilings is not null) {
            var verdict = CountersCeilings.Check(
                ceilings: ceilings,
                report: report
            );

            foreach (var note in verdict.Notes) {
                Console.Out.WriteLine(value: $"{Verb}: {note}");
            }

            differences.AddRange(collection: verdict.Failures);
            Console.Out.WriteLine(value: $"{Verb}: ceilings {CliPaths.ToDisplay(fullPath: ceilingsFile)} {((verdict.Failures.Count == 0) ? "hold" : $"fail {verdict.Failures.Count}")}");
        }

        return Report(differences: differences);
    }

    public static Command Create() {
        var outputOption = CliOptions.Output(description: "Where the report is written; the run's scratch directory when omitted.");
        var checkOption = CliOptions.Check(description: $"Also hold the report to the counted-cost ceilings ({CountersCeilings.CeilingsPath}) and fail naming each count over its ceiling or breaking its required zero.");
        var recordOption = new Option<bool>(name: "--record") {
            Description = $"Also record the report's counts as the counted-cost ceilings ({CountersCeilings.CeilingsPath}).",
        };
        var ceilingsOption = new Option<string>(name: "--ceilings") {
            Description = $"The ceilings file --check reads and --record writes; {CountersCeilings.CeilingsPath} when omitted.",
        };
        var worldOption = new Option<string>(name: "--world") {
            Description = $"The authored JSON workload; {WorldPath} when omitted.",
        };
        var scriptOption = new Option<string>(name: "--script") {
            Description = $"The console script containing one counters reading; {ScriptPath} when omitted.",
        };
        var command = new Command(
            description: "Collect the counters workload's work counts offscreen once per backend, check the two agree, and hold them to their ceilings.",
            name: Verb
        ) { outputOption, checkOption, recordOption, ceilingsOption, worldOption, scriptOption };

        command.Detail(detail: $"""
            Boots {WorldPath} offscreen once per backend (vulkan, then directx; no window
            is shown) from the World build of the checkout's current sources, runs {ScriptPath}
            on its console, and reads the one 'world.counters --json' response the script asks for. Writes a
            {WorldCountersReport.SchemaVersion} report (schema: tests/Puck.Counters/{WorldCountersReport.SchemaVersion}.schema.json)
            holding, per backend, the device identity, the offscreen resolution, the shader toolchain identity,
            the World's GC mode, every count tagged with its class (deterministic, per-backend-deterministic,
            pacing, allocation-zero-nonzero), and each render node's pass states. Prints the report's path, then
            one line per deterministic count or pass state the two backends disagree on, naming its kind, pass
            and node. Standard error carries progress; transcripts stay in the run's scratch directory.

            --world and --script select another authored workload and its script. The report carries both paths,
            so a ceilings file for a different workload or script refuses the comparison. The sky-still,
            sky-drift, sky-twinkle and sky-cycle fixtures under tests/Puck.Counters isolate sky change classes; each
            has its own ceilings beside it (sky-<workload>.ceilings.json), which --ceilings names.

            --check holds the report to the counted-cost ceilings, a {WorldCountersCeilings.SchemaVersion}
            document (schema: tests/Puck.Counters/{WorldCountersCeilings.SchemaVersion}.schema.json). They state,
            per backend, every render node's GPU submission counts that are deterministic or
            per-backend-deterministic, pass by pass and outside every pass, the march steps and texels written the
            SDF kernels count among them: each reads at most its ceiling, and a ceiling of zero is a required zero.
            A per-backend-deterministic count is judged only on the device the backend's ceilings were recorded on,
            except a ceiling marked requiredZero (a zero of a kernel kind, which its pass never counts, a structural
            contract), which is judged on every device; a line says how many counts were not judged elsewhere and
            how many required zeros still were. Every ceiling must have been measured, of the class
            it was recorded as, or its pass reported and not executed. One line names each count over its ceiling,
            each required zero broken, each ceiling not measured or measured as another class, and each count no
            ceiling was recorded for. --record writes the report's counts as the ceilings instead, each reading its
            own ceiling, every kind of a pass that did not execute as a zero, and each zero of a kernel kind as a
            required zero. A record writes nothing, and leaves an existing ceilings file as it was, when the
            backends disagree on a deterministic count or pass state, which then fails the run as it does without --record.

            Performance is judged by these counts, never by time; 'puck bench' is the only wall-clock tool.

            Exit codes: 0 the backends agree and every judged count holds its ceiling, 1 a deterministic count or
            pass state differs or a ceiling fails, 2 a build, leg or reading refusal (a missing device or shader
            tool, or a ceilings file that is missing or not a ceilings document, included).
            """);
        command.Subcommands.Add(item: CreateCompare());
        command.SetAction(action: parseResult => Run(
            ceilingsPath: parseResult.GetValue(option: ceilingsOption),
            check: parseResult.GetValue(option: checkOption),
            output: parseResult.GetValue(option: outputOption),
            record: parseResult.GetValue(option: recordOption),
            scriptPath: parseResult.GetValue(option: scriptOption),
            world: parseResult.GetValue(option: worldOption)
        ));

        return command;
    }

    private static Command CreateCompare() {
        var leftArgument = new Argument<string>(name: "left") { Description = $"The first {WorldCountersReport.SchemaVersion} report." };
        var rightArgument = new Argument<string>(name: "right") { Description = "The second report, compared backend by backend against the first." };
        var command = new Command(
            description: "Compare two counters reports and name every count that should agree and does not.",
            name: "compare"
        ) { leftArgument, rightArgument };

        command.Detail(detail: """
            Holds each backend's run in the right report to the same backend's run in the left: deterministic
            and per-backend-deterministic counts must be equal, an allocation reading must be zero in both or
            not zero in both, and pacing counts are never compared. A count or pass one side lacks, a pass
            state that moved, or a count whose class moved is a difference. Prints one line per difference
            naming its backend, class, kind, pass and node; a note on standard error says when the reports ran
            different sources.

            Exit codes: 0 every comparable count agrees, 1 a difference, 2 a usage error or a file that is not
            a readable puck.counters.report.v1 report.
            """);
        command.SetAction(action: parseResult => Compare(
            left: parseResult.GetRequiredValue(argument: leftArgument),
            right: parseResult.GetRequiredValue(argument: rightArgument)
        ));

        return command;
    }
}
