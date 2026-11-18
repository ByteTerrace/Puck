using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Cli.Locks;

/// <summary>Keeps the versions the lock files already pin when a restore has re-resolved them. A restore that rewrites a
/// lock file (its project's references changed, or the restore evaluated the whole graph afresh) resolves every floating
/// range, <c>[11.*, )</c> and the like, to the newest package the feed holds, so an unrelated package advances with the
/// reference change that rewrote its project's lock file. <see cref="Hold"/> takes the rewritten text back to the
/// versions the previous text pinned wherever the same project still asks for the same package the same way, and a
/// package the file did not list before to the version the solution already pins (<see cref="Collect"/>), so one
/// package keeps one version across every project that reaches it.</summary>
public static class LockPins {
    private static readonly JsonSerializerOptions Format = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>Finds the version of each package the lock files pin, the lowest where they differ: a project that gains the package compiles against no newer assembly than a dependent deploys.</summary>
    /// <param name="texts">The text of each lock file.</param>
    /// <returns>Each package id's entry (its version, contentHash and dependency list) at the lowest pinned version.</returns>
    public static Dictionary<string, JsonObject> Collect(IEnumerable<string> texts) {
        ArgumentNullException.ThrowIfNull(argument: texts);

        var pinned = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);

        foreach (var text in texts) {
            if ((JsonNode.Parse(json: text) is not JsonObject root) || (root["dependencies"] is not JsonObject frameworks)) { continue; }

            foreach (var (_, frameworkNode) in frameworks) {
                if (frameworkNode is not JsonObject packages) { continue; }

                foreach (var (id, node) in packages) {
                    if ((node is not JsonObject entry) || (Text(node: entry["type"]) is null or "Project") || (Text(node: entry["resolved"]) is not { } resolved)) { continue; }
                    if (!pinned.TryGetValue(key: id, value: out var known) || (Compare(left: resolved, right: Text(node: known["resolved"])!) < 0)) { pinned[id] = entry; }
                }
            }
        }

        return pinned;
    }
    /// <summary>Holds each pinned version in a rewritten lock file at the version the previous file pinned, or the
    /// solution pins for a package the previous file did not list.</summary>
    /// <param name="previous">The lock file's text before the restore.</param>
    /// <param name="current">The lock file's text after the restore.</param>
    /// <param name="packages">Receives each package whose pinned version the text now holds again, as
    /// <c>id pinned-version (restored from current-version)</c>, in file order.</param>
    /// <param name="solution">What <see cref="Collect"/> found across every lock file, or <see langword="null"/> to hold
    /// only the versions <paramref name="previous"/> pinned.</param>
    /// <returns>The text with its pins held: <paramref name="previous"/> itself when nothing else differs from it,
    /// otherwise <paramref name="current"/> with the pinned versions, contentHash and dependency lists written back and
    /// its line endings and final newline kept; <see langword="null"/> when no pin moved and the text stands as it is.</returns>
    public static string? Hold(string previous, string current, List<string> packages, IReadOnlyDictionary<string, JsonObject>? solution = null) {
        ArgumentNullException.ThrowIfNull(argument: previous);
        ArgumentNullException.ThrowIfNull(argument: current);
        ArgumentNullException.ThrowIfNull(argument: packages);

        if ((JsonNode.Parse(json: previous) is not JsonObject before) || (JsonNode.Parse(json: current) is not JsonObject after)) { return null; }
        if ((before["dependencies"] is not JsonObject oldFrameworks) || (after["dependencies"] is not JsonObject newFrameworks)) { return null; }

        var held = false;

        foreach (var (framework, newNode) in newFrameworks) {
            if (newNode is not JsonObject newPackages) { continue; }

            var oldPackages = (oldFrameworks[framework] as JsonObject);

            foreach (var (id, newEntry) in newPackages) {
                if (newEntry is not JsonObject entry) { continue; }

                var type = Text(node: entry["type"]);

                // A project entry carries no version. A pin follows a package the project still asks for in the same role
                // and range (a changed request is the reference change itself), or a package the file did not list.
                if (type is null or "Project") { continue; }

                JsonObject? source = null;

                if (oldPackages?[id] is JsonObject old) {
                    if ((type == Text(node: old["type"])) && (Text(node: entry["requested"]) == Text(node: old["requested"]))) { source = old; }
                } else if ((solution is not null) && solution.TryGetValue(key: id, value: out var pinned) && ((type != "Direct") || Admits(range: Text(node: entry["requested"]), version: Text(node: pinned["resolved"])))) {
                    source = pinned;
                }
                if ((source is null) || (Text(node: entry["resolved"]) == Text(node: source["resolved"]))) { continue; }

                packages.Add(item: $"{id} {Text(node: source["resolved"])} (restored from {Text(node: entry["resolved"])})");
                entry["resolved"] = source["resolved"]?.DeepClone();
                entry["contentHash"] = source["contentHash"]?.DeepClone();
                _ = entry.Remove(propertyName: "dependencies");
                if (source["dependencies"] is { } dependencies) { entry["dependencies"] = dependencies.DeepClone(); }
                held = true;
            }
        }

        if (!held) { return null; }
        if (JsonNode.DeepEquals(node1: after, node2: JsonNode.Parse(json: previous))) { return previous; }

        var newline = (current.Contains(value: "\r\n", comparisonType: StringComparison.Ordinal) ? "\r\n" : "\n");
        var text = after.ToJsonString(options: Format).ReplaceLineEndings(replacementText: newline);

        return (current.EndsWith(value: '\n') ? (text + newline) : text);
    }

    // Whether a version lies in a lock file's requested range, spelled as NuGet writes it: a bracket or parenthesis, a lower
    // bound (a floating part is a wildcard), a comma, an optional upper bound, a bracket or parenthesis. A spelling it does
    // not read admits nothing, so the entry keeps what the restore resolved.
    private static bool Admits(string? range, string? version) {
        if ((range is null) || (version is null) || (range.Length < 3)) { return false; }

        var parts = range[1..^1].Split(separator: ',');

        if (parts.Length != 2) { return false; }

        var lower = parts[0].Trim().Replace(oldValue: "*", newValue: "0", comparisonType: StringComparison.Ordinal);
        var upper = parts[1].Trim();

        if ((lower.Length != 0) && ((Compare(left: version, right: lower) < 0) || ((range[0] == '(') && (Compare(left: version, right: lower) == 0)))) { return false; }
        if (upper.Length == 0) { return true; }

        var order = Compare(left: version, right: upper);

        return ((order < 0) || ((order == 0) && (range[^1] == ']')));
    }
    // Orders two versions as NuGet does for the spellings a lock file holds: the numeric parts, then a release above its
    // prerelease.
    private static int Compare(string left, string right) {
        static (Version Core, bool Release) Parse(string version) {
            var dash = version.IndexOf(value: '-');
            var core = ((dash < 0) ? version : version[..dash]);

            while (core.Count(predicate: static character => (character == '.')) < 1) { core += ".0"; }

            return (Version.TryParse(input: core, result: out var parsed) ? parsed : new Version(), (dash < 0));
        }

        var (leftCore, leftRelease) = Parse(version: left);
        var (rightCore, rightRelease) = Parse(version: right);
        var order = leftCore.CompareTo(value: rightCore);

        return ((order != 0) ? order : leftRelease.CompareTo(rightRelease));
    }
    private static string? Text(JsonNode? node) =>
        ((node is JsonValue value) && value.TryGetValue<string>(value: out var text) ? text : null);
}
