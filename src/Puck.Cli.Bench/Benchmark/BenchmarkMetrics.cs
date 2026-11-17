using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.Cli.Bench;

/// <summary>How a reading answers a change in the code or in the machine, which decides how a comparison reads it.</summary>
internal enum BenchmarkReadingKind {
    /// <summary>Counted per tick and exact: compared exactly on any machine under any load.</summary>
    Deterministic,
    /// <summary>Counted over rendered frames or iterations, so it is read with its denominator under like conditions.</summary>
    Cadence,
    /// <summary>A timing, or work that depends on residency and what the machine was doing: read in a quiet window.</summary>
    Load,
}

/// <summary>One number a chapter reports: its value, its unit and the kind of reading it is.</summary>
/// <param name="Value">The value.</param>
/// <param name="Unit">The unit: <c>ms</c>, <c>bytes</c>, <c>per second</c> or <c>count</c>.</param>
/// <param name="Kind">How a comparison reads it.</param>
internal readonly record struct BenchmarkMetric(double Value, string Unit, BenchmarkReadingKind Kind);

/// <summary>The numbers of a benchmark chapter by name: the flat set a report keeps its metrics under and the comparer
/// lines two runs up by.</summary>
internal static class BenchmarkMetrics {
    private static readonly string[] FrameCounts = ["iterations", "over", "overTwice", "over50", "over100", "steps", "gen0", "gen1", "gen2"];
    private static readonly string[] FrameTimes = ["seconds", "pacingDeviationMs", "pacingStepMs", "pauseTotalMs", "pauseLongestMs", "longestStallMs"];
    private static readonly string[] Distribution = ["mean", "p50", "p95", "p99", "p999", "max"];

    /// <summary>Names a chapter in a metric: its name lower-cased with every run of other characters one hyphen.</summary>
    /// <param name="name">The chapter's name.</param>
    /// <returns>The slug.</returns>
    public static string Slug(string name) {
        var slug = new StringBuilder();

        foreach (var character in name) {
            if (char.IsAsciiLetterOrDigit(c: character)) {
                _ = slug.Append(value: char.ToLowerInvariant(c: character));
            } else if ((slug.Length > 0) && (slug[^1] != '-')) {
                _ = slug.Append(value: '-');
            }
        }

        return slug.ToString().TrimEnd(trimChar: '-');
    }

    /// <summary>Reads every number a chapter's report holds as a named metric: the frame statistics, the hitch count, each
    /// GPU pass's mean and longest and the change of each streaming and counter reading across the chapter.</summary>
    /// <param name="chapter">The chapter's report.</param>
    /// <returns>The metrics by name, in ordinal name order. A chapter with no frame statistics holds none of the frame names.</returns>
    public static SortedDictionary<string, BenchmarkMetric> Of(JsonObject chapter) {
        var metrics = new SortedDictionary<string, BenchmarkMetric>(comparer: StringComparer.Ordinal);

        if (chapter["frames"] is JsonObject frames) {
            foreach (var name in FrameCounts) { Add(metrics, $"frames.{name}", frames[name], "count", BenchmarkReadingKind.Cadence); }
            foreach (var name in FrameTimes) { Add(metrics, $"frames.{name}", frames[name], "ms", BenchmarkReadingKind.Load); }
            Add(metrics, "frames.averageRate", frames["averageRate"], "per second", BenchmarkReadingKind.Load);
            Add(metrics, "frames.allocatedBytesPerIteration", frames["allocatedBytesPerIteration"], "bytes", BenchmarkReadingKind.Cadence);
            foreach (var group in new[] { "interval", "work" }) {
                foreach (var name in Distribution) { Add(metrics, $"frames.{group}.{name}", (frames[group] as JsonObject)?[name], "ms", BenchmarkReadingKind.Load); }
            }
        }
        Add(metrics, "hitches.count", (chapter["hitches"] as JsonObject)?["count"], "count", BenchmarkReadingKind.Load);
        if (chapter["gpu"] is JsonArray passes) {
            foreach (var pass in passes.OfType<JsonObject>()) {
                var name = PassName(pass: pass["pass"]?.GetValue<string>());

                Add(metrics, $"gpu.{name}.mean", pass["meanMs"], "ms", BenchmarkReadingKind.Load);
                Add(metrics, $"gpu.{name}.max", pass["maxMs"], "ms", BenchmarkReadingKind.Load);
            }
        }
        if (chapter["readings"] is JsonObject readings) {
            foreach (var (name, reading) in readings) {
                Add(metrics, $"reading.{name}", (reading as JsonObject)?["change"], "count", (name.StartsWith(value: "counters.", comparisonType: StringComparison.Ordinal) ? BenchmarkReadingKind.Cadence : BenchmarkReadingKind.Load));
            }
        }

        return metrics;
    }

    /// <summary>Names a GPU pass in a metric: the node's name and the pass with the slash between them written as a colon,
    /// so a metric name never holds a pointer's separator.</summary>
    /// <param name="pass">The pass as <c>world.gpu-timing</c> prints it.</param>
    /// <returns>The name.</returns>
    public static string PassName(string? pass) => (pass ?? string.Empty).Replace(oldChar: '/', newChar: ':');

    /// <summary>Whether a metric must be in both reports of a comparison: the frame statistics, the hitch count and the GPU
    /// passes. A reading of the streaming and counter set is compared where both have it and noted where one does not.</summary>
    /// <param name="name">The metric's name.</param>
    /// <returns><see langword="true"/> for a metric a comparison refuses to go on without.</returns>
    public static bool IsRequired(string name) => !name.StartsWith(value: "reading.", comparisonType: StringComparison.Ordinal);

    private static void Add(SortedDictionary<string, BenchmarkMetric> metrics, string name, JsonNode? value, string unit, BenchmarkReadingKind kind) {
        if (value is JsonValue number && number.TryGetValue<double>(value: out var parsed) && double.IsFinite(d: parsed)) {
            metrics[name] = new BenchmarkMetric(Kind: kind, Unit: unit, Value: parsed);
        }
    }

    /// <summary>Reads the change of every counter and streaming reading across a chapter from the raw answers taken at both
    /// ends of it. A reading whose answer is a refusal, or is absent at either end, has no entry.</summary>
    /// <param name="reads">The chapter's raw <c>reads</c>: each the console line, its answer at the start and its answer at the end.</param>
    /// <returns>Each reading by name with its start, end and change, and why any answer held none.</returns>
    public static (JsonObject Readings, JsonArray Unmeasured) Readings(JsonArray? reads) {
        var readings = new JsonObject();
        var unmeasured = new JsonArray();

        foreach (var read in (reads ?? []).OfType<JsonObject>()) {
            var line = (read["read"]?.GetValue<string>() ?? string.Empty);
            var (parse, group) = ParseOf(line: line);
            var before = ConsoleReadings.Values(parse: parse, answer: read["begin"]?.GetValue<string>(), note: out var beforeNote);
            var after = ConsoleReadings.Values(parse: parse, answer: read["end"]?.GetValue<string>(), note: out var afterNote);

            if ((beforeNote ?? afterNote) is { } note) {
                unmeasured.Add(item: $"{line}: {note}");

                continue;
            }

            var keys = ConsoleReadings.Keys.Where(predicate: key => (key.Read == group)).ToArray();

            foreach (var (name, end) in after) {
                var counted = ((parse == ReadingParse.Counters) || keys.Any(predicate: key => ((name == key.Key) || name.EndsWith(value: ("." + key.Key), comparisonType: StringComparison.Ordinal))));

                if (!counted || !before.TryGetValue(key: name, value: out var start) || !TryNumber(text: start, number: out var first) || !TryNumber(text: end, number: out var last)) {
                    continue;
                }

                var key = ((parse == ReadingParse.Counters) ? $"counters.{name}" : $"{group}.{name}");

                readings[key] = new JsonObject { ["begin"] = first, ["end"] = last, ["change"] = (last - first) };
            }
        }

        return (readings, unmeasured);
    }

    // The reading's parse and the group its keys are listed under, from the console line that answers it.
    private static (ReadingParse Parse, string Group) ParseOf(string line) {
        if (line.StartsWith(value: "world.counters ", comparisonType: StringComparison.Ordinal)) {
            return (ReadingParse.Counters, line.Split(separator: ' ')[1]);
        }

        return ((line == "world.residency") ? (ReadingParse.Residency, "residency") : (ReadingParse.Memory, "memory"));
    }

    private static bool TryNumber(string text, out double number) {
        var match = ConsoleReadings.Number().Match(input: text);

        number = 0.0;

        return (match.Success && double.TryParse(s: match.Value, style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out number));
    }
}
