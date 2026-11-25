using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;
using Puck.Abstractions;

namespace Puck.Cli.Bench;

// Times the edit loops as the World times them (world.iteration), each edit class on its own: a cold-cache-free boot, a
// .puck edit saved and reloaded, the edit reverted, and the restart a C# edit outside hot reload costs. The World runs on
// a private copy of the repository's worlds tree, so an edit never touches the checkout, and the copy keeps its path and
// file facts between runs, so its compile cache stays warm.
public static partial class IterationBenchmarks {
    // The stages a loop line reports, in order.
    private static readonly string[] Stages = ["first", "frame", "shaded", "shown", "meshes", "lit"];

    public static Command Create(BenchHostSample? sampleHost) {
        var world = new Argument<string?>(name: "world") { Arity = ArgumentArity.ZeroOrOne, Description = "The world to boot, relative to the repository's worlds directory or under it. Default: the debug world's package, worlds/debug." };
        var edit = new Option<string?>(name: "--edit") { Description = "The .puck edit, as <file>|<text>|<replacement>: the file relative to the worlds directory, and text it holds exactly once. Default for the debug world: one palette colour in debug.puck." };
        var artifact = new Option<string?>(name: "--world-artifact") { Description = "Measure this Puck.World entry assembly instead of the build of the checkout's sources, which is built first, outside every sample." };
        var output = new Option<string?>(name: "--output") { Description = "Parent of the run directory and of the worlds copy the World edits. Default: artifacts/loops." };
        var cache = new Option<string?>(name: "--cache-dir") { Description = "World cache root; omitted, the per-user caches, so the boot reads the warm mesh store." };
        var samples = CliOptions.Count(defaultValue: 1, description: "Rounds of boot, edit, revert and restart, 1 to 20.", maximum: 20, minimum: 1, name: "--samples");
        var timeout = CliOptions.Count(defaultValue: 600, description: "Seconds a loop may take to settle before the round fails, 10 to 3600.", maximum: 3600, minimum: 10, name: "--timeout-seconds");
        var backend = WorldLaunch.BackendOption(description: "Graphics backend: auto, directx or vulkan.");
        var json = new Option<bool>("--json") { Description = "Print the report (puck.bench.v1) on standard output instead of the summary lines." };
        var command = new Command(description: "Time the edit loops in a windowed World: boot, a .puck edit, its revert, and a restart, each from the edit to the first frame showing it and to the settled frame.", name: "loops") { world, edit, artifact, output, cache, samples, timeout, backend, json };

        command.SetAction(action: parse => Run(parse.GetValue(argument: world), parse.GetValue(option: edit), parse.GetValue(option: artifact), parse.GetValue(option: output),
            parse.GetValue(option: cache), parse.GetValue(option: samples), parse.GetValue(option: timeout), parse.GetValue(option: backend), parse.GetValue(option: json), sampleHost));
        command.Detail(detail: "Each round boots a windowed World (under the GPU lease, alone on the GPU) on a private copy of the worlds directory, waits for the boot loop to settle, turns the source watch off, applies the .puck edit to the copy and reloads, waits for that loop, reverts the edit and reloads again, quits, then boots a second process over the same caches: the restart a C# edit outside hot reload costs, less the World's build. Every figure is the World's own (world.iteration, seconds from the edit to the first frame showing it and to each later stage), never blended across edit classes. Writes report.json (puck.bench.v1) under a unique run directory: metrics <class>.<stage> (boot, edit, revert, restart), and rows holding each loop's line. A hot-reload edit is timed by the World as its own loop (hot-reload) when the World runs under dotnet watch; this verb does not drive one. Exit 0 when every round settled, 1 when one did not, 2 for invalid arguments.");
        return command;
    }

    private static int Run(string? world, string? edit, string? artifact, string? output, string? cache, int samples, int timeout, Puck.World.WorldBackendPreference? backend, bool json, BenchHostSample? sampleHost) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) { return 2; }
        var worlds = PuckPaths.Normalize(path: Path.Combine(path1: root, path2: "worlds"));
        var relative = PuckPaths.Slashed(path: (world ?? "debug"));

        relative = (relative.StartsWith(value: "worlds/", comparisonType: StringComparison.Ordinal) ? relative["worlds/".Length..] : relative);
        if (!(Directory.Exists(path: Path.Combine(path1: worlds, path2: relative)) || File.Exists(path: Path.Combine(path1: worlds, path2: relative)))) {
            return CliExit.Refuse("bench loops", "world", $"'{relative}' is not under {worlds}.");
        }
        if (!TryParseEdit(edit: (edit ?? ((relative == "debug") ? DebugEdit : null)), file: out var file, text: out var text, replacement: out var replacement, error: out var editError)) {
            return CliExit.Refuse("bench loops", "--edit", editError);
        }

        var parent = PuckPaths.Normalize(path: Path.GetFullPath(path: (output ?? Path.Combine(path1: root, path2: "artifacts/loops"))));
        var copy = PuckPaths.Normalize(path: Path.Combine(path1: parent, path2: "worlds"));
        var run = PuckPaths.Normalize(path: Path.Combine(path1: parent, path2: Guid.NewGuid().ToString(format: "N")));

        Directory.CreateDirectory(path: run);
        if (!WorldLaunch.TryResolve(build: out var build, error: out var buildError, logDirectory: run, named: artifact, repositoryRoot: root, spirv: WorldLaunch.NeedsSpirv(backend: backend, shape: WorldShape.Windowed), verb: "loops")) {
            Console.Error.WriteLine(value: $"loops: {buildError}");
            return 2;
        }
        using var held = build;
        var lines = new List<KeyValuePair<string, string>>();
        var figures = new Dictionary<string, List<double>>(comparer: StringComparer.Ordinal);
        var failed = false;
        var start = BenchReport.Start(build: BenchBuild.Of(entryAssembly: build.Path), repeats: samples, settings: new Dictionary<string, string> {
            ["world"] = relative,
            ["edit"] = $"{file}|{text}|{replacement}",
            ["backend"] = (backend?.ToString() ?? "document"),
            ["cacheDir"] = ((cache is null) ? "(per user)" : PuckPaths.Normalize(path: Path.GetFullPath(path: cache))),
            ["copy"] = copy,
        }, verb: "bench loops");
        using var host = new BenchHostRecorder(sample: sampleHost);
        var progress = (json ? Console.Error : Console.Out);

        for (var sample = 1; (sample <= samples) && !failed; sample++) {
            Mirror(source: worlds, target: copy);
            var target = Path.Combine(path1: copy, path2: file);
            var original = File.ReadAllText(path: target);

            if (CountOf(haystack: original, needle: text) != 1) {
                return CliExit.Refuse("bench loops", "--edit", $"'{file}' holds '{text}' {CountOf(haystack: original, needle: text)} times; it must hold it exactly once.");
            }
            try {
                failed |= !Round(edit: () => File.WriteAllText(path: target, contents: original.Replace(oldValue: text, newValue: replacement, comparisonType: StringComparison.Ordinal)),
                    revert: () => File.WriteAllText(path: target, contents: original), figures: figures, lines: lines, progress: progress, request: Request(artifact: build.Path, backend: backend, cache: cache, copy: copy, directory: $"{run}/{sample:D3}", relative: relative),
                    restart: Request(artifact: build.Path, backend: backend, cache: cache, copy: copy, directory: $"{run}/{sample:D3}-restart", relative: relative), sample: sample, timeout: TimeSpan.FromSeconds(seconds: timeout));
            } finally {
                File.WriteAllText(path: target, contents: original);
            }
        }

        var report = start with {
            Complete = !failed,
            HostLoad = host.Finish(),
            Metrics = figures.Where(predicate: static entry => (entry.Value.Count > 0)).ToDictionary(keySelector: static entry => entry.Key, elementSelector: static entry => BenchMetric.Of(samples: entry.Value, unit: "s"), comparer: StringComparer.Ordinal),
            Rows = lines,
        };

        _ = report.Write(directory: run);
        if (json) { Console.Out.WriteLine(value: report.ToJson()); } else { foreach (var line in report.MetricLines()) { Console.WriteLine(value: $"loops: {line}"); } }
        return (failed ? 1 : 0);
    }

    // The debug world's default edit: the first palette colour of the cube the boot spawn faces.
    private const string DebugEdit = "debug/debug.puck|color: \"#E8B341\"|color: \"#41B3E8\"";

    private static WorldLaunchRequest Request(string artifact, Puck.World.WorldBackendPreference? backend, string? cache, string copy, string directory, string relative) {
        Directory.CreateDirectory(path: directory);
        return new WorldLaunchRequest(Artifact: artifact, Step: "bench loops") {
            Arguments = (cache is null) ? [] : ["--cache-dir", Path.GetFullPath(path: cache)],
            Backend = backend,
            ExitAfterSeconds = 0,
            Shape = WorldShape.Windowed,
            StateDirectory = $"{directory}/state",
            Timing = true,
            World = Path.Combine(path1: copy, path2: relative),
        };
    }

    // One round: the boot loop, the edit's loop and the revert's in one process, then the restart's boot loop in another.
    private static bool Round(WorldLaunchRequest request, WorldLaunchRequest restart, Action edit, Action revert, Dictionary<string, List<double>> figures, List<KeyValuePair<string, string>> lines, TextWriter progress, int sample, TimeSpan timeout) {
        using (var launch = WorldLaunch.Admit(request: request))
        using (var world = launch.Start(logDirectory: Path.GetDirectoryName(path: request.StateDirectory!)!)) {
            if (!Loop(world: world, figures: figures, lines: lines, name: "boot", number: 1, progress: progress, sample: sample, timeout: timeout)) { return false; }
            _ = world.Send(line: "world.watch off");
            edit();
            _ = world.Send(line: "world.reload");
            if (!Loop(world: world, figures: figures, lines: lines, name: "edit", number: 2, progress: progress, sample: sample, timeout: timeout)) { return false; }
            revert();
            _ = world.Send(line: "world.reload");
            if (!Loop(world: world, figures: figures, lines: lines, name: "revert", number: 3, progress: progress, sample: sample, timeout: timeout)) { return false; }
            _ = world.Send(line: "quit");
            _ = world.WaitForExit(timeout: TimeSpan.FromSeconds(seconds: 60));
        }
        using (var launch = WorldLaunch.Admit(request: restart))
        using (var world = launch.Start(logDirectory: Path.GetDirectoryName(path: restart.StateDirectory!)!)) {
            var settled = Loop(world: world, figures: figures, lines: lines, name: "restart", number: 1, progress: progress, sample: sample, timeout: timeout);

            _ = world.Send(line: "quit");
            _ = world.WaitForExit(timeout: TimeSpan.FromSeconds(seconds: 60));
            return settled;
        }
    }

    // Waits for the World to settle the loop of a number (its [world.iteration] line on stderr) and records its stages.
    private static bool Loop(WorldProcess world, Dictionary<string, List<double>> figures, List<KeyValuePair<string, string>> lines, string name, int number, TextWriter progress, int sample, TimeSpan timeout) {
        var prefix = $"[world.iteration] #{number.ToString(provider: CultureInfo.InvariantCulture)} ";
        var line = world.WaitFor(accept: candidate => candidate.Text.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal), from: 0, timeout: timeout);

        if (line is null) {
            progress.WriteLine(value: $"FAIL loops #{sample} {name}: no settled loop #{number} within {timeout.TotalSeconds:0} s");
            return false;
        }
        lines.Add(item: new KeyValuePair<string, string>($"{sample:D3}.{name}", line.Text));
        foreach (var stage in Stages) {
            if (StageSeconds(line: line.Text, stage: stage) is { } seconds) {
                if (!figures.TryGetValue(key: $"{name}.{stage}", value: out var values)) { figures[$"{name}.{stage}"] = values = []; }
                values.Add(item: seconds);
            }
        }
        progress.WriteLine(value: $"loops #{sample} {name}: {line.Text["[world.iteration] ".Length..]}");
        return true;
    }

    // A stage's seconds in a loop line (" frame=1.234s"), or null when the line reports it pending.
    public static double? StageSeconds(string line, string stage) {
        var match = StageValue().Matches(input: line).FirstOrDefault(predicate: candidate => (candidate.Groups["stage"].Value == stage));

        return ((match is not null) && double.TryParse(s: match.Groups["seconds"].Value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var seconds)) ? seconds : null;
    }

    [GeneratedRegex(pattern: @" (?<stage>[a-z]+)=(?<seconds>[0-9]+\.[0-9]+)s")]
    private static partial Regex StageValue();

    private static bool TryParseEdit(string? edit, out string file, out string text, out string replacement, out string error) {
        (file, text, replacement, error) = (string.Empty, string.Empty, string.Empty, string.Empty);
        if (edit is null) { error = "names no edit; give --edit <file>|<text>|<replacement> for a world other than the debug world."; return false; }

        var parts = edit.Split(separator: '|');

        if ((parts.Length != 3) || parts.Any(predicate: string.IsNullOrEmpty) || (parts[1] == parts[2])) { error = "is not <file>|<text>|<replacement> with a replacement that differs from the text."; return false; }
        (file, text, replacement) = (PuckPaths.Slashed(path: parts[0]), parts[1], parts[2]);
        return true;
    }

    private static int CountOf(string haystack, string needle) {
        var count = 0;

        for (var at = haystack.IndexOf(value: needle, comparisonType: StringComparison.Ordinal); (at >= 0); at = haystack.IndexOf(value: needle, startIndex: (at + needle.Length), comparisonType: StringComparison.Ordinal)) { count++; }
        return count;
    }

    // Makes the target a copy of the source: a file whose bytes differ is written, a file the source lacks is deleted, and
    // a file already equal is left alone, so its write time stands and the compile cache's file facts hold across runs.
    private static void Mirror(string source, string target) {
        Directory.CreateDirectory(path: target);
        var kept = new HashSet<string>(comparer: PuckPaths.Comparer);

        foreach (var path in Directory.EnumerateFiles(path: source, searchPattern: "*", searchOption: SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(relativeTo: source, path: path);
            var destination = Path.Combine(path1: target, path2: relative);

            _ = kept.Add(item: relative);
            if (File.Exists(path: destination) && File.ReadAllBytes(path: destination).AsSpan().SequenceEqual(other: File.ReadAllBytes(path: path))) { continue; }
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            File.Copy(sourceFileName: path, destFileName: destination, overwrite: true);
        }
        foreach (var path in Directory.EnumerateFiles(path: target, searchPattern: "*", searchOption: SearchOption.AllDirectories)) {
            if (!kept.Contains(item: Path.GetRelativePath(relativeTo: target, path: path))) { File.Delete(path: path); }
        }
    }
}
