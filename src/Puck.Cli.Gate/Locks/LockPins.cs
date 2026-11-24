using static Puck.Transpiler.Lowering.JsonNodeExtensions;
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

    /// <summary>Lists every package a lock file resolves from a feed, as NuGet records it: not a project reference, with
    /// the version it resolved to.</summary>
    /// <param name="text">The lock file's text.</param>
    /// <returns>Each package's id, its entry (version, contentHash and dependency list) and its resolved version, once per
    /// target framework that lists it.</returns>
    public static IEnumerable<(string Id, JsonObject Entry, string Version)> Resolved(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        if ((JsonNode.Parse(json: text) is not JsonObject root) || (root["dependencies"] is not JsonObject frameworks)) { yield break; }

        foreach (var (_, frameworkNode) in frameworks) {
            if (frameworkNode is not JsonObject packages) { continue; }

            foreach (var (id, node) in packages) {
                if ((node is not JsonObject entry) || (StringValue(node: entry["type"]) is null or "Project") || (StringValue(node: entry["resolved"]) is not { } resolved)) { continue; }
                yield return (id, entry, resolved);
            }
        }
    }
    /// <summary>Finds the version of each package the lock files pin, the lowest where they differ: a project that gains the package compiles against no newer assembly than a dependent deploys.</summary>
    /// <param name="texts">The text of each lock file.</param>
    /// <returns>Each package id's entry (its version, contentHash and dependency list) at the lowest pinned version.</returns>
    public static Dictionary<string, JsonObject> Collect(IEnumerable<string> texts) {
        ArgumentNullException.ThrowIfNull(argument: texts);

        var pinned = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);

        foreach (var (id, entry, resolved) in texts.SelectMany(selector: Resolved)) {
            if (!pinned.TryGetValue(key: id, value: out var known) || (Compare(left: resolved, right: StringValue(node: known["resolved"])!) < 0)) { pinned[id] = entry; }
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
        if ((JsonNode.Parse(json: current) as JsonObject)?["dependencies"] is not JsonObject restoredFrameworks) { return null; }

        var held = false;

        foreach (var (framework, newNode) in newFrameworks) {
            if (newNode is not JsonObject newPackages) { continue; }

            var oldPackages = (oldFrameworks[framework] as JsonObject);
            var heldIds = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var (id, newEntry) in newPackages) {
                if (newEntry is not JsonObject entry) { continue; }

                var type = StringValue(node: entry["type"]);

                // A project entry carries no version. A pin follows a package the project still asks for in the same role
                // and range (a changed request is the reference change itself), or a package the file did not list.
                if (type is null or "Project") { continue; }

                JsonObject? source = null;

                if (oldPackages?[id] is JsonObject old) {
                    if ((type == StringValue(node: old["type"])) && (StringValue(node: entry["requested"]) == StringValue(node: old["requested"]))) { source = old; }
                } else if ((solution is not null) && solution.TryGetValue(key: id, value: out var pinned) && ((type != "Direct") || Admits(range: StringValue(node: entry["requested"]), version: StringValue(node: pinned["resolved"])))) {
                    source = pinned;
                }
                if ((source is null) || (StringValue(node: entry["resolved"]) == StringValue(node: source["resolved"]))) { continue; }

                Pin(entry: entry, source: source);
                _ = heldIds.Add(item: id);
            }
            ReleaseUnmet(heldIds: heldIds, packages: newPackages, restored: (restoredFrameworks[framework] as JsonObject), solution: solution);
            foreach (var (id, node) in newPackages) {
                if (!heldIds.Contains(item: id)) { continue; }
                packages.Add(item: $"{id} {StringValue(node: node!["resolved"])} (restored from {StringValue(node: (restoredFrameworks[framework] as JsonObject)?[id]?["resolved"])})");
                held = true;
            }
        }

        if (!held) { return null; }
        if (JsonNode.DeepEquals(node1: after, node2: JsonNode.Parse(json: previous))) { return previous; }

        var newline = (current.Contains(value: "\r\n", comparisonType: StringComparison.Ordinal) ? "\r\n" : "\n");
        var text = after.ToJsonString(options: Format).ReplaceLineEndings(replacementText: newline);

        return (current.EndsWith(value: '\n') ? (text + newline) : text);
    }

    // Writes a pinned entry's version, contentHash and dependency list over a restored entry.
    private static void Pin(JsonObject entry, JsonObject source) {
        entry["resolved"] = source["resolved"]?.DeepClone();
        entry["contentHash"] = source["contentHash"]?.DeepClone();
        _ = entry.Remove(propertyName: "dependencies");
        if (source["dependencies"] is { } dependencies) { entry["dependencies"] = dependencies.DeepClone(); }
    }
    // A held pin stands only while every entry in the framework that depends on the package admits it. A locked restore
    // checks a lock file's direct requests and project closure, never a transitive package against the minimum another
    // entry declares, so a pin held below such a minimum restores cleanly and fails the compile instead (CS1705: an
    // assembly built against a newer version than the one referenced). Each pin below a declared minimum moves to the
    // solution's pin when that meets it, else back to what the restore resolved, which NuGet chose to meet every minimum;
    // either move can change the entry's own dependency list, so the check repeats until nothing moves.
    private static void ReleaseUnmet(HashSet<string> heldIds, JsonObject packages, JsonObject? restored, IReadOnlyDictionary<string, JsonObject>? solution) {
        for (var moved = true; moved;) {
            moved = false;
            foreach (var (_, node) in packages) {
                if ((node as JsonObject)?["dependencies"] is not JsonObject dependencies) { continue; }

                foreach (var (id, specification) in dependencies) {
                    if (!heldIds.Contains(item: id) || (packages[id] is not JsonObject entry) || (Minimum(specification: StringValue(node: specification)) is not { } minimum)) { continue; }
                    if (Meets(minimum: minimum, version: StringValue(node: entry["resolved"]))) { continue; }

                    if ((solution is not null) && solution.TryGetValue(key: id, value: out var pinned) && Meets(minimum: minimum, version: StringValue(node: pinned["resolved"]))) {
                        Pin(entry: entry, source: pinned);
                    } else if (restored?[id] is JsonObject resolved) {
                        Pin(entry: entry, source: resolved);
                        _ = heldIds.Remove(item: id);
                    } else {
                        continue;
                    }
                    moved = true;
                }
            }
        }
    }
    // The lower bound of a dependency as a lock file spells it: a bare version (NuGet's minimum) or a range whose lower
    // bound may float; an exclusive bound is marked so the version itself does not meet it.
    private static (string Version, bool Exclusive)? Minimum(string? specification) {
        if (string.IsNullOrWhiteSpace(value: specification)) { return null; }
        if ((specification[0] != '[') && (specification[0] != '(')) { return (specification.Trim(), false); }

        var lower = specification[1..].Split(separator: ',')[0].Trim().TrimEnd(']', ')').Replace(oldValue: "*", newValue: "0", comparisonType: StringComparison.Ordinal);

        return ((lower.Length == 0) ? null : (lower, (specification[0] == '(')));
    }
    private static bool Meets((string Version, bool Exclusive) minimum, string? version) {
        if (version is null) { return false; }

        var order = Compare(left: version, right: minimum.Version);

        return ((order > 0) || ((order == 0) && !minimum.Exclusive));
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
    // Orders two versions by DependencyVersion's order; a spelling that is not a version counts as 0.
    private static int Compare(string left, string right) {
        var zero = DependencyVersion.Parse(text: "0");

        return (DependencyVersion.TryParse(text: left, version: out var first) ? first : zero).CompareTo(other: (DependencyVersion.TryParse(text: right, version: out var second) ? second : zero));
    }
}
