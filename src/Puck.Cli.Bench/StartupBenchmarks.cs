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
        var artifact = new Option<string?>("--world-artifact") { Description = "Existing World DLL; defaults to src/Puck.World/bin/Release/net11.0/Puck.World.dll. Never builds." };
        var output = new Option<string?>("--output") { Description = "Parent for a unique run directory containing report.json, transcripts, and captures." };
        var cache = new Option<string?>("--cache-dir") { Description = "World cache root shared across samples; an empty directory measures a cold boot followed by warm boots." };
        var samples = new Option<int>("--samples") { DefaultValueFactory = _ => 3, Description = "Fresh processes measured per world, 1 to 100." };
        var timeout = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 120, Description = "Seconds one sample may take before it is abandoned, 1 to 3600." };
        var headless = new Option<bool>("--headless") { Description = "Measure console readiness only; default launches the windowed renderer and requires a completed PNG capture." };
        var width = new Option<int>("--width") { DefaultValueFactory = _ => 1280, Description = "Window width in pixels for the rendered capture, 1 to 16384." };
        var height = new Option<int>("--height") { DefaultValueFactory = _ => 720, Description = "Window height in pixels for the rendered capture, 1 to 16384." };
        var json = new Option<bool>("--json") { Description = "Print the report (puck.bench.v1) on standard output instead of the summary line." };
        var command = new Command(description: "Measure fresh-process World startup serially, with isolated persistence for every sample.", name: "startup") { worlds, artifact, output, cache, samples, timeout, headless, width, height, json };

        command.SetAction(action: parse => Run(
            (parse.GetValue(argument: worlds) ?? []), parse.GetValue(option: artifact), parse.GetValue(option: output),
            parse.GetValue(option: samples), parse.GetValue(option: timeout), parse.GetValue(option: headless), parse.GetValue(option: width), parse.GetValue(option: height), parse.GetValue(option: cache),
            parse.GetValue(option: json), sampleHost));
        command.Detail(detail: "Writes report.json (puck.bench.v1) in a unique run directory under --output after every sample: the World build's identity (version with its commit, SHA-256 of its engine assemblies), the host load sampled during the run, and the metric `startup` (console readiness headless, the completed capture windowed, in ms) as median, p10 and p90 over every sample of every world. `puck measure` reads it as /metrics/startup/median. Exit 0 when every sample passed, 1 when one failed, 2 for invalid arguments.");
        return command;
    }

    private static int Run(string[] worlds, string? artifact, string? output, int samples, int timeout, bool headless, int width, int height, string? cache, bool json, BenchHostSample? sampleHost) {
        if ((samples is < 1 or > 100) || (timeout is < 1 or > 3600) || (width is < 1 or > 16384) || (height is < 1 or > 16384)) {
            Console.Error.WriteLine(value: "startup: samples must be 1..100, timeout 1..3600 seconds, and dimensions 1..16384.");
            return 2;
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) { return 2; }
        artifact = Path.GetFullPath(path: (artifact ?? Path.Combine(path1: root, path2: "src/Puck.World/bin/Release/net11.0/Puck.World.dll")));
        if (!File.Exists(path: artifact)) {
            Console.Error.WriteLine(value: $"startup: missing World artifact {artifact}; build World in Release before measuring.");
            return 2;
        }
        if (worlds.Length == 0) {
            // The debug world is the one live world; every other world is quarantined in experimental/.
            worlds = [Path.Combine(path1: root, path2: WorldBenchmarks.DefaultWorldRelativePath)];
        }
        worlds = worlds.Select(selector: Path.GetFullPath).ToArray();
        if (worlds.Any(predicate: path => !(File.Exists(path: path) || Directory.Exists(path: path))) || (worlds.Distinct(comparer: PuckPaths.Comparer).Count() != worlds.Length)) {
            Console.Error.WriteLine(value: "startup: every world must exist and occur exactly once.");
            return 2;
        }
        var run = PuckPaths.Normalize(path: Path.Combine(path1: (output ?? Path.Combine(path1: root, path2: "artifacts/startup")), path2: Guid.NewGuid().ToString(format: "N")));

        Directory.CreateDirectory(path: run);
        var rows = new List<StartupSample>();
        var start = BenchReport.Start(build: BenchBuild.Of(entryAssembly: artifact), repeats: samples, settings: new Dictionary<string, string> {
            ["worlds"] = string.Join(separator: ", ", values: worlds.Select(selector: static world => CliPaths.ToDisplay(fullPath: world))),
            ["headless"] = (headless ? "true" : "false"),
            ["size"] = $"{width}x{height}",
            ["cacheDir"] = ((cache is null) ? "(per sample)" : PuckPaths.Normalize(path: Path.GetFullPath(path: cache))),
        }, verb: "bench startup");
        var progress = (json ? Console.Error : Console.Out);
        using var host = new BenchHostRecorder(sample: sampleHost);
        BenchReport Report(bool finished) => start with {
            Complete = (finished && (Summarize(rows, (worlds.Length * samples), headless) is not null)),
            HostLoad = (finished ? host.Finish() : null),
            Metrics = ((Summarize(rows, (worlds.Length * samples), headless) is { } metric) ? new Dictionary<string, BenchMetric> { ["startup"] = metric } : new Dictionary<string, BenchMetric>()),
            Startup = rows.ToArray(),
        };

        progress.WriteLine(value: $"startup: {worlds.Length} worlds x {samples} fresh processes; {(headless ? "headless" : "windowed")} {width}x{height}; {run}");
        // Interleave repetitions so all samples of one world do not uniquely benefit from adjacent disk-cache use.
        for (var sample = 1; (sample <= samples); sample++) {
            for (var index = 0; (index < worlds.Length); index++) {
                var directory = $"{run}/{index:D3}/sample-{sample:D3}";

                Directory.CreateDirectory(path: directory);
                var capture = $"{directory}/frame.png";
                var arguments = new[] { artifact, "--world", worlds[index], "--devices", "none", "--headless", (headless ? "true" : "false"), "--exit-after-seconds", "0", "--width", width.ToString(provider: CultureInfo.InvariantCulture), "--height", height.ToString(provider: CultureInfo.InvariantCulture), "--state-dir", $"{directory}/state", "--capture-dir", $"{directory}/captures" };
                if (cache is not null) { arguments = [.. arguments, "--cache-dir", Path.GetFullPath(cache)]; }
                var input = (("world.status\n" + (headless ? "" : $"world.wait 1\nworld.screenshot {JsonSerializer.Serialize(capture, StartupJsonContext.Default.String)}\n")) + "wire.errors\n");
                StartupSample row;

                try {
                    WorldAdmission.Wait(step: "bench startup", worldArguments: arguments);

                    var result = CliProcess.RunCaptured("dotnet", arguments, input, TimeSpan.FromSeconds(seconds: timeout),
                        continueWhen: line => (headless
                            ? ((line.Stream == CliProcessOutputStream.Stdout) && (line.Line == "[wire.errors: 0 rejected]"))
                            : IsCaptureCompletion(capture: capture, line: line)),
                        continuationInput: "quit\n");

                    File.WriteAllText($"{directory}/stdout.log", result.Stdout);
                    File.WriteAllText($"{directory}/stderr.log", result.Stderr);
                    row = Assess(worlds[index], sample, result, headless, capture);
                } catch (Exception exception) when ((exception is Win32Exception or IOException or InvalidOperationException)) {
                    row = new(worlds[index], sample, null, null, null, exception.Message);
                }
                rows.Add(item: row);
                progress.WriteLine(value: ((row.Error is null)
                    ? $"{CliPaths.ToDisplay(fullPath: worlds[index])} #{sample}: console {row.ConsoleMilliseconds:F1} ms, capture {row.CaptureMilliseconds:F1} ms"
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

    // The `startup` metric: every sample of every world, or null until all of them passed.
    public static BenchMetric? Summarize(IReadOnlyList<StartupSample> rows, int expected, bool headless) {
        if ((rows.Count != expected) || (rows.Count == 0) || rows.Any(predicate: row => (row.Error is not null))) { return null; }
        var values = rows.Select(selector: row => (headless ? row.ConsoleMilliseconds : row.CaptureMilliseconds)).ToArray();

        if (values.Any(predicate: value => ((value is null) || !double.IsFinite(d: value.Value) || (value.Value < 0)))) { return null; }
        return BenchMetric.Of(samples: [.. values.Select(selector: static value => value!.Value)], unit: "ms");
    }
}
public sealed record StartupSample(string World, int Sample, double? ConsoleMilliseconds, double? CaptureMilliseconds, int? ExitCode, string? Error);

[JsonSerializable(typeof(string))]
internal sealed partial class StartupJsonContext : JsonSerializerContext;
