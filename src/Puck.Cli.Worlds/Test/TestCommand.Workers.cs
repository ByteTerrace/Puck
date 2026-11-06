using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Confinement;

namespace Puck.Cli.Test;

public static partial class TestCommand {
    // The files that record a leg for comparison: each authority's per-tick hashes, each authority's delivered snapshot
    // entries hashed per tick, the instance host's crossing log, and the schedule's exports and manifest. A recording
    // under --record holds exactly these.
    private const string AuthoritiesFile = "reproduction.json";
    private const string SnapshotsFile = "snapshots.json";
    private const string CrossingsFile = "crossings.json";
    private const string ExportsDirectory = "out";

    /// <summary>One authored host run of every scenario: its directory under each world's, and the worker count its
    /// World boots with (<see langword="null"/> passes no <c>--workers</c>).</summary>
    private sealed record TestLeg(string Name, int? Workers) {
        public string Label => ((Workers is { } count) ? $"workers {count.ToString(provider: CultureInfo.InvariantCulture)}" : Name);
    }

    // The legs one run boots: one per --workers count, or the one ordinary leg.
    private static bool TryParseLegs(string? workers, out IReadOnlyList<TestLeg> legs, out string? reason) {
        reason = null;
        legs = [new TestLeg(Name: "run1", Workers: null)];
        if (workers is null) { return true; }
        var counts = new List<int>();

        foreach (var token in workers.Split(separator: ',', options: (StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))) {
            if (!int.TryParse(s: token, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out var count)) {
                reason = $"--workers '{workers}' is not a comma-separated list of worker counts (0 is the machine's logical processor count).";
                return false;
            }
            if (counts.Contains(item: count)) {
                reason = $"--workers '{workers}' names {count} twice.";
                return false;
            }
            counts.Add(item: count);
        }
        if (counts.Count == 0) {
            reason = "--workers names no worker count.";
            return false;
        }
        legs = [.. counts.Select(selector: static count => new TestLeg(Name: $"workers-{count.ToString(provider: CultureInfo.InvariantCulture)}", Workers: count))];
        return true;
    }

    // Requires every leg after the first to have finished and to match the first, and the first to match the recording
    // under `compare`; then records the first under `record`. Returns 0, 1 for a difference (a judged divergence), or 2
    // for a leg that did not finish or a missing recording, each named on `error`.
    private static int CompareAndRecord(TestWorld test, string worldDirectory, IReadOnlyList<TestLeg> legs, string? compare, string? record, TextWriter output, TextWriter error) {
        var first = Path.Combine(path1: worldDirectory, path2: legs[0].Name);
        var refused = false;
        var diverged = false;

        foreach (var leg in legs.Skip(count: 1)) {
            var directory = Path.Combine(path1: worldDirectory, path2: leg.Name);

            if (!TryReadBatchLeg(error: error, legDirectory: directory, reading: out _, world: test.Path)) {
                refused = true;
                continue;
            }
            diverged |= Report(differences: CompareLegs(actual: directory, expected: first), error: error, output: output,
                subject: $"{leg.Label} against {legs[0].Label}");
        }
        if (compare is { }) {
            var recorded = Path.Combine(path1: Path.GetFullPath(path: compare), path2: test.Name);

            if (!File.Exists(path: Path.Combine(path1: recorded, path2: AuthoritiesFile))) {
                error.WriteLine(value: $"  REFUSED: no recording of {test.Name} under {recorded}; record one with --record.");
                refused = true;
            } else {
                diverged |= Report(differences: CompareLegs(actual: first, expected: recorded), error: error, output: output,
                    subject: $"{legs[0].Label} against the recording {recorded}");
            }
        }
        if ((record is { }) && !refused && !diverged) {
            var destination = Path.Combine(path1: Path.GetFullPath(path: record), path2: test.Name);

            ConfinedDirectory.RemoveTree(path: destination);
            CopyRecording(destination: destination, source: first);
            output.WriteLine(value: $"  recorded {legs[0].Label} -> {destination.Replace(newChar: '/', oldChar: '\\')}");
        }
        return (refused ? 2 : (diverged ? 1 : 0));
    }
    private static bool Report(IReadOnlyList<string> differences, string subject, TextWriter output, TextWriter error) {
        if (differences.Count == 0) {
            output.WriteLine(value: $"  agree: {subject} (authority hashes, snapshot entries, exports, crossing log)");
            return false;
        }
        error.WriteLine(value: $"  DIVERGED: {subject}");
        foreach (var difference in differences) { error.WriteLine(value: $"    {difference}"); }
        return true;
    }
    private static void CopyRecording(string source, string destination) {
        _ = Directory.CreateDirectory(path: Path.Combine(path1: destination, path2: ExportsDirectory));
        foreach (var file in (string[])[AuthoritiesFile, SnapshotsFile, CrossingsFile]) {
            File.Copy(destFileName: Path.Combine(path1: destination, path2: file), sourceFileName: Path.Combine(path1: source, path2: file));
        }
        foreach (var file in Directory.EnumerateFiles(path: Path.Combine(path1: source, path2: ExportsDirectory))) {
            File.Copy(destFileName: Path.Combine(path1: destination, path2: ExportsDirectory, path3: Path.GetFileName(path: file)), sourceFileName: file);
        }
    }

    // Every way a recorded leg differs from the expected one, each naming the first point of difference: the
    // authority and recorded tick where the per-tick hashes part, the export file and JSON path, the crossing entry.
    private static IReadOnlyList<string> CompareLegs(string expected, string actual) {
        var differences = new List<string>();

        CompareAuthorities(actual: ReadArray(path: Path.Combine(path1: actual, path2: AuthoritiesFile)), differences: differences,
            expected: ReadArray(path: Path.Combine(path1: expected, path2: AuthoritiesFile)), file: AuthoritiesFile, subject: "the state hash");
        CompareAuthorities(actual: ReadArray(path: Path.Combine(path1: actual, path2: SnapshotsFile)), differences: differences,
            expected: ReadArray(path: Path.Combine(path1: expected, path2: SnapshotsFile)), file: SnapshotsFile, subject: "the delivered snapshot's entries");
        CompareExports(actual: Path.Combine(path1: actual, path2: ExportsDirectory), differences: differences,
            expected: Path.Combine(path1: expected, path2: ExportsDirectory));
        CompareCrossings(actual: ReadArray(path: Path.Combine(path1: actual, path2: CrossingsFile)), differences: differences,
            expected: ReadArray(path: Path.Combine(path1: expected, path2: CrossingsFile)));
        return differences;
    }
    private static JsonArray? ReadArray(string path) => (File.Exists(path: path) ? (JsonNode.Parse(json: File.ReadAllText(path: path)) as JsonArray) : null);
    // Compares one file of per-authority, per-tick hashes: the state hashes, or the delivered snapshot entries' hashes.
    private static void CompareAuthorities(JsonArray? expected, JsonArray? actual, List<string> differences, string file, string subject) {
        if ((expected is null) || (actual is null)) {
            differences.Add(item: $"{file} is missing from the {((expected is null) ? "expected" : "actual")} leg.");
            return;
        }
        static string Name(JsonNode? row) => (row?["world"]?.GetValue<string>() ?? "?");
        var expectedNames = expected.Select(selector: Name).ToArray();
        var actualNames = actual.Select(selector: Name).ToArray();

        if (!expectedNames.SequenceEqual(second: actualNames)) {
            differences.Add(item: $"{file}: the authorities differ: [{string.Join(separator: ", ", values: expectedNames)}] against [{string.Join(separator: ", ", values: actualNames)}].");
        }
        foreach (var name in expectedNames.Intersect(second: actualNames)) {
            var left = Hashes(row: expected.First(predicate: row => (Name(row: row) == name)));
            var right = Hashes(row: actual.First(predicate: row => (Name(row: row) == name)));
            var shared = Math.Min(val1: left.Length, val2: right.Length);
            var tick = Enumerable.Range(start: 0, count: shared).FirstOrDefault(predicate: index => (left[index] != right[index]), defaultValue: -1);

            if (tick >= 0) {
                differences.Add(item: $"authority '{name}': {subject} differs first at recorded tick {tick} ({left[tick]:X16} against {right[tick]:X16}).");
            } else if (left.Length != right.Length) {
                differences.Add(item: $"authority '{name}': {subject}: {left.Length} recorded ticks against {right.Length}; the first {shared} agree.");
            }
        }
    }
    private static ulong[] Hashes(JsonNode? row) => [.. ((row?["hashes"] as JsonArray) ?? []).Select(selector: static hash => hash!.GetValue<ulong>())];
    private static void CompareExports(string expected, string actual, List<string> differences) {
        static string[] Files(string directory) => (Directory.Exists(path: directory)
            ? [.. Directory.EnumerateFiles(path: directory).Select(selector: static file => Path.GetFileName(path: file)!).Order(comparer: StringComparer.Ordinal)]
            : []);
        var expectedFiles = Files(directory: expected);
        var actualFiles = Files(directory: actual);

        if (!expectedFiles.SequenceEqual(second: actualFiles)) {
            differences.Add(item: $"the exports differ: [{string.Join(separator: ", ", values: expectedFiles)}] against [{string.Join(separator: ", ", values: actualFiles)}].");
        }
        foreach (var file in expectedFiles.Intersect(second: actualFiles)) {
            var left = File.ReadAllBytes(path: Path.Combine(path1: expected, path2: file));
            var right = File.ReadAllBytes(path: Path.Combine(path1: actual, path2: file));

            if (!left.AsSpan().SequenceEqual(other: right)) {
                differences.Add(item: $"{ExportsDirectory}/{file} differs at {Puck.World.Server.WorldJsonDifference.First(left: JsonNode.Parse(utf8Json: left), right: JsonNode.Parse(utf8Json: right))}.");
            }
        }
    }
    private static void CompareCrossings(JsonArray? expected, JsonArray? actual, List<string> differences) {
        if ((expected is null) || (actual is null)) {
            differences.Add(item: $"{CrossingsFile} is missing from the {((expected is null) ? "expected" : "actual")} leg.");
            return;
        }
        for (var index = 0; (index < Math.Max(val1: expected.Count, val2: actual.Count)); index++) {
            var left = ((index < expected.Count) ? expected[index] : null);
            var right = ((index < actual.Count) ? actual[index] : null);

            if (!JsonNode.DeepEquals(node1: left, node2: right)) {
                differences.Add(item: $"the crossing log differs first at entry {index}: {Crossing(entry: left)} against {Crossing(entry: right)}.");
                return;
            }
        }
    }
    private static string Crossing(JsonNode? entry) => ((entry is null) ? "no entry" : $"tick {entry["tick"]} {entry["text"]}");
}
