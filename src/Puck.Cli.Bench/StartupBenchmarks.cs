using System.CommandLine;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions;

namespace Puck.Cli.Bench;

// Measures the real host in fresh processes. Builds and shutdown are outside the readiness interval; PNG readback
// and pipe delivery are inside the capture interval, making it a conservative bound on visible-frame readiness.
public static class StartupBenchmarks {
    public static Command Create(BenchHostSample? sampleHost) {
        var worlds = new Argument<string[]>(name: "worlds") { Arity = ArgumentArity.ZeroOrMore, Description = "Worlds to boot: asset package folders, sources or documents. Default: the debug world's package, worlds/debug." };
        var artifact = new Option<string?>("--world-artifact") { Description = "Measure this Puck.World entry assembly instead of the build of the checkout's sources, which is built first, outside every sample, when no run has built that source state." };
        var output = new Option<string?>("--output") { Description = "Parent for a unique run directory containing report.json, transcripts, and captures." };
        var cache = new Option<string?>("--cache-dir") { Description = "World cache root shared across samples; an empty directory measures a cold boot followed by warm boots." };
        var samples = CliOptions.Count(defaultValue: 3, description: "Fresh processes measured per world, 1 to 100.", maximum: 100, minimum: 1, name: "--samples");
        var timeout = CliOptions.Count(defaultValue: 120, description: "Seconds one sample may take before it is abandoned, 1 to 3600.", maximum: 3600, minimum: 1, name: "--timeout-seconds");
        var headless = new Option<bool>("--headless") { Description = "Measure console readiness only; default launches the windowed renderer and requires a completed PNG capture." };
        var bake = new Option<bool>("--bake") { Description = "Measure from the first mesh-bake start through completion of every residency reported by world.residency, using a fresh cache and --paused --lockstep to isolate baking from lighting. This is not an ordinary windowed cold boot." };
        var backend = WorldLaunch.BackendOption(description: "Graphics backend for the windowed sample: auto, directx or vulkan.");
        var width = CliOptions.Count(defaultValue: 1280, description: "Window width in pixels for the rendered capture, 1 to 16384.", maximum: 16384, minimum: 1, name: "--width");
        var height = CliOptions.Count(defaultValue: 720, description: "Window height in pixels for the rendered capture, 1 to 16384.", maximum: 16384, minimum: 1, name: "--height");
        var json = new Option<bool>("--json") { Description = "Print the report (puck.bench.v1) on standard output instead of the summary line." };
        var command = new Command(description: "Measure fresh-process World startup serially, with isolated persistence for every sample.", name: "startup") { worlds, artifact, output, cache, samples, timeout, headless, bake, backend, width, height, json };

        command.SetAction(action: parse => Run(
            (parse.GetValue(argument: worlds) ?? []), parse.GetValue(option: artifact), parse.GetValue(option: output),
            parse.GetValue(option: samples), parse.GetValue(option: timeout), parse.GetValue(option: headless), parse.GetValue(option: width), parse.GetValue(option: height), parse.GetValue(option: cache),
            parse.GetValue(option: json), sampleHost, parse.GetValue(option: bake), parse.GetValue(option: backend)));
        command.Detail(detail: "Writes report.json (puck.bench.v1) in a unique run directory under --output after every sample: the World build's identity (version with its commit, SHA-256 of its engine assemblies), the host load sampled during the run, and the metric `startup` (console readiness headless, the completed capture windowed, in ms), or `cold-bake` with --bake, as median, p10 and p90. --bake uses --paused --lockstep to isolate baking from lighting, rather than measuring an ordinary windowed cold boot. It times the first bake start through completion of every residency world.residency reports after world.wait bakes, matched by residency identity; missing completions fail the sample. Boot, shutdown and lighting convergence are outside the interval. `puck measure` reads /metrics/startup/median or /metrics/cold-bake/median. Exit 0 when every sample passed, 1 when one failed, 2 for invalid arguments.");
        return command;
    }

    private static int Run(string[] worlds, string? artifact, string? output, int samples, int timeout, bool headless, int width, int height, string? cache, bool json, BenchHostSample? sampleHost, bool bake, Puck.World.WorldBackendPreference? backend) {

        if (bake && (headless || cache is not null)) { return CliExit.Refuse("bench startup", "--bake", "requires a windowed World and a fresh per-sample cache; omit --headless and --cache-dir."); }
        if (headless && backend is not null) { return CliExit.Refuse("bench startup", "--backend", "requires a windowed sample."); }

        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) { return 2; }
        var run = PuckPaths.Normalize(path: Path.Combine(path1: (output ?? Path.Combine(path1: root, path2: "artifacts/startup")), path2: Guid.NewGuid().ToString(format: "N")));

        Directory.CreateDirectory(path: run);
        // The build happens before the first sample, so no sample's interval holds it.
        if (!WorldLaunch.TryResolve(build: out var build, error: out var buildError, logDirectory: run, named: artifact, repositoryRoot: root, spirv: (!headless && WorldLaunch.NeedsSpirv(backend: backend, shape: WorldShape.Windowed)), verb: "startup")) {
            Console.Error.WriteLine(value: $"startup: {buildError}");
            return 2;
        }
        using var held = build;
        artifact = build.Path;
        if (worlds.Length == 0) {
            // The debug world is the one live world; every other world is quarantined in experimental/.
            worlds = [Path.Combine(path1: root, path2: WorldBenchmarks.DefaultWorldRelativePath)];
        }
        worlds = worlds.Select(selector: Path.GetFullPath).ToArray();
        if (worlds.Any(predicate: path => !(File.Exists(path: path) || Directory.Exists(path: path))) || (worlds.Distinct(comparer: PuckPaths.Comparer).Count() != worlds.Length)) {
            Console.Error.WriteLine(value: "startup: every world must exist and occur exactly once.");
            return 2;
        }
        var rows = new List<StartupSample>();
        var start = BenchReport.Start(build: BenchBuild.Of(entryAssembly: artifact), repeats: samples, settings: new Dictionary<string, string> {
            ["worlds"] = string.Join(separator: ", ", values: worlds.Select(selector: static world => CliPaths.ToDisplay(fullPath: world))),
            ["headless"] = (headless ? "true" : "false"),
            ["bake"] = (bake ? "true" : "false"),
            ["backend"] = (backend?.ToString() ?? "document"),
            ["size"] = $"{width}x{height}",
            ["cacheDir"] = ((cache is null) ? "(per sample)" : PuckPaths.Normalize(path: Path.GetFullPath(path: cache))),
        }, verb: "bench startup");
        var progress = (json ? Console.Error : Console.Out);
        using var host = new BenchHostRecorder(sample: sampleHost);
        BenchReport Report(bool finished) => start with {
            Complete = (finished && (Summarize(rows, (worlds.Length * samples), headless, bake) is not null)),
            HostLoad = (finished ? host.Finish() : null),
            Metrics = ((Summarize(rows, (worlds.Length * samples), headless, bake) is { } metric) ? new Dictionary<string, BenchMetric> { [(bake ? "cold-bake" : "startup")] = metric } : new Dictionary<string, BenchMetric>()),
            Startup = rows.ToArray(),
        };

        progress.WriteLine(value: $"startup: {worlds.Length} worlds x {samples} fresh processes; {(headless ? "headless" : "windowed")} {width}x{height}; {run}");
        // Interleave repetitions so all samples of one world do not uniquely benefit from adjacent disk-cache use.
        for (var sample = 1; (sample <= samples); sample++) {
            for (var index = 0; (index < worlds.Length); index++) {
                var directory = $"{run}/{index:D3}/sample-{sample:D3}";

                Directory.CreateDirectory(path: directory);
                var capture = $"{directory}/frame.png";
                var request = new WorldLaunchRequest(Artifact: artifact, Step: "bench startup") {
                    Arguments = ["--width", width.ToString(provider: CultureInfo.InvariantCulture), "--height", height.ToString(provider: CultureInfo.InvariantCulture), "--capture-dir", $"{directory}/captures", "--cache-dir", Path.GetFullPath(cache ?? $"{directory}/cache"), .. (bake ? new[] { "--paused", "--lockstep" } : [])],
                    Backend = backend,
                    ExitAfterSeconds = 0,
                    Shape = (headless ? WorldShape.Headless : WorldShape.Windowed),
                    StateDirectory = $"{directory}/state",
                    World = worlds[index],
                };
                var input = "world.status\n" + (bake
                    ? $"world.wait bakes {Math.Clamp(timeout - 5, 1, 600)}\nworld.residency\n"
                    : headless ? "" : $"world.wait 1\nworld.screenshot {JsonSerializer.Serialize(capture, StartupJsonContext.Default.String)}\n") + "wire.errors\n";
                StartupSample row;

                try {
                    using var launch = WorldLaunch.Admit(request: request);

                    var result = launch.Run(input: input, timeout: TimeSpan.FromSeconds(seconds: timeout),
                        continueWhen: line => ((headless || bake)
                            ? ((line.Stream == CliProcessOutputStream.Stdout) && (line.Line == "[wire.errors: 0 rejected]"))
                            : IsCaptureCompletion(capture: capture, line: line)),
                        continuationInput: "quit\n");

                    File.WriteAllText($"{directory}/stdout.log", result.Stdout);
                    File.WriteAllText($"{directory}/stderr.log", result.Stderr);
                    row = Assess(worlds[index], sample, result, (headless || bake), capture);
                    if (bake) {
                        row = StartupBake.Assess(row, result);
                    }
                } catch (Exception exception) when ((exception is Win32Exception or IOException or InvalidOperationException or TimeoutException)) {
                    row = new(worlds[index], sample, null, null, null, exception.Message);
                }
                rows.Add(item: row);
                progress.WriteLine(value: ((row.Error is null)
                    ? (bake ? $"{CliPaths.ToDisplay(fullPath: worlds[index])} #{sample}: cold bake {row.BakeMilliseconds:F1} ms" : $"{CliPaths.ToDisplay(fullPath: worlds[index])} #{sample}: console {row.ConsoleMilliseconds:F1} ms, capture {row.CaptureMilliseconds:F1} ms")
                    : $"FAIL {CliPaths.ToDisplay(fullPath: worlds[index])} #{sample}: {row.Error}"));
                // Persist partial evidence after every child; interruption never turns the completed prefix into a full run.
                _ = Report(finished: false).Write(directory: run);
            }
        }
        var report = Report(finished: true);

        _ = report.Write(directory: run);
        if (json) { Console.Out.WriteLine(value: report.ToJson()); }
        else { Console.WriteLine(value: (report.Complete ? $"startup: {report.MetricLines().Single()}" : "startup: incomplete or failed samples; no corpus timing claimed.")); }
        return (report.Complete ? 0 : 1);
    }

    public static StartupSample Assess(string world, int sample, CliProcessResult result, bool headless, string capture) {
        var ready = result.OutputLines.FirstOrDefault(predicate: line => ((line.Stream == CliProcessOutputStream.Stdout) && line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[world.status:")));
        var frame = result.OutputLines.FirstOrDefault(predicate: line => IsCaptureCompletion(capture: capture, line: line));
        var errors = result.OutputLines.Any(predicate: line => ((line.Stream == CliProcessOutputStream.Stdout) && (line.Line == "[wire.errors: 0 rejected]")));
        var error = (result.TimedOut ? "process timed out" : ((result.ExitCode != 0) ? $"process exited {result.ExitCode}" : ((ready is null) ? "missing console readiness" :
            (!errors ? "missing clean command verdict" : ((!headless && ((frame is null) || !File.Exists(path: capture) || (new FileInfo(fileName: capture).Length == 0))) ? "missing completed frame capture" : null)))));

        if (!headless && result.OutputLines.Any(predicate: line =>
            ((line.Stream == CliProcessOutputStream.Stderr) && line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[unified-overlay] skipped:")))) {
            error ??= "overlay unavailable; rendered startup is degraded";
        }

        return new(world, sample, ready?.ElapsedMilliseconds, frame?.ElapsedMilliseconds, result.ExitCode, error);
    }

    private static bool IsCaptureCompletion(CliProcessOutputLine line, string capture) =>
        ((line.Stream == CliProcessOutputStream.Stderr) &&
        line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[capture] ") &&
        line.Line.Replace(newChar: '/', oldChar: '\\').EndsWith($" -> {capture.Replace(newChar: '/', oldChar: '\\')}", StringComparison.Ordinal));

    // Every sample of every world, or null until all of them passed.
    public static BenchMetric? Summarize(IReadOnlyList<StartupSample> rows, int expected, bool headless, bool bake = false) {
        if ((rows.Count != expected) || (rows.Count == 0) || rows.Any(predicate: row => (row.Error is not null))) { return null; }
        var values = rows.Select(selector: row => (bake ? row.BakeMilliseconds : headless ? row.ConsoleMilliseconds : row.CaptureMilliseconds)).ToArray();

        if (values.Any(predicate: value => ((value is null) || !double.IsFinite(d: value.Value) || (value.Value < 0)))) { return null; }
        return BenchMetric.Of(samples: [.. values.Select(selector: static value => value!.Value)], unit: "ms");
    }
}
public sealed record StartupSample(string World, int Sample, double? ConsoleMilliseconds, double? CaptureMilliseconds, int? ExitCode, string? Error) {
    /// <summary>Gets the first bake start through completion of every reported residency under paused lockstep scheduling,
    /// in milliseconds. Lockstep isolates the bake from lighting; this is not ordinary windowed startup.</summary>
    public double? BakeMilliseconds { get; init; }
    /// <summary>Gets each reported residency's identity and observed bake endpoints, relative to process launch.</summary>
    public IReadOnlyList<StartupBakeResidency>? BakeResidencies { get; init; }
}

[JsonSerializable(typeof(string))]
internal sealed partial class StartupJsonContext : JsonSerializerContext;
