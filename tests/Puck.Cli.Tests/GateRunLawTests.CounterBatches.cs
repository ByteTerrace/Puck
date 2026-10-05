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

        WriteCounterBatch(directory: directory);
        directory.WriteText(name: "tests/Puck.Counters/ordinary.ceilings.json",
            text: """{"workload":"ordinary.puck","script":"ordinary.script.txt"}""");
        var steps = GatePlan.CounterWorkloads(repositoryRoot: directory.RootPath, step: CounterStep()).ToArray();

        Assert.Equal(2, steps.Length);
        Assert.Equal(new[] { "counters", "--batch", "tests/Puck.Counters/comparison/comparison.batch.json", "--check" }, steps[0].Arguments);
        Assert.True(condition: steps[0].Gpu);
        Assert.Equal(new[] { "counters", "--check", "--world", "ordinary.puck", "--ceilings",
            "tests/Puck.Counters/ordinary.ceilings.json", "--script", "ordinary.script.txt" }, steps[1].Arguments);
    }
    [InlineData("ambiguous")]
    [InlineData("missing")]
    [InlineData("misplaced")]
    [Theory]
    public void ARecordedBatchRefusesAmbiguousOrIncompleteLedgerAssociations(string defect) {
        using var directory = new TemporaryDirectory(prefix: "puck-gate-counter-association-law-");

        WriteCounterBatch(directory: directory);
        const string Home = "tests/Puck.Counters/comparison/";

        if (defect == "ambiguous") {
            directory.WriteText(name: (Home + "duplicate.batch.json"), text: File.ReadAllText(path: directory.PathOf(name: (Home + "comparison.batch.json"))));
        } else if (defect == "missing") {
            File.Delete(path: directory.PathOf(name: (Home + "cone.ceilings.json")));
        } else {
            File.Move(directory.PathOf(name: (Home + "cache.ceilings.json")), directory.PathOf(name: (Home + "wrong.ceilings.json")));
        }
        Assert.Throws<InvalidDataException>(testCode: () => GatePlan.CounterWorkloads(repositoryRoot: directory.RootPath, step: CounterStep()).ToArray());
    }

    private static GateStep CounterStep() => GatePlan.Steps.Single(predicate: step => (step.Kind == GateStepKind.Counters));
    private static void WriteCounterBatch(TemporaryDirectory directory) {
        const string Home = "tests/Puck.Counters/comparison/";

        directory.WriteText(name: (Home + "fixture.puck"), text: "world {}\n");
        directory.WriteText(name: (Home + "prelude.script.txt"), text: "world.wait ready 180\n");
        string[] methods = ["cache", "cone"];

        foreach (var method in methods) {
            directory.WriteText(name: ((Home + method) + ".script.txt"),
                text: $"world.indirect-method {method}\nworld.rate pause\nworld.wait indirect 180\nworld.rate resume\nworld.wait 120\nworld.counters --json\n");
            directory.WriteText(name: ((Home + method) + ".ceilings.json"), text: JsonSerializer.Serialize(new {
                workload = (Home + "fixture.puck"),
                script = ((Home + method) + ".script.txt"),
            }));
        }
        directory.WriteText(name: (Home + "comparison.batch.json"), text: JsonSerializer.Serialize(new CountersBatchManifest(
            "fixture.puck", [new CountersBatchGroup("medium", "prelude.script.txt", methods.Select(selector: method =>
                new CountersBatchObservation(Ceilings: (method + ".ceilings.json"), Method: method, Name: method, Report: (method + ".report.json"),
                    Script: (method + ".script.txt"))).ToArray())]), CountersBatchInput.Json));
    }
}
