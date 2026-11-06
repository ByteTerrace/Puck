using System.CommandLine;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions;

namespace Puck.Cli.Bench;

// Measures the real host in fresh processes. Builds and shutdown are outside the readiness interval; PNG readback
// and pipe delivery are inside the capture interval, making it a conservative bound on visible-frame readiness.
public static class StartupBenchmarks {
    public static Command Create() {
        var worlds = new Argument<string[]>(name: "worlds") { Arity = ArgumentArity.ZeroOrMore, Description = "Worlds to boot: asset package folders, sources or documents. Default: the debug world's package, worlds/debug." };
        var artifact = new Option<string?>("--world-artifact") { Description = "Measure this Puck.World entry assembly instead of the build of the checkout's sources, which is built first, outside every sample, when no run has built that source state." };
        var output = new Option<string?>("--output") { Description = "Parent for a unique run directory containing report.json, transcripts, and captures." };
        var cache = new Option<string?>("--cache-dir") { Description = "World cache root shared across samples; an empty directory measures a cold boot followed by warm boots." };
        var samples = new Option<int>("--samples") { DefaultValueFactory = _ => 3, Description = "Fresh processes measured per world, 1 to 100." };
        var timeout = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 120, Description = "Seconds one sample may take before it is abandoned, 1 to 3600." };
        var headless = new Option<bool>("--headless") { Description = "Measure console readiness only; default launches the windowed renderer and requires a completed PNG capture." };
        var width = new Option<int>("--width") { DefaultValueFactory = _ => 1280, Description = "Window width in pixels for the rendered capture, 1 to 16384." };
        var height = new Option<int>("--height") { DefaultValueFactory = _ => 720, Description = "Window height in pixels for the rendered capture, 1 to 16384." };
        var command = new Command(description: "Measure fresh-process World startup serially, with isolated persistence for every sample.", name: "startup") { worlds, artifact, output, cache, samples, timeout, headless, width, height };

        command.SetAction(action: parse => Run(
            (parse.GetValue(argument: worlds) ?? []), parse.GetValue(option: artifact), parse.GetValue(option: output),
            parse.GetValue(option: samples), parse.GetValue(option: timeout), parse.GetValue(option: headless), parse.GetValue(option: width), parse.GetValue(option: height), parse.GetValue(option: cache)));
        return command;
    }

    private static int Run(string[] worlds, string? artifact, string? output, int samples, int timeout, bool headless, int width, int height, string? cache) {
        if ((samples is < 1 or > 100) || (timeout is < 1 or > 3600) || (width is < 1 or > 16384) || (height is < 1 or > 16384)) {
            Console.Error.WriteLine(value: "startup: samples must be 1..100, timeout 1..3600 seconds, and dimensions 1..16384.");
            return 2;
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) { return 2; }
        var run = PuckPaths.Normalize(path: Path.Combine(path1: (output ?? Path.Combine(path1: root, path2: "artifacts/startup")), path2: Guid.NewGuid().ToString(format: "N")));

        Directory.CreateDirectory(path: run);
        // The build happens before the first sample, so no sample's interval holds it.
        if (!WorldLaunch.TryResolve(build: out var build, error: out var buildError, logDirectory: run, named: artifact, repositoryRoot: root, spirv: (!headless && WorldLaunch.NeedsSpirv(backend: null, shape: WorldShape.Windowed)), verb: "startup")) {
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

        Console.WriteLine(value: $"startup: {worlds.Length} worlds x {samples} fresh processes; {(headless ? "headless" : "windowed")} {width}x{height}; {run}");
        // Interleave repetitions so all samples of one world do not uniquely benefit from adjacent disk-cache use.
        for (var sample = 1; (sample <= samples); sample++) {
            for (var index = 0; (index < worlds.Length); index++) {
                var directory = $"{run}/{index:D3}/sample-{sample:D3}";

                Directory.CreateDirectory(path: directory);
                var capture = $"{directory}/frame.png";
                var request = new WorldLaunchRequest(Artifact: artifact, Step: "bench startup") {
                    Arguments = ["--width", width.ToString(provider: CultureInfo.InvariantCulture), "--height", height.ToString(provider: CultureInfo.InvariantCulture), "--capture-dir", $"{directory}/captures", .. ((cache is null) ? Array.Empty<string>() : ["--cache-dir", Path.GetFullPath(cache)])],
                    ExitAfterSeconds = 0,
                    Shape = (headless ? WorldShape.Headless : WorldShape.Windowed),
                    StateDirectory = $"{directory}/state",
                    World = worlds[index],
                };
                var input = (("world.status\n" + (headless ? "" : $"world.wait 1\nworld.screenshot {JsonSerializer.Serialize(capture, StartupJsonContext.Default.String)}\n")) + "wire.errors\n");
                StartupSample row;

                try {
                    using var launch = WorldLaunch.Admit(request: request);

                    var result = launch.Run(input: input, timeout: TimeSpan.FromSeconds(seconds: timeout),
                        continueWhen: line => (headless
                            ? ((line.Stream == CliProcessOutputStream.Stdout) && (line.Line == "[wire.errors: 0 rejected]"))
                            : IsCaptureCompletion(capture: capture, line: line)),
                        continuationInput: "quit\n");

                    File.WriteAllText($"{directory}/stdout.log", result.Stdout);
                    File.WriteAllText($"{directory}/stderr.log", result.Stderr);
                    row = Assess(worlds[index], sample, result, headless, capture);
                } catch (Exception exception) when ((exception is Win32Exception or IOException or InvalidOperationException or TimeoutException)) {
                    row = new(worlds[index], sample, null, null, null, exception.Message);
                }
                rows.Add(item: row);
                Console.WriteLine(value: ((row.Error is null)
                    ? $"{CliPaths.ToDisplay(fullPath: worlds[index])} #{sample}: console {row.ConsoleMilliseconds:F1} ms, capture {row.CaptureMilliseconds:F1} ms"
                    : $"FAIL {CliPaths.ToDisplay(fullPath: worlds[index])} #{sample}: {row.Error}"));
                // Persist partial evidence after every child; interruption never turns the completed prefix into a full run.
                var report = new StartupReport(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, artifact, headless, width, height, (worlds.Length * samples), rows.ToArray(), Summarize(rows, (worlds.Length * samples), headless));

                File.WriteAllText($"{run}/report.json", JsonSerializer.Serialize(report, StartupJsonContext.Default.StartupReport));
            }
        }
        var summary = Summarize(rows, (worlds.Length * samples), headless);

        Console.WriteLine(value: ((summary is null) ? "startup: incomplete or failed samples; no corpus timing claimed." : $"startup: mean {summary.MeanMilliseconds:F1} ms; median {summary.MedianMilliseconds:F1} ms; p95 {summary.P95Milliseconds:F1} ms; max {summary.MaximumMilliseconds:F1} ms."));
        return ((summary is null) ? 1 : 0);
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

    public static StartupSummary? Summarize(IReadOnlyList<StartupSample> rows, int expected, bool headless) {
        if ((rows.Count != expected) || (rows.Count == 0) || rows.Any(predicate: row => (row.Error is not null))) { return null; }
        var values = rows.Select(selector: row => (headless ? row.ConsoleMilliseconds : row.CaptureMilliseconds)).ToArray();

        if (values.Any(predicate: value => ((value is null) || !double.IsFinite(d: value.Value) || (value.Value < 0)))) { return null; }
        var ordered = values.Select(selector: value => value!.Value).Order().ToArray();
        var middle = (ordered.Length / 2);

        return new(ordered.Average(), (((ordered.Length % 2) == 0) ? ((ordered[(middle - 1)] + ordered[middle]) / 2) : ordered[middle]), ordered[(((int)Math.Ceiling(a: (ordered.Length * .95))) - 1)], ordered[^1]);
    }
}
public sealed record StartupSample(string World, int Sample, double? ConsoleMilliseconds, double? CaptureMilliseconds, int? ExitCode, string? Error);
public sealed record StartupSummary(double MeanMilliseconds, double MedianMilliseconds, double P95Milliseconds, double MaximumMilliseconds);

internal sealed record StartupReport(string Runtime, string OperatingSystem, string Artifact, bool Headless, int Width, int Height, int ExpectedSamples, StartupSample[] Samples, StartupSummary? Summary);
[JsonSerializable(typeof(StartupReport))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(NewLine = "\n", WriteIndented = true)]
internal sealed partial class StartupJsonContext : JsonSerializerContext;
