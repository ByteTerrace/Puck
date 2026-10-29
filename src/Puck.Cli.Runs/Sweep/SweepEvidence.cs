using System.Text.Json;
using Puck.Hosting;

namespace Puck.Cli.Sweep;

/// <summary>The single evidence boundary for immediate commands, signals and tick-scheduled commands.</summary>
public static class SweepEvidence {
    /// <summary>Judges an answer before any caller may call it clean. Null means the expected answer is missing.</summary>
    public static (string Status, List<string> Reasons, SortedDictionary<string, string> Metrics) Check(SweepAnswer? answer, SweepSignal? signal = null) {
        var reasons = new List<string>();
        var metrics = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        if ((answer is null) || string.IsNullOrWhiteSpace(value: answer.Output)) {
            reasons.Add(item: "unmeasured: no answer");
        }
        if (answer is { Ok: false }) {
            reasons.AddRange(collection: answer.Refusals);
            if (answer.Refusals.Count == 0) {
                reasons.Add(item: $"refused: {answer.Output}");
            }
            return ((answer.Unmeasured ? SweepStatus.Unknown : SweepStatus.Red), reasons, metrics);
        }
        if (reasons.Count > 0) {
            return (SweepStatus.Unknown, reasons, metrics);
        }
        if (signal is null) {
            return (SweepStatus.Clean, reasons, metrics);
        }
        metrics = SweepSignals.Read(signal.Parse, answer!.Output, signal.Metrics);
        if (metrics.TryGetValue(key: "error", value: out var error)) {
            reasons.Add(item: error);
            return (SweepStatus.Unknown, reasons, metrics);
        }
        if ((signal.Parse != "text") && (SweepSignals.Read(signal.Parse, answer.Output, null).Count == 1)) {
            reasons.Add(item: $"unmeasured: the answer held no {signal.Parse} metric");
        }
        if (((signal.Parse == "cells") && !metrics.ContainsKey(key: "row.cells")) || ((signal.Parse == "frames") && !metrics.ContainsKey(key: "iterations"))) {
            reasons.Add(item: $"unmeasured: the answer lacks its {signal.Parse} header");
        }
        // Explicitly requested metrics must exist even when no colour rule happens to read them.
        foreach (var name in (signal.Metrics?.Keys ?? []).Concat(second: (signal.Compare ?? new Dictionary<string, string>()).Where(predicate: entry => (!entry.Key.Contains(value: '*') && (entry.Value != "none"))).Select(selector: entry => entry.Key)).Distinct()) {
            if (!metrics.ContainsKey(key: name)) {
                reasons.Add(item: $"unmeasured: the answer lacks {name}");
            }
        }
        var status = ((reasons.Count == 0) ? SweepStatus.Clean : SweepStatus.Unknown);

        foreach (var (rules, when) in new[] { (signal.AmberRules, SweepStatus.Amber), (signal.RedRules, SweepStatus.Red) }) {
            foreach (var text in rules) {
                if (!SweepRule.TryParse(rule: out var rule, text: text) || (rule.Holds(metrics: metrics) is not { } holds)) {
                    reasons.Add(item: $"unmeasured: cannot read rule '{text}'");
                    status = SweepStatus.Worse(left: status, right: SweepStatus.Unknown);
                } else if (holds) {
                    reasons.Add(item: $"{when}: {rule}");
                    status = SweepStatus.Worse(left: status, right: when);
                }
            }
        }
        return (status, reasons, metrics);
    }
    /// <summary>Accounts for every submitted script line, including intermediate answers and transport truncation.</summary>
    public static SweepAnswer FromResponse(string script, ControlResponse response) {
        var expected = script.Split('\n').Select(selector: (line, index) => (Line: (index + 1), Text: line.Trim()))
            .Where(predicate: line => ((line.Text.Length > 0) && !line.Text.StartsWith(value: '#'))).ToArray();
        var failures = new List<string>();
        var status = (response.IsError ? SweepStatus.Red : (response.Truncated ? SweepStatus.Unknown : SweepStatus.Clean));

        if (response.IsError || response.Truncated) {
            failures.Add(item: (response.Truncated ? "unmeasured: the answer was truncated" : $"refused: {response.Output}"));
        }
        foreach (var line in expected) {
            SweepAnswer? answer = null;

            if (response.Commands is { } commands) {
                var matches = commands.Where(predicate: command => ((command.Line == line.Line) && (command.Command.Trim() == line.Text))).ToArray();

                if (matches.Length == 1) {
                    var command = matches[0];

                    answer = Outcome(command.Status, command.Output, command.IsError, command.Truncated);
                }
            } else if (expected.Length == 1) {
                answer = Outcome(response.Status, response.Output, response.IsError, response.Truncated);
            }
            var evidence = Check(answer);

            status = SweepStatus.Worse(left: status, right: evidence.Status);
            failures.AddRange(collection: evidence.Reasons.Select(selector: reason => $"{line.Text}: {reason}"));
        }
        if (expected.Length == 0) {
            failures.Add(item: "unmeasured: no command was submitted");
            status = SweepStatus.Worse(left: status, right: SweepStatus.Unknown);
        }
        return new SweepAnswer(Ok: (failures.Count == 0), Output: ((response.Commands is { Length: > 0 } entries) ? entries[^1].Output : response.Output), Refusals: failures, Unmeasured: (status == SweepStatus.Unknown));
    }
    /// <summary>Matches each expected tick and command against its eventual world.at result, never its arm receipt.</summary>
    public static SweepAnswer Scheduled(SweepAnswer read, IReadOnlyList<(ulong Tick, string Command)> expected) {
        var initial = Check(read);
        var failures = new List<string>(collection: initial.Reasons);
        var status = initial.Status;

        try {
            using var document = JsonDocument.Parse(read.Output);
            var root = document.RootElement;

            foreach (var step in expected) {
                SweepAnswer? answer = null;

                if ((root.ValueKind == JsonValueKind.Object) && root.TryGetProperty(propertyName: "ran", value: out var ran) && (ran.ValueKind == JsonValueKind.Array)) {
                    var matches = ran.EnumerateArray().Where(predicate: row => (row.TryGetProperty(propertyName: "tick", value: out var tick) && tick.TryGetUInt64(value: out var value) && (value == step.Tick)
                        && row.TryGetProperty(propertyName: "command", value: out var command) && (command.GetString() == step.Command))).ToArray();

                    if ((matches.Length == 1) && matches[0].TryGetProperty(propertyName: "output", value: out var output) && (output.ValueKind == JsonValueKind.String)
                        && matches[0].TryGetProperty(propertyName: "status", value: out var outcome) && (outcome.ValueKind == JsonValueKind.String)) {
                        answer = Outcome(((outcome.GetString() == "ran") ? "completed" : outcome.GetString()!), output.GetString()!, (outcome.GetString() is "refused" or "faulted"), false);
                        if (step.Command.StartsWith(comparisonType: StringComparison.Ordinal, value: "view.reseed") && output.GetString()!.Contains(comparisonType: StringComparison.Ordinal, value: "restarted 0 registration(s)")) {
                            answer = new SweepAnswer(false, output.GetString()!, ["view.reseed restarted no registration"]);
                        }
                    }
                }
                var evidence = Check(answer);

                status = SweepStatus.Worse(left: status, right: evidence.Status);
                failures.AddRange(collection: evidence.Reasons.Select(selector: reason => $"tick {step.Tick} {step.Command}: {reason}"));
            }
        } catch (Exception exception) when ((exception is JsonException or InvalidOperationException)) {
            status = SweepStatus.Worse(left: status, right: SweepStatus.Unknown);
            failures.Add(item: "unmeasured: world.at did not return a JSON log");
        }
        return new SweepAnswer(Ok: (failures.Count == 0), Output: read.Output, Refusals: failures, Unmeasured: (status == SweepStatus.Unknown));
    }

    private static SweepAnswer Outcome(string status, string output, bool refused, bool truncated) => new(
        (!refused && !truncated && (status == "completed")), output,
        (refused ? [$"refused: {output}"] : (truncated ? ["unmeasured: the answer was truncated"] : ((status != "completed") ? [$"unmeasured: outcome {status}"] : []))), Unmeasured: !refused);
}
