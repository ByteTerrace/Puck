using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions;

namespace Puck.Cli.Counters;

// Batch input is CLI orchestration data; every reported workload and script still uses the existing report format.
internal sealed record CountersBatchManifest(string World, IReadOnlyList<CountersBatchGroup> Groups) {
    public const string SchemaVersion = "puck.counters.batch.v1";
    [JsonRequired]
    public string Schema { get; init; } = SchemaVersion;
}
internal sealed record CountersBatchGroup(string Name, string Prelude, IReadOnlyList<CountersBatchObservation> Observations) {
    // Sky-only work waits for the existing engine verdict without installing an indirect cache.
    public string Completion { get; init; } = "indirect";
}
internal sealed record CountersBatchObservation(string Name, string Method, string Script, string Report, string Ceilings);
internal sealed record PreparedCountersObservation(CountersBatchObservation Definition, string ScriptPath, string Script,
    string ReportPath, string CeilingsPath);
internal sealed record PreparedCountersGroup(string Name, string PreludePath, string Prelude,
    IReadOnlyList<PreparedCountersObservation> Observations) {
    public string Completion { get; init; } = "indirect";
    public string Script => Prelude + string.Concat(Observations.Select(observation => observation.Script));
}
internal sealed record PreparedCountersBatch(string WorldPath, IReadOnlyList<PreparedCountersGroup> Groups, string ManifestHash);

internal static class CountersBatchInput {
    internal static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        NewLine = "\n",
    };

    public static PreparedCountersBatch Read(string path, string output) {
        var text = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<CountersBatchManifest>(text, Json)
            ?? throw new FormatException("the batch manifest is null");
        if (manifest.Schema != CountersBatchManifest.SchemaVersion || string.IsNullOrWhiteSpace(manifest.World)
            || manifest.Groups is not { Count: > 0 }) {
            throw new FormatException("a batch needs its schema, workload and at least one ordered group");
        }
        var home = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var groups = new List<PreparedCountersGroup>();
        var names = new HashSet<string>(PuckPaths.Comparer);
        var reports = new HashSet<string>(PuckPaths.Comparer);
        var ceilings = new HashSet<string>(PuckPaths.Comparer);
        var scripts = new HashSet<string>(PuckPaths.Comparer);
        var groupNames = new HashSet<string>(PuckPaths.Comparer);
        foreach (var group in manifest.Groups) {
            if (group is null || !IsName(group.Name) || !groupNames.Add(group.Name)
                || group.Observations is not { Count: > 0 } || group.Completion is not ("indirect" or "engine")) {
                throw new FormatException("each group needs a unique file-safe name, observations and an indirect or engine completion");
            }
            var preludePath = SourcePath(home, group.Prelude);
            var prelude = ReadScript(preludePath);
            if (Commands(prelude).Any(line => line.StartsWith("world.counters", StringComparison.Ordinal))) {
                throw new FormatException($"group {group.Name}'s prelude contains a counters response");
            }
            var waitPrefix = group.Completion == "engine" ? "world.wait ready " : "world.wait indirect ";
            if (Commands(prelude).Any(line => line.StartsWith(waitPrefix, StringComparison.Ordinal))) {
                throw new FormatException($"group {group.Name}'s prelude contains an observation completion wait");
            }
            var observations = new List<PreparedCountersObservation>();
            foreach (var observation in group.Observations) {
                if (observation is null || !IsName(observation.Name) || !names.Add(observation.Name)
                    || observation.Method is not ("cache" or "screen" or "cone")) {
                    throw new FormatException("each observation needs a unique file-safe name and cache, screen or cone method");
                }
                var scriptPath = SourcePath(home, observation.Script);
                var reportPath = ProductPath(output, observation.Report);
                var ceilingsPath = SourcePath(home, observation.Ceilings);
                if (!scripts.Add(scriptPath) || !reports.Add(reportPath) || !ceilings.Add(ceilingsPath)) {
                    throw new FormatException("observations must own distinct scripts, report paths and ceilings paths");
                }
                var script = ReadScript(scriptPath);
                var commands = Commands(script).ToArray();
                var selected = Array.IndexOf(commands, $"world.indirect-method {observation.Method}");
                var paused = Array.IndexOf(commands, "world.rate pause");
                var warmed = Array.FindIndex(commands, line => line.StartsWith(waitPrefix, StringComparison.Ordinal));
                var resumed = Array.IndexOf(commands, "world.rate resume");
                var advanced = Array.IndexOf(commands, "world.wait 120");
                // Completion must describe the selected method, before the active input begins.
                if (commands.Count(line => line.StartsWith("world.counters", StringComparison.Ordinal)) != 1
                    || commands.LastOrDefault() != "world.counters --json"
                    || commands.Count(line => line.StartsWith("world.indirect-method", StringComparison.Ordinal)) != 1
                    || selected < 0 || selected >= warmed
                    || commands.Count(line => line.StartsWith(waitPrefix, StringComparison.Ordinal)) != 1
                    || commands.Count(line => line.StartsWith("world.wait ", StringComparison.Ordinal)) != 2
                    || commands.Count(line => line == "world.wait 120") != 1
                    || commands.Count(line => line == "world.rate pause") != 1
                    || commands.Count(line => line == "world.rate resume") != 1
                    || paused < 0 || warmed <= paused || resumed <= warmed || advanced <= resumed) {
                    throw new FormatException($"observation {observation.Name} must select its method, pause through its {group.Completion} completion, resume for 120 active ticks and end with its only JSON counters read");
                }
                var disabled = Array.IndexOf(commands, "world.indirect off");
                if (group.Completion == "engine" && (observation.Method != "cache"
                    || commands.Count(line => line.StartsWith("world.indirect ", StringComparison.Ordinal)) != 1
                    || disabled < 0 || disabled >= warmed)) {
                    throw new FormatException($"observation {observation.Name} must disable indirect work before its engine completion");
                }
                observations.Add(new PreparedCountersObservation(observation, scriptPath, script, reportPath, ceilingsPath));
            }
            groups.Add(new PreparedCountersGroup(group.Name, preludePath, prelude, observations) { Completion = group.Completion });
        }
        if (reports.Overlaps(ceilings)) { throw new FormatException("a report output names a ceilings file"); }
        foreach (var report in reports) {
            var relative = PuckPaths.Normalize(Path.GetRelativePath(output, report));
            if (groupNames.Contains(relative.Split('/')[0]) || PuckPaths.Comparer.Equals(relative, "Puck.World.build.log")) {
                throw new FormatException("a report path names runner-owned transcript or build storage");
            }
        }
        var world = SourcePath(home, manifest.World);
        var inputs = new HashSet<string>(scripts, PuckPaths.Comparer) { world, Path.GetFullPath(path) };
        foreach (var group in groups) { inputs.Add(group.PreludePath); }
        if (reports.Overlaps(inputs) || ceilings.Overlaps(inputs)) {
            throw new FormatException("an output or ceiling names a batch input");
        }
        return new PreparedCountersBatch(world, groups, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    private static bool IsName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string SourcePath(string home, string? value) => string.IsNullOrWhiteSpace(value)
        ? throw new FormatException("a batch path is empty") : PuckPaths.Normalize(Path.GetFullPath(value, home));
    private static string ProductPath(string output, string? value) {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) { throw new FormatException("a report path must be relative to the product directory"); }
        var path = SourcePath(output, value);
        var relative = PuckPaths.Normalize(Path.GetRelativePath(output, path));
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) {
            throw new FormatException("a report path leaves the product directory");
        }
        if (PuckPaths.Comparer.Equals(relative, "batch.observations.json") || PuckPaths.Comparer.Equals(relative, "observations")
            || relative.StartsWith("observations/", StringComparison.OrdinalIgnoreCase)) {
            throw new FormatException("a report path names reserved batch provenance storage");
        }
        return path;
    }
    private static string ReadScript(string path) {
        var script = File.ReadAllText(path).ReplaceLineEndings("\n");
        if (Commands(script).Any(line => line == "quit" || line.StartsWith("wire.errors", StringComparison.Ordinal))) {
            throw new FormatException($"script {path} contains a runner-owned terminal command");
        }
        return script.EndsWith('\n') ? script : script + "\n";
    }
    private static IEnumerable<string> Commands(string script) => script.Split('\n').Select(line => line.Trim())
        .Where(line => line.Length != 0 && !line.StartsWith('#'));
}
