using System.CommandLine;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Puck.Cli.Canary;

namespace Puck.Cli.Qualification;

/// <summary>
/// <c>puck qualify</c>: qualifies a producer-built <c>Puck.World</c> package against the release profile. It verifies
/// the package is in the profile's publish mode, installs a clean copy, runs the profile's functional canaries against
/// that copy's World, then runs the stability matrix offscreen through the same leg machinery as <c>puck counters</c>:
/// every workload at every resolution on every backend, each from a fresh state root and so a cold pipeline cache,
/// warmed up, soaked, reloaded and churned the profile's number of times. Each cell is judged from its deterministic
/// counters and its pipeline instance's inspections against the cell's threshold and reported pass, fail or blocked.
/// </summary>
internal static class QualifyCommand {
    private const string ReportFileName = "qualification.report.json";
    private const string ScratchPrefix = "puck-qualify-";
    private const string Verb = "qualify";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One cell with everything its leg needs.</summary>
    /// <param name="Cell">The cell.</param>
    /// <param name="Script">The cell's script.</param>
    private sealed record PlannedCell(QualificationCell Cell, QualificationScript Script);

    public static Command Create() {
        var packageArgument = new Argument<string>(name: "package") { Description = "The producer-built Puck.World package directory, as CI's publish writes it. Never built here." };
        var profileOption = new Option<string?>(name: "--profile") { Description = $"The release profile; {ReleaseProfileLoader.DefaultPath} when omitted." };
        var listOption = new Option<bool>(name: "--list") { Description = "Verify the package and print the matrix and every cell's script without running anything." };
        var outputOption = CliOptions.Output(description: "Where the report is written; the run's scratch directory when omitted.");
        var command = new Command(
            description: "Qualify a producer-built Puck.World package: the functional canaries, then the stability matrix, judged against the release profile.",
            name: Verb
        ) { packageArgument, listOption, outputOption, profileOption };

        command.Detail(detail: $"""
            Reads the release profile ({ReleaseProfileLoader.DefaultPath}, a {ReleaseProfile.SchemaVersion} document)
            and verifies the package: its entry assembly exists and is in the profile's publish mode. Then it
            installs a clean copy of the package in the run's scratch directory and qualifies that copy:

              the functional canaries the profile names, run on the copy's World (puck canary --world-artifact);
              the stability matrix: every workload at every resolution on every backend, offscreen, each cell
                booting a generated overlay of the workload's world from a fresh state root, so a cold
                pipeline cache, with the validation layer on for the backends the profile lists, and, when the
                profile's compiler discovery is None, with every directory holding dxc off the World's path.

            A cell warms up, soaks, reloads its world, and reloads, resizes, unloads and loads its pipeline
            instance the profile's number of times; lengths are ticks and frames, never time. A cell passes
            when every wait is reached, no GPU object is created across a soak window (the gpu.created.*
            counts of world.counters), every settled pipeline.inspect shows the instance owning exactly its
            installed graph, every unload releases the instance, every world.reload applies, the peak owned
            pipeline bytes stay within the cell's threshold, and no validation message appears. A cell is blocked when this machine lacks the
            backend's device, when a shader tool the profile lets the World find is absent, or when the run's
            own infrastructure refuses. The checks the profile defers are printed with why.

            --list verifies the package and prints the matrix and every cell's script, booting nothing.
            The report ({QualificationReport.SchemaVersion}) is written to --output or the run's scratch
            directory beside every cell's script, transcripts and state.

            Exit codes: 0 everything passed, 1 a canary or a cell failed, 2 a refusal (the profile, the package or
            a cell's plan) or a blocked check with nothing failed.
            """);
        command.SetAction(action: parseResult => Run(
            list: parseResult.GetValue(option: listOption),
            output: parseResult.GetValue(option: outputOption),
            package: parseResult.GetRequiredValue(argument: packageArgument),
            profilePath: parseResult.GetValue(option: profileOption)
        ));

        return command;
    }

    private static int Run(string package, string? profilePath, bool list, string? output) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        var profileFile = ((profilePath is null)
            ? Path.Combine(
                path1: repositoryRoot,
                path2: ReleaseProfileLoader.DefaultPath
            )
            : Path.GetFullPath(path: profilePath)
        );

        if (!ReleaseProfileLoader.TryLoad(
            path: profileFile,
            profile: out var profile,
            reason: out var profileReason
        )) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: profileFile), why: profileReason);
        }

        var packageDirectory = Path.GetFullPath(path: package);

        if (!QualificationPackage.TryVerify(
            directory: packageDirectory,
            entry: out var packageEntry,
            publish: profile.Publish,
            reason: out var packageReason
        )) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: packageDirectory), why: packageReason);
        }
        if (!TryPlan(
            package: packageDirectory,
            planned: out var planned,
            profile: profile,
            reason: out var planReason
        )) {
            return CliExit.Refuse(verb: Verb, what: "the stability matrix", why: planReason);
        }

        Console.Out.WriteLine(value: $"{Verb}: profile {CliPaths.ToDisplay(fullPath: profileFile)}; package {CliPaths.ToDisplay(fullPath: packageDirectory)}, whose {profile.Publish.EntryAssembly} is a {profile.Publish.Mode} publish.");

        if (list) {
            List(
                entry: packageEntry,
                planned: planned,
                profile: profile
            );

            return CliExit.Success;
        }

        return Qualify(
            output: output,
            package: packageDirectory,
            planned: planned,
            profile: profile,
            profileFile: profileFile,
            repositoryRoot: repositoryRoot
        );
    }
    // Every cell's script, read from what the package's worlds author, so a world that lost the layout or pipeline row a
    // workload churns is refused before anything boots.
    private static bool TryPlan(ReleaseProfile profile, string package, [NotNullWhen(returnValue: true)] out IReadOnlyList<PlannedCell>? planned, out string reason) {
        planned = null;

        var rows = new Dictionary<string, QualificationPipelineRows?>(comparer: StringComparer.Ordinal);

        foreach (var workload in profile.Workloads) {
            var world = Path.Combine(
                path1: package,
                path2: workload.World
            );

            if (!File.Exists(path: world)) {
                reason = $"workload '{workload.Name}': the package has no {workload.World}";

                return false;
            }
            if (workload.Pipeline is not { } pipeline) {
                rows[workload.Name] = null;

                continue;
            }
            if (!QualificationPlan.TryReadRows(
                pipeline: pipeline,
                reason: out var rowsReason,
                rows: out var read,
                worldText: File.ReadAllText(path: world)
            )) {
                reason = $"workload '{workload.Name}': {workload.World}: {rowsReason}";

                return false;
            }

            rows[workload.Name] = read;
        }

        var cells = new List<PlannedCell>();

        foreach (var cell in QualificationPlan.Cells(profile: profile)) {
            if (!QualificationPlan.TryWrite(
                cell: cell,
                reason: out var scriptReason,
                rows: rows[cell.Workload.Name],
                script: out var script
            )) {
                reason = $"cell {cell.Id}: {scriptReason}";

                return false;
            }

            cells.Add(item: new PlannedCell(
                Cell: cell,
                Script: script
            ));
        }

        planned = cells;
        reason = string.Empty;

        return true;
    }
    private static void List(ReleaseProfile profile, string entry, IReadOnlyList<PlannedCell> planned) {
        Console.Out.WriteLine(value: $"{Verb}: compiler discovery {profile.Publish.Compiler}; validation layers on {((profile.DebugLayers.Count == 0) ? "no backend" : string.Join(separator: ", ", values: profile.DebugLayers))}.");

        if (profile.Functional.Count != 0) {
            Console.Out.WriteLine(value: $"{Verb}: functional: puck canary --world-artifact <installed copy of {CliPaths.ToDisplay(fullPath: entry)}> {string.Join(separator: " ", values: profile.Functional)}");
        }

        foreach (var (cell, script) in planned.Select(selector: static plan => (plan.Cell, plan.Script))) {
            var expectation = script.Expectation;

            Console.Out.WriteLine(value: $"{Verb}: cell {cell.Id}: threshold {Threshold(threshold: cell.Threshold)}; {expectation.CountersReadings} counters reading(s), {expectation.SoakWindows.Count} soak window(s), {expectation.Inspections} inspection(s), {expectation.Releases} release(s), {expectation.ArmedWaits} wait(s); overlay {QualificationPlan.OverlayFileName(cell: cell)}.");

            foreach (var line in script.Text.Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: '\n'
            )) {
                Console.Out.WriteLine(value: $"  {line}");
            }
        }

        Deferred(profile: profile);
    }
    private static int Qualify(ReleaseProfile profile, string profileFile, string package, IReadOnlyList<PlannedCell> planned, string repositoryRoot, string? output) {
        CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);

        var runDirectory = Directory.CreateTempSubdirectory(prefix: ScratchPrefix).FullName;
        var install = Path.Combine(
            path1: runDirectory,
            path2: "install"
        );

        Console.Error.WriteLine(value: $"{Verb}: artifacts {CliPaths.ToDisplay(fullPath: runDirectory)}");
        Console.Error.WriteLine(value: $"{Verb}: installed {QualificationPackage.Install(install: install, package: package)} file(s) of the package.");

        if (!QualificationPackage.TryVerify(
            directory: install,
            entry: out var entry,
            publish: profile.Publish,
            reason: out var installReason
        )) {
            return CliExit.Refuse(verb: Verb, what: "the installed copy", why: installReason);
        }

        QualificationFunctionalResult? functional = null;

        if (profile.Functional.Count != 0) {
            Console.Error.WriteLine(value: $"{Verb}: running the functional canaries on the installed World.");

            // Every World the run starts, the functional canaries' included, boots with the validation layer of each
            // backend the profile lists.
            var exit = CanaryCommand.RunNamed(
                debugLayers: profile.DebugLayers,
                ids: profile.Functional,
                worldArtifact: entry
            );

            functional = new QualificationFunctionalResult(
                Canaries: profile.Functional,
                ExitCode: exit,
                Outcome: exit switch {
                    CliExit.Success => QualificationOutcome.Pass,
                    CliExit.Failed => QualificationOutcome.Fail,
                    _ => QualificationOutcome.Blocked,
                }
            );
            Console.Out.WriteLine(value: $"{Verb}: functional canaries: {Label(outcome: functional.Outcome)} (puck canary exited {exit}).");
        }

        var legEnvironment = QualificationPackage.LegEnvironment(
            compiler: profile.Publish.Compiler,
            holdsCompiler: QualificationPackage.HoldsCompiler,
            searchPath: Environment.GetEnvironmentVariable(variable: "PATH")
        );
        var compiler = new Puck.Shaders.ShaderToolchain().Identity;
        var results = new List<QualificationCellResult>(capacity: planned.Count);

        foreach (var (cell, script) in planned.Select(selector: static plan => (plan.Cell, plan.Script))) {
            results.Add(item: RunCell(
                cell: cell,
                compiler: compiler,
                entry: entry,
                environment: legEnvironment,
                install: install,
                profile: profile,
                runDirectory: runDirectory,
                script: script
            ));
        }

        Deferred(profile: profile);

        var outcome = Overall(
            cells: results.Select(selector: static result => result.Outcome).Concat(second: ((functional is null) ? [] : [functional.Outcome]))
        );
        var report = new QualificationReport(
            Cells: results,
            Commit: (CliGit.TryResolveCommit(
                repository: repositoryRoot,
                resolved: out var commit,
                revision: "HEAD"
            )
                ? commit
                : "unknown"
            ),
            Deferred: profile.Deferred,
            Functional: functional,
            Outcome: outcome,
            Package: package.Replace(
                newChar: '/',
                oldChar: '\\'
            ),
            Profile: profileFile.Replace(
                newChar: '/',
                oldChar: '\\'
            ),
            Publish: profile.Publish,
            Schema: QualificationReport.SchemaVersion
        );
        var reportPath = ((output is null)
            ? Path.Combine(
                path1: runDirectory,
                path2: ReportFileName
            )
            : Path.GetFullPath(path: output)
        );

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: reportPath)!);
        File.WriteAllText(
            contents: $"{JsonSerializer.Serialize(value: report, jsonTypeInfo: QualificationJsonContext.Default.QualificationReport)}\n",
            encoding: Utf8,
            path: reportPath
        );
        Console.Out.WriteLine(value: $"{Verb}: {results.Count(predicate: static result => (result.Outcome == QualificationOutcome.Pass))} of {results.Count} cell(s) passed, {results.Count(predicate: static result => (result.Outcome == QualificationOutcome.Fail))} failed, {results.Count(predicate: static result => (result.Outcome == QualificationOutcome.Blocked))} blocked; {Label(outcome: outcome)}.");
        Console.Out.WriteLine(value: $"{Verb}: report {CliPaths.ToDisplay(fullPath: reportPath)}");

        return outcome switch {
            QualificationOutcome.Pass => CliExit.Success,
            QualificationOutcome.Fail => CliExit.Failed,
            _ => CliExit.Refused,
        };
    }
    private static QualificationCellResult RunCell(ReleaseProfile profile, QualificationCell cell, QualificationScript script, string install, string entry, string runDirectory, IReadOnlyDictionary<string, string?> environment, string compiler) {
        var cellDirectory = Path.Combine(
            path1: runDirectory,
            path2: cell.DirectoryName
        );
        var overlay = Path.Combine(
            path1: install,
            path2: Path.GetDirectoryName(path: cell.Workload.World)!,
            path3: QualificationPlan.OverlayFileName(cell: cell)
        );
        var debugLayers = profile.DebugLayers.Contains(value: cell.Backend);

        _ = Directory.CreateDirectory(path: cellDirectory);
        File.WriteAllText(
            contents: QualificationPlan.Overlay(cell: cell),
            encoding: Utf8,
            path: overlay
        );
        File.WriteAllText(
            contents: script.Text,
            encoding: Utf8,
            path: Path.Combine(
                path1: cellDirectory,
                path2: "script.txt"
            )
        );

        var leg = WorldOffscreenLeg.Launch(
            arguments: [
                "--capture-dir", Path.Combine(
                    path1: cellDirectory,
                    path2: "captures"
                ),
                .. QualificationPackage.DebugLayerArguments(
                    backend: cell.Backend,
                    profile: profile
                ),
            ],
            artifact: entry,
            backend: cell.Backend,
            budget: TimeSpan.FromSeconds(value: cell.Workload.TimeoutSeconds),
            environment: environment,
            exitAfterSeconds: cell.Workload.TimeoutSeconds,
            expectedRejections: script.Expectation.Releases,
            runDirectory: cellDirectory,
            script: script.Text,
            suiteClock: Stopwatch.StartNew(),
            verb: $"{Verb} {cell.Id}",
            world: overlay
        );
        var readings = ((leg.Process is { } process)
            ? QualificationJudge.Read(
                cell: cell,
                compiler: compiler,
                process: process
            )
            : null
        );
        var verdict = QualificationJudge.Judge(
            cell: cell,
            compiler: profile.Publish.Compiler,
            debugLayers: debugLayers,
            expectation: script.Expectation,
            leg: leg.Status,
            legDetail: leg.Detail,
            readings: readings
        );

        Console.Out.WriteLine(value: $"{Verb}: {cell.Id}: {Label(outcome: verdict.Outcome)}{((readings?.PeakOwnedPipelineBytes is { } peak) ? $"; peak owned pipeline bytes {peak} of {Threshold(threshold: cell.Threshold)}" : string.Empty)}.");

        foreach (var finding in verdict.Findings) {
            Console.Out.WriteLine(value: $"  {finding}");
        }

        return new QualificationCellResult(
            Backend: cell.Backend,
            Cell: cell.Id,
            DebugLayers: debugLayers,
            Device: readings?.Counters.FirstOrDefault()?.Device,
            Findings: verdict.Findings,
            Height: cell.Resolution.Height,
            MemoryProfile: readings?.MemoryProfile,
            Outcome: verdict.Outcome,
            PeakOwnedPipelineBytes: readings?.PeakOwnedPipelineBytes,
            PeakOwnedPipelineBytesThreshold: cell.Threshold.PeakOwnedPipelineBytes,
            Pipeline: cell.Workload.Pipeline,
            SoakTicks: cell.Workload.SoakTicks,
            ValidationMessages: (readings?.ValidationMessages.Count ?? 0),
            WarmupTicks: cell.Workload.WarmupTicks,
            Width: cell.Resolution.Width,
            Workload: cell.Workload.Name,
            WorldReloads: cell.Workload.WorldReloads
        );
    }

    /// <summary>Folds outcomes into one: any failure fails, otherwise any blocked check blocks, otherwise a pass.</summary>
    /// <param name="cells">The outcomes.</param>
    /// <returns>The folded outcome.</returns>
    internal static QualificationOutcome Overall(IEnumerable<QualificationOutcome> cells) {
        var outcomes = cells.ToArray();

        return (outcomes.Contains(value: QualificationOutcome.Fail)
            ? QualificationOutcome.Fail
            : (outcomes.Contains(value: QualificationOutcome.Blocked)
                ? QualificationOutcome.Blocked
                : QualificationOutcome.Pass
            )
        );
    }

    private static void Deferred(ReleaseProfile profile) {
        foreach (var deferral in profile.Deferred) {
            Console.Out.WriteLine(value: $"{Verb}: deferred {deferral.Check}: {deferral.Reason}");
        }
    }
    private static string Label(QualificationOutcome outcome) => outcome switch {
        QualificationOutcome.Pass => "PASS",
        QualificationOutcome.Fail => "FAIL",
        _ => "BLOCKED",
    };
    private static string Threshold(QualificationThreshold threshold) => ((threshold.PeakOwnedPipelineBytes is { } bytes)
        ? $"{bytes} peak owned pipeline bytes"
        : "no pipeline threshold (the workload churns no pipeline)"
    );
}
