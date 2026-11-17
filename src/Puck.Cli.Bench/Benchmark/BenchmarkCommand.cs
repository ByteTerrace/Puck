using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Abstractions;
using Puck.Cli.Sweep;
using Puck.Commands;
using Puck.World;

namespace Puck.Cli.Bench;

/// <summary>
/// <c>puck benchmark</c>: boots a fresh paused World, arms a route at a named tick, plays it in real time on each backend
/// asked for and writes the run's report; <c>puck benchmark compare</c> and <c>--against</c> compare two reports. The World
/// plays the route itself (<c>world.benchmark</c>); this verb launches, collects and compares, and holds no copy of the
/// route.
/// </summary>
public static partial class BenchmarkCommand {
    private const string Verb = "benchmark";
    // Seconds the verb's own budget outlasts the run's wall-time limit: the boot, the renderer's readiness and the
    // collection, so a run that passes its limit ends itself and leaves its partial report before this verb gives up.
    private const int BudgetGraceSeconds = 240;
    private const int DefaultTimeoutSeconds = 600;
    // The most loops world.benchmark plays; the World refuses more.
    private const int MaxLoops = 16;
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(seconds: 120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(seconds: 1);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(seconds: 15);

    [GeneratedRegex(@"ticks (?<first>\d+)\.\.(?<last>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TickSpan();

    private sealed class RunOptions {
        public readonly Option<string> World = new("--world") { DefaultValueFactory = _ => "worlds/debug", Description = "The world's asset package folder, .puck source or .world.json document (default worlds/debug)." };
        public readonly Option<string> Route = new("--route") { Description = "The .puck source whose test blocks are the route, each a chapter: its when lines are the controller program and its expect lines the arrival.", Required = true };
        public readonly Option<string?> Chapter = new("--chapter") { Description = "Play only the chapters whose names start with this prefix; omitted plays every chapter in declaration order." };
        public readonly Option<string[]> Backends = new("--backend") { AllowMultipleArgumentsPerToken = true, Description = "Windowed graphics backends to play on, each in its own fresh World: directx and vulkan. Omitted plays both on Windows and vulkan elsewhere. Ignored with --headless." };
        public readonly Option<bool> Headless = new("--headless") { Description = "Play without a window or renderer: the arrival, state hash and host iterations are reported and every reading that needs a renderer is absent." };
        public readonly Option<int?> At = CliOptions.OptionalCount<int>("--at", "The tick the run is armed at: its first chapter's first line lands at this tick, or four ticks after the boot when that is later. Omitted starts four ticks after the boot.", 1);
        public readonly Option<int> Loops = CliOptions.Count("--loops", $"Play the route this many times from one boot, each loop from the state the one before left (1..{MaxLoops}, default 1). Reported as cold (the first), warm (the second) and steady (the rest).", 1, 1, MaxLoops);
        public readonly Option<bool> Uncapped = new("--uncapped") { Description = "Boot with the present mode that does not wait for the display (immediate), for a throughput trend; never a frame bar. The report says it ran uncapped and a comparison refuses to set it beside a paced run." };
        public readonly Option<double?> Budget = CliOptions.Amount("--budget", "The host work, in milliseconds, past which an iteration is over (default 16.667, one 60 Hz period).", 0.1);
        public readonly Option<int> Timeout = CliOptions.Count("--timeout-seconds", $"The run's wall-time limit (1..7200, default {DefaultTimeoutSeconds}): a run that passes it ends itself and keeps a partial report. The verb waits a further {BudgetGraceSeconds} s for the boot and collection.", 1, DefaultTimeoutSeconds, 7200);
        public readonly Option<int> Width = CliOptions.Count("--width", "Window width (default 1280).", 1, 1280, 16384);
        public readonly Option<int> Height = CliOptions.Count("--height", "Window height (default 720).", 1, 720, 16384);
        public readonly Option<string?> Cache = new("--cache-dir") { Description = "The mesh cache directory the World uses." };
        public readonly Option<string?> Artifact = new("--world-artifact") { Description = "A World build's Puck.World.dll to play in place of the checkout's own build." };
        public readonly Option<string?> Output = new("--output") { Description = "Parent of a unique run directory that receives report.json (puck.bench.v1), each backend's run.json and frames.csv, and its logs." };
        public readonly Option<string?> Against = new("--against") { Description = "A report (or its directory) from an earlier run to compare this run with, backend by backend; the verb exits with the comparison's status." };
        public readonly Option<bool> All = new("--all") { Description = "With --against, write every metric of a comparable chapter and not its headline." };
        public readonly Option<bool> Json = new("--json") { Description = "Print the report (puck.bench.v1) on standard output, with the summary on standard error." };
        public Command Command { get; }

        public RunOptions() => Command = new Command(Verb, "Play a route through the debug world in real time on a fresh paused World and report every chapter's frame statistics, hitches, GPU passes and counters; compare with an earlier report.") {
            World, Route, Chapter, Backends, Headless, At, Loops, Uncapped, Budget, Timeout, Width, Height, Cache, Artifact, Output, Against, All, Json,
        };
    }

    /// <summary>Creates the <c>benchmark</c> verb.</summary>
    /// <param name="sampleHost">The host sampler a run records the machine's load with, or <see langword="null"/> to record none.</param>
    /// <returns>The verb.</returns>
    public static Command Create(BenchHostSample? sampleHost) {
        var options = new RunOptions();

        options.Command.SetAction((parse, token) => Task.FromResult(Run(options, parse, sampleHost, token)));
        options.Command.Detail("""
            Boots a fresh World paused before its first tick, waits for the renderer with authority held, arms the route at the
            named tick and resumes, so every line of a fresh run lands on the same absolute tick from the boot image. The World
            plays the route itself in real time and records every host iteration; nothing in the run pauses, settles or waits.
            Each --backend gets a World of its own, one after another, under the GPU lease. A report.json (puck.bench.v1) holds,
            for each run, every loop's chapters: arrival, end state hash, frame statistics (interval and work as mean, median,
            95th, 99th and 99.9th percentile and maximum), the hitch list with each hitch's tick, last sampled position and phase
            split, each GPU pass's mean and maximum, and the change of the streaming and counter readings across the chapter;
            beside the run's boot span (the host's first iterations through the first chapter), its conditions and the machine's
            load. run.json and frames.csv per backend hold the World's own report and every iteration.

            --against compares each backend's run with the same backend's run in an earlier report, and `puck benchmark compare`
            compares two reports. A comparison is refused, by name, for another route, unlike conditions, a run that began in
            lockstep, or a metric one report lacks; a chapter that is off course, disturbed, unfinished or missing is
            unavailable and nothing of it is compared; a chapter whose program changed is described and not compared. Exit 0:
            compared. Exit 1: the run did not finish, or no chapter could be compared. Exit 2: a refusal, a usage error or an
            infrastructure failure.
            """);

        var baseline = new Argument<string>("baseline") { Description = "The earlier report.json, or the directory holding it." };
        var candidate = new Argument<string>("candidate") { Description = "The later report.json, or the directory holding it." };
        var all = new Option<bool>("--all") { Description = "Write every metric of a comparable chapter and not its headline." };
        var json = new Option<bool>("--json") { Description = "Print the comparison as JSON." };
        var compare = new Command("compare", "Compare two benchmark reports chapter by chapter.") { baseline, candidate, all, json };

        compare.SetAction(parse => {
            try {
                var comparison = BenchmarkComparison.Of(baseline: Load(path: parse.GetValue(baseline)!), candidate: Load(path: parse.GetValue(candidate)!));

                if (parse.GetValue(json)) { Console.WriteLine(comparison.ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
                else { comparison.Write(Console.Out, parse.GetValue(all)); }

                return comparison.ExitCode;
            } catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) {
                return CliExit.Refuse($"{Verb} compare", "report", exception.Message);
            }
        });
        compare.Detail("""
            Compares two puck.bench.v1 reports of the benchmark verb: each backend the candidate ran with the same backend of the
            baseline, loop by loop and chapter by chapter, the numbers set side by side with their difference and no threshold.
            Refusals are named: another-route, unlike-conditions, began-in-lockstep and missing-metric. Exit 0: compared. Exit 1:
            no chapter could be compared. Exit 2: refused or unreadable.
            """);
        options.Command.Add(compare);

        return options.Command;
    }

    // The benchmark section of a report file or of the report.json a directory holds.
    private static JsonObject Load(string path) {
        var file = (Directory.Exists(path: path) ? Path.Combine(path1: path, path2: "report.json") : path);

        if (!File.Exists(path: file)) {
            throw new FileNotFoundException(message: $"no report at {PuckPaths.Normalize(path: file)}");
        }

        var root = (JsonNode.Parse(json: File.ReadAllText(path: file)) as JsonObject) ?? throw new InvalidDataException(message: $"{PuckPaths.Normalize(path: file)} is not a JSON object");

        if ((root["schema"]?.GetValue<string>() != BenchReport.SchemaName) || (root["verb"]?.GetValue<string>() != Verb) || (root["benchmark"] is not JsonObject benchmark)) {
            throw new InvalidDataException(message: $"{PuckPaths.Normalize(path: file)} is not a {BenchReport.SchemaName} report of the {Verb} verb");
        }

        return benchmark;
    }

    private static int Run(RunOptions options, ParseResult parse, BenchHostSample? sampleHost, CancellationToken token) {
        var root = RepositoryPaths.RequireRoot();
        var route = Path.GetFullPath(path: parse.GetValue(options.Route)!);

        if (!File.Exists(path: route)) {
            return CliExit.Refuse(Verb, "--route", $"{PuckPaths.Normalize(path: route)} does not exist.");
        }

        var world = Path.GetFullPath(path: parse.GetValue(options.World)!);
        var headless = parse.GetValue(options.Headless);
        WorldBackendPreference?[] backends;

        try {
            backends = BackendsOf(headless: headless, named: parse.GetValue(options.Backends));
        } catch (ArgumentException exception) {
            return CliExit.Refuse(Verb, "--backend", exception.Message);
        }

        var directory = Path.GetFullPath(path: Path.Combine(path1: (parse.GetValue(options.Output) ?? "artifacts/benchmark"), path2: Guid.NewGuid().ToString(format: "N")));

        _ = Directory.CreateDirectory(path: directory);

        var shape = (headless ? WorldShape.Headless : WorldShape.Windowed);

        if (!WorldLaunch.TryResolve(Verb, root, directory, parse.GetValue(options.Artifact), backends.Any(predicate: backend => WorldLaunch.NeedsSpirv(shape, backend)), out var build, out var error)) {
            return CliExit.Refuse(Verb, "build", error);
        }

        using var held = build;
        using var host = new BenchHostRecorder(sampleHost);
        var routeName = RouteName(root: root, path: route);
        var prefix = (parse.GetValue(options.Chapter) ?? string.Empty);
        var runs = new JsonArray();
        var print = (parse.GetValue(options.Json) ? Console.Error : Console.Out);
        var failures = new List<string>();

        foreach (var backend in backends) {
            var name = ((backend is { } chosen) ? WorldBackendTokens.Token(backend: chosen) : "headless");
            var conditions = Conditions(options: options, parse: parse, backend: name, headless: headless, world: PuckPaths.Normalize(path: world));

            try {
                using var budget = new SuiteBudget(TimeSpan.FromSeconds(seconds: (parse.GetValue(options.Timeout) + BudgetGraceSeconds)), token);
                var played = Play(options: options, parse: parse, build: build, directory: Path.Combine(path1: directory, path2: name), backend: backend, shape: shape, world: world, route: route, prefix: prefix, budget: budget, print: print.Write);

                runs.Add(item: Fold(world: played, conditions: conditions, backend: name, routeName: (prefix.Length == 0 ? routeName : $"{routeName}#{prefix}")));
            } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or TimeoutException) {
                failures.Add(item: $"{name}: {exception.Message}");
                Console.Error.WriteLine(value: $"{Verb}: {name}: {exception.Message}; evidence {PuckPaths.Normalize(path: directory)}");
            }
        }

        var benchmark = new JsonObject {
            ["route"] = (prefix.Length == 0 ? routeName : $"{routeName}#{prefix}"),
            ["world"] = PuckPaths.Normalize(path: world),
            ["runs"] = runs,
        };
        var complete = ((failures.Count == 0) && runs.OfType<JsonObject>().All(predicate: static run => (run["complete"]?.GetValue<bool>() == true)));
        var chapters = runs.OfType<JsonObject>().SelectMany(selector: static run => (run["loops"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(selector: static loop => (loop["chapters"] as JsonArray ?? []).OfType<JsonObject>())).ToArray();
        var everyUnavailable = ((chapters.Length > 0) && chapters.All(predicate: static chapter => BenchmarkComparison.IsUnavailable(chapter: chapter)));
        var exit = (complete && !everyUnavailable && (runs.Count > 0)) ? CliExit.Success : CliExit.Failed;

        if (everyUnavailable) {
            Console.Error.WriteLine(value: $"{Verb}: every chapter of the run is off course, disturbed, unfinished or unmeasured, so the run is a failure and not a measurement");
        }
        if (parse.GetValue(options.Against) is { } against) {
            try {
                var comparison = BenchmarkComparison.Of(baseline: Load(path: against), candidate: benchmark);

                benchmark["comparison"] = comparison.ToJson();
                comparison.Write(print, parse.GetValue(options.All));
                exit = ((comparison.ExitCode == CliExit.Refused) ? CliExit.Refused : Math.Max(val1: exit, val2: comparison.ExitCode));
            } catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) {
                exit = CliExit.Refuse(Verb, "--against", exception.Message);
            }
        }

        var report = BenchReport.Start(Verb, BenchBuild.Of(build.Path), 1, Settings(options: options, parse: parse, route: routeName, world: world, backends: backends)) with {
            Complete = complete,
            HostLoad = host.Finish(),
            Metrics = Metrics(runs: runs),
            Benchmark = benchmark,
        };

        _ = report.Write(directory);
        if (parse.GetValue(options.Json)) {
            Console.WriteLine(report.ToJson());
        } else {
            print.WriteLine($"{Verb}: {report.Metrics.Count} metric(s) over {runs.Count} run(s); report {PuckPaths.Normalize(path: Path.Combine(path1: directory, path2: "report.json"))}");
        }

        return exit;
    }

    // The backends to play on: none for a headless run (one World, no device), the ones named, or both on Windows and
    // Vulkan elsewhere. A report holds one run per backend and says which, so automatic selection is no backend.
    private static WorldBackendPreference?[] BackendsOf(bool headless, string[]? named) {
        if (headless) {
            return [null];
        }
        if (named is not { Length: > 0 }) {
            return (OperatingSystem.IsWindows() ? [WorldBackendPreference.DirectX, WorldBackendPreference.Vulkan] : [WorldBackendPreference.Vulkan]);
        }

        var backends = named.Select(selector: static spelling => WorldLaunch.ParseBackend(token: spelling)).Distinct().ToArray();

        if (backends.Contains(value: WorldBackendPreference.Auto)) {
            throw new ArgumentException(message: "name directx or vulkan: a report holds one run per backend and must say which.");
        }
        if (backends.Contains(value: WorldBackendPreference.DirectX) && !OperatingSystem.IsWindows()) {
            throw new ArgumentException(message: "directx is a Windows backend.");
        }

        return backends;
    }

    // The route's name in a report: its path from the repository root when it lies within it, so two checkouts of one
    // route name it alike, and otherwise its file name.
    private static string RouteName(string root, string path) => (PuckPaths.IsWithin(path: path, root: root) ? PuckPaths.Relative(path: path, relativeTo: root) : Path.GetFileName(path: path));

    // Plays the route on one fresh paused World and returns the World's own report of it.
    private static JsonObject Play(RunOptions options, ParseResult parse, WorldBuild build, string directory, WorldBackendPreference? backend, WorldShape shape, string world, string route, string prefix, SuiteBudget budget, Action<string> print) {
        _ = Directory.CreateDirectory(path: directory);

        var timeout = parse.GetValue(options.Timeout);
        var request = new WorldLaunchRequest(Verb, build.Path) {
            Arguments = ["--paused", "--width", parse.GetValue(options.Width).ToString(CultureInfo.InvariantCulture), "--height", parse.GetValue(options.Height).ToString(CultureInfo.InvariantCulture),
                .. (parse.GetValue(options.Cache) is { } cache ? new[] { "--cache-dir", Path.GetFullPath(cache) } : []),
                .. ((parse.GetValue(options.Uncapped) && (shape == WorldShape.Windowed)) ? new[] { "--present-mode", "immediate" } : [])],
            Backend = backend,
            ExitAfterSeconds = WorldLaunch.Backstop(TimeSpan.FromSeconds(timeout + BudgetGraceSeconds)),
            Shape = shape,
            StateDirectory = Path.Combine(directory, "state"),
            World = world,
        };

        if (!SweepWorld.TryBoot(request, directory, BootTimeout, budget, out var session, out var bootError)) {
            throw new InvalidOperationException($"the World did not boot: {bootError}");
        }

        using var live = session!;

        // Authority is held at the boot's first tick, so the renderer can finish starting without the route moving.
        if ((shape == WorldShape.Windowed) && !live.Run("world.wait ready 60", 70).Ok) {
            throw new InvalidOperationException("the renderer was not ready within 60 s");
        }

        var runDirectory = Path.Combine(directory, "world");
        var line = string.Create(CultureInfo.InvariantCulture, $"world.benchmark run {CommandEcho.Quote(PuckPaths.Normalize(route))} {(prefix.Length == 0 ? "-" : CommandEcho.Quote(prefix))}{((parse.GetValue(options.At) is { } at) ? $" at {at}" : string.Empty)}{((parse.GetValue(options.Budget) is { } budgetMs) ? $" budget {budgetMs}" : string.Empty)} timeout {timeout} loops {parse.GetValue(options.Loops)} output {CommandEcho.Quote(PuckPaths.Normalize(runDirectory))}");
        var armed = live.Run(line, 60);

        if (!armed.Ok) {
            throw new InvalidOperationException($"the World refused the route: {armed.Output}");
        }
        print($"{Verb}: {armed.Output}\n");
        if (!live.Run("world.rate resume", 30).Ok) {
            throw new InvalidOperationException("the World would not resume");
        }

        var clock = Stopwatch.StartNew();
        var announced = TimeSpan.Zero;
        string? summary = null;

        while (summary is null) {
            if (budget.Expired) {
                throw new TimeoutException($"{budget.Reason}; the World was still playing");
            }

            var status = live.Run("world.benchmark", 30);

            if (!status.Ok) {
                throw new InvalidOperationException($"the World stopped answering: {status.Output}");
            }
            if (!status.Output.StartsWith("[world.benchmark: playing", StringComparison.Ordinal)) {
                summary = status.Output;
            } else if ((clock.Elapsed - announced) >= ProgressInterval) {
                announced = clock.Elapsed;
                print($"{Verb}: {status.Output}\n");
            } else {
                _ = budget.Token.WaitHandle.WaitOne(PollInterval);
            }
        }
        print($"{summary}\n");

        var path = Path.Combine(runDirectory, "run.json");

        if (!File.Exists(path)) {
            throw new InvalidDataException($"the run wrote no report at {PuckPaths.Normalize(path)}: {summary}");
        }

        return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject) ?? throw new InvalidDataException($"{PuckPaths.Normalize(path)} is not a JSON object");
    }

    // The conditions a comparison reads: what must be alike for two runs' numbers to mean the same, written by the verb
    // from what it asked for and the machine it ran on.
    private static JsonObject Conditions(RunOptions options, ParseResult parse, string backend, bool headless, string world) {
        var conditions = new JsonObject {
            ["world"] = world,
            ["backend"] = backend,
            ["shape"] = (headless ? "headless" : "windowed"),
            ["uncapped"] = (!headless && parse.GetValue(options.Uncapped)),
            ["at"] = (parse.GetValue(options.At) is { } at ? at : 0),
            ["loops"] = parse.GetValue(options.Loops),
            ["os"] = RuntimeInformation.OSDescription,
            ["cpus"] = Environment.ProcessorCount,
        };

        if (!headless) {
            conditions["width"] = parse.GetValue(options.Width);
            conditions["height"] = parse.GetValue(options.Height);
        }

        return conditions;
    }

    // One run as a report holds it: the World's own report with its readings' raw answers replaced by their change across
    // each chapter, and the verb's conditions beside it.
    private static JsonObject Fold(JsonObject world, JsonObject conditions, string backend, string routeName) {
        var run = (JsonObject)JsonNode.Parse(world.ToJsonString())!;

        // The budget an iteration is judged against is the World's, which says it whether or not the verb named one.
        conditions["budgetMilliseconds"] = world["budgetMilliseconds"]?.DeepClone();
        run["backend"] = backend;
        run["conditions"] = conditions;
        run["route"] = routeName;
        foreach (var loop in (run["loops"] as JsonArray ?? []).OfType<JsonObject>()) {
            foreach (var chapter in (loop["chapters"] as JsonArray ?? []).OfType<JsonObject>()) {
                var (readings, unmeasured) = BenchmarkMetrics.Readings(chapter["reads"] as JsonArray);

                _ = chapter.Remove("reads");
                chapter["readings"] = readings;
                chapter["unmeasured"] = unmeasured;
            }
        }

        return run;
    }

    // Every number of every chapter as a one-sample metric, named backend, loop kind, chapter and metric, so puck measure
    // reads one as /metrics/<name>/median.
    private static Dictionary<string, BenchMetric> Metrics(JsonArray runs) {
        var metrics = new Dictionary<string, BenchMetric>(StringComparer.Ordinal);

        foreach (var run in runs.OfType<JsonObject>()) {
            var backend = run["backend"]!.GetValue<string>();

            foreach (var loop in (run["loops"] as JsonArray ?? []).OfType<JsonObject>()) {
                var kind = loop["kind"]!.GetValue<string>();
                var slugs = new HashSet<string>(StringComparer.Ordinal);

                foreach (var chapter in (loop["chapters"] as JsonArray ?? []).OfType<JsonObject>()) {
                    var slug = BenchmarkMetrics.Slug(chapter["name"]!.GetValue<string>());

                    for (var suffix = 2; !slugs.Add(slug); suffix++) {
                        slug = $"{BenchmarkMetrics.Slug(chapter["name"]!.GetValue<string>())}-{suffix}";
                    }
                    foreach (var (name, metric) in BenchmarkMetrics.Of(chapter)) {
                        metrics[$"{backend}.{kind}.{slug}.{name}"] = BenchMetric.Of(metric.Unit, [metric.Value]);
                    }
                }
            }
        }

        return metrics;
    }

    private static Dictionary<string, string> Settings(RunOptions options, ParseResult parse, string route, string world, WorldBackendPreference?[] backends) => new() {
        ["route"] = route,
        ["chapter"] = (parse.GetValue(options.Chapter) ?? "(every)"),
        ["world"] = PuckPaths.Normalize(world),
        ["backends"] = string.Join(',', backends.Select(backend => ((backend is { } chosen) ? WorldBackendTokens.Token(chosen) : "headless"))),
        ["loops"] = parse.GetValue(options.Loops).ToString(CultureInfo.InvariantCulture),
        ["at"] = (parse.GetValue(options.At)?.ToString(CultureInfo.InvariantCulture) ?? "(boot)"),
        ["uncapped"] = parse.GetValue(options.Uncapped).ToString(),
        ["timeoutSeconds"] = parse.GetValue(options.Timeout).ToString(CultureInfo.InvariantCulture),
    };
}
