using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using Puck.Abstractions;
using Puck.Cli.Sweep;

namespace Puck.Cli.Bench;

/// <summary>Windowed host-frame distributions and completed GPU pass windows, through the sweep's World session.</summary>
public static partial class FrameBenchmarks {
    private static readonly string[] FrameFields = ["avg", "p50", "p99", "max"];

    // Filing and execution parse the same grammar and derive the same metric names from the selected stops and passes.
    private sealed class Options {
        public readonly Argument<string> World = new("world") { Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => "worlds/debug" };
        public readonly Option<string?> Artifact = new("--world-artifact");
        public readonly Option<string?> Output = new("--output");
        public readonly Option<string?> Plan = new("--plan") { Description = "Sweep plan whose tour to measure; omitted measures boot, retaining its initial frame history." };
        public readonly Option<string[]> Stops = new("--stop") { AllowMultipleArgumentsPerToken = true, Description = "Tour stop names; omitted selects every stop. Requires --plan." };
        public readonly Option<string[]> Passes = new("--gpu-pass") { AllowMultipleArgumentsPerToken = true, Description = "Exact node/pass names from world.gpu-timing to require and report, e.g. world/sdf.world$glass-shade." };
        public readonly Option<int> Ticks = CliOptions.Count("--ticks", "Simulation ticks in each measured frame window (1..600, default 120).", 1, 120, 600);
        public readonly Option<int> Samples = CliOptions.Count("--samples", "Fresh windowed processes (1..100, default 1).", 1, 1, 100);
        public readonly Option<int> Timeout = CliOptions.Count("--timeout-seconds", "Bound for each process, excluding lease admission (default 600).", 1, 600, 7200);
        public readonly Option<int> Width = CliOptions.Count("--width", "Window width (default 1280).", 1, 1280, 16384);
        public readonly Option<int> Height = CliOptions.Count("--height", "Window height (default 720).", 1, 720, 16384);
        public readonly Option<string?> Cache = new("--cache-dir");
        public readonly Option<Puck.World.WorldBackendPreference?> Backend = WorldLaunch.BackendOption("Windowed graphics backend: auto, directx or vulkan.");
        public readonly Option<bool> Json = new("--json");
        public Command Command { get; }

        public Options() => Command = new Command("frames", "Measure windowed boot or sweep-tour frames and named GPU passes.") {
            World, Artifact, Output, Plan, Stops, Passes, Ticks, Samples, Timeout, Width, Height, Cache, Backend, Json,
        };

        public (SweepTour Tour, SweepStop[] Stops, string[] Passes) Selection(ParseResult parse) {
            var plan = parse.GetValue(Plan);
            var stops = parse.GetValue(Stops) ?? [];
            var passes = parse.GetValue(Passes) ?? [];
            if (passes.Any(string.IsNullOrWhiteSpace) || passes.Distinct(StringComparer.Ordinal).Count() != passes.Length) {
                throw new ArgumentException("--gpu-pass names must be nonempty and distinct.");
            }
            if (plan is null) {
                if (stops.Length > 0) { throw new ArgumentException("--stop requires --plan."); }
                return (SweepTour.Empty, [new SweepStop("boot", null, null, [])], passes);
            }
            if (!SweepPlan.TryParse(File.ReadAllText(plan), out var parsed, out var error)) { throw new ArgumentException(error); }
            if (stops.Any(name => !parsed.Tour.Stops.Any(stop => stop.Name == name))) { throw new ArgumentException("--stop names a stop absent from the sweep plan."); }
            var selected = parsed.Tour.Stops.Where(stop => stops.Length == 0 || stops.Contains(stop.Name, StringComparer.Ordinal)).ToArray();
            if (selected.Length == 0) { throw new ArgumentException("The plan has no selected tour stops."); }
            return (parsed.Tour, selected, passes);
        }
    }

    public static Command Create(BenchHostSample? sampleHost) {
        var options = new Options();
        options.Command.SetAction((parse, token) => Task.FromResult(Run(options, parse, sampleHost, token)));
        return options.Command;
    }

    public static IReadOnlyDictionary<string, BenchMetric> MetricSchema(string[] arguments) {
        var options = new Options();
        var parse = options.Command.Parse(arguments);
        if (parse.Errors.Count > 0) { throw new ArgumentException(string.Join("; ", parse.Errors.Select(error => error.Message))); }
        var (_, stops, passes) = options.Selection(parse);
        return stops.SelectMany(stop => FrameFields.Select(field => $"{stop.Name}.frames.{field}")
            .Concat(passes.Select(pass => $"{stop.Name}.gpu.{pass}")))
            .ToDictionary(name => name, _ => BenchMetric.Of("ms", new double[parse.GetValue(options.Samples)]), StringComparer.Ordinal);
    }

    private static int Run(Options options, ParseResult parse, BenchHostSample? sampleHost, CancellationToken token) {
        var (tour, stops, passes) = options.Selection(parse);
        var root = RepositoryPaths.RequireRoot();
        var directory = Path.GetFullPath(Path.Combine(parse.GetValue(options.Output) ?? "artifacts/frames", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var backend = parse.GetValue(options.Backend);
        if (!WorldLaunch.TryResolve("bench frames", root, directory, parse.GetValue(options.Artifact), WorldLaunch.NeedsSpirv(WorldShape.Windowed, backend), out var build, out var error)) {
            return CliExit.Refuse("bench frames", "build", error);
        }
        using var held = build;
        using var host = new BenchHostRecorder(sampleHost);
        var samples = parse.GetValue(options.Samples);
        var ticks = parse.GetValue(options.Ticks);
        var boot = parse.GetValue(options.Plan) is null;
        var readings = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var rows = new List<KeyValuePair<string, string>>();
        var report = BenchReport.Start("bench frames", BenchBuild.Of(build.Path), samples, new Dictionary<string, string> {
            ["world"] = PuckPaths.Normalize(Path.GetFullPath(parse.GetValue(options.World)!)),
            ["plan"] = parse.GetValue(options.Plan) ?? "(boot)", ["stops"] = string.Join(",", stops.Select(stop => stop.Name)),
            ["ticks"] = ticks.ToString(CultureInfo.InvariantCulture), ["gpuPasses"] = string.Join(",", passes),
            ["size"] = $"{parse.GetValue(options.Width)}x{parse.GetValue(options.Height)}", ["backend"] = backend?.ToString() ?? "document",
        });
        var complete = false;
        try {
            for (var sample = 0; sample < samples; sample++) {
                using var budget = new SweepBudget(TimeSpan.FromSeconds(parse.GetValue(options.Timeout)), token);
                var log = Path.Combine(directory, $"sample-{sample:D3}");
                Directory.CreateDirectory(log);
                var request = new WorldLaunchRequest("bench frames", build.Path) {
                    Shape = WorldShape.Windowed, Backend = backend, World = Path.GetFullPath(parse.GetValue(options.World)!),
                    StateDirectory = Path.Combine(log, "state"), ExitAfterSeconds = 0,
                    Arguments = ["--width", parse.GetValue(options.Width).ToString(CultureInfo.InvariantCulture), "--height", parse.GetValue(options.Height).ToString(CultureInfo.InvariantCulture),
                        .. (parse.GetValue(options.Cache) is { } cache ? new[] { "--cache-dir", Path.GetFullPath(cache) } : [])],
                };
                if (!SweepWorld.TryBoot(request, log, TimeSpan.FromSeconds(120), budget, out var session, out error)) { throw new InvalidOperationException(error); }
                using var world = session!;
                string Ask(string line, bool authored = false) {
                    var answer = authored ? SweepRun.RunTourLine(world, line) : world.Run(line, 300);
                    rows.Add(new($"{sample}: {line}", answer.Output));
                    if (!answer.Ok) { throw new InvalidOperationException(string.Join("; ", answer.Refusals)); }
                    return answer.Output;
                }
                if (!boot) {
                    Ask("world.lockstep off\nworld.rate resume");
                    // A frozen artifact can boot against an empty mesh store. Settle its cold bake before
                    // applying a stop's own edits and bounded residency wait.
                    Ask("world.wait ready 60\nworld.wait bakes 180");
                    foreach (var line in tour.Prelude ?? []) {
                        if (!line.TrimStart().StartsWith("world.lockstep ", StringComparison.Ordinal)) { Ask(line, true); }
                    }
                } else {
                    Ask("world.wait ready 60");
                }
                foreach (var stop in stops) {
                    if (!boot) {
                        Ask("world.rate pause");
                        foreach (var line in (tour.Reset ?? []).Concat(stop.Lines)) { Ask(line, true); }
                        // Share the sweep's pinning and verdict, while leaving ordinary presentation running at the end.
                        var reasons = new List<string>();
                        if (!SweepRun.PinStop(world, stop, tour, "world.rate pause", reasons, out var settledTick, out var expected)) {
                            throw new InvalidOperationException(string.Join("; ", reasons));
                        }
                        Ask($"world.wait until {settledTick}");
                        var scheduled = SweepEvidence.Scheduled(new SweepAnswer(true, Ask("world.at --json"), []), expected);
                        if (!scheduled.Ok) { throw new InvalidOperationException(string.Join("; ", scheduled.Refusals)); }
                        // Framing and the stop's warmup must produce the view's actual program before asking
                        // whether its geometry is resident. Then hold authority while its uploads finish.
                        Ask("world.wait bakes 60\nworld.wait ready 60\nworld.wait residency 20");
                        Ask("world.rate resume");
                    }
                    if (passes.Length > 0) { Ask("world.gpu-timing off\nworld.gpu-timing on"); }
                    if (!boot) { Ask("host.frames reset"); }
                    Ask($"world.wait {ticks}");
                    if (passes.Length > 0) {
                        // A pass mean must contain a full window from this stop, never timestamps from the previous view.
                        Dictionary<string, double>? gpu = null;
                        for (var attempt = 0; attempt < 64; attempt++) {
                            gpu = ReadGpu(Ask("world.gpu-timing"), passes);
                            if (gpu is not null) { break; }
                            Ask("world.wait 8");
                        }
                        if (gpu is null) { throw new InvalidDataException($"{stop.Name}: GPU passes {string.Join(", ", passes)} never completed their timestamp window."); }
                        foreach (var (pass, value) in gpu) { Add($"{stop.Name}.gpu.{pass}", value); }
                    }
                    var frames = SweepSignals.Read("frames", Ask("host.frames 4096 16.7 0"), null);
                    foreach (var field in FrameFields) {
                        if (!frames.TryGetValue(field, out var text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) {
                            throw new InvalidDataException($"{stop.Name}: missing finite host.frames {field}.");
                        }
                        Add($"{stop.Name}.frames.{field}", value);
                    }
                    foreach (var line in stop.After ?? []) { Ask(line, true); }
                }
            }
            complete = true;
        } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or TimeoutException) {
            Console.Error.WriteLine($"bench frames: {exception.Message}; evidence {PuckPaths.Normalize(directory)}");
        } finally {
            report = report with { Complete = complete, HostLoad = host.Finish(), Rows = rows,
                Metrics = readings.ToDictionary(pair => pair.Key, pair => BenchMetric.Of("ms", pair.Value), StringComparer.Ordinal) };
            report.Write(directory);
        }
        if (parse.GetValue(options.Json)) { Console.WriteLine(report.ToJson()); }
        else { foreach (var line in report.MetricLines()) { Console.WriteLine(line); } }
        return complete ? 0 : 1;

        void Add(string name, double value) {
            if (!readings.TryGetValue(name, out var values)) { readings.Add(name, values = []); }
            values.Add(value);
        }
    }

    public static Dictionary<string, double>? ReadGpu(string output, IReadOnlyList<string> passes) {
        var header = GpuWindowPattern().Match(output);
        if (!header.Success || !int.TryParse(header.Groups["window"].Value, CultureInfo.InvariantCulture, out var window) || window < 1) { return null; }
        var rows = GpuPattern().Matches(output).ToDictionary(match => match.Groups["name"].Value,
            match => (Value: double.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture), Samples: int.Parse(match.Groups["samples"].Value, CultureInfo.InvariantCulture)), StringComparer.Ordinal);
        if (passes.Any(pass => !rows.TryGetValue(pass, out var row) || row.Samples < window || !double.IsFinite(row.Value))) { return null; }
        return passes.ToDictionary(pass => pass, pass => rows[pass].Value, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\[world\.gpu-timing: on window=(?<window>[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex GpuWindowPattern();

    [GeneratedRegex(@"(?m)^(?<name>[^\r\n]+): (?<ms>[0-9.]+) ms samples=(?<samples>[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex GpuPattern();
}
