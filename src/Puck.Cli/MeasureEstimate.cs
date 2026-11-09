using System.Globalization;
using System.Text.Json;
using Puck.Cli.Bench;

namespace Puck.Cli;

/// <summary>Sizes a measurement window from the wall time earlier samples took, so a lead sees what a window will answer before it starts and while it runs.
/// The wall times only size windows; they are never a metric or an answer.</summary>
public static class MeasureEstimate {
    /// <summary>Reads the median wall seconds of one sample, per command, from every report the queue has written.</summary>
    /// <param name="queue">The measurement queue directory.</param>
    /// <returns>The median sample wall seconds for each command (its arguments joined by spaces); a command with no recorded sample is absent.</returns>
    public static Dictionary<string, double> History(string queue) {
        var walls = new Dictionary<string, List<double>>(comparer: StringComparer.Ordinal);

        if (Directory.Exists(path: queue)) {
            foreach (var request in Directory.EnumerateDirectories(path: queue)) {
                var answers = Path.Combine(path1: request, path2: "answers");

                if (!Directory.Exists(path: answers)) { continue; }
                foreach (var path in Directory.EnumerateDirectories(path: answers).Select(selector: answer => Path.Combine(path1: answer, path2: "report.json")).Where(predicate: File.Exists)) {
                    MeasurementReport? report;

                    try { report = JsonSerializer.Deserialize(File.ReadAllText(path: path), MeasureJsonContext.Default.MeasurementReport); } catch (Exception error) when (error is JsonException or IOException) { continue; }
                    foreach (var sample in (report?.Samples ?? []).Where(predicate: sample => (sample.WallSeconds > 0))) {
                        var key = Key(command: report!.Request.Command);

                        if (!walls.TryGetValue(key, out var list)) { walls[key] = (list = []); }
                        list.Add(item: sample.WallSeconds);
                    }
                }
            }
        }
        return walls.ToDictionary(keySelector: pair => pair.Key, elementSelector: pair => Median(values: pair.Value), comparer: StringComparer.Ordinal);
    }

    /// <summary>Describes what draining the requests is expected to cost, one line per request and a total.</summary>
    /// <param name="pending">The requests in drain order.</param>
    /// <param name="rounds">The paired rounds each request runs; every round is two samples.</param>
    /// <param name="timeoutSeconds">The per-sample bound, which is the ceiling shown for a command with no recorded sample.</param>
    /// <param name="settleSeconds">The idle settle before the first sample.</param>
    /// <param name="windowSeconds">The window deadline, which includes acquisition and settling.</param>
    /// <param name="history">The result of <see cref="History"/>.</param>
    /// <returns>The lines to print.</returns>
    public static IReadOnlyList<string> Lines(IReadOnlyList<MeasurementRequest> pending, int rounds, int timeoutSeconds, int settleSeconds, int windowSeconds, IReadOnlyDictionary<string, double> history) {
        var lines = new List<string>();
        var samples = (2 * rounds);
        var elapsed = ((double)settleSeconds);
        var sized = true;
        var known = 0.0;
        var ceiling = 0.0;
        var unsized = 0;

        foreach (var item in pending) {
            if (history.TryGetValue(Key(command: item.Command), out var each)) {
                var seconds = (each * samples);

                known += seconds;
                elapsed += seconds;
                lines.Add(item: string.Create(CultureInfo.InvariantCulture, $"measure: estimate {item.Id} {item.Name}: {Format(seconds: seconds)} for {samples} samples at {each:F0} s, the median of this command's earlier samples{(sized ? $"; done about {Format(seconds: elapsed)} into the window{((elapsed > windowSeconds) ? ", past its deadline" : "")}" : "")}"));
            } else {
                var most = (((double)timeoutSeconds) * samples);

                sized = false;
                unsized++;
                ceiling += most;
                lines.Add(item: string.Create(CultureInfo.InvariantCulture, $"measure: estimate {item.Id} {item.Name}: no earlier sample of this command to size it; at most {Format(seconds: most)} at --timeout-seconds {timeoutSeconds}"));
            }
        }
        lines.Add(item: string.Create(CultureInfo.InvariantCulture, $"measure: estimate total: {Format(seconds: known)} of sampling for {pending.Count - unsized} sized request(s){((unsized > 0) ? $" plus {unsized} unsized (at most {Format(seconds: ceiling)})" : "")}, after {settleSeconds} s of settling; the window deadline is {windowSeconds} s."));
        return lines;
    }

    /// <summary>Describes one completed pair and what the rest of its request is expected to take at that pace.</summary>
    /// <param name="name">The request's name.</param>
    /// <param name="round">The zero-based round that just finished.</param>
    /// <param name="rounds">The paired rounds the request runs.</param>
    /// <param name="pairs">The wall seconds of every completed pair of this request so far, the finished one last.</param>
    /// <param name="before">The before sample's wall seconds.</param>
    /// <param name="after">The after sample's wall seconds.</param>
    /// <returns>The line to print.</returns>
    public static string PairLine(string name, int round, int rounds, IReadOnlyList<double> pairs, double before, double after) {
        var left = (rounds - (round + 1));

        return string.Create(CultureInfo.InvariantCulture, $"measure {name} pair #{round + 1}/{rounds}: wall {pairs[^1]:F1} s (before {before:F1} s, after {after:F1} s); {left} pair(s) left, about {Format(seconds: (left * Median(values: pairs)))} at this pace.");
    }

    private static string Key(string[] command) => string.Join(separator: ' ', value: command);
    private static string Format(double seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds:F0} s ({(seconds / 60):F1} min)");
    private static double Median(IReadOnlyList<double> values) {
        var ordered = values.Order().ToArray();

        return BenchMetric.Quantile(fraction: .5, ordered: ordered);
    }
}
