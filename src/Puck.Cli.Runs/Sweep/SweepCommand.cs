using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Puck.Cli.Sweep;

/// <summary>
/// <c>puck sweep</c> — the debug world's sweep: one run that boots the world headless, drives its development tools
/// for a bounded span, reads every signal the plan names, optionally runs scenario files, and, on a windowed World,
/// reads the rendered signals and takes the capture tour; it writes one report that a later sweep compares with.
/// </summary>
public static partial class SweepCommand {
    /// <summary>The plan the verb runs when <c>--plan</c> names none, repository-relative.</summary>
    public const string DefaultPlan = "worlds/debug/sweep.json";
    /// <summary>The world the verb boots when <c>--world</c> names none, repository-relative.</summary>
    public const string DefaultWorld = "worlds/debug";

    private const string Verb = "sweep";

    /// <summary>Creates the <c>sweep</c> verb.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var planOption = new Option<string>(name: "--plan") { Description = $"The sweep plan; {DefaultPlan} when omitted." };
        var outputOption = CliOptions.Output(description: "The directory the report, answers, stills and sheets are written to; a kept run directory under the temporary root when omitted.");
        var baselineOption = new Option<string>(name: "--baseline") { Description = "A previous sweep's report.json to compare every signal and still with." };
        var tourOption = new Option<bool>(name: "--tour") { Description = "Also run the windowed leg: rendered signals and the capture tour, booting a windowed World under the GPU lease unless --attach names one." };
        var attachOption = new Option<string>(name: "--attach") { Description = "Run the windowed leg on this running World (process id, attachment id or attachment file) instead of booting one; implies --tour." };
        var noHeadlessOption = new Option<bool>(name: "--no-headless") { Description = "Skip the headless leg (its tools and signals)." };
        var stopOption = new Option<string[]>(name: "--stop") { AllowMultipleArgumentsPerToken = true, Description = "Run only the named tour stops (repeat or list several)." };
        var scenariosOption = new Option<string[]>(name: "--scenarios") { AllowMultipleArgumentsPerToken = true, Description = "Run these test files with puck test --jobs 1 and read their verdicts; none when omitted." };
        var worldOption = new Option<string>(name: "--world") { Description = $"The world to boot; {DefaultWorld} when omitted." };
        var artifactOption = new Option<string>(name: "--world-artifact") { Description = "Run this Puck.World entry assembly instead of the build of the checkout's sources. Never builds." };
        var timeoutOption = new Option<int>(name: "--timeout") { DefaultValueFactory = static _ => 40, Description = "The most minutes the whole run may take, a build excepted." };
        var command = new Command(
            description: "Sweep the debug world: run its tools, read every signal, take the capture tour, and write a report a later sweep compares with.",
            name: Verb
        ) { planOption, outputOption, baselineOption, tourOption, attachOption, noHeadlessOption, stopOption, scenariosOption, worldOption, artifactOption, timeoutOption };

        command.Detail(detail: $"""
            Runs the plan ({DefaultPlan}) against the debug world through its Operator attachment, so every line is an
            existing console verb or an authored commands row and every answer is the line's own.

            The headless leg boots {DefaultWorld} headless and unpaced (no GPU, no physical device, its own state
            directory), runs each tool for its bounded span (the fuzzer, the traversal bot's studio and gallery passes,
            the chess table's scoped replay), then reads every signal the plan names for that leg: the watchdog, the
            tools' records, the collider and contact reports, budgets, counters, frames and memory.

            --tour adds the windowed leg: a windowed World booted under the GPU lease, or the running World --attach
            names (one puck_world_start started, say). It waits for the renderer and the bakes, reads the rendered
            signals (bake and lighting counters, frame times standing and walking, memory), then takes the tour: each
            stop poses the body and camera, waits, and takes a settled capture. Each group of stills becomes a contact
            sheet; against a baseline, a compare- sheet sets each baseline still above its current one.

            --scenarios runs the named test files with puck test --jobs 1 and reads their verdicts. The whole set
            (worlds/debug/*-tests.puck) boots the world once per scenario, so the plan runs none by default.

            The output directory holds report.json (every signal with its colour, metrics and answer file; every tool,
            still and sheet), the answers under headless/ and windowed/, and the stills and sheets under tour/.
            --baseline names a previous report.json: each signal then lists what changed by the plan's compare modes,
            and each still its changed-pixel fraction beyond the tour's level tolerance.

            Exit codes: 0 nothing red, 1 something red, 2 a refusal (a plan, a build or a World that did not boot).
            """);
        command.SetAction(action: parseResult => Run(
            artifact: parseResult.GetValue(option: artifactOption),
            attach: parseResult.GetValue(option: attachOption),
            baselinePath: parseResult.GetValue(option: baselineOption),
            noHeadless: parseResult.GetValue(option: noHeadlessOption),
            output: parseResult.GetValue(option: outputOption),
            planPath: parseResult.GetValue(option: planOption),
            scenarios: (parseResult.GetValue(option: scenariosOption) ?? []),
            stops: (parseResult.GetValue(option: stopOption) ?? []),
            timeoutMinutes: parseResult.GetValue(option: timeoutOption),
            tour: parseResult.GetValue(option: tourOption),
            worldPath: parseResult.GetValue(option: worldOption)
        ));

        return command;
    }

    private static int Run(string? planPath, string? output, string? baselinePath, bool tour, string? attach, bool noHeadless, string[] stops, string[] scenarios, string? worldPath, string? artifact, int timeoutMinutes) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        var planFile = Path.GetFullPath(path: (planPath ?? Path.Combine(path1: repositoryRoot, path2: DefaultPlan)));

        if (!File.Exists(path: planFile)) {
            return CliExit.Refuse(verb: Verb, what: "the plan", why: $"{CliPaths.ToDisplay(fullPath: planFile)} does not exist");
        }
        if (!SweepPlan.TryParse(error: out var planError, json: File.ReadAllText(path: planFile), plan: out var plan)) {
            return CliExit.Refuse(verb: Verb, what: CliPaths.ToDisplay(fullPath: planFile), why: planError);
        }

        var unknown = stops.Where(predicate: name => !plan.Tour.Stops.Any(predicate: stop => (stop.Name == name))).ToArray();

        if (unknown.Length != 0) {
            return CliExit.Refuse(verb: Verb, what: $"--stop {string.Join(separator: " ", values: unknown)}", why: "the plan's tour has no such stop");
        }

        SweepBaseline? baseline = null;

        if ((baselinePath is not null) && !SweepBaseline.TryRead(baseline: out baseline, error: out var baselineError, path: baselinePath)) {
            return CliExit.Refuse(verb: Verb, what: "--baseline", why: baselineError);
        }

        var windowed = (tour || (attach is not null));
        var directory = Path.GetFullPath(path: (output ?? RunDirectory.CreatePath(prefix: "puck-sweep-")));
        var world = Path.GetFullPath(path: (worldPath ?? Path.Combine(path1: repositoryRoot, path2: DefaultWorld)));
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var deadline = TimeSpan.FromMinutes(value: timeoutMinutes);
        var legs = new List<SweepLegResult>();
        var tools = new List<SweepToolResult>();
        var signals = new List<SweepSignalResult>();
        SweepTourResult? tourResult = null;

        _ = Directory.CreateDirectory(path: directory);
        Console.Error.WriteLine(value: $"{Verb}: output {CliPaths.ToDisplay(fullPath: directory)}");

        string? entry = null;
        IDisposable? build = null;

        if (!noHeadless || (windowed && (attach is null))) {
            if (!WorldArtifactBuild.TryResolveNamed(error: out var buildError, lease: out var lease, logDirectory: directory, named: artifact, path: out var resolved, repositoryRoot: repositoryRoot, timeout: TimeSpan.FromMinutes(value: 30), verb: Verb)) {
                return CliExit.Refuse(verb: Verb, what: "the World build", why: buildError.ReplaceLineEndings(replacementText: " "));
            }

            entry = resolved;
            build = lease;
        }

        using (build) {
            if (!noHeadless) {
                var logs = Path.Combine(path1: directory, path2: SweepLegs.Headless);

                if (!SweepWorld.TryBoot(
                    arguments: Arguments(deadline: deadline, extra: ["--headless", "true", "--unpaced", "true"], state: Path.Combine(path1: logs, path2: "state"), world: world),
                    artifact: entry!,
                    bootTimeout: TimeSpan.FromMinutes(value: 5),
                    error: out var bootError,
                    lease: null,
                    logDirectory: logs,
                    session: out var booted,
                    workingDirectory: repositoryRoot
                )) {
                    return CliExit.Refuse(verb: Verb, what: "the headless World", why: bootError);
                }

                using (var headless = booted!) {
                    var run = new SweepRun(baseline: baseline, directory: directory, plan: plan, progress: Console.Error, world: headless);

                    Console.Error.WriteLine(value: $"{Verb}: headless World {headless.ProcessId.ToString(provider: CultureInfo.InvariantCulture)} attached");
                    tools.AddRange(collection: run.Tools());
                    signals.AddRange(collection: run.Signals());
                    legs.Add(item: new SweepLegResult(Booted: true, Leg: SweepLegs.Headless, Logs: CliPaths.ToDisplay(fullPath: logs), Notes: run.Notes, ProcessId: headless.ProcessId));
                }
            }

            if (windowed) {
                SweepWorld? rendered;
                string? logs = null;

                if (attach is not null) {
                    if (!SweepWorld.TryAttach(error: out var attachError, session: out rendered, world: attach)) {
                        return CliExit.Refuse(verb: Verb, what: $"--attach {attach}", why: attachError);
                    }
                } else {
                    logs = Path.Combine(path1: directory, path2: SweepLegs.Windowed);

                    var gpu = CliLease.AcquireAsync(
                        clock: TimeProvider.System,
                        holder: Verb,
                        name: "gpu",
                        root: CliLease.RootAsync(checkout: repositoryRoot).GetAwaiter().GetResult(),
                        wait: TimeSpan.FromMinutes(value: 20)
                    ).GetAwaiter().GetResult();

                    if (!SweepWorld.TryBoot(
                        arguments: Arguments(deadline: deadline, extra: ["--headless", "false"], state: Path.Combine(path1: logs, path2: "state"), world: world),
                        artifact: entry!,
                        bootTimeout: TimeSpan.FromMinutes(value: 5),
                        error: out var bootError,
                        lease: gpu,
                        logDirectory: logs,
                        session: out rendered,
                        workingDirectory: repositoryRoot
                    )) {
                        return CliExit.Refuse(verb: Verb, what: "the windowed World", why: bootError);
                    }
                }

                using (var shown = rendered!) {
                    if (shown.Leg != SweepLegs.Windowed) {
                        return CliExit.Refuse(verb: Verb, what: $"World {shown.ProcessId.ToString(provider: CultureInfo.InvariantCulture)}", why: "it composes no renderer, so it cannot take the tour");
                    }

                    var run = new SweepRun(baseline: baseline, directory: directory, plan: plan, progress: Console.Error, world: shown);

                    Console.Error.WriteLine(value: $"{Verb}: windowed World {shown.ProcessId.ToString(provider: CultureInfo.InvariantCulture)} attached");
                    run.Settle(deadline: TimeSpan.FromMinutes(value: 10));
                    signals.AddRange(collection: run.Signals());
                    tourResult = run.Tour(names: stops);
                    legs.Add(item: new SweepLegResult(Booted: shown.Owned, Leg: SweepLegs.Windowed, Logs: ((logs is null) ? null : CliPaths.ToDisplay(fullPath: logs)), Notes: run.Notes, ProcessId: shown.ProcessId));
                }
            }
        }

        var report = new SweepReport(
            Baseline: baseline?.Path.Replace(newChar: '/', oldChar: '\\'),
            Commit: (CliGit.TryResolveCommit(repository: repositoryRoot, resolved: out var commit, revision: "HEAD") ? commit : "unknown"),
            Legs: legs,
            Scenarios: Scenarios(directory: directory, files: scenarios, repositoryRoot: repositoryRoot),
            Schema: SweepReport.SchemaName,
            Seconds: clock.Elapsed.TotalSeconds,
            Signals: signals,
            Started: started,
            Tools: tools,
            Tour: tourResult,
            World: CliPaths.ToDisplay(fullPath: world, relativeTo: repositoryRoot)
        );
        var reportFile = Path.Combine(path1: directory, path2: SweepReport.FileName);

        report.Write(path: reportFile);
        Console.Out.Write(value: report.Summary());
        Console.Out.WriteLine(value: $"{Verb}: report {CliPaths.ToDisplay(fullPath: reportFile)}");

        return ((report.Status == SweepStatus.Red) ? CliExit.Failed : CliExit.Success);
    }

    private static List<string> Arguments(string world, string state, TimeSpan deadline, IReadOnlyList<string> extra) => [
        "--world", world,
        .. WorldDevices.NoneArguments,
        "--state-dir", state,
        "--exit-after-seconds", (((int)deadline.TotalSeconds) + 120).ToString(provider: CultureInfo.InvariantCulture),
        .. extra,
    ];
    // Each file through this CLI's own puck test, one worker at a time, read from its "test <name>:" and
    // "<passed>/<total> verdict(s) passed" lines and its exit code.
    private static SweepScenarioResult Scenarios(string directory, string[] files, string repositoryRoot) {
        var command = $"puck test {((files.Length == 0) ? "worlds/debug/*-tests.puck" : string.Join(separator: " ", values: files))} --jobs 1";

        if (files.Length == 0) {
            return new SweepScenarioResult(Command: command, Reasons: ["not run: the whole set boots the world once per scenario (pass --scenarios to run files)"], Status: SweepStatus.Unknown, Worlds: new Dictionary<string, string>());
        }

        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
        };

        startInfo.ArgumentList.Add(item: typeof(SweepCommand).Assembly.Location.Replace(oldValue: "Puck.Cli.Runs.dll", newValue: "Puck.Cli.dll"));
        startInfo.ArgumentList.Add(item: "test");

        foreach (var file in files) {
            startInfo.ArgumentList.Add(item: file);
        }

        startInfo.ArgumentList.Add(item: "--jobs");
        startInfo.ArgumentList.Add(item: "1");

        using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: "Failed to start dotnet."));
        var error = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        var stderr = error.GetAwaiter().GetResult();

        File.WriteAllText(contents: (stdout + stderr), path: Path.Combine(path1: directory, path2: "scenarios.txt"));

        var worlds = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var reasons = new List<string>();
        string? current = null;

        foreach (var line in stdout.Split(separator: '\n')) {
            var name = TestNamePattern().Match(input: line);

            if (name.Success) {
                current = name.Groups["name"].Value;

                continue;
            }

            var verdicts = VerdictPattern().Match(input: line);

            if (verdicts.Success && (current is not null)) {
                worlds[current] = $"{verdicts.Groups["passed"].Value}/{verdicts.Groups["total"].Value}";

                if (verdicts.Groups["passed"].Value != verdicts.Groups["total"].Value) {
                    reasons.Add(item: $"{current} passed {worlds[current]}");
                }
            }
        }

        if ((process.ExitCode != 0) && (reasons.Count == 0)) {
            reasons.Add(item: $"puck test exited {process.ExitCode.ToString(provider: CultureInfo.InvariantCulture)}; see scenarios.txt");
        }

        return new SweepScenarioResult(Command: command, Reasons: reasons, Status: ((process.ExitCode == 0) ? SweepStatus.Clean : SweepStatus.Red), Worlds: worlds);
    }

    [GeneratedRegex(pattern: @"^test (?<name>[^:]+):", options: RegexOptions.CultureInvariant)]
    private static partial Regex TestNamePattern();
    [GeneratedRegex(pattern: @"^\s+(?<passed>\d+)/(?<total>\d+) verdict\(s\) passed", options: RegexOptions.CultureInvariant)]
    private static partial Regex VerdictPattern();
}
