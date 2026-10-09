using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions;

namespace Puck.Cli.Counters;

// Batch input is CLI orchestration data; every reported workload and script still uses the existing report format.
public sealed record CountersBatchManifest(string World, IReadOnlyList<CountersBatchGroup> Groups) {
    public const string SchemaVersion = "puck.counters.batch.v1";

    [JsonRequired]
    public string Schema { get; init; } = SchemaVersion;
}
public sealed record CountersBatchGroup(string Name, string Prelude, IReadOnlyList<CountersBatchObservation> Observations) {
    // Sky-only work waits for the existing engine verdict without installing an indirect cache.
    public string Completion { get; init; } = "indirect";
}
public sealed record CountersBatchObservation(string Name, string Method, string Script, string Report, string Ceilings);
public sealed record PreparedCountersObservation(CountersBatchObservation Definition, string ScriptPath, string Script,
    string ReportPath, string CeilingsPath);
public sealed record PreparedCountersGroup(string Name, string PreludePath, string Prelude,
    IReadOnlyList<PreparedCountersObservation> Observations) {
    public string Completion { get; init; } = "indirect";
    public string Script => (Prelude + string.Concat(values: Observations.Select(selector: observation => observation.Script)));
}
public sealed record PreparedCountersBatch(string WorldPath, IReadOnlyList<PreparedCountersGroup> Groups, string ManifestHash);
public static class CountersBatchInput {
    public static readonly JsonSerializerOptions Json = new() {
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static PreparedCountersBatch Read(string path, string output) {
        var text = File.ReadAllText(path: path);
        var manifest = (JsonSerializer.Deserialize<CountersBatchManifest>(json: text, options: Json)
            ?? throw new FormatException(message: "the batch manifest is null"));

        if ((manifest.Schema != CountersBatchManifest.SchemaVersion) || string.IsNullOrWhiteSpace(value: manifest.World)
            || (manifest.Groups is not { Count: > 0 })) {
            throw new FormatException(message: "a batch needs its schema, workload and at least one ordered group");
        }
        var home = Path.GetDirectoryName(path: Path.GetFullPath(path: path))!;
        var groups = new List<PreparedCountersGroup>();
        var names = new HashSet<string>(comparer: PuckPaths.Comparer);
        var reports = new HashSet<string>(comparer: PuckPaths.Comparer);
        var ceilings = new HashSet<string>(comparer: PuckPaths.Comparer);
        var scripts = new HashSet<string>(comparer: PuckPaths.Comparer);
        var groupNames = new HashSet<string>(comparer: PuckPaths.Comparer);

        foreach (var group in manifest.Groups) {
            if ((group is null) || !IsName(name: group.Name) || !groupNames.Add(item: group.Name)
                || (group.Observations is not { Count: > 0 }) || (group.Completion is not ("indirect" or "engine"))) {
                throw new FormatException(message: "each group needs a unique file-safe name, observations and an indirect or engine completion");
            }
            var preludePath = SourcePath(home: home, value: group.Prelude);
            var prelude = ReadScript(path: preludePath);

            if (Commands(script: prelude).Any(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.counters"))) {
                throw new FormatException(message: $"group {group.Name}'s prelude contains a counters response");
            }
            var waitPrefix = ((group.Completion == "engine") ? "world.wait ready " : "world.wait indirect ");

            if (Commands(script: prelude).Any(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: waitPrefix))) {
                throw new FormatException(message: $"group {group.Name}'s prelude contains an observation completion wait");
            }
            var observations = new List<PreparedCountersObservation>();

            foreach (var observation in group.Observations) {
                if ((observation is null) || !IsName(name: observation.Name) || !names.Add(item: observation.Name)
                    || (observation.Method is not ("cache" or "screen" or "cone"))) {
                    throw new FormatException(message: "each observation needs a unique file-safe name and cache, screen or cone method");
                }
                var scriptPath = SourcePath(home: home, value: observation.Script);
                var reportPath = ProductPath(output: output, value: observation.Report);
                var ceilingsPath = SourcePath(home: home, value: observation.Ceilings);

                if (!scripts.Add(item: scriptPath) || !reports.Add(item: reportPath) || !ceilings.Add(item: ceilingsPath)) {
                    throw new FormatException(message: "observations must own distinct scripts, report paths and ceilings paths");
                }
                var script = ReadScript(path: scriptPath);
                var commands = Commands(script: script).ToArray();
                var selected = Array.IndexOf(array: commands, value: $"world.indirect-method {observation.Method}");
                var paused = Array.IndexOf(array: commands, value: "world.rate pause");
                var warmed = Array.FindIndex(array: commands, match: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: waitPrefix));
                var resumed = Array.IndexOf(array: commands, value: "world.rate resume");
                var advanced = Array.IndexOf(array: commands, value: "world.wait 120");
                // Completion must describe the selected method, before the active input begins.
                if ((commands.Count(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.counters")) != 1)
                    || (commands.LastOrDefault() != "world.counters --json")
                    || (commands.Count(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.indirect-method")) != 1)
                    || (selected < 0) || (selected >= warmed)
                    || (commands.Count(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: waitPrefix)) != 1)
                    || (commands.Count(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.wait ")) != 2)
                    || (commands.Count(predicate: line => (line == "world.wait 120")) != 1)
                    || (commands.Count(predicate: line => (line == "world.rate pause")) != 1)
                    || (commands.Count(predicate: line => (line == "world.rate resume")) != 1)
                    || (paused < 0) || (warmed <= paused) || (resumed <= warmed) || (advanced <= resumed)) {
                    throw new FormatException(message: $"observation {observation.Name} must select its method, pause through its {group.Completion} completion, resume for 120 active ticks and end with its only JSON counters read");
                }
                var disabled = Array.IndexOf(array: commands, value: "world.indirect off");

                if ((group.Completion == "engine") && ((observation.Method != "cache")
                    || (commands.Count(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.indirect ")) != 1)
                    || (disabled < 0) || (disabled >= warmed))) {
                    throw new FormatException(message: $"observation {observation.Name} must disable indirect work before its engine completion");
                }
                observations.Add(item: new PreparedCountersObservation(CeilingsPath: ceilingsPath, Definition: observation, ReportPath: reportPath, Script: script, ScriptPath: scriptPath));
            }
            groups.Add(item: new PreparedCountersGroup(group.Name, preludePath, prelude, observations) { Completion = group.Completion });
        }
        if (reports.Overlaps(other: ceilings)) { throw new FormatException(message: "a report output names a ceilings file"); }
        foreach (var report in reports) {
            var relative = Path.GetRelativePath(path: report, relativeTo: output).Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar);

            if (groupNames.Contains(item: relative.Split('/')[0]) || PuckPaths.Comparer.Equals(x: relative, y: "Puck.World.build.log")) {
                throw new FormatException(message: "a report path names runner-owned transcript or build storage");
            }
        }
        var world = SourcePath(home: home, value: manifest.World);
        var inputs = new HashSet<string>(collection: scripts, comparer: PuckPaths.Comparer) { world, Path.GetFullPath(path: path) };

        foreach (var group in groups) { inputs.Add(item: group.PreludePath); }
        if (reports.Overlaps(other: inputs) || ceilings.Overlaps(other: inputs)) {
            throw new FormatException(message: "an output or ceiling names a batch input");
        }
        return new PreparedCountersBatch(world, groups, Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: text))));
    }

    private static bool IsName(string? name) => (!string.IsNullOrWhiteSpace(value: name)
        && name.All(predicate: character => (char.IsAsciiLetterOrDigit(c: character) || (character is '-' or '_'))));
    private static string SourcePath(string home, string? value) => (string.IsNullOrWhiteSpace(value: value)
        ? throw new FormatException(message: "a batch path is empty") : PuckPaths.Normalize(path: Path.GetFullPath(basePath: home, path: value)));
    private static string ProductPath(string output, string? value) {
        if (string.IsNullOrWhiteSpace(value: value) || Path.IsPathRooted(path: value)) { throw new FormatException(message: "a report path must be relative to the product directory"); }
        var path = SourcePath(home: output, value: value);
        var relative = Path.GetRelativePath(path: path, relativeTo: output).Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar);

        if ((relative == "..") || relative.StartsWith(comparisonType: StringComparison.Ordinal, value: "../")) {
            throw new FormatException(message: "a report path leaves the product directory");
        }
        if (PuckPaths.Comparer.Equals(x: relative, y: "batch.observations.json") || PuckPaths.Comparer.Equals(x: relative, y: "observations")
            || relative.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "observations/")) {
            throw new FormatException(message: "a report path names reserved batch provenance storage");
        }
        return path;
    }
    private static string ReadScript(string path) {
        var script = File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n");

        if (Commands(script: script).Any(predicate: line => ((line == "quit") || line.StartsWith(comparisonType: StringComparison.Ordinal, value: "wire.errors")))) {
            throw new FormatException(message: $"script {path} contains a runner-owned terminal command");
        }
        return (script.EndsWith(value: '\n') ? script : (script + "\n"));
    }
    private static IEnumerable<string> Commands(string script) => script.Split('\n').Select(selector: line => line.Trim())
        .Where(predicate: line => ((line.Length != 0) && !line.StartsWith(value: '#')));
}
