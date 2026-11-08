using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Puck.Cli.Sweep;

/// <summary>Turns a read's answer into named metrics, by the parser a signal names.</summary>
public static partial class SweepSignals {
    /// <summary>The parser names a signal may give.</summary>
    public static readonly IReadOnlyList<string> Parsers = ["cells", "counters", "frames", "gpu-transfers", "pairs", "report", "text", "words"];

    /// <summary>The name of the regular-expression group a metric pattern uses to read the total of a group over every match.</summary>
    public const string SumGroup = "sum";

    /// <summary>Reads metrics from an answer.</summary>
    /// <param name="parse">The parser's name.</param>
    /// <param name="output">The answer's text.</param>
    /// <param name="extra">Extra metrics, each a regular expression whose first group is the value.</param>
    /// <returns>The metrics by name, in name order; a value that reads as a number is written in invariant form.</returns>
    public static SortedDictionary<string, string> Read(string parse, string output, IReadOnlyDictionary<string, string>? extra) {
        var metrics = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        switch (parse) {
            case "cells":
                // The row's own header is a reading even when the row holds no cell.
                if (RowPattern().Match(input: output) is { Success: true } row) {
                    metrics["row.cells"] = row.Groups["cells"].Value;
                }

                foreach (Match match in CellPattern().Matches(input: output)) {
                    metrics[match.Groups["key"].Value] = Unquote(value: match.Groups["value"].Value);
                }

                break;
            case "counters":
                // Typed control carries bare JSON; a scheduled console read narrates the same JSON in its echo.
                var counterOutput = output.Trim();
                const string CounterEcho = "[world.counters:";
                if (counterOutput.StartsWith(CounterEcho, StringComparison.Ordinal) && counterOutput.EndsWith(']')) {
                    counterOutput = counterOutput[CounterEcho.Length..^1].Trim();
                }
                if (counterOutput.StartsWith('{')) {
                    using var document = JsonDocument.Parse(counterOutput);
                    if (!document.RootElement.TryGetProperty("sources", out var sources)) { throw new JsonException("A counter JSON answer needs sources."); }
                    foreach (var source in sources.EnumerateArray()) {
                        foreach (var count in source.GetProperty("counts").EnumerateObject()) { metrics[count.Name] = count.Value.GetRawText(); }
                    }
                    break;
                }
                foreach (var line in output.Split(separator: '\n')) {
                    var match = CounterPattern().Match(input: line.Trim());

                    if (match.Success) {
                        metrics[match.Groups["name"].Value] = match.Groups["value"].Value;
                    }
                }

                break;
            case "gpu-transfers":
                ReadGpuTransfers(output, metrics);
                break;
            case "frames":
                ReadFrames(metrics: metrics, output: output);

                break;
            case "pairs":
                foreach (Match match in PairPattern().Matches(input: output)) {
                    metrics.TryAdd(key: match.Groups["key"].Value, value: match.Groups["value"].Value);
                }

                break;
            case "report":
                ReadReport(metrics: metrics, output: output);

                break;
            case "words":
                var words = output.Trim().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ');

                for (var index = 1; ((index + 1) < words.Length); index += 2) {
                    metrics[words[index]] = words[(index + 1)];
                }

                break;
        }

        metrics["text"] = output.Trim();

        foreach (var (name, pattern) in (extra ?? new Dictionary<string, string>())) {
            var expression = new Regex(options: RegexOptions.CultureInvariant, pattern: pattern, matchTimeout: TimeSpan.FromSeconds(value: 2));

            // A pattern with a group named sum reads the total of that group over every match: a count that is zero
            // in each place it is stated still gives a reading, 0.
            if (expression.GetGroupNames().Contains(value: SumGroup)) {
                var sum = 0d;
                var found = false;

                foreach (Match match in expression.Matches(input: output)) {
                    if (SweepRule.TryNumber(number: out var part, text: match.Groups[SumGroup].Value)) {
                        found = true;
                        sum += part;
                    }
                }

                if (found) {
                    metrics[name] = sum.ToString(provider: CultureInfo.InvariantCulture);
                }

                continue;
            }

            var first = expression.Match(input: output);

            if (first.Success && (first.Groups.Count > 1)) {
                metrics[name] = first.Groups[1].Value;
            }
        }

        return metrics;
    }

    // [host.frames: 4 iterations | avg=707.92 p50=591.47 p99=2023.74 max=2023.74 ms | over 16.7 ms: 4 | ...], then one
    // "  <phase>: avg=… p50=… p99=… max=… ms" line per phase.
    private static void ReadFrames(SortedDictionary<string, string> metrics, string output) {
        var lines = output.Split(separator: '\n');
        var header = FramesHeaderPattern().Match(input: lines[0]);

        if (header.Success) {
            metrics["iterations"] = header.Groups["iterations"].Value;
            metrics["avg"] = header.Groups["avg"].Value;
            metrics["p50"] = header.Groups["p50"].Value;
            metrics["p99"] = header.Groups["p99"].Value;
            metrics["max"] = header.Groups["max"].Value;
            metrics["over"] = header.Groups["over"].Value;
        }

        foreach (var line in lines.Skip(count: 1)) {
            var phase = FramesPhasePattern().Match(input: line);

            if (phase.Success) {
                var name = phase.Groups["phase"].Value;

                metrics[$"{name}.avg"] = phase.Groups["avg"].Value;
                metrics[$"{name}.p50"] = phase.Groups["p50"].Value;
                metrics[$"{name}.p99"] = phase.Groups["p99"].Value;
                metrics[$"{name}.max"] = phase.Groups["max"].Value;
            }
        }
    }
    // A world.debug-view report --json: the findings counted by severity, and the coverage the report states once.
    private static void ReadReport(SortedDictionary<string, string> metrics, string output) {
        try {
            using var document = JsonDocument.Parse(json: output);
            var root = document.RootElement;
            var severities = new SortedDictionary<string, int>(comparer: StringComparer.Ordinal);

            if ((root.ValueKind == JsonValueKind.Object) && root.TryGetProperty(propertyName: "findings", value: out var findings) && (findings.ValueKind == JsonValueKind.Array)) {
                foreach (var finding in findings.EnumerateArray()) {
                    var severity = (finding.TryGetProperty(propertyName: "severity", value: out var value) ? (value.GetString() ?? "none") : "none");

                    severities[severity] = (severities.GetValueOrDefault(key: severity) + 1);
                }

                metrics["findings"] = findings.GetArrayLength().ToString(provider: CultureInfo.InvariantCulture);
            }

            if (!metrics.ContainsKey(key: "findings")) {
                metrics["error"] = "unmeasured: the report lacks its findings array";
                return;
            }
            foreach (var severity in new[] { "red", "unknown" }) {
                metrics[$"findings.{severity}"] = severities.GetValueOrDefault(key: severity).ToString(provider: CultureInfo.InvariantCulture);
            }
            foreach (var (severity, count) in severities) {
                metrics[$"findings.{severity}"] = count.ToString(provider: CultureInfo.InvariantCulture);
            }

            if (root.TryGetProperty(propertyName: "tick", value: out var tick)) {
                metrics["tick"] = tick.GetRawText();
            }
            if (root.TryGetProperty(propertyName: "coverage", value: out var coverage) && (coverage.ValueKind == JsonValueKind.Object)) {
                foreach (var name in new[] { "tested", "eligible", "complete", "pending" }) {
                    if (coverage.TryGetProperty(propertyName: name, value: out var value)) {
                        metrics[$"coverage.{name}"] = value.GetRawText();
                    }
                }
                if (coverage.TryGetProperty(propertyName: "comparisons", value: out var comparisons) && (comparisons.ValueKind == JsonValueKind.Object)) {
                    foreach (var comparison in comparisons.EnumerateObject()) {
                        foreach (var count in comparison.Value.EnumerateObject()) {
                            metrics[$"comparisons.{comparison.Name}.{count.Name}"] = count.Value.GetRawText();
                        }
                    }
                }
                foreach (var name in new[] { "skipped", "excluded" }) {
                    if (coverage.TryGetProperty(propertyName: name, value: out var reasons) && (reasons.ValueKind == JsonValueKind.Object)) {
                        metrics[$"coverage.{name}"] = reasons.EnumerateObject().Sum(selector: static entry => (entry.Value.TryGetInt64(value: out var count) ? count : 0L)).ToString(provider: CultureInfo.InvariantCulture);
                    }
                }
            }
        } catch (Exception exception) when ((exception is JsonException or InvalidOperationException)) {
            metrics["error"] = $"the answer is not a JSON report: {exception.Message}";
        }
    }
    private static string Unquote(string value) => (((value.Length >= 2) && (value[0] == '\'') && (value[^1] == '\'')) ? value[1..^1] : value);
    [GeneratedRegex(pattern: @"\[world\.state\.row '[^']*' kind=\w+ cells=(?<cells>\d+)/[^\]\r\n]*\]", options: RegexOptions.CultureInvariant)]
    private static partial Regex RowPattern();
    [GeneratedRegex(pattern: @"\[world\.state\.cell '[^']*'\.'(?<key>[^']*)' value=(?<value>'[^']*'|[^\]]*)\]", options: RegexOptions.CultureInvariant)]
    private static partial Regex CellPattern();
    [GeneratedRegex(pattern: @"^(?<name>[A-Za-z][\w.-]*\.[\w.-]+)\s+(?<value>-?\d+(\.\d+)?)$", options: RegexOptions.CultureInvariant)]
    private static partial Regex CounterPattern();
    [GeneratedRegex(pattern: @"(?<![\w.-])(?<key>[A-Za-z][\w.-]*)[=:](?<value>-?\d+(\.\d+)?)(?![\w.])", options: RegexOptions.CultureInvariant)]
    private static partial Regex PairPattern();
    [GeneratedRegex(pattern: @"(?<iterations>\d+) iterations \| avg=(?<avg>[\d.]+) p50=(?<p50>[\d.]+) p99=(?<p99>[\d.]+) max=(?<max>[\d.]+) ms \| over [\d.]+ ms: (?<over>\d+)", options: RegexOptions.CultureInvariant)]
    private static partial Regex FramesHeaderPattern();
    [GeneratedRegex(pattern: @"^\s+(?<phase>[a-z+-]+): avg=(?<avg>[\d.]+) p50=(?<p50>[\d.]+) p99=(?<p99>[\d.]+) max=(?<max>[\d.]+) ms", options: RegexOptions.CultureInvariant)]
    private static partial Regex FramesPhasePattern();
}
/// <summary>One colouring rule, <c>&lt;metric&gt; &lt;op&gt; &lt;value&gt;</c>: the value is a number, another metric's
/// name, or text; <c>~</c> tests that the metric's text contains the value.</summary>
/// <param name="Metric">The metric the rule reads.</param>
/// <param name="Operator">The comparison.</param>
/// <param name="Value">The value, or the name of the metric it compares with.</param>
public sealed record SweepRule(string Metric, string Operator, string Value) {
    /// <summary>The operators a rule may use.</summary>
    public static readonly IReadOnlyList<string> Operators = ["==", "!=", ">", ">=", "<", "<=", "~"];

    /// <summary>Parses a rule.</summary>
    /// <param name="text">The rule's text.</param>
    /// <param name="rule">The rule.</param>
    /// <returns><see langword="true"/> when the text is a rule.</returns>
    public static bool TryParse(string text, out SweepRule rule) {
        var parts = text.Split(count: 3, options: StringSplitOptions.RemoveEmptyEntries, separator: ' ');

        rule = new SweepRule(Metric: string.Empty, Operator: string.Empty, Value: string.Empty);

        if ((parts.Length != 3) || !Operators.Contains(value: parts[1])) {
            return false;
        }

        rule = new SweepRule(Metric: parts[0], Operator: parts[1], Value: parts[2]);

        return true;
    }
    /// <summary>Evaluates the rule over a signal's metrics.</summary>
    /// <param name="metrics">The metrics.</param>
    /// <returns><see langword="true"/> when the rule holds, <see langword="false"/> when it does not, and
    /// <see langword="null"/> when the signal has no such metric.</returns>
    public bool? Holds(IReadOnlyDictionary<string, string> metrics) {
        if (!metrics.TryGetValue(key: Metric, value: out var left)) {
            return null;
        }

        var right = (metrics.TryGetValue(key: Value, value: out var named) ? named : Value);

        if (Operator == "~") {
            return left.Contains(comparisonType: StringComparison.Ordinal, value: right);
        }
        if (TryNumber(number: out var a, text: left) && TryNumber(number: out var b, text: right)) {
            return Operator switch {
                "==" => (a == b),
                "!=" => (a != b),
                ">" => (a > b),
                ">=" => (a >= b),
                "<" => (a < b),
                _ => (a <= b),
            };
        }

        return Operator switch {
            "==" => string.Equals(a: left, b: right, comparisonType: StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(a: left, b: right, comparisonType: StringComparison.OrdinalIgnoreCase),
            _ => null,
        };
    }
    /// <summary>Reads a metric's value as a number.</summary>
    /// <param name="text">The value.</param>
    /// <param name="number">The number.</param>
    /// <returns><see langword="true"/> when the value is an invariant-culture number.</returns>
    public static bool TryNumber(string text, out double number) => double.TryParse(provider: CultureInfo.InvariantCulture, result: out number, s: text, style: NumberStyles.Float);
    /// <inheritdoc/>
    public override string ToString() => $"{Metric} {Operator} {Value}";
}
