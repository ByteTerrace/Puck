using Puck.Cli.Sweep;
using Puck.Confinement;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <c>puck sweep</c>'s pure parts. The shipped plan parses and a malformed one is refused by name;
/// each parser turns a World's answer into the metrics its rules read; a rule compares numbers, text or another metric;
/// a baseline names exactly the metrics that moved beyond their compare mode; and a report's colour is its worst.
/// </summary>
public sealed class SweepLawTests {
    private static string Refusal(string json) {
        Assert.False(condition: SweepPlan.TryParse(error: out var error, json: json, plan: out _));

        return error;
    }

    [Fact]
    public void TheShippedPlanParses() {
        var root = AppContext.BaseDirectory;

        while (!File.Exists(path: Path.Combine(path1: root, path2: "Puck.slnx"))) {
            root = Path.GetDirectoryName(path: root)!;
        }

        Assert.True(condition: SweepPlan.TryParse(error: out var error, json: File.ReadAllText(path: Path.Combine(path1: root, path2: SweepCommand.DefaultPlan)), plan: out var plan), userMessage: error);
        Assert.NotEmpty(collection: plan.Tools);
        Assert.Contains(collection: plan.Signals, filter: static signal => (signal.Name == "watchdog"));
        Assert.Contains(collection: plan.Tour.Stops, filter: static stop => (stop.Group == "findings"));
    }
    [Fact]
    public void AMalformedPlanIsRefusedByName() {
        Assert.Contains(expectedSubstring: "two signals are named 'a'", actualString: Refusal(json: """{ "signals": [ { "name": "a", "line": "x" }, { "name": "a", "line": "y" } ] }"""));
        Assert.Contains(expectedSubstring: "parses as 'csv'", actualString: Refusal(json: """{ "signals": [ { "name": "a", "line": "x", "parse": "csv" } ] }"""));
        Assert.Contains(expectedSubstring: "names the leg 'gpu'", actualString: Refusal(json: """{ "signals": [ { "name": "a", "line": "x", "leg": "gpu" } ] }"""));
        Assert.Contains(expectedSubstring: "the rule 'count over 3'", actualString: Refusal(json: """{ "signals": [ { "name": "a", "line": "x", "red": ["count over 3"] } ] }"""));
        Assert.Contains(expectedSubstring: "names itself", actualString: Refusal(json: """{ "signals": [ { "name": "", "line": "x" } ] }"""));
        Assert.Contains(expectedSubstring: "two tour stops are named 's'", actualString: Refusal(json: """{ "tour": { "stops": [ { "name": "s", "lines": [] }, { "name": "s", "lines": [] } ] } }"""));
        Assert.Contains(expectedSubstring: "a file name", actualString: Refusal(json: """{ "tour": { "stops": [ { "name": "a/b", "lines": [] } ] } }"""));
        Assert.Contains(expectedSubstring: "invalid expectedMotion", actualString: Refusal(json: """{ "tour": { "stops": [ { "name": "s", "lines": [], "expectedMotion": "[" } ] } }"""));
        Assert.Contains(expectedSubstring: "no plan", actualString: Refusal(json: "null"));
        Assert.NotEmpty(collection: Refusal(json: "{ \"signals\": 3 }"));
    }
    [Fact]
    public void EachParserReadsTheAnswersTheWorldGives() {
        var cells = SweepSignals.Read(extra: null, output: "[world.state.row 'w' kind=Int cells=2/4096 domain=keys]\n[world.state.cell 'w'.'count' value=2]\n[world.state.cell 'w'.'last' value='t=5 below-floor']", parse: "cells");

        Assert.Equal(expected: "2", actual: cells["count"]);
        Assert.Equal(expected: "t=5 below-floor", actual: cells["last"]);

        var counters = SweepSignals.Read(extra: null, output: "[world.counters: state.arena\nstate.arena.visits 332017\nstate.rules.firings 21155\n]", parse: "counters");

        Assert.Equal(expected: "332017", actual: counters["state.arena.visits"]);
        Assert.Equal(expected: "21155", actual: counters["state.rules.firings"]);
        var counterJson = SweepSignals.Read(extra: null, output: """{"sources":[{"name":"state","counts":{"state.arena.visits":332017,"state.rules.firings":21155}}]}""", parse: "counters");
        Assert.Equal(expected: counters["state.arena.visits"], actual: counterJson["state.arena.visits"]);
        Assert.Equal(expected: counters["state.rules.firings"], actual: counterJson["state.rules.firings"]);
        var narratedCounters = SweepSignals.Read(extra: null, output: "[world.counters: " + counterJson["text"] + "]", parse: "counters");
        Assert.Equal(expected: counterJson["state.arena.visits"], actual: narratedCounters["state.arena.visits"]);
        Assert.Equal(expected: counterJson["state.rules.firings"], actual: narratedCounters["state.rules.firings"]);

        var frames = SweepSignals.Read(extra: null, output: "[host.frames: 4 iterations | avg=7.92 p50=5.47 p99=20.74 max=20.74 ms | over 16.7 ms: 1 | busiest]\n  simulate: avg=6.60 p50=5.45 p99=20.72 max=20.72 ms", parse: "frames");

        Assert.Equal(expected: "20.74", actual: frames["p99"]);
        Assert.Equal(expected: "1", actual: frames["over"]);
        Assert.Equal(expected: "5.45", actual: frames["simulate.p50"]);

        var pairs = SweepSignals.Read(extra: null, output: "[host.memory: process ws=354 private=232 MiB | managed heap=129 gen0=2]", parse: "pairs");

        Assert.Equal(expected: "354", actual: pairs["ws"]);
        Assert.Equal(expected: "129", actual: pairs["heap"]);

        var words = SweepSignals.Read(extra: null, output: "chess-demo status 4 tick 3330/3330 inputs 37", parse: "words");

        Assert.Equal(expected: "4", actual: words["status"]);
        Assert.Equal(expected: "37", actual: words["inputs"]);

        var report = SweepSignals.Read(extra: null, output: """{ "tick": 69, "findings": [ { "severity": "red" }, { "severity": "red" }, { "severity": "unknown" }, {} ], "coverage": { "tested": 9, "eligible": 9, "complete": false, "skipped": { "a": 2, "b": 1 }, "comparisons": { "mesh": { "red": 2, "clean": 7 } } } }""", parse: "report");

        Assert.Equal(expected: "2", actual: report["findings.red"]);
        Assert.Equal(expected: "1", actual: report["findings.none"]);
        Assert.Equal(expected: "false", actual: report["coverage.complete"]);
        Assert.Equal(expected: "3", actual: report["coverage.skipped"]);
        Assert.Equal(expected: "7", actual: report["comparisons.mesh.clean"]);
        Assert.Contains(expectedSubstring: "not a JSON report", actualString: SweepSignals.Read(extra: null, output: "[truncated", parse: "report")["error"]);

        var text = SweepSignals.Read(extra: new Dictionary<string, string> { ["rules"] = @"rules (\d+),", ["absent"] = @"nothing (\d+)" }, output: " rules 735, worst 9 ", parse: "text");

        Assert.Equal(expected: "735", actual: text["rules"]);
        Assert.Equal(expected: "rules 735, worst 9", actual: text["text"]);
        Assert.False(condition: text.ContainsKey(key: "absent"));

        var sums = SweepSignals.Read(extra: new Dictionary<string, string> { ["dropped"] = @"dropped:(?<sum>\d+)", ["none"] = @"gone:(?<sum>\d+)" }, output: "a dropped:0 b dropped:2", parse: "text");

        Assert.Equal(expected: "2", actual: sums["dropped"]);
        Assert.False(condition: sums.ContainsKey(key: "none"));
        Assert.Equal(expected: "0", actual: SweepSignals.Read(extra: null, output: "[world.state.row 'w' kind=Text cells=0/4096 domain=keys]", parse: "cells")["row.cells"]);
    }
    [Fact]
    public void APinnedStepIsAConsoleLineBeforeTheCapture() {
        Assert.Contains(expectedSubstring: "not pinnable", actualString: Refusal(json: """{ "tour": { "settle": 9, "stops": [ { "name": "s", "lines": [], "steps": [ { "at": 1, "run": "world.wait 3" } ] } ] } }"""));
        Assert.Contains(expectedSubstring: "after its capture", actualString: Refusal(json: """{ "tour": { "settle": 9, "stops": [ { "name": "s", "lines": [], "steps": [ { "at": 10, "run": "body.stop 0" } ] } ] } }"""));
    }
    [Fact]
    public void ARuleComparesNumbersTextAndOtherMetrics() {
        var metrics = new Dictionary<string, string> { ["work"] = "5", ["ceiling"] = "4", ["state"] = "Incomplete", ["text"] = "bakes: not settled" };

        bool? Holds(string text) {
            Assert.True(condition: SweepRule.TryParse(rule: out var rule, text: text));

            return rule.Holds(metrics: metrics);
        }

        Assert.True(condition: Holds(text: "work > ceiling"));
        Assert.True(condition: Holds(text: "work >= 5"));
        Assert.True(condition: Holds(text: "work <= 5"));
        Assert.False(condition: Holds(text: "work < 5"));
        Assert.True(condition: Holds(text: "work != 4"));
        Assert.False(condition: Holds(text: "work == 4"));
        Assert.True(condition: Holds(text: "state == incomplete"));
        Assert.False(condition: Holds(text: "state != incomplete"));
        Assert.Null(@object: Holds(text: "state > 3"));
        Assert.True(condition: Holds(text: "text ~ not settled"));
        Assert.Null(@object: Holds(text: "missing == 1"));
        Assert.False(condition: SweepRule.TryParse(rule: out _, text: "work"));
        Assert.Equal(expected: "work > 3", actual: new SweepRule(Metric: "work", Operator: ">", Value: "3").ToString());
    }
    [Fact]
    public void ABaselineNamesOnlyWhatMovedBeyondItsCompareMode() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-sweep-law-").FullName;

        try {
            var still = Path.Combine(path1: directory, path2: "tour", path3: "stage.png");

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: still)!);
            File.WriteAllBytes(bytes: [1], path: still);

            var signal = new SweepSignal(Compare: new Dictionary<string, string> { ["count"] = "exact", ["p50"] = "relative:0.2", ["counters.*"] = "exact", ["counters.noisy.*"] = "none" }, Line: "l", Name: "s");
            var previous = new SweepReport(
                Baseline: null,
                Commit: "c",
                Legs: [],
                Scenarios: new SweepScenarioResult(Command: "puck test", Reasons: [], Status: SweepStatus.Unknown, Worlds: new Dictionary<string, string>()),
                Schema: SweepReport.SchemaName,
                Seconds: 1,
                Signals: [new SweepSignalResult(Changes: [], File: "headless/s.txt", Leg: SweepLegs.Headless, Line: "l", Metrics: new Dictionary<string, string> { ["count"] = "1", ["p50"] = "10", ["counters.a"] = "3", ["counters.noisy.b"] = "1", ["counters.gone"] = "2" }, Name: "s", Reasons: [], Status: SweepStatus.Clean)],
                Started: DateTimeOffset.UnixEpoch,
                Tools: [new SweepToolResult(Name: "t", Reasons: [], Status: SweepStatus.Clean, Ticks: 3)],
                Tour: new SweepTourResult(AmberFraction: 0.02, LevelTolerance: 16, Sheets: new Dictionary<string, string> { ["outdoor"] = "tour/sheet-outdoor.png" }, Stills: [new SweepStillResult(Capture: "tick=1", ChangedFraction: 0.5, File: "tour/stage.png", Group: "outdoor", Name: "stage", Note: null, Reasons: ["x"], Status: SweepStatus.Amber)]),
                World: "worlds/debug"
            );
            var path = Path.Combine(path1: directory, path2: SweepReport.FileName);

            previous.Write(path: path);

            Assert.Equal(expected: SweepStatus.Unknown, actual: previous.Status);
            Assert.True(condition: previous.Failed);
            Assert.Contains(expectedSubstring: "sweep unmeasured: 0 red, 1 amber, 1 unmeasured, 2 clean", actualString: previous.Summary());
            Assert.True(condition: SweepBaseline.TryRead(baseline: out var baseline, error: out var error, path: path), userMessage: error);
            Assert.Equal(expected: Path.GetFullPath(path: still), actual: baseline!.StillFor(name: "stage"));
            Assert.Null(@object: baseline.StillFor(name: "garden"));

            var changes = baseline.Changes(leg: SweepLegs.Headless, metrics: new Dictionary<string, string> { ["count"] = "2", ["p50"] = "11", ["counters.a"] = "3", ["counters.noisy.b"] = "9", ["counters.new"] = "1" }, signal: signal, status: SweepStatus.Red);

            Assert.Equal(expected: ["clean -> red", "count 1 -> 2", "counters.gone gone (was 2)", "counters.new new (1)"], actual: changes);
            Assert.Equal(expected: ["new signal"], actual: baseline.Changes(leg: SweepLegs.Windowed, metrics: new Dictionary<string, string>(), signal: signal, status: SweepStatus.Clean));
            Assert.False(condition: SweepBaseline.TryRead(baseline: out _, error: out var refusal, path: still));
            Assert.Contains(expectedSubstring: "could not be read", actualString: refusal);

            File.WriteAllText(contents: """{ "schema": "other" }""", path: still);
            Assert.False(condition: SweepBaseline.TryRead(baseline: out _, error: out refusal, path: still));
            Assert.Contains(expectedSubstring: "is not a puck.sweep.v1 report", actualString: refusal);
        } finally {
            ConfinedDirectory.RemoveTree(path: directory);
        }
    }
    [Fact]
    public void AComparisonsRedIsNeverCarriedIntoTheNextComparison() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-sweep-law-").FullName;

        try {
            var signal = new SweepSignal(Compare: new Dictionary<string, string> { ["count"] = "exact" }, Line: "l", Name: "s");
            var metrics = new Dictionary<string, string> { ["count"] = "2" };
            var compared = new SweepReport(
                Baseline: "earlier/report.json",
                Commit: "c",
                Legs: [],
                Scenarios: new SweepScenarioResult(Command: "puck test", Reasons: [], Status: SweepStatus.Clean, Worlds: new Dictionary<string, string>()),
                Schema: SweepReport.SchemaName,
                Seconds: 1,
                Signals: [new SweepSignalResult(Changes: ["count 1 -> 2"], Comparison: SweepStatus.Red, File: "headless/s.txt", Leg: SweepLegs.Headless, Line: "l", Metrics: metrics, Name: "s", Reasons: [], Status: SweepStatus.Clean)],
                Started: DateTimeOffset.UnixEpoch,
                Tools: [],
                Tour: null,
                World: "worlds/debug"
            );
            var path = Path.Combine(path1: directory, path2: SweepReport.FileName);

            compared.Write(path: path);

            // The run that compared is red, and the report it wrote still records the rules' clean.
            Assert.Equal(expected: SweepStatus.Red, actual: compared.Status);
            Assert.Contains(expectedSubstring: "\"status\": \"clean\"", actualString: File.ReadAllText(path: path));
            Assert.True(condition: SweepBaseline.TryRead(baseline: out var baseline, error: out var error, path: path), userMessage: error);

            // The next comparison, over the same metrics and the same clean rules, finds nothing changed.
            Assert.Empty(collection: baseline!.Changes(leg: SweepLegs.Headless, metrics: metrics, signal: signal, status: SweepStatus.Clean));
        } finally {
            ConfinedDirectory.RemoveTree(path: directory);
        }
    }
    [Fact]
    public void AReportsColourIsItsWorst() {
        Assert.Equal(expected: SweepStatus.Red, actual: SweepStatus.Worse(left: SweepStatus.Amber, right: SweepStatus.Red));
        Assert.Equal(expected: SweepStatus.Unknown, actual: SweepStatus.Worse(left: SweepStatus.Amber, right: SweepStatus.Unknown));
        Assert.True(condition: SweepStatus.Fails(status: SweepStatus.Unknown));
        Assert.True(condition: SweepStatus.Fails(status: SweepStatus.Red));
        Assert.False(condition: SweepStatus.Fails(status: SweepStatus.Amber));
        Assert.False(condition: SweepStatus.Fails(status: SweepStatus.Skipped));
        Assert.Equal(expected: SweepStatus.Clean, actual: SweepStatus.Worse(left: SweepStatus.Clean, right: SweepStatus.Clean));
        Assert.True(condition: new SweepSignal(Leg: SweepLegs.Windowed, Line: "l", Name: "s").ReadsOn(leg: SweepLegs.Windowed));
        Assert.False(condition: new SweepSignal(Leg: SweepLegs.Windowed, Line: "l", Name: "s").ReadsOn(leg: SweepLegs.Headless));
        Assert.True(condition: new SweepSignal(Line: "l", Name: "s").ReadsOn(leg: SweepLegs.Headless));
    }
}
