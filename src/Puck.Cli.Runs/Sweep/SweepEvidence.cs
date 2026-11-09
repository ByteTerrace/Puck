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
        var status = Judge(amber: signal.AmberRules, metrics: metrics, reasons: reasons, red: signal.RedRules, status: ((reasons.Count == 0) ? SweepStatus.Clean : SweepStatus.Unknown));

        return (status, reasons, metrics);
    }
    /// <summary>Colours metrics by a plan's rules: amber or red for each rule that holds, unmeasured for a rule whose
    /// metric the metrics lack, each with its reason added.</summary>
    /// <param name="metrics">The metrics, by name.</param>
    /// <param name="amber">The rules any one of which makes the reading amber.</param>
    /// <param name="red">The rules any one of which makes the reading red.</param>
    /// <param name="reasons">The reasons, which each rule that holds or cannot be read is added to.</param>
    /// <param name="status">The colour before the rules are read.</param>
    /// <returns>The worse of <paramref name="status"/> and every rule's colour.</returns>
    public static string Judge(IReadOnlyDictionary<string, string> metrics, IReadOnlyList<string> amber, IReadOnlyList<string> red, List<string> reasons, string status) {
        foreach (var (rules, when) in new[] { (amber, SweepStatus.Amber), (red, SweepStatus.Red) }) {
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

        return status;
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
    /// <summary>Reduces a <c>world.exposure</c> answer to the first view's reading: its EV100, what the meter read and
    /// which source answered.</summary>
    /// <param name="output">The answer.</param>
    /// <returns>The reading as <c>ev100=… meter=… source=…</c>, or <see langword="null"/> when the answer holds no view.</returns>
    public static string? ExposureReading(string output) {
        var match = System.Text.RegularExpressions.Regex.Match(input: output, pattern: @"view 0: ev100=(?<ev>\S+) multiplier=\S+ meter=(?<meter>.+?) source=(?<source>\S+)", options: System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeout: TimeSpan.FromSeconds(value: 2));

        return (match.Success ? $"ev100={match.Groups["ev"].Value} meter={match.Groups["meter"].Value} source={match.Groups["source"].Value}" : null);
    }
    /// <summary>Says whether a still that changed against its baseline was taken at another exposure, which moves every
    /// pixel of the image and is the lighting's history rather than the scene.</summary>
    /// <param name="before">The baseline still's reading, or <see langword="null"/>.</param>
    /// <param name="after">This still's reading, or <see langword="null"/>.</param>
    /// <returns>The note for the still's reasons, or <see langword="null"/> when either reading is missing.</returns>
    public static string? ExposureNote(string? before, string? after) => (((before is null) || (after is null))
        ? null
        : ((before == after) ? $"exposure unchanged ({after}): the difference is in the scene" : $"exposure differs: {before} against {after}"));
    /// <summary>Says whether every expected tick and command has an outcome in a world.at log. A line pinned to a tick
    /// runs in the drain after that tick completes, and a simulation line answers only when its own tick applies, so a
    /// wait that releases on the tick can read the line still pending; a reader asks again until this holds.</summary>
    /// <param name="log">The world.at --json answer.</param>
    /// <param name="expected">The ticks and commands the stop pinned.</param>
    /// <returns><see langword="false"/> while any expected line is armed or ran without an answer yet; <see langword="true"/>
    /// otherwise, including for a log that is not JSON, which <see cref="Scheduled"/> then reports.</returns>
    public static bool Settled(string log, IReadOnlyList<(ulong Tick, string Command)> expected) {
        try {
            using var document = JsonDocument.Parse(log);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) { return true; }
            foreach (var group in new[] { "pending", "ran" }) {
                if (!root.TryGetProperty(propertyName: group, value: out var rows) || (rows.ValueKind != JsonValueKind.Array)) { continue; }
                foreach (var row in rows.EnumerateArray()) {
                    if (row.TryGetProperty(propertyName: "status", value: out var status) && (status.GetString() == "pending")
                        && row.TryGetProperty(propertyName: "tick", value: out var tick) && tick.TryGetUInt64(value: out var value)
                        && row.TryGetProperty(propertyName: "command", value: out var command)
                        && expected.Contains(value: (value, command.GetString()!))) {
                        return false;
                    }
                }
            }
            return true;
        } catch (JsonException) {
            return true;
        }
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
