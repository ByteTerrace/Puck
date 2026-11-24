using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions;
using Puck.Cli.Host;
using Puck.Cli.Sweep;
using Puck.Commands;
using Puck.Hosting.Telemetry;
using Puck.World;

namespace Puck.Cli.Bench;

/// <summary>
/// <c>puck benchmark</c>: the measurement instrument. It boots fresh Worlds, plays routes through them in real time on each
/// backend asked for, in the topologies asked for, and writes one report (<c>report.json</c>) and its human-readable page
/// (<c>summary.md</c>); <c>puck benchmark compare</c> and <c>--against</c> compare two reports. The World plays each route
/// itself (<c>world.benchmark</c>); this verb launches, collects and compares, and holds no copy of a route.
/// </summary>
/// <remarks>Without <c>--route</c> it runs the standing set (<see cref="StandingLegs"/>): the debug route on every
/// backend, the moon round trip, the debug route against a remote authority on this machine, and the four-seat route. A
/// leg whose dependency the World does not have yet is reported unrun with the World's own reason, never skipped
/// silently.</remarks>
public static partial class BenchmarkCommand {
    private const string Verb = "benchmark";
    // Seconds the verb's own budget outlasts the run's wall-time limit: the boot, the renderer's readiness and the
    // collection, so a run that passes its limit ends itself and leaves its partial report before this verb gives up.
    private const int BudgetGraceSeconds = 240;
    private const int DefaultTimeoutSeconds = 600;
    // The most loops world.benchmark plays; the World refuses more.
    private const int MaxLoops = 16;
    private const int MaxSeats = 4;
    // The routes the standing set plays: the debug walk, and the moon round trip once the world authors it.
    private const string DebugRoute = "debug";
    private const string MoonRoute = "moon";
    // What world.benchmark answers while a route waits for the navigation it plans over, and while a run plays.
    private const string PreparingPrefix = "[world.benchmark: preparing";
    private const string PlayingPrefix = "[world.benchmark: playing";
    // What world.benchmark answers for a route the running document does not author, and for a seat count it cannot play.
    private const string NoRouteMarker = "names no route of the running world";
    private const string SeatsMarker = "waits on the multi-seat client";
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(seconds: 120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(seconds: 1);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(seconds: 15);
    // How long a client of a remote authority may take for its seat's body to be handed over and delivered.
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(seconds: 120);

    // One route a leg plays in its World: the name its run is reported under, the route, and why it is not run when the
    // running document does not author it (null for a route that must be authored).
    private sealed record Play(string Name, string Route, string? WaitsOn = null);

    // One World a run boots, and the routes it plays one after another: the backend (null headless), the remote
    // authority its seat joins (null in process, "local" for a listening World this verb starts beside it, or an
    // ip:port), and the seats it plays on.
    private sealed record Leg(string Name, WorldBackendPreference? Backend, string? Remote, int Seats, IReadOnlyList<Play> Plays);

    private sealed class RunOptions {
        public readonly Option<string> World = new("--world") { DefaultValueFactory = _ => "worlds/debug", Description = "The world's asset package folder, .puck source or .world.json document (default worlds/debug). A player install's package (its world folder) and its Puck.World.dll (--world-artifact) run the release a player runs." };
        public readonly Option<string?> Route = new("--route") { Description = "The route: the name of a `routes` row of the world's document (each chapter a list of places its seat program walks to), or a .puck source whose test blocks are the route, each a chapter. Omitted runs the standing set: the debug route on every backend, the moon round trip, the debug route against a remote authority on this machine and the four-seat route." };
        public readonly Option<string?> Chapter = new("--chapter") { Description = "Play only the chapters whose names start with this prefix; omitted plays every chapter in declaration order." };
        public readonly Option<string[]> Backends = new("--backend") { AllowMultipleArgumentsPerToken = true, Description = "Windowed graphics backends to play on, each in its own fresh World: directx and vulkan. Omitted plays both on Windows and vulkan elsewhere. Ignored with --headless." };
        public readonly Option<bool> Headless = new("--headless") { Description = "Play without a window or renderer: the arrival, state hash and host iterations are reported and every reading that needs a renderer is absent." };
        public readonly Option<string?> Remote = new("--remote") { Description = "Play against a remote authority: local starts a listening World on this machine from a staged copy of the world (its own federation key, admitting one remote player) and boots the measured World with --connect to it; an ip:port connects to an authority already listening there. Omitted plays with the authority in process." };
        public readonly Option<int> Seats = CliOptions.Count("--seats", $"Play the route on this many seats of one client at once (1..{MaxSeats}, default 1). A World that cannot yet seat them reports the leg unrun with its reason.", 1, 1, MaxSeats);
        public readonly Option<int?> At = CliOptions.OptionalCount<int>("--at", "The tick the run is armed at: its first chapter's first line lands at this tick, or four ticks after the boot when that is later. Omitted starts four ticks after the boot.", 1);
        public readonly Option<int> Loops = CliOptions.Count("--loops", $"Play the route this many times from one boot, each loop from the state the one before left (1..{MaxLoops}, default 1). Reported as cold (the first), warm (the second) and steady (the rest).", 1, 1, MaxLoops);
        public readonly Option<string> Profile = new("--profile") { DefaultValueFactory = _ => "auto", Description = "The device profile the run plays under, applied before the first chapter (`world.device-profile`): a name of the world's render.devices, auto (the one matching the adapter, the default) or none. Its quality table, memory bars and frame bar are the run's conditions, and the report names them." };
        public readonly Option<bool> Uncapped = new("--uncapped") { Description = "Boot with the present mode that does not wait for the display (immediate), for a throughput trend; never a frame bar. The report says it ran uncapped and a comparison refuses to set it beside a paced run." };
        public readonly Option<double?> Budget = CliOptions.Amount("--budget", "The host work, in milliseconds, past which an iteration is over (default the period of the device profile's frame bar, else 16.667).", 0.1);
        public readonly Option<int> Timeout = CliOptions.Count("--timeout-seconds", $"Each run's wall-time limit (1..7200, default {DefaultTimeoutSeconds}): a run that passes it ends itself and keeps a partial report, and a route whose navigation is still baking after as long reports it never started and fails the verb. The verb waits a further {BudgetGraceSeconds} s for the boot and collection.", 1, DefaultTimeoutSeconds, 7200);
        public readonly Option<int?> Width = CliOptions.OptionalCount<int>("--width", "Window width. Omitted with --height, the window opens at the device profile's frame bar (--frame-bar): 2560 by 1440 on the RTX 4070, 1920 by 1080 on the RTX 2060.", 1);
        public readonly Option<int?> Height = CliOptions.OptionalCount<int>("--height", "Window height; see --width.", 1);
        public readonly Option<string?> Cache = new("--cache-dir") { Description = "The mesh cache directory the World uses: a player install's prewarmed store plays as a player's does." };
        public readonly Option<string?> Artifact = new("--world-artifact") { Description = "A World build's Puck.World.dll to play in place of the checkout's own build, such as a player install's." };
        public readonly Option<string?> Output = new("--output") { Description = "Parent of a unique run directory that receives report.json (puck.bench.v1), summary.md, each run's run.json and frames.csv, and its logs." };
        public readonly Option<string?> Against = new("--against") { Description = "A report (or its directory) from an earlier run to compare this run with, run by run; the verb exits with the comparison's status." };
        public readonly Option<bool> All = new("--all") { Description = "With --against, write every metric of a comparable chapter and not its headline." };
        public readonly Option<bool> Json = new("--json") { Description = "Print the report (puck.bench.v1) on standard output, with the summary on standard error." };
        public Command Command { get; }

        public RunOptions() => Command = new Command(Verb, "Play routes through a world in real time on fresh Worlds, on each backend and topology, and report every chapter's frame statistics, frame-thread parts, GPU passes, counted work, memory census and the machine's conditions; compare with an earlier report.") {
            World, Route, Chapter, Backends, Headless, Remote, Seats, At, Loops, Profile, Uncapped, Budget, Timeout, Width, Height, Cache, Artifact, Output, Against, All, Json,
        };
    }

    /// <summary>Creates the <c>benchmark</c> verb.</summary>
    /// <param name="sampleHost">The host sampler a run records the machine's load with, or <see langword="null"/> to record none.</param>
    /// <returns>The verb.</returns>
    public static Command Create(BenchHostSample? sampleHost) {
        var options = new RunOptions();

        options.Command.SetAction((parse, token) => Task.FromResult(Run(options, parse, sampleHost, token)));
        options.Command.Detail($"""
            Without --route it runs the standing set, one World per leg: the debug route on each backend, then the moon round
            trip in the same World (reported unrun, with the reason, until the world authors a `moon` route); the debug route
            against a listening World started on this machine (--remote local); and the debug route on four seats (reported
            unrun until the World seats four players on one client). With --route it plays that route on each backend, in
            process or against --remote, on --seats seats.
            Each World boots paused before its first tick, waits for the renderer with authority held, applies the device
            profile, arms the route at the named tick and resumes, so every line of a fresh run lands on the same absolute tick
            from the boot image. A client of a remote authority boots running, since a paused boot commits no transfer, waits
            for its seat's body to be handed over and delivered, and its runs are attached. Nothing in a run pauses, settles or
            waits. A windowed World opens at the device profile's frame bar unless --width and --height name a size.
            report.json (puck.bench.v1) holds, for each run, every loop's chapters: arrival, end state hash, frame statistics
            (interval and work as mean, median, 95th, 99th and 99.9th percentile and maximum), the hitch list, the frame
            thread's parts, each GPU pass's mean and maximum, the change of the streaming and counter readings across the
            chapter, and the chapter's memory census (host private and working set, the device ledger against the budget and
            the profile's bar, every residency with a digest of what it holds, and the residencies that hold the same groups);
            beside each run's boot span, window, topology, device profile, conditions and the machine's load. summary.md is the
            same report in a page a person reads. run.json and frames.csv per run hold the World's own report and every
            iteration.
            Each run also records the machine it ran on: the adapter's clocks, power, utilization, performance state and clock
            events sampled through every chapter (`adapter`), and the CPU every process outside the run's own tree used, the
            programs that used most, and any other World or lease holder seen (`machine`). A run whose other processes used more
            than {HostCompetitionRecorder.DisturbedShare:P0} of the machine's CPU or {HostCompetitionRecorder.DisturbedCores:0} core, that shared the machine with another World, or whose adapter's clock sat
            under {AdapterTelemetrySummary.HeldClockFraction:P0} of its ceiling while loaded and not power or thermal limited, is DISTURBED: it is named in the summary
            line, stored as `conditions` in report.json and `machine.verdict` of the run, and its timings are not a verdict on the build.
            --against compares each run with the run of the same name and backend in an earlier report, and `puck benchmark
            compare` compares two reports. A comparison is refused, by name, for another route, unlike conditions (the verb's,
            and the World's own: its table, adapter, window, topology and overlay), a run that began in lockstep, a disturbed run,
            or a metric one report lacks; a chapter that is off course, disturbed, unfinished or missing is unavailable and
            nothing of it is compared; a chapter whose program changed is described and not compared. Exit 0: compared. Exit 1:
            a run did not finish, or no chapter could be compared. Exit 2: a refusal, a usage error or an infrastructure failure.
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
            Compares two puck.bench.v1 reports of the benchmark verb: each run the candidate holds with the run of the same name
            and backend in the baseline, loop by loop and chapter by chapter, the numbers set side by side with their difference
            and no threshold. Refusals are named: another-route, unlike-conditions, began-in-lockstep, disturbed-run and
            missing-metric. Exit 0: compared. Exit 1: no chapter could be compared. Exit 2: refused or unreadable.
            """);
        options.Command.Add(compare);

        var folded = new Argument<string[]>("runs") { Arity = ArgumentArity.OneOrMore, Description = "Each run.json that world.benchmark wrote, or the directory holding it." };
        var foldOutput = new Option<string?>("--output") { Description = "Parent of a unique directory that receives report.json and summary.md (default artifacts/benchmark)." };
        var fold = new Command("fold", "Fold run.json reports that world.benchmark wrote on its own, such as a player build's, into one report.") { folded, foldOutput };

        fold.SetAction(parse => {
            try {
                return Fold(paths: parse.GetValue(folded)!, output: parse.GetValue(foldOutput));
            } catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) {
                return CliExit.Refuse($"{Verb} fold", "runs", exception.Message);
            }
        });
        fold.Detail("""
            Folds runs typed into a World by hand (`world.benchmark run <route>` in a player build's console, which writes
            run.json under the World's state directory) into one puck.bench.v1 report and its summary.md, as `puck benchmark`
            folds the runs it plays. Each run states its own conditions from what the World recorded: its build, backend,
            window, device profile, adapter, topology, overlay, operating system and processors; it records no competition from
            the rest of the machine, since no verb watched it, so its machine verdict judges only the adapter's clock. A folded
            report compares, with `puck benchmark compare`, only with another whose runs state the same conditions. Exit 0:
            folded. Exit 2: a run could not be read.
            """);
        options.Command.Add(fold);

        return options.Command;
    }

    // Folds run.json files written by world.benchmark on its own into one report beside its summary.
    private static int Fold(string[] paths, string? output) {
        var runs = new JsonArray();
        string? build = null;

        foreach (var given in paths) {
            var file = (Directory.Exists(path: given) ? Path.Combine(path1: given, path2: "run.json") : given);

            if (!File.Exists(path: file)) {
                throw new FileNotFoundException(message: $"no run at {PuckPaths.Normalize(path: file)}");
            }

            var world = (JsonNode.Parse(json: File.ReadAllText(path: file)) as JsonObject) ?? throw new InvalidDataException(message: $"{PuckPaths.Normalize(path: file)} is not a JSON object");
            var route = (world["route"]?.GetValue<string>() ?? throw new InvalidDataException(message: $"{PuckPaths.Normalize(path: file)} names no route; it is not a world.benchmark run"));
            var backend = (world["backend"]?.GetValue<string>() ?? "unstated");
            var conditions = new JsonObject {
                ["backend"] = backend,
                ["shape"] = ((world["window"] is null) ? "headless" : "windowed"),
                ["folded"] = true,
                ["os"] = world["os"]?.DeepClone(),
                ["cpus"] = world["cpus"]?.DeepClone(),
            };

            build ??= world["build"]?.GetValue<string>();
            runs.Add(item: Fold(world: world, conditions: conditions, backend: backend, leg: route, routeName: route, competition: null));
        }

        var directory = Path.GetFullPath(path: Path.Combine(path1: (output ?? "artifacts/benchmark"), path2: Guid.NewGuid().ToString(format: "N")));
        var benchmark = new JsonObject { ["route"] = "folded", ["world"] = "(as each run names)", ["runs"] = runs, ["unrun"] = new JsonArray() };
        var verdict = BenchmarkConditions.Describe(runs: runs.OfType<JsonObject>());
        var report = BenchReport.Start(Verb, new BenchBuild(Path: "(folded from run.json)", Sha256: string.Empty, Version: (build ?? "unstated")), 1, new Dictionary<string, string> { ["folded"] = string.Join(separator: ", ", values: paths.Select(selector: PuckPaths.Normalize)) }) with {
            Complete = runs.OfType<JsonObject>().All(predicate: static run => (run["complete"]?.GetValue<bool>() == true)),
            Conditions = (verdict ?? BenchmarkConditions.Clean),
            Metrics = Metrics(runs: runs),
            Benchmark = benchmark,
        };

        _ = Directory.CreateDirectory(path: directory);
        _ = report.Write(directory);
        File.WriteAllText(path: Path.Combine(path1: directory, path2: "summary.md"), contents: BenchmarkSummary.Of(report: (JsonObject)JsonNode.Parse(json: report.ToJson())!));
        Console.WriteLine(value: $"{Verb} fold: {runs.Count} run(s); conditions {(verdict ?? BenchmarkConditions.Clean)}; report {PuckPaths.Normalize(path: Path.Combine(path1: directory, path2: "report.json"))}");

        return CliExit.Success;
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
        // A route names a `routes` row of the world's document, which the World resolves, unless it names a .puck source.
        var named = parse.GetValue(options.Route);
        var route = ((named is not null) && File.Exists(path: named)) ? Path.GetFullPath(path: named) : named;

        if ((named is not null) && named.EndsWith(value: ".puck", comparisonType: StringComparison.OrdinalIgnoreCase) && !File.Exists(path: named)) {
            return CliExit.Refuse(Verb, "--route", $"{PuckPaths.Normalize(path: Path.GetFullPath(path: named))} does not exist.");
        }
        if ((parse.GetValue(options.Width) is null) != (parse.GetValue(options.Height) is null)) {
            return CliExit.Refuse(Verb, "--width", "name both --width and --height, or neither for the device profile's frame bar.");
        }

        var world = Path.GetFullPath(path: parse.GetValue(options.World)!);
        var headless = parse.GetValue(options.Headless);
        WorldBackendPreference?[] backends;

        try {
            backends = BackendsOf(headless: headless, named: parse.GetValue(options.Backends));
        } catch (ArgumentException exception) {
            return CliExit.Refuse(Verb, "--backend", exception.Message);
        }

        var remote = parse.GetValue(options.Remote);

        if ((remote is not null) && (remote != "local") && !System.Net.IPEndPoint.TryParse(s: remote, result: out _)) {
            return CliExit.Refuse(Verb, "--remote", $"'{remote}' is neither local nor an ip:port.");
        }

        var directory = Path.GetFullPath(path: Path.Combine(path1: (parse.GetValue(options.Output) ?? "artifacts/benchmark"), path2: Guid.NewGuid().ToString(format: "N")));

        _ = Directory.CreateDirectory(path: directory);

        var shape = (headless ? WorldShape.Headless : WorldShape.Windowed);

        if (!WorldLaunch.TryResolve(Verb, root, directory, parse.GetValue(options.Artifact), backends.Any(predicate: backend => WorldLaunch.NeedsSpirv(shape, backend)), out var build, out var error)) {
            return CliExit.Refuse(Verb, "build", error);
        }

        using var held = build;
        using var host = new BenchHostRecorder(sampleHost);
        var routeName = ((route is null) ? null : (File.Exists(path: route) ? RouteName(root: root, path: route) : route));
        var prefix = (parse.GetValue(options.Chapter) ?? string.Empty);
        var legs = ((route is null)
            ? StandingLegs(backends: backends)
            : backends.Select(selector: backend => new Leg(Backend: backend, Name: routeName!, Plays: [new Play(Name: routeName!, Route: route)], Remote: remote, Seats: parse.GetValue(options.Seats))).ToArray());
        var runs = new JsonArray();
        var unrun = new JsonArray();
        var print = (parse.GetValue(options.Json) ? Console.Error : Console.Out);
        var failures = new List<string>();

        foreach (var leg in legs) {
            var backend = ((leg.Backend is { } chosen) ? WorldBackendTokens.Token(backend: chosen) : "headless");

            try {
                using var budget = new SuiteBudget(TimeSpan.FromSeconds(seconds: (Allowance(timeout: parse.GetValue(options.Timeout)) * leg.Plays.Count)), token);

                foreach (var outcome in PlayLeg(options: options, parse: parse, build: build, directory: Path.Combine(path1: directory, path2: $"{BenchmarkMetrics.Slug(name: leg.Name)}-{backend}"), leg: leg, shape: shape, world: world, prefix: prefix, budget: budget, print: print.Write)) {
                    if (outcome.Unrun is { } reason) {
                        unrun.Add(item: new JsonObject { ["leg"] = outcome.Name, ["backend"] = backend, ["reason"] = reason });
                        print.Write($"{Verb}: {outcome.Name} on {backend}: not run: {reason}\n");

                        continue;
                    }

                    var folded = Fold(world: outcome.Run!, conditions: Conditions(options: options, parse: parse, backend: backend, headless: headless, world: PuckPaths.Normalize(path: world), leg: leg), backend: backend, leg: outcome.Name, routeName: (prefix.Length == 0 ? outcome.Route : $"{outcome.Route}#{prefix}"), competition: outcome.Competition);

                    runs.Add(item: folded);
                    if (BenchmarkConditions.ReasonsOf(run: folded) is { Count: > 0 } reasons) {
                        print.Write($"{Verb}: {outcome.Name} on {backend}: DISTURBED: {string.Join(separator: "; ", values: reasons)}\n");
                    }
                }
            } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException or TimeoutException) {
                failures.Add(item: $"{leg.Name} on {backend}: {exception.Message}");
                unrun.Add(item: new JsonObject { ["leg"] = leg.Name, ["backend"] = backend, ["reason"] = exception.Message, ["failed"] = true });
                Console.Error.WriteLine(value: $"{Verb}: {leg.Name} on {backend}: {exception.Message}; evidence {PuckPaths.Normalize(path: directory)}");
            }
        }

        var benchmark = new JsonObject {
            ["route"] = ((routeName is null) ? "standing" : (prefix.Length == 0 ? routeName : $"{routeName}#{prefix}")),
            ["world"] = PuckPaths.Normalize(path: world),
            ["runs"] = runs,
            ["unrun"] = unrun,
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

        var verdict = BenchmarkConditions.Describe(runs: runs.OfType<JsonObject>());
        var report = BenchReport.Start(Verb, BenchBuild.Of(build.Path), 1, Settings(options: options, parse: parse, route: (routeName ?? "standing"), world: world, backends: backends)) with {
            Complete = complete,
            Conditions = (verdict ?? BenchmarkConditions.Clean),
            HostLoad = host.Finish(),
            Metrics = Metrics(runs: runs),
            Benchmark = benchmark,
        };
        var summary = Path.Combine(path1: directory, path2: "summary.md");

        _ = report.Write(directory);
        File.WriteAllText(path: summary, contents: BenchmarkSummary.Of(report: (JsonObject)JsonNode.Parse(json: report.ToJson())!));
        if (parse.GetValue(options.Json)) {
            Console.WriteLine(report.ToJson());
        } else {
            print.WriteLine($"{Verb}: {runs.Count} run(s), {unrun.Count} not run; conditions {(verdict ?? BenchmarkConditions.Clean)}; report {PuckPaths.Normalize(path: Path.Combine(path1: directory, path2: "report.json"))}; summary {PuckPaths.Normalize(path: summary)}");
        }

        return exit;
    }

    // The standing set: on each backend, one World plays the debug route and then the moon round trip; then, on the first
    // backend, the debug route against a listening World on this machine, and the debug route on four seats.
    private static Leg[] StandingLegs(WorldBackendPreference?[] backends) => [
        .. backends.Select(selector: static backend => new Leg(Backend: backend, Name: DebugRoute, Plays: [
            new Play(Name: DebugRoute, Route: DebugRoute),
            new Play(Name: MoonRoute, Route: MoonRoute, WaitsOn: "the world authors no `moon` route: the round trip (the pad, the sky seam, a landing on the moon and back) plays here once it does"),
        ], Remote: null, Seats: 1)),
        new Leg(Backend: backends[0], Name: "remote", Plays: [new Play(Name: "remote", Route: DebugRoute)], Remote: "local", Seats: 1),
        new Leg(Backend: backends[0], Name: "four-seat", Plays: [new Play(Name: "four-seat", Route: DebugRoute)], Remote: null, Seats: MaxSeats),
    ];

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

    // The seconds one route's run may take: the wall-time limit twice, for a route's wait for its navigation and then the
    // run, and the grace for the boot and the collection.
    private static int Allowance(int timeout) => ((2 * timeout) + BudgetGraceSeconds);

    // The route's name in a report: its path from the repository root when it lies within it, so two checkouts of one
    // route name it alike, and otherwise its file name.
    private static string RouteName(string root, string path) => (PuckPaths.IsWithin(path: path, root: root) ? PuckPaths.Relative(path: path, relativeTo: root) : Path.GetFileName(path: path));

    // One route a leg played, as the World reported it, or why it was not run.
    private sealed record Outcome(string Name, string Route, JsonObject? Run, HostCompetitionReport? Competition, string? Unrun);

    // Boots one World (and, against a local remote authority, the authority beside it) and plays each of the leg's routes
    // in it, one after another.
    private static List<Outcome> PlayLeg(RunOptions options, ParseResult parse, WorldBuild build, string directory, Leg leg, WorldShape shape, string world, string prefix, SuiteBudget budget, Action<string> print) {
        _ = Directory.CreateDirectory(path: directory);

        var outcomes = new List<Outcome>();
        var timeout = parse.GetValue(options.Timeout);
        RemotePair? pair = null;
        SweepWorld? authority = null;

        try {
            if (leg.Remote == "local") {
                pair = RemotePair.Stage(directory: Path.Combine(directory, "remote"), world: world);
                authority = BootAuthority(build: build, budget: budget, directory: Path.Combine(directory, "authority"), pair: pair, timeout: timeout);
            }

            var connect = (pair?.Endpoint ?? leg.Remote);
            var request = new WorldLaunchRequest(Verb, build.Path) {
                Arguments = [
                    // A client of a remote authority runs from its boot: a paused boot commits no transfer.
                    .. ((connect is null) ? new[] { "--paused" } : ["--connect", connect]),
                    .. SizeArguments(options: options, parse: parse, shape: shape),
                    .. (parse.GetValue(options.Cache) is { } cache ? new[] { "--cache-dir", Path.GetFullPath(cache) } : []),
                    .. ((pair is not null) ? new[] { "--federation-key-file", pair.KeyFile } : []),
                    .. ((parse.GetValue(options.Uncapped) && (shape == WorldShape.Windowed)) ? new[] { "--present-mode", "immediate" } : [])],
                Backend = leg.Backend,
                ExitAfterSeconds = WorldLaunch.Backstop(TimeSpan.FromSeconds(Allowance(timeout) * leg.Plays.Count)),
                Shape = shape,
                StateDirectory = Path.Combine(directory, "state"),
                Timing = true,
                World = (pair?.Document ?? world),
            };

            if (!SweepWorld.TryBoot(request, directory, BootTimeout, budget, out var session, out var bootError)) {
                throw new InvalidOperationException($"the World did not boot: {bootError}");
            }

            using var live = session!;

            // Authority is held at the boot's first tick, so the renderer can finish starting without the route moving. A
            // cold pipeline cache (a first Vulkan boot) takes minutes, so the wait runs in rounds up to its bound and says
            // what still stands.
            if (shape == WorldShape.Windowed) {
                WaitForRenderer(budget: budget, live: live, print: print);
            }

            var profile = live.Run($"world.device-profile {CommandEcho.Quote(parse.GetValue(options.Profile) ?? "auto")}", 60);

            if (!profile.Ok) {
                throw new InvalidOperationException($"the World refused the device profile: {profile.Output}");
            }
            print($"{Verb}: {profile.Output}\n");
            if (connect is not null) {
                WaitForRemote(authority: authority, budget: budget, clientLog: Path.Combine(directory, "stderr.log"), live: live, print: print);
            }

            var paused = (connect is null);

            foreach (var play in leg.Plays) {
                var outcome = PlayRoute(directory: Path.Combine(directory, BenchmarkMetrics.Slug(name: play.Name)), live: live, options: options, parse: parse, paused: paused, play: play, prefix: prefix, seats: leg.Seats, budget: budget, print: print);

                outcomes.Add(item: outcome);
                paused &= (outcome.Run is null);
            }
            // The key views come after every timed run of the World, so waiting for them disturbs none of its frames.
            if (shape == WorldShape.Windowed) {
                foreach (var outcome in outcomes.Where(predicate: static outcome => outcome.Run is not null)) {
                    outcome.Run!["captures"] = Capture(directory: Path.Combine(directory, BenchmarkMetrics.Slug(name: outcome.Name), "captures"), live: live, print: print, route: outcome.Route);
                }
            }
        } finally {
            authority?.Dispose();
            pair?.Dispose();
        }

        return outcomes;
    }

    // The sides each key place is framed from, as the yaw each view faces (0 faces -Z, 90 faces -X); the share of the
    // region's half extent the eye stands back from its centre; and the metres the view's target sits below the eye.
    private static readonly int[] CaptureYaws = [0, 90, 180, 270];
    private const double CaptureInset = 0.85;
    private const double CaptureDrop = 0.6;
    // The bound on the settle waits before a place's first capture and before each later side, in seconds, and on the whole
    // pass; a capture whose waits release unsettled is partial, and one the pass's time ran out before is not taken.
    private const int CaptureSettleSeconds = 60;
    private const int CaptureSideSeconds = 15;
    private static readonly TimeSpan CapturePassLimit = TimeSpan.FromSeconds(seconds: 240);

    // Frames each place the route's captures name from four sides with the operator camera, each after the World's bakes
    // and resident geometry settle (or marked partial with what stood), and returns what each capture wrote.
    private static JsonArray Capture(string directory, SweepWorld live, Action<string> print, string route) {
        var captures = new JsonArray();
        var answer = live.Run($"world.benchmark marks {CommandEcho.Quote(route)}", 30);

        if (!answer.Ok) {
            captures.Add(item: new JsonObject { ["name"] = route, ["refused"] = answer.Output });

            return captures;
        }

        var json = answer.Output[(answer.Output.IndexOf('[', 1))..^1];

        var clock = Stopwatch.StartNew();

        _ = Directory.CreateDirectory(path: directory);
        foreach (var mark in (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>()) {
            var place = mark["place"]!.GetValue<string>();
            var center = mark["center"]!.AsArray().Select(selector: static value => value!.GetValue<double>()).ToArray();
            var half = mark["half"]!.AsArray().Select(selector: static value => value!.GetValue<double>()).ToArray();

            foreach (var yaw in CaptureYaws) {
                var name = string.Create(CultureInfo.InvariantCulture, $"{place}-{yaw:000}");

                if (clock.Elapsed > CapturePassLimit) {
                    captures.Add(item: new JsonObject { ["name"] = name, ["place"] = place, ["yawDegrees"] = yaw, ["refused"] = string.Create(CultureInfo.InvariantCulture, $"not taken: the capture pass passed its {CapturePassLimit.TotalSeconds:0} s limit") });

                    continue;
                }

                var settle = ((yaw == CaptureYaws[0]) ? CaptureSettleSeconds : CaptureSideSeconds);
                var path = PuckPaths.Normalize(Path.Combine(directory, $"{name}.png"));
                // From inside the region, near the wall behind the view, looking across to the far wall, so an indoor
                // place is framed from within its walls (yaw 0 faces -Z, 90 faces -X).
                var (dx, dz) = (yaw switch { 0 => (0.0, -1.0), 90 => (-1.0, 0.0), 180 => (0.0, 1.0), _ => (1.0, 0.0) });
                var reach = ((dx != 0.0) ? half[0] : half[2]);
                var aim = live.Run(string.Create(CultureInfo.InvariantCulture, $"view.override camera look {(center[0] - (dx * CaptureInset * reach)):0.###} {center[1]:0.###} {(center[2] - (dz * CaptureInset * reach)):0.###} {(center[0] + (dx * reach)):0.###} {(center[1] - CaptureDrop):0.###} {(center[2] + (dz * reach)):0.###}"), 30);
                var bakes = live.Run($"world.wait bakes {settle}", settle + 10);
                var resident = live.Run($"world.wait residency {settle}", settle + 10);
                var shot = live.Run($"world.screenshot {CommandEcho.Quote(path)} presented 1", 30);
                var written = live.Run("world.wait captures 30", 40);
                var settled = (bakes.Ok && resident.Ok);

                captures.Add(item: new JsonObject {
                    ["name"] = name,
                    ["place"] = place,
                    ["yawDegrees"] = yaw,
                    ["path"] = ((aim.Ok && shot.Ok && written.Ok && File.Exists(path)) ? path : null),
                    ["refused"] = (aim.Ok ? (shot.Ok ? (written.Ok ? null : written.Output) : shot.Output) : aim.Output),
                    ["partial"] = !settled,
                    ["settle"] = $"{bakes.Output.Trim()} {resident.Output.Trim()}",
                });
            }
            print($"{Verb}: captured {place} from {CaptureYaws.Length} sides\n");
        }
        _ = live.Run("view.override camera auto", 30);

        return captures;
    }

    // How long a windowed World may take to build its pipelines and produce its first frame, and the round each wait
    // call takes.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(minutes: 10);
    private const int ReadyRoundSeconds = 60;

    // Waits for the renderer in rounds until it is ready or the bound passes, then refuses naming what the World last said
    // stood (its pipelines building, a frame withheld).
    private static void WaitForRenderer(SuiteBudget budget, SweepWorld live, Action<string> print) {
        var clock = Stopwatch.StartNew();
        var last = string.Empty;

        while (clock.Elapsed < ReadyTimeout) {
            if (budget.Expired) {
                throw new TimeoutException($"{budget.Reason}; the renderer was not ready: {last}");
            }

            var ready = live.Run($"world.wait ready {ReadyRoundSeconds}", (ReadyRoundSeconds + 10));

            if (ready.Ok) {
                return;
            }
            last = ready.Output;
            print($"{Verb}: {last}\n");
        }

        throw new InvalidOperationException($"the renderer was not ready within {ReadyTimeout.TotalMinutes:0} minutes: {last}");
    }

    // The window size arguments: the named size, or the device profile's frame bar.
    private static string[] SizeArguments(RunOptions options, ParseResult parse, WorldShape shape) {
        if (shape != WorldShape.Windowed) {
            return [];
        }
        if ((parse.GetValue(options.Width) is { } width) && (parse.GetValue(options.Height) is { } height)) {
            return ["--width", width.ToString(CultureInfo.InvariantCulture), "--height", height.ToString(CultureInfo.InvariantCulture)];
        }

        var profile = (parse.GetValue(options.Profile) ?? "auto");

        return ["--frame-bar", ((profile == "none") ? "auto" : profile)];
    }

    // Plays one route in a booted World: arms it (waiting for any navigation it plans over), resumes a paused World, and
    // waits for the World's own report.
    private static Outcome PlayRoute(string directory, SweepWorld live, RunOptions options, ParseResult parse, bool paused, Play play, string prefix, int seats, SuiteBudget budget, Action<string> print) {
        var timeout = parse.GetValue(options.Timeout);
        var runDirectory = Path.Combine(directory, "world");
        var line = string.Create(CultureInfo.InvariantCulture, $"world.benchmark run {CommandEcho.Quote(File.Exists(path: play.Route) ? PuckPaths.Normalize(play.Route) : play.Route)} {(prefix.Length == 0 ? "-" : CommandEcho.Quote(prefix))}{((parse.GetValue(options.At) is { } at) ? $" at {at}" : string.Empty)}{((parse.GetValue(options.Budget) is { } budgetMs) ? $" budget {budgetMs}" : string.Empty)} timeout {timeout} loops {parse.GetValue(options.Loops)}{((seats > 1) ? $" seats {seats}" : string.Empty)} output {CommandEcho.Quote(PuckPaths.Normalize(runDirectory))}");
        var armed = live.Run(line, 60);

        if (!armed.Ok) {
            if ((play.WaitsOn is { } waits) && armed.Output.Contains(NoRouteMarker, StringComparison.Ordinal)) {
                return new Outcome(Competition: null, Name: play.Name, Route: play.Route, Run: null, Unrun: waits);
            }
            if (armed.Output.Contains(SeatsMarker, StringComparison.Ordinal)) {
                return new Outcome(Competition: null, Name: play.Name, Route: play.Route, Run: null, Unrun: armed.Output);
            }

            throw new InvalidOperationException($"the World refused the route '{play.Route}': {armed.Output}");
        }
        print($"{Verb}: {armed.Output}\n");

        // A route that needs navigation baked waits for it with the World still paused, so the run arms from the boot image
        // as one that needs none does; the World reports it never started once the run's own limit passes.
        var waited = Stopwatch.StartNew();
        var told = TimeSpan.Zero;

        while (armed.Output.StartsWith(PreparingPrefix, StringComparison.Ordinal)) {
            if (budget.Expired) {
                throw new TimeoutException($"{budget.Reason}; the World was still preparing the route");
            }
            _ = budget.Token.WaitHandle.WaitOne(PollInterval);
            armed = live.Run("world.benchmark", 30);
            if (!armed.Ok) {
                throw new InvalidOperationException($"the World stopped answering: {armed.Output}");
            }
            if (armed.Output.StartsWith(PreparingPrefix, StringComparison.Ordinal)) {
                if ((waited.Elapsed - told) >= ProgressInterval) {
                    told = waited.Elapsed;
                    print($"{Verb}: {armed.Output}\n");
                }
            } else if (!armed.Output.StartsWith(PlayingPrefix, StringComparison.Ordinal)) {
                throw new InvalidOperationException($"the World never started the route: {armed.Output}");
            }
        }
        // What else the machine does from the first tick to the last is read from the operating system, beside the run.
        using var competitors = new HostCompetitionRecorder();

        if (paused && !live.Run("world.rate resume", 30).Ok) {
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
            if (!status.Output.StartsWith(PlayingPrefix, StringComparison.Ordinal)) {
                summary = status.Output;
            } else if ((clock.Elapsed - announced) >= ProgressInterval) {
                announced = clock.Elapsed;
                print($"{Verb}: {status.Output}\n");
            } else {
                _ = budget.Token.WaitHandle.WaitOne(PollInterval);
            }
        }

        var competition = competitors.Finish();

        print($"{summary}\n");

        var path = Path.Combine(runDirectory, "run.json");

        if (!File.Exists(path)) {
            throw new InvalidDataException($"the run wrote no report at {PuckPaths.Normalize(path)}: {summary}");
        }

        return new Outcome(Competition: competition, Name: play.Name, Route: play.Route, Run: (JsonNode.Parse(File.ReadAllText(path)) as JsonObject) ?? throw new InvalidDataException($"{PuckPaths.Normalize(path)} is not a JSON object"), Unrun: null);
    }

    // The conditions a comparison reads that the verb states: what must be alike for two runs' numbers to mean the same,
    // written from what it asked for and the machine it ran on. The World's own (its table, adapter, window, topology and
    // overlay) join them as the run is folded.
    private static JsonObject Conditions(RunOptions options, ParseResult parse, string backend, bool headless, string world, Leg leg) {
        var conditions = new JsonObject {
            ["world"] = world,
            ["backend"] = backend,
            ["shape"] = (headless ? "headless" : "windowed"),
            ["profile"] = parse.GetValue(options.Profile),
            ["uncapped"] = (!headless && parse.GetValue(options.Uncapped)),
            ["at"] = (parse.GetValue(options.At) is { } at ? at : 0),
            ["loops"] = parse.GetValue(options.Loops),
            ["remote"] = leg.Remote,
            ["seats"] = leg.Seats,
            ["os"] = RuntimeInformation.OSDescription,
            ["cpus"] = Environment.ProcessorCount,
        };

        if (!headless && (parse.GetValue(options.Width) is null)) {
            conditions["frameBar"] = (parse.GetValue(options.Profile) is { } profile and not "none" ? profile : "auto");
        }

        return conditions;
    }

    // One run as a report holds it: the World's own report with its readings' raw answers replaced by their change across
    // each chapter and the chapter's memory census, and the conditions beside it: the verb's, and the World's own.
    private static JsonObject Fold(JsonObject world, JsonObject conditions, string backend, string leg, string routeName, HostCompetitionReport? competition) {
        var run = (JsonObject)JsonNode.Parse(world.ToJsonString())!;
        var profile = (run["profile"] as JsonObject);
        var hostBar = profile?["hostMemoryCapBytes"]?.GetValue<long>();
        var deviceBar = profile?["deviceMemoryCapBytes"]?.GetValue<long>();

        run["machine"] = BenchmarkConditions.Machine(run: run, competition: competition);
        // The budget an iteration is judged against is the World's, which says it whether or not the verb named one.
        conditions["budgetMilliseconds"] = world["budgetMilliseconds"]?.DeepClone();
        conditions["table"] = profile?["table"]?.DeepClone();
        conditions["adapter"] = (profile?["adapter"] ?? run["adapter"]?["adapter"])?.DeepClone();
        conditions["topology"] = run["topology"]?["kind"]?.DeepClone();
        conditions["overlay"] = run["overlay"]?["enabledAtEntry"]?.DeepClone();
        conditions["width"] = run["window"]?["width"]?.DeepClone();
        conditions["height"] = run["window"]?["height"]?.DeepClone();
        run["backend"] = backend;
        run["leg"] = leg;
        run["conditions"] = conditions;
        run["route"] = routeName;
        foreach (var loop in (run["loops"] as JsonArray ?? []).OfType<JsonObject>()) {
            foreach (var chapter in (loop["chapters"] as JsonArray ?? []).OfType<JsonObject>()) {
                var (readings, unmeasured) = BenchmarkMetrics.Readings(chapter["reads"] as JsonArray);

                chapter["census"] = BenchmarkCensus.Of(chapter: chapter, deviceBarBytes: deviceBar, hostBarBytes: hostBar);
                _ = chapter.Remove("reads");
                chapter["readings"] = readings;
                chapter["unmeasured"] = unmeasured;
            }
        }

        return run;
    }

    // Every number of every chapter as a one-sample metric, named run, backend, loop kind, chapter and metric, so puck
    // measure reads one as /metrics/<name>/median.
    private static Dictionary<string, BenchMetric> Metrics(JsonArray runs) {
        var metrics = new Dictionary<string, BenchMetric>(StringComparer.Ordinal);

        foreach (var run in runs.OfType<JsonObject>()) {
            var backend = run["backend"]!.GetValue<string>();
            var leg = BenchmarkMetrics.Slug(run["leg"]!.GetValue<string>());

            foreach (var loop in (run["loops"] as JsonArray ?? []).OfType<JsonObject>()) {
                var kind = loop["kind"]!.GetValue<string>();
                var slugs = new HashSet<string>(StringComparer.Ordinal);

                foreach (var chapter in (loop["chapters"] as JsonArray ?? []).OfType<JsonObject>()) {
                    var slug = BenchmarkMetrics.Slug(chapter["name"]!.GetValue<string>());

                    for (var suffix = 2; !slugs.Add(slug); suffix++) {
                        slug = $"{BenchmarkMetrics.Slug(chapter["name"]!.GetValue<string>())}-{suffix}";
                    }
                    foreach (var (name, metric) in BenchmarkMetrics.Of(chapter)) {
                        metrics[$"{leg}.{backend}.{kind}.{slug}.{name}"] = BenchMetric.Of(metric.Unit, [metric.Value]);
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
        ["remote"] = (parse.GetValue(options.Remote) ?? "(in process)"),
        ["seats"] = parse.GetValue(options.Seats).ToString(CultureInfo.InvariantCulture),
        ["loops"] = parse.GetValue(options.Loops).ToString(CultureInfo.InvariantCulture),
        ["at"] = (parse.GetValue(options.At)?.ToString(CultureInfo.InvariantCulture) ?? "(boot)"),
        ["profile"] = (parse.GetValue(options.Profile) ?? "auto"),
        ["uncapped"] = parse.GetValue(options.Uncapped).ToString(),
        ["timeoutSeconds"] = parse.GetValue(options.Timeout).ToString(CultureInfo.InvariantCulture),
    };
}
