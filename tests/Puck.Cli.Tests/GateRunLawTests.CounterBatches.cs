using System.Text.Json;
using Puck.Cli.Counters;
using Puck.Cli.Gate;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

public sealed partial class GateRunLawTests {
    [Fact]
    public void RecordedBatchObservationsRouteOnceWhileOrdinaryLedgersKeepTheirOwnScripts() {
        using var directory = new TemporaryDirectory(prefix: "puck-gate-counter-batch-law-");
        WriteCounterBatch(directory);
        directory.WriteText("tests/Puck.Counters/ordinary.ceilings.json",
            """{"workload":"ordinary.puck","script":"ordinary.script.txt"}""");
        var steps = GatePlan.CounterWorkloads(directory.RootPath, CounterStep()).ToArray();
        Assert.Equal(2, steps.Length);
        Assert.Equal(new[] { "counters", "--batch", "tests/Puck.Counters/comparison/comparison.batch.json", "--check" }, steps[0].Arguments);
        Assert.True(steps[0].Gpu);
        Assert.Equal(new[] { "counters", "--check", "--world", "ordinary.puck", "--ceilings",
            "tests/Puck.Counters/ordinary.ceilings.json", "--script", "ordinary.script.txt" }, steps[1].Arguments);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("missing")]
    [InlineData("misplaced")]
    public void ARecordedBatchRefusesAmbiguousOrIncompleteLedgerAssociations(string defect) {
        using var directory = new TemporaryDirectory(prefix: "puck-gate-counter-association-law-");
        WriteCounterBatch(directory);
        const string Home = "tests/Puck.Counters/comparison/";
        if (defect == "ambiguous") {
            directory.WriteText(Home + "duplicate.batch.json", File.ReadAllText(directory.PathOf(Home + "comparison.batch.json")));
        } else if (defect == "missing") {
            File.Delete(directory.PathOf(Home + "cone.ceilings.json"));
        } else {
            File.Move(directory.PathOf(Home + "cache.ceilings.json"), directory.PathOf(Home + "wrong.ceilings.json"));
        }
        Assert.Throws<InvalidDataException>(() => GatePlan.CounterWorkloads(directory.RootPath, CounterStep()).ToArray());
    }

    private static GateStep CounterStep() => GatePlan.Steps.Single(step => step.Kind == GateStepKind.Counters);

    private static void WriteCounterBatch(TemporaryDirectory directory) {
        const string Home = "tests/Puck.Counters/comparison/";
        directory.WriteText(Home + "fixture.puck", "world {}\n");
        directory.WriteText(Home + "prelude.script.txt", "world.wait ready 180\n");
        string[] methods = ["cache", "cone"];
        foreach (var method in methods) {
            directory.WriteText(Home + method + ".script.txt",
                $"world.indirect-method {method}\nworld.rate pause\nworld.wait indirect 180\nworld.rate resume\nworld.wait 120\nworld.counters --json\n");
            directory.WriteText(Home + method + ".ceilings.json", JsonSerializer.Serialize(new {
                workload = Home + "fixture.puck", script = Home + method + ".script.txt",
            }));
        }
        directory.WriteText(Home + "comparison.batch.json", JsonSerializer.Serialize(new CountersBatchManifest(
            "fixture.puck", [new CountersBatchGroup("medium", "prelude.script.txt", methods.Select(method =>
                new CountersBatchObservation(method, method, method + ".script.txt", method + ".report.json",
                    method + ".ceilings.json")).ToArray())]), CountersBatchInput.Json));
    }
}
