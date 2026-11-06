using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Puck.Abstractions;
using Puck.World;

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
        var scenariosOption = new Option<string[]>(name: "--scenarios") { AllowMultipleArgumentsPerToken = true, Description = "Override the plan's scenario sources with these paths or file patterns; run serially with puck test --jobs 1." };
        var noScenariosOption = new Option<bool>(name: "--no-scenarios") { Description = "Skip the plan's scenario set for a focused tools or tour run." };
        var worldOption = new Option<string>(name: "--world") { Description = $"The world to boot; {DefaultWorld} when omitted." };
        var artifactOption = new Option<string>(name: "--world-artifact") { Description = "Run this Puck.World entry assembly instead of the build of the checkout's sources. Never builds." };
        var backendOption = WorldLaunch.BackendOption(description: "The graphics backend for a new windowed World: auto, directx or vulkan; the document's when omitted. Requires --tour and cannot change an attached World.");
        var captureToolOption = new Option<string>(name: "--capture-tool") { Description = "Load the World's existing GPU capture tool before device creation (pix or renderdoc), so a plan can invoke gpu.capture. Requires a new windowed World." };
        var cacheOption = new Option<string>(name: "--cache-dir") { Description = "The World's device-cache root, shared by fresh Worlds; its per-user default when omitted. Use a writable lane directory in a sandbox." };
        var timeoutOption = new Option<int>(name: "--timeout") { DefaultValueFactory = static _ => 40, Description = "The most minutes the whole run may take, a build excepted: every World call, boot, lease wait and scenario process is bounded by what remains." };
        var command = new Command(
            description: "Sweep the debug world: run its tools, read every signal, take the capture tour, and write a report a later sweep compares with.",
            name: Verb
        ) { planOption, outputOption, baselineOption, tourOption, attachOption, noHeadlessOption, stopOption, scenariosOption, noScenariosOption, worldOption, artifactOption, backendOption, captureToolOption, cacheOption, timeoutOption };

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

            The plan's scenarios run serially with puck test --jobs 1, reusing compatible boots within each source.
            --scenarios overrides the sources; --no-scenarios skips them for a focused tools or tour run.

            The output directory holds report.json (every signal with its colour, metrics and answer file; every tool,
            still and sheet), the answers under headless/ and windowed/, and the stills and sheets under tour/.
            --baseline names a previous report.json: each signal then lists what changed by the plan's compare modes,
            and each still its changed-pixel fraction beyond the tour's level tolerance.

            Silence is never success: a signal the sweep could not measure (the attachment closed, the time passed, an
            answer lacking a metric a rule reads, a tool that did not finish) is reported unmeasured and fails the run
            like a red one. --timeout bounds the whole sweep, an attached World and the scenarios included, and an
            interrupt ends it the same way; the Worlds, the GPU lease and the processes the sweep started are released
            on every exit path.

            Exit codes: 0 nothing red or unmeasured, 1 something red or unmeasured, 2 a refusal (a plan, a build, a
            baseline folder used as --output, or a World that did not boot), 130 an interrupt.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => Task.FromResult(result: Run(
            artifact: parseResult.GetValue(option: artifactOption),
            attach: parseResult.GetValue(option: attachOption),
            baselinePath: parseResult.GetValue(option: baselineOption),
            backend: parseResult.GetValue(option: backendOption),
            captureTool: parseResult.GetValue(option: captureToolOption),
            cacheDirectory: parseResult.GetValue(option: cacheOption),
            cancellation: cancellationToken,
            noHeadless: parseResult.GetValue(option: noHeadlessOption),
            noScenarios: parseResult.GetValue(option: noScenariosOption),
            output: parseResult.GetValue(option: outputOption),
            planPath: parseResult.GetValue(option: planOption),
            scenarios: (parseResult.GetValue(option: scenariosOption) ?? []),
            stops: (parseResult.GetValue(option: stopOption) ?? []),
            timeoutMinutes: parseResult.GetValue(option: timeoutOption),
            tour: parseResult.GetValue(option: tourOption),
            worldPath: parseResult.GetValue(option: worldOption)
        )));

        return command;
    }

    private static int Run(string? planPath, string? output, string? baselinePath, bool tour, string? attach, bool noHeadless, bool noScenarios, string[] stops, string[] scenarios, string? worldPath, string? artifact, string? cacheDirectory, WorldBackendPreference? backend, string? captureTool, int timeoutMinutes, CancellationToken cancellation) {
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
        if ((backend is not null || captureTool is not null) && (!windowed || attach is not null)) {
            return CliExit.Refuse(verb: Verb, what: "--backend/--capture-tool", why: "require --tour with a newly booted windowed World; an attached device already exists");
        }

        if (noScenarios && (scenarios.Length > 0)) {
            return CliExit.Refuse(verb: Verb, what: "--scenarios", why: "cannot be combined with --no-scenarios");
        }
        scenarios = (noScenarios ? [] : ((scenarios.Length > 0) ? scenarios : (plan.Scenarios?.ToArray() ?? [])));

        if (noHeadless && !windowed && (scenarios.Length == 0)) {
            return CliExit.Refuse(verb: Verb, what: "the run", why: "no leg or scenario was selected; the run would read nothing");
        }
        var directory = Path.GetFullPath(path: (output ?? RunDirectory.CreatePath(prefix: "puck-sweep-")));
        var world = Path.GetFullPath(path: (worldPath ?? Path.Combine(path1: repositoryRoot, path2: DefaultWorld)));

        // The baseline's answers and stills are read while this run writes its own beside them: one folder for both
        // would replace the baseline before it is compared.
        if ((baseline is not null) && CliPaths.Same(left: Path.GetDirectoryName(path: baseline.Path)!, right: directory)) {
            return CliExit.Refuse(verb: Verb, what: "--output", why: $"{CliPaths.ToDisplay(fullPath: directory)} holds the --baseline report, and this run would overwrite its answers and stills before comparing them; name another directory");
        }

        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var legs = new List<SweepLegResult>();
        var tools = new List<SweepToolResult>();
        var signals = new List<SweepSignalResult>();
        SweepTourResult? tourResult = null;

        _ = Directory.CreateDirectory(path: directory);
        Console.Error.WriteLine(value: $"{Verb}: output {CliPaths.ToDisplay(fullPath: directory)}");

        string? entry = null;
        IDisposable? build = null;

        if (!noHeadless || (windowed && (attach is null))) {
            if (!WorldLaunch.TryResolve(build: out var resolved, error: out var buildError, logDirectory: directory, named: artifact, repositoryRoot: repositoryRoot, spirv: (windowed && (attach is null) && WorldLaunch.NeedsSpirv(backend: backend, shape: WorldShape.Windowed)), verb: Verb)) {
                return CliExit.Refuse(verb: Verb, what: "the World build", why: buildError.ReplaceLineEndings(replacementText: " "));
            }

            entry = resolved.Path;
            build = resolved;
        }

        // The whole sweep, a build and its lease waits excepted, is bounded by one budget, and an interrupt ends it the same way: every
        // wait of a World, a lease or a scenario honours it, and the Worlds, leases and processes below are released by
        // the scopes that own them, in order.
        using var budget = new SweepBudget(caller: cancellation, total: TimeSpan.FromMinutes(value: timeoutMinutes));
        var deadline = TimeSpan.FromMinutes(value: timeoutMinutes);

        using (build) {
            if (!noHeadless) {
                var logs = Path.Combine(path1: directory, path2: SweepLegs.Headless);

                if (!SweepWorld.TryBoot(
                    bootTimeout: TimeSpan.FromMinutes(value: 5),
                    budget: budget,
                    error: out var bootError,
                    logDirectory: logs,
                    request: Request(artifact: entry!, cacheDirectory: cacheDirectory, deadline: deadline, extra: ["--unpaced", "true", "--paused"], repositoryRoot: repositoryRoot, shape: WorldShape.Headless, state: Path.Combine(path1: logs, path2: "state"), world: world),
                    session: out var booted
                )) {
                    return (cancellation.IsCancellationRequested ? CliExit.Cancelled : CliExit.Refuse(verb: Verb, what: "the headless World", why: bootError));
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
                SweepWorld? rendered = null;
                string? logs = null;

                if (attach is not null) {
                    if (!SweepWorld.TryAttach(budget: budget, error: out var attachError, session: out rendered, world: attach)) {
                        return (cancellation.IsCancellationRequested ? CliExit.Cancelled : CliExit.Refuse(verb: Verb, what: $"--attach {attach}", why: attachError));
                    }
                } else if (budget.Reason is { } over) {
                    signals.Add(item: WindowedNotRun(reason: over));
                } else {
                    logs = Path.Combine(path1: directory, path2: SweepLegs.Windowed);

                    var request = Request(artifact: entry!, cacheDirectory: cacheDirectory, deadline: deadline, extra: ["--paused", .. ((captureTool is null) ? [] : new[] { "--capture-tool", captureTool })], repositoryRoot: repositoryRoot, shape: WorldShape.Windowed, state: Path.Combine(path1: logs, path2: "state"), world: world) with { Backend = backend };

                    // The launch takes the GPU lease, nested under an outer holder, and the World releases it when it stops.
                    if (!SweepWorld.TryBoot(
                        bootTimeout: TimeSpan.FromMinutes(value: 5),
                        budget: budget,
                        error: out var bootError,
                        logDirectory: logs,
                        request: request,
                        session: out rendered
                    )) {
                        return (cancellation.IsCancellationRequested ? CliExit.Cancelled : CliExit.Refuse(verb: Verb, what: "the windowed World", why: bootError));
                    }
                }

                if (rendered is not null) {
                    using var shown = rendered;

                    if (shown.Leg != SweepLegs.Windowed) {
                        return CliExit.Refuse(verb: Verb, what: $"World {shown.ProcessId.ToString(provider: CultureInfo.InvariantCulture)}", why: "it composes no renderer, so it cannot take the tour");
                    }

                    var run = new SweepRun(baseline: baseline, directory: directory, plan: plan, progress: Console.Error, world: shown);
                    // Warmup and frame signals measure ordinary presentation. Only the still tour converges its clock/lighting.
                    var held = shown.Run(script: "world.rate pause\nworld.lockstep off");

                    if (!held.Ok) {
                        signals.Add(item: WindowedNotRun(reason: held.Output));
                    }

                    Console.Error.WriteLine(value: $"{Verb}: windowed World {shown.ProcessId.ToString(provider: CultureInfo.InvariantCulture)} attached");

                    if (run.Settle(deadline: TimeSpan.FromMinutes(value: 10)) is { } unsettled) {
                        signals.Add(item: unsettled);
                    }

                    var warmed = shown.Run(script: "world.rate resume\nworld.wait 300\nworld.rate pause", timeoutSeconds: 120);

                    if (!warmed.Ok) {
                        signals.Add(item: WindowedNotRun(reason: warmed.Output));
                    }
                    signals.AddRange(collection: run.Signals());
                    tourResult = run.Tour(names: stops);
                    signals.AddRange(collection: run.Recheck());
                    legs.Add(item: new SweepLegResult(Booted: shown.Owned, Leg: SweepLegs.Windowed, Logs: ((logs is null) ? null : CliPaths.ToDisplay(fullPath: logs)), Notes: run.Notes, ProcessId: shown.ProcessId));
                }
            }
        }

        if (baseline is not null) {
            signals.AddRange(collection: baseline.MissingSignals(current: signals, legs: legs).ToArray());
        }
        var report = new SweepReport(
            Baseline: baseline?.Path.Replace(newChar: '/', oldChar: '\\'),
            Commit: (CliGit.TryResolveCommit(repository: repositoryRoot, resolved: out var commit, revision: "HEAD") ? commit : "unknown"),
            Legs: legs,
            Scenarios: Scenarios(artifact: artifact, budget: budget, cacheDirectory: cacheDirectory, directory: directory, files: scenarios, repositoryRoot: repositoryRoot),
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

        if (cancellation.IsCancellationRequested) {
            return CliExit.Cancelled;
        }

        // Red, and whatever the sweep could not measure, fails the run: a run that read nothing is not a clean one.
        return (report.Failed ? CliExit.Failed : CliExit.Success);
    }
    private static SweepSignalResult WindowedNotRun(string reason) => new(Changes: [], File: string.Empty, Leg: SweepLegs.Windowed, Line: string.Empty, Metrics: new Dictionary<string, string>(), Name: "windowed-leg", Reasons: [$"not run: {reason}"], Status: SweepStatus.Unknown);
    private static bool SamePath(string left, string right) => string.Equals(a: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: left)), b: Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: right)), comparisonType: (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    private static WorldLaunchRequest Request(string artifact, string world, string state, string? cacheDirectory, TimeSpan deadline, WorldShape shape, string repositoryRoot, IReadOnlyList<string> extra) => new(Artifact: artifact, Step: Verb) {
        Arguments = [.. ((cacheDirectory is null) ? Array.Empty<string>() : ["--cache-dir", PuckPaths.Normalize(path: cacheDirectory)]), .. extra],
        ExitAfterSeconds = WorldLaunch.Backstop(deadline: deadline),
        Shape = shape,
        StateDirectory = state,
        WorkingDirectory = repositoryRoot,
        World = world,
    };
    // Each file through this CLI's own puck test, one worker at a time, read from its "test <name>:" and
    // "<passed>/<total> verdict(s) passed" lines and its exit code.
    private static SweepScenarioResult Scenarios(string? artifact, SweepBudget budget, string? cacheDirectory, string directory, string[] files, string repositoryRoot) {
        var worlds = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var reasons = new List<string>();
        var status = SweepStatus.Clean;
        var command = $"puck test {string.Join(separator: " ", values: files)} --jobs 1";
        if (files.Length == 0) {
            return new SweepScenarioResult(Command: command, Reasons: ["not selected"], Status: SweepStatus.Skipped, Worlds: worlds);
        }
        var expanded = new SortedSet<string>(comparer: PuckPaths.Comparer);
        foreach (var file in files) {
            try {
                var path = PuckPaths.Normalize(path: Path.GetFullPath(path: file, basePath: repositoryRoot));
                var pattern = Path.GetFileName(path: path);
                var matches = ((pattern.Contains(value: '*') || pattern.Contains(value: '?'))
                    ? Directory.GetFiles(path: Path.GetDirectoryName(path: path)!, searchPattern: pattern)
                    : [path]);
                if (matches.Length == 0) {
                    reasons.Add(item: $"no scenario source matches {file}");
                    status = SweepStatus.Red;
                }
                expanded.UnionWith(other: matches.Select(selector: PuckPaths.Normalize));
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) {
                reasons.Add(item: $"cannot read scenario sources {file}: {exception.Message.Replace(oldChar: Path.DirectorySeparatorChar, newChar: '/')}");
                status = SweepStatus.Red;
            }
        }
        var ordinal = 0;
        foreach (var file in expanded) {
            var folder = Path.Combine(path1: directory, path2: "scenarios", path3: (ordinal++).ToString(provider: CultureInfo.InvariantCulture));
            _ = Directory.CreateDirectory(path: folder);
            Console.Error.WriteLine(value: $"sweep: scenarios {CliPaths.ToDisplay(fullPath: file)}");
            var result = ScenarioFile(artifact: artifact, budget: budget, cacheDirectory: cacheDirectory, directory: folder, file: file, repositoryRoot: repositoryRoot);
            status = SweepStatus.Worse(left: status, right: result.Status);
            reasons.AddRange(collection: result.Reasons.Select(selector: reason => $"{CliPaths.ToDisplay(fullPath: file)}: {reason}"));
            foreach (var (name, value) in result.Worlds) {
                worlds[$"{CliPaths.ToDisplay(fullPath: file)}::{name}"] = value;
            }
        }
        return new SweepScenarioResult(Command: command, Reasons: reasons, Status: status, Worlds: worlds);
    }
    private static SweepScenarioResult ScenarioFile(string? artifact, SweepBudget budget, string? cacheDirectory, string directory, string file, string repositoryRoot) {
        var command = $"puck test {file} --jobs 1";

        if (budget.Reason is { } over) {
            return new SweepScenarioResult(Command: command, Reasons: [$"not run: {over}"], Status: SweepStatus.Unknown, Worlds: new Dictionary<string, string>());
        }

        string[] arguments = [
            PuckPaths.Normalize(path: typeof(SweepCommand).Assembly.Location.Replace(newValue: "Puck.Cli.dll", oldValue: "Puck.Cli.Runs.dll")),
            "test", file, "--jobs", "1", "--keep", Path.Combine(path1: directory, path2: "run"),
            .. ((artifact is null) ? [] : new[] { "--world-artifact", PuckPaths.Normalize(path: artifact) }),
            .. ((cacheDirectory is null) ? [] : new[] { "--cache-dir", PuckPaths.Normalize(path: cacheDirectory) }),
        ];
        string? unfinished = null;
        var stdout = string.Empty;
        var stderr = string.Empty;
        var exitCode = 0;

        // The run ends with the sweep's time, and takes its process tree with it; it joins the job that ends it with this
        // process, so the Worlds it starts never outlive the sweep.
        try {
            var run = CliProcess.RunCaptured(
                arguments: arguments,
                cancellationToken: budget.Token,
                fileName: "dotnet",
                input: string.Empty,
                started: process => {
                    ChildProcessJob.Assign(process: process);
                    Console.Error.WriteLine(value: $"sweep: scenario process {process.Id.ToString(provider: CultureInfo.InvariantCulture)}");
                },
                timeout: Timeout.InfiniteTimeSpan,
                workingDirectory: repositoryRoot
            );

            stdout = run.Stdout;
            stderr = run.Stderr;
            exitCode = run.ExitCode;
        } catch (OperationCanceledException) {
            unfinished = $"puck test did not finish: {budget.Reason}";
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            unfinished = $"puck test did not start: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
        }

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

        var status = SweepStatus.Clean;

        if (unfinished is not null) {
            reasons.Add(item: unfinished);
            status = SweepStatus.Unknown;
        } else if (exitCode != 0) {
            status = SweepStatus.Red;

            if (reasons.Count == 0) {
                reasons.Add(item: $"puck test exited {exitCode.ToString(provider: CultureInfo.InvariantCulture)}; see scenarios.txt");
            }
        } else if (worlds.Count == 0) {
            reasons.Add(item: "puck test exited 0 but printed no verdict; see scenarios.txt");
            status = SweepStatus.Unknown;
        }

        return new SweepScenarioResult(Command: command, Reasons: reasons, Status: status, Worlds: worlds);
    }
    [GeneratedRegex(pattern: @"^test (?<name>[^:]+):", options: RegexOptions.CultureInvariant)]
    private static partial Regex TestNamePattern();
    [GeneratedRegex(pattern: @"^\s+(?<passed>\d+)/(?<total>\d+) verdict\(s\) passed", options: RegexOptions.CultureInvariant)]
    private static partial Regex VerdictPattern();
}
