using System.Text.Json;
using Puck.Cli.Sweep;
using Puck.Hosting;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>Every shipped signal and command form must cross the same evidence boundary.</summary>
public sealed class SweepEvidenceLawTests {
    private static SweepPlan Plan() {
        var root = AppContext.BaseDirectory;

        while (!File.Exists(path: Path.Combine(path1: root, path2: "Puck.slnx"))) {
            root = Path.GetDirectoryName(path: root)!;
        }
        Assert.True(condition: SweepPlan.TryParse(File.ReadAllText(path: Path.Combine(path1: root, path2: SweepCommand.DefaultPlan)), out var plan, out var error), userMessage: error);
        return plan;
    }

    [Fact]
    public void EveryShippedSignalAndRecheckFailsWithoutEvidence() {
        var plan = Plan();

        Assert.Equal(SweepSignals.Parsers.Order(), plan.Signals.Select(selector: signal => signal.Parse).Distinct().Order());
        foreach (var signal in plan.Signals.Concat(second: plan.Signals.Where(predicate: signal => signal.Recheck))) {
            foreach (var answer in new SweepAnswer?[] {
                null, new(Ok: true, Output: "", Refusals: []), new(Ok: true, Output: " \n", Refusals: []), new(Ok: false, Output: "refused", Refusals: ["refused"]),
                SweepEvidence.FromResponse(signal.Line, new ControlResponse(1, "completed", "partial answer", Truncated: true)),
            }) {
                var result = SweepEvidence.Check(answer: answer, signal: signal);

                Assert.True(condition: SweepStatus.Fails(status: result.Status), userMessage: $"{signal.Name} ({signal.Parse}): {answer}");
                Assert.NotEmpty(collection: result.Reasons);
            }
        }
        // A syntactically valid report with no findings is not a report measuring zero findings.
        var report = plan.Signals.First(predicate: signal => (signal.Parse == "report"));

        Assert.True(condition: SweepStatus.Fails(status: SweepEvidence.Check(answer: new(Ok: true, Output: "{}", Refusals: []), signal: report).Status));
        Assert.Equal(SweepStatus.Clean, SweepEvidence.Check(answer: new(Ok: true, Output: "[world.rule.failures: none]", Refusals: []), signal: plan.Signals.Single(predicate: signal => (signal.Name == "rule-failures"))).Status);
        // Neither a missing extra metric nor a missing numeric rule operand can turn into a colour alone.
        Assert.True(condition: SweepStatus.Fails(status: SweepEvidence.Check(answer: new(Ok: true, Output: "some text", Refusals: []), signal: new("extra", "read", Metrics: new Dictionary<string, string> { ["count"] = "count=(\\d+)" })).Status));
        Assert.True(condition: SweepStatus.Fails(status: SweepEvidence.Check(answer: new(Ok: true, Output: "count=2", Refusals: []), signal: new("rule", "read", "pairs", Red: ["count > ceiling"])).Status));
        Assert.Equal(SweepStatus.Unknown, SweepEvidence.Check(answer: new(true, "unparsed=2", []),
            signal: new("extra", "read", "counters", Metrics: new Dictionary<string, string> { ["extra"] = "unparsed=(\\d+)" })).Status);
        Assert.False(condition: SweepEvidence.Scheduled(new(true,
            """{"ran":[{"tick":50,"command":"view.reseed 0","status":"ran","output":"[view.reseed: body 0 restarted 0 registration(s)]"}]}""", []),
            [(50UL, "view.reseed 0")]).Ok);
    }
    [Fact]
    public void EveryShippedCommandFormNeedsEachImmediateAndScheduledAnswer() {
        var plan = Plan();
        var forms = new Dictionary<string, IEnumerable<string>> {
            ["tools"] = plan.Tools.SelectMany(selector: tool => tool.Lines),
            ["polls"] = plan.Tools.Where(predicate: tool => (tool.Until is not null)).Select(selector: tool => tool.Until!.Line),
            ["signals"] = plan.Signals.Select(selector: signal => signal.Line),
            ["rechecks"] = plan.Signals.Where(predicate: signal => signal.Recheck).Select(selector: signal => signal.Line),
            ["prelude"] = plan.Tour.Prelude!,
            ["reset"] = plan.Tour.Reset!,
            ["frame"] = plan.Tour.Frame!,
            ["stops"] = plan.Tour.Stops.SelectMany(selector: stop => stop.Lines),
            ["after"] = plan.Tour.Stops.SelectMany(selector: stop => (stop.After ?? [])),
            ["world.at"] = plan.Tour.Stops.SelectMany(selector: stop => (stop.Steps ?? [])).Select(selector: step => step.Run),
        };

        foreach (var (form, scripts) in forms) {
            Assert.NotEmpty(collection: scripts);
            foreach (var script in scripts) {
                var lines = script.Split('\n');
                var good = lines.Select(selector: (line, index) => new ControlCommandResult(ClearTranscript: false, Command: line, IsError: false, Line: (index + 1), Output: "measured", Status: "completed", Truncated: false)).ToArray();

                Assert.True(condition: SweepEvidence.FromResponse(script, new(1, "completed", "measured", Commands: good)).Ok, userMessage: form);
                for (var index = 0; (index < good.Length); index++) {
                    foreach (var broken in new ControlCommandResult?[] {
                        null, good[index] with { Output = "" }, good[index] with { IsError = true, Status = "refused" },
                        good[index] with { Truncated = true }, good[index] with { Status = "submitted" },
                    }) {
                        var entries = good.Where(predicate: (_, slot) => (slot != index)).ToList();

                        if (broken is not null) {
                            entries.Add(item: broken);
                        }
                        Assert.False(condition: SweepEvidence.FromResponse(script, new(1, "completed", "measured", Commands: entries.ToArray())).Ok, userMessage: $"{form}: {script}");
                    }
                }
                // The same command must be observed when it runs on a tick, not just when it is armed.
                foreach (var line in lines) {
                    foreach (var status in new[] { "ran", "refused", "faulted", "pending", "unreached" }) {
                        foreach (var output in new[] { "", "measured" }) {
                            var json = JsonSerializer.Serialize(new { ran = new[] { new { tick = 50, command = line, status, output } } });

                            Assert.Equal(((status == "ran") && (output.Length > 0)), SweepEvidence.Scheduled(new(Ok: true, Output: json, Refusals: []), [(50UL, line)]).Ok);
                        }
                    }
                    Assert.False(condition: SweepEvidence.Scheduled(new(Ok: true, Output: "{\"ran\":[]}", Refusals: []), [(50UL, line)]).Ok);
                    Assert.False(condition: SweepEvidence.Scheduled(new(Ok: false, Output: "truncated", Refusals: ["truncated"]), [(50UL, line)]).Ok);
                }
            }
        }
    }
}
