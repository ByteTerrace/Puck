using System.Text.Json;
using Puck.Abstractions;
using Puck.Cli.Counters;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Runs.Tests;

public sealed partial class CountersLawTests {
    private static PreparedCountersBatch Batch(int count = 15) => new("fixture.puck", [
        Group(count: count, name: "medium"), Group(count: count, name: "high"),
    ], "manifest-hash");
    private static PreparedCountersGroup Group(string name, int count) => new(name, "prelude.txt", "prelude\n",
        Enumerable.Range(count: count, start: 0).Select(selector: index => {
            var method = new[] { "cache", "screen", "cone" }[(index % 3)];
            var id = $"{name}-{index}";

            return new PreparedCountersObservation(new CountersBatchObservation(Ceilings: (id + ".ceilings.json"), Method: method, Name: id,
                Report: (id + ".json"), Script: (id + ".script.txt")), (id + ".script.txt"), "script\n", (id + ".json"), (id + ".ceilings.json"));
        }).ToArray());
    private static CliProcessResult BatchTranscript(PreparedCountersGroup group, string backend, string defect = "") {
        var stdout = new List<string>();
        var stderr = new List<string>();

        for (var index = 0; (index < group.Observations.Count); index++) {
            var method = group.Observations[index].Definition.Method;

            stdout.Add(item: $"[world.indirect-method: {(((defect == "method") && (index == 0)) ? "invalid" : method)}]");
            if ((defect != "wait") || (index != 0)) {
                stdout.Add(item: $"[world.wait: 120 ticks from {(index * 200)} — releasing at tick {((index * 200) + (((defect == "tick") && (index == 0)) ? 119 : 120))}]");
            }
            if ((defect != "missing") || (index != 0)) {
                var reading = Reading.ReplaceLineEndings(replacementText: string.Empty).Replace(comparisonType: StringComparison.Ordinal,
                    newValue: $"\"backend\":\"{(((defect == "backend") && (index == 0)) ? "wrong" : backend)}\"", oldValue: "\"backend\":\"vulkan\"");

                stdout.Add(item: (((defect == "json") && (index == 0)) ? "[world.counters: {broken}]" : $"[world.counters: {reading}]"));
            }
            if ((defect != "warm") || (index != 0)) {
                stderr.Add(item: (((defect == "warm-shape") && (index == 0)) ? "[indirect: settled at tick 0: residency=world allocation=9223372036854775808 epoch=1 generation=0 stamp=1 source=1]"
                    : $"[indirect: settled at tick {(index * 200)}: residency=world allocation=1 epoch=1 generation=0 stamp=1 source=1]"));
            }
        }
        if (defect == "extra") { stdout.Add(item: stdout[^1]); }
        if (defect == "deadline") { stderr.Add(item: "[indirect: not settled after 180 seconds, so world.wait released]"); }
        stdout.Add(item: ((defect == "refusal") ? "[wire.errors: 1 rejected]" : "[wire.errors: 0 rejected]"));
        return new CliProcessResult(ExitCode: ((defect == "exit") ? 1 : 0),
            OutputLines: stdout.Select(selector: (line, index) => new CliProcessOutputLine(ElapsedMilliseconds: index, Line: line, Sequence: index, Stream: CliProcessOutputStream.Stdout))
                .Concat(second: stderr.Select(selector: (line, index) => new CliProcessOutputLine(line, (stdout.Count + index),
                    CliProcessOutputStream.Stderr, (stdout.Count + index)))).ToArray(),
            Stderr: string.Join(separator: '\n', values: stderr), Stdout: string.Join(separator: '\n', values: stdout), TimedOut: (defect == "timeout"));
    }

    [Fact]
    public void FourSerialBootsRetainAllSixtyActualObservationIdentities() {
        var calls = new List<string>();
        var batch = Batch();

        Assert.True(condition: CountersCommand.TryCollectBatch(batch, 1920, 1080, "toolchain", (group, backend) => {
            calls.Add(item: ((group.Name + "/") + backend));
            return BatchTranscript(group, backend);
        }, out var observations, out var reason), userMessage: reason);
        Assert.Equal(actual: calls, expected: ["medium/vulkan", "medium/directx", "high/vulkan", "high/directx"]);
        Assert.Equal(60, observations.Count);
        Assert.Equal(30, observations.Select(selector: item => item.Observation.Definition.Name).Distinct(comparer: StringComparer.Ordinal).Count());
        foreach (var observation in observations) {
            Assert.Equal(observation.Backend, observation.Reading.Run.Device.Backend);
            Assert.Equal(observation.Observation.Definition.Method, observation.Reading.Method);
            Assert.Equal(((ulong)(((observation.Reading.Ordinal - 1) * 200) + 120)), observation.Reading.Tick);
            Assert.Equal((observation.Reading.Ordinal * 3), observation.Reading.Line);
            Assert.Equal(observation.Reading.Ordinal, observation.CompletionLine);
            Assert.Contains("allocation=1 epoch=1 generation=0 stamp=1 source=1", observation.Completion, StringComparison.Ordinal);
            Assert.Equal(1920, observation.Reading.Run.Width);
            Assert.Equal(1080, observation.Reading.Run.Height);
            Assert.Equal(12, observation.Reading.Run.Counts.Single(predicate: count => (count.Kind == "state.arena.visits")).Value);
        }
    }
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("malformed")]
    [InlineData("late")]
    [InlineData("deadline")]
    [InlineData("active-cache")]
    [InlineData("late-disable")]
    [InlineData("late-method")]
    [InlineData("prelude-wait")]
    [InlineData("unknown-completion")]
    [Theory]
    public void SkyOnlyBatchesKeepEngineCompletionAndRefuseUnprovedWarmups(string defect) {
        using var directory = new TemporaryDirectory(prefix: "puck-gfx-sky-batch-law-");

        File.WriteAllText(Path.Combine(path1: directory.RootPath, path2: "sky.script.txt"),
            "world.indirect-method cache\nworld.indirect off\nworld.rate pause\nworld.wait ready 180\nworld.rate resume\nworld.wait 120\nworld.counters --json\n");
        if (defect == "active-cache") {
            var scriptPath = Path.Combine(path1: directory.RootPath, path2: "sky.script.txt");

            File.WriteAllText(scriptPath, File.ReadAllText(path: scriptPath).Replace(comparisonType: StringComparison.Ordinal, newValue: "world.indirect medium", oldValue: "world.indirect off"));
        }
        if (defect is "late-disable" or "late-method") {
            var scriptPath = Path.Combine(path1: directory.RootPath, path2: "sky.script.txt");
            var command = ((defect == "late-disable") ? "world.indirect off\n" : "world.indirect-method cache\n");

            File.WriteAllText(scriptPath, File.ReadAllText(path: scriptPath).Replace(comparisonType: StringComparison.Ordinal, newValue: "", oldValue: command)
                .Replace(comparisonType: StringComparison.Ordinal, newValue: ("world.rate resume\n" + command), oldValue: "world.rate resume\n"));
        }
        File.WriteAllText(Path.Combine(path1: directory.RootPath, path2: "prelude.script.txt"), ("world.cadence on\nworld.quality low\n"
            + ((defect == "prelude-wait") ? "world.wait ready 180\n" : "")));
        var manifest = new CountersBatchManifest("fixture.puck", [
            new CountersBatchGroup("sky", "prelude.script.txt", [
                new CountersBatchObservation(Ceilings: "sky-low.ceilings.json", Method: "cache", Name: "sky-low", Report: "sky-low.json", Script: "sky.script.txt"),
            ]) { Completion = ((defect == "unknown-completion") ? "timer" : "engine") },
        ]);
        var path = Path.Combine(path1: directory.RootPath, path2: "batch.json");

        File.WriteAllText(path, JsonSerializer.Serialize(options: CountersBatchInput.Json, value: manifest));
        if (defect is "active-cache" or "late-disable" or "late-method" or "prelude-wait" or "unknown-completion") {
            Assert.Throws<FormatException>(testCode: () => CountersBatchInput.Read(path, Path.Combine(path1: directory.RootPath, path2: "products")));
            return;
        }
        var batch = CountersBatchInput.Read(path, Path.Combine(path1: directory.RootPath, path2: "products"));
        var group = Assert.Single(collection: batch.Groups);

        Assert.Equal("engine", group.Completion);
        Assert.Contains("world.indirect off", group.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("world.wait indirect", group.Script, StringComparison.Ordinal);
        var succeeded = CountersCommand.TryCollectBatch(batch, 1920, 1080, "toolchain", (selected, backend) => {
            var transcript = BatchTranscript(selected, backend);
            var completion = defect switch {
                "foreign" => "[indirect: settled at tick 0: residency=world allocation=1 epoch=1 generation=0 stamp=1 source=1]",
                "malformed" => "[engine: ready at tick unknown]",
                "late" => "[engine: ready at tick 1]",
                "deadline" => "[engine: not ready after 180 seconds, so world.wait released: pipeline pending]",
                _ => "[engine: ready at tick 0]",
            };
            var lines = transcript.OutputLines.Where(predicate: line => (line.Stream == CliProcessOutputStream.Stdout)).ToList();

            if (defect != "missing") {
                lines.Add(item: new CliProcessOutputLine(completion, lines.Count, CliProcessOutputStream.Stderr, lines.Count));
            }
            return transcript with { OutputLines = lines.ToArray(), Stderr = ((defect == "missing") ? "" : completion) };
        }, out var observations, out var reason);

        if (defect == "valid") {
            Assert.True(condition: succeeded, userMessage: reason);
            Assert.NotNull(@object: observations);
            Assert.Equal(2, observations.Count);
            Assert.All(observations, observation => {
                Assert.Equal("[engine: ready at tick 0]", observation.Completion);
                Assert.Equal(120UL, observation.Reading.Tick);
                Assert.Equal("cache", observation.Reading.Method);
            });
        } else {
            Assert.False(condition: succeeded);
            Assert.Null(@object: observations);
            Assert.NotEmpty(collection: reason);
        }
    }
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("json")]
    [InlineData("backend")]
    [InlineData("method")]
    [InlineData("wait")]
    [InlineData("tick")]
    [InlineData("refusal")]
    [InlineData("warm")]
    [InlineData("warm-shape")]
    [InlineData("deadline")]
    [InlineData("exit")]
    [InlineData("timeout")]
    [Theory]
    public void AnUntrustworthyBatchLegCannotCreatePairedProducts(string defect) {
        Assert.False(condition: CountersCommand.TryCollectBatch(Batch(count: 2), 256, 144, "toolchain",
            (group, backend) => BatchTranscript(backend: backend, defect: defect, group: group), out var observations, out var reason));
        Assert.Null(@object: observations);
        Assert.NotEmpty(collection: reason);
    }
    [InlineData("valid")]
    [InlineData("duplicate")]
    [InlineData("escape")]
    [InlineData("reserved")]
    [InlineData("transcript")]
    [InlineData("build-log")]
    [InlineData("reserved-directory")]
    [InlineData("group-case")]
    [InlineData("observation-case")]
    [InlineData("terminal")]
    [InlineData("unknown")]
    [InlineData("warm-order")]
    [Theory]
    public void BatchInputsKeepTheirOwnScriptsAndProductsAndRefuseAliases(string defect) {
        using var directory = new TemporaryDirectory(prefix: "puck-gfx-batch-input-law-");
        var script = Path.Combine(path1: directory.RootPath, path2: "cache.script.txt");

        File.WriteAllText(contents: ("world.indirect-method cache\nworld.rate pause\nworld.wait indirect 180\nworld.rate resume\nworld.wait 120\nworld.counters --json\n"
            + ((defect == "terminal") ? "quit\n" : "")), path: script);
        if (defect == "warm-order") { File.WriteAllText(script, File.ReadAllText(path: script).Replace(comparisonType: StringComparison.Ordinal, newValue: "world.wait indirect 180\nworld.rate pause\n", oldValue: "world.rate pause\nworld.wait indirect 180\n")); }
        File.WriteAllText(Path.Combine(path1: directory.RootPath, path2: "prelude.script.txt"), "world.wait ready 180\n");
        var observation = new CountersBatchObservation(Ceilings: "medium-cache-pan.ceilings.json", Method: "cache", Name: "medium-cache-pan",
            Report: defect switch {
                "escape" => "../outside.json",
                "reserved" => "batch.observations.json",
                "transcript" => "medium/vulkan-stdout.log",
                "build-log" => "Puck.World.build.log",
                "reserved-directory" => "observations",
                _ => "medium-cache-pan.json",
            },
            Script: "cache.script.txt");
        var other = observation with {
            Name = ((defect == "observation-case") ? "MEDIUM-CACHE-PAN" : "other"),
            Script = "other.script.txt",
            Report = "other.json",
            Ceilings = "other.ceilings.json",
        };

        File.WriteAllText(Path.Combine(path1: directory.RootPath, path2: other.Script), File.ReadAllText(path: script));
        var group = new CountersBatchGroup(Name: "medium", Observations: ((defect == "duplicate") ? [observation, observation] : ((defect == "observation-case") ? [observation, other] : [observation])),
            Prelude: "prelude.script.txt");
        var manifest = new CountersBatchManifest("fixture.puck", ((defect == "group-case")
            ? [group, new CountersBatchGroup(Name: "MEDIUM", Observations: [other], Prelude: "prelude.script.txt")] : [group]));
        var path = Path.Combine(path1: directory.RootPath, path2: "batch.json");
        var text = JsonSerializer.Serialize(options: CountersBatchInput.Json, value: manifest);

        File.WriteAllText(path, ((defect == "unknown") ? text.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"typo\":0,\"schema\":", oldValue: "\"schema\":") : text));
        var output = Path.Combine(path1: directory.RootPath, path2: "products");

        if ((defect is "group-case" or "observation-case") && !PuckPaths.Comparer.Equals(x: "medium", y: "MEDIUM")) {
            Assert.NotEmpty(collection: CountersBatchInput.Read(output: output, path: path).Groups);
        } else if (defect == "valid") {
            var batch = CountersBatchInput.Read(output: output, path: path);
            var selected = Assert.Single(collection: Assert.Single(collection: batch.Groups).Observations);

            Assert.Equal("medium-cache-pan", selected.Definition.Name);
            Assert.Equal(Path.GetFullPath(path: script).Replace(newChar: '/', oldChar: '\\'), selected.ScriptPath);
            Assert.Contains("world.wait indirect 180", selected.Script, StringComparison.Ordinal);
        } else {
            if (defect == "unknown") { Assert.Throws<JsonException>(testCode: () => CountersBatchInput.Read(output: output, path: path)); } else { Assert.Throws<FormatException>(testCode: () => CountersBatchInput.Read(output: output, path: path)); }
        }
    }
}
