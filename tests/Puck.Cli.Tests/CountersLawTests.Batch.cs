using System.Text.Json;
using Puck.Abstractions;
using Puck.Cli.Counters;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

public sealed partial class CountersLawTests {
    private static PreparedCountersBatch Batch(int count = 15) => new("fixture.puck", [
        Group("medium", count), Group("high", count),
    ], "manifest-hash");
    private static PreparedCountersGroup Group(string name, int count) => new(name, "prelude.txt", "prelude\n",
        Enumerable.Range(0, count).Select(index => {
            var method = new[] { "cache", "screen", "cone" }[index % 3];
            var id = $"{name}-{index}";
            return new PreparedCountersObservation(new CountersBatchObservation(id, method, id + ".script.txt",
                id + ".json", id + ".ceilings.json"), id + ".script.txt", "script\n", id + ".json", id + ".ceilings.json");
        }).ToArray());
    private static CliProcessResult BatchTranscript(PreparedCountersGroup group, string backend, string defect = "") {
        var stdout = new List<string>();
        var stderr = new List<string>();
        for (var index = 0; index < group.Observations.Count; index++) {
            var method = group.Observations[index].Definition.Method;
            stdout.Add($"[world.indirect-method: {(defect == "method" && index == 0 ? "invalid" : method)}]");
            if (defect != "wait" || index != 0) {
                stdout.Add($"[world.wait: 120 ticks from {index * 200} — releasing at tick {index * 200 + (defect == "tick" && index == 0 ? 119 : 120)}]");
            }
            if (defect != "missing" || index != 0) {
                var reading = Reading.ReplaceLineEndings(string.Empty).Replace("\"backend\":\"vulkan\"",
                    $"\"backend\":\"{(defect == "backend" && index == 0 ? "wrong" : backend)}\"", StringComparison.Ordinal);
                stdout.Add(defect == "json" && index == 0 ? "[world.counters: {broken}]" : $"[world.counters: {reading}]");
            }
            if (defect != "warm" || index != 0) {
                stderr.Add(defect == "warm-shape" && index == 0 ? "[indirect: settled at tick 0: residency=world allocation=9223372036854775808 epoch=1 generation=0 stamp=1 source=1]"
                    : $"[indirect: settled at tick {index * 200}: residency=world allocation=1 epoch=1 generation=0 stamp=1 source=1]");
            }
        }
        if (defect == "extra") { stdout.Add(stdout[^1]); }
        if (defect == "deadline") { stderr.Add("[indirect: not settled after 180 seconds, so world.wait released]"); }
        stdout.Add(defect == "refusal" ? "[wire.errors: 1 rejected]" : "[wire.errors: 0 rejected]");
        return new CliProcessResult(defect == "exit" ? 1 : 0,
            stdout.Select((line, index) => new CliProcessOutputLine(line, index, CliProcessOutputStream.Stdout, index))
                .Concat(stderr.Select((line, index) => new CliProcessOutputLine(line, stdout.Count + index,
                    CliProcessOutputStream.Stderr, stdout.Count + index))).ToArray(),
            string.Join('\n', stderr), string.Join('\n', stdout), defect == "timeout");
    }

    [Fact]
    public void FourSerialBootsRetainAllSixtyActualObservationIdentities() {
        var calls = new List<string>();
        var batch = Batch();
        Assert.True(CountersCommand.TryCollectBatch(batch, 1920, 1080, "toolchain", (group, backend) => {
            calls.Add(group.Name + "/" + backend);
            return BatchTranscript(group, backend);
        }, out var observations, out var reason), reason);
        Assert.Equal(["medium/vulkan", "medium/directx", "high/vulkan", "high/directx"], calls);
        Assert.Equal(60, observations.Count);
        Assert.Equal(30, observations.Select(item => item.Observation.Definition.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var observation in observations) {
            Assert.Equal(observation.Backend, observation.Reading.Run.Device.Backend);
            Assert.Equal(observation.Observation.Definition.Method, observation.Reading.Method);
            Assert.Equal((ulong)((observation.Reading.Ordinal - 1) * 200 + 120), observation.Reading.Tick);
            Assert.Equal(observation.Reading.Ordinal * 3, observation.Reading.Line);
            Assert.Equal(observation.Reading.Ordinal, observation.CompletionLine);
            Assert.Contains("allocation=1 epoch=1 generation=0 stamp=1 source=1", observation.Completion, StringComparison.Ordinal);
            Assert.Equal(1920, observation.Reading.Run.Width);
            Assert.Equal(1080, observation.Reading.Run.Height);
            Assert.Equal(12, observation.Reading.Run.Counts.Single(count => count.Kind == "state.arena.visits").Value);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("malformed")]
    [InlineData("late")]
    [InlineData("deadline")]
    [InlineData("active-cache")]
    [InlineData("prelude-wait")]
    [InlineData("unknown-completion")]
    public void SkyOnlyBatchesKeepEngineCompletionAndRefuseUnprovedWarmups(string defect) {
        using var directory = new TemporaryDirectory(prefix: "puck-gfx-sky-batch-law-");
        File.WriteAllText(Path.Combine(directory.RootPath, "sky.script.txt"),
            "world.indirect-method cache\nworld.indirect off\nworld.rate pause\nworld.wait ready 180\nworld.rate resume\nworld.wait 120\nworld.counters --json\n");
        if (defect == "active-cache") {
            var scriptPath = Path.Combine(directory.RootPath, "sky.script.txt");
            File.WriteAllText(scriptPath, File.ReadAllText(scriptPath).Replace("world.indirect off", "world.indirect medium", StringComparison.Ordinal));
        }
        File.WriteAllText(Path.Combine(directory.RootPath, "prelude.script.txt"), "world.cadence on\nworld.quality low\n"
            + (defect == "prelude-wait" ? "world.wait ready 180\n" : ""));
        var manifest = new CountersBatchManifest("fixture.puck", [
            new CountersBatchGroup("sky", "prelude.script.txt", [
                new CountersBatchObservation("sky-low", "cache", "sky.script.txt", "sky-low.json", "sky-low.ceilings.json"),
            ]) { Completion = defect == "unknown-completion" ? "timer" : "engine" },
        ]);
        var path = Path.Combine(directory.RootPath, "batch.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, CountersBatchInput.Json));
        if (defect is "active-cache" or "prelude-wait" or "unknown-completion") {
            Assert.Throws<FormatException>(() => CountersBatchInput.Read(path, Path.Combine(directory.RootPath, "products")));
            return;
        }
        var batch = CountersBatchInput.Read(path, Path.Combine(directory.RootPath, "products"));
        var group = Assert.Single(batch.Groups);
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
            var lines = transcript.OutputLines.Where(line => line.Stream == CliProcessOutputStream.Stdout).ToList();
            if (defect != "missing") {
                lines.Add(new CliProcessOutputLine(completion, lines.Count, CliProcessOutputStream.Stderr, lines.Count));
            }
            return transcript with { OutputLines = lines.ToArray(), Stderr = defect == "missing" ? "" : completion };
        }, out var observations, out var reason);
        if (defect == "valid") {
            Assert.True(succeeded, reason);
            Assert.Equal(2, observations.Count);
            Assert.All(observations, observation => {
                Assert.Equal("[engine: ready at tick 0]", observation.Completion);
                Assert.Equal(120UL, observation.Reading.Tick);
                Assert.Equal("cache", observation.Reading.Method);
            });
        } else {
            Assert.False(succeeded);
            Assert.Null(observations);
            Assert.NotEmpty(reason);
        }
    }

    [Theory]
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
    public void AnUntrustworthyBatchLegCannotCreatePairedProducts(string defect) {
        Assert.False(CountersCommand.TryCollectBatch(Batch(count: 2), 256, 144, "toolchain",
            (group, backend) => BatchTranscript(group, backend, defect), out var observations, out var reason));
        Assert.Null(observations);
        Assert.NotEmpty(reason);
    }

    [Theory]
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
    public void BatchInputsKeepTheirOwnScriptsAndProductsAndRefuseAliases(string defect) {
        using var directory = new TemporaryDirectory(prefix: "puck-gfx-batch-input-law-");
        var script = Path.Combine(directory.RootPath, "cache.script.txt");
        File.WriteAllText(script, "world.indirect-method cache\nworld.rate pause\nworld.wait indirect 180\nworld.rate resume\nworld.wait 120\nworld.counters --json\n"
            + (defect == "terminal" ? "quit\n" : ""));
        if (defect == "warm-order") { File.WriteAllText(script, File.ReadAllText(script).Replace("world.rate pause\nworld.wait indirect 180\n", "world.wait indirect 180\nworld.rate pause\n", StringComparison.Ordinal)); }
        File.WriteAllText(Path.Combine(directory.RootPath, "prelude.script.txt"), "world.wait ready 180\n");
        var observation = new CountersBatchObservation("medium-cache-pan", "cache", "cache.script.txt",
            defect switch {
                "escape" => "../outside.json", "reserved" => "batch.observations.json",
                "transcript" => "medium/vulkan-stdout.log", "build-log" => "Puck.World.build.log",
                "reserved-directory" => "observations", _ => "medium-cache-pan.json",
            },
            "medium-cache-pan.ceilings.json");
        var other = observation with {
            Name = defect == "observation-case" ? "MEDIUM-CACHE-PAN" : "other",
            Script = "other.script.txt", Report = "other.json", Ceilings = "other.ceilings.json",
        };
        File.WriteAllText(Path.Combine(directory.RootPath, other.Script), File.ReadAllText(script));
        var group = new CountersBatchGroup("medium", "prelude.script.txt",
            defect == "duplicate" ? [observation, observation] : defect == "observation-case" ? [observation, other] : [observation]);
        var manifest = new CountersBatchManifest("fixture.puck", defect == "group-case"
            ? [group, new CountersBatchGroup("MEDIUM", "prelude.script.txt", [other])] : [group]);
        var path = Path.Combine(directory.RootPath, "batch.json");
        var text = JsonSerializer.Serialize(manifest, CountersBatchInput.Json);
        File.WriteAllText(path, defect == "unknown" ? text.Replace("\"schema\":", "\"typo\":0,\"schema\":", StringComparison.Ordinal) : text);
        var output = Path.Combine(directory.RootPath, "products");
        if ((defect is "group-case" or "observation-case") && !PuckPaths.Comparer.Equals("medium", "MEDIUM")) {
            Assert.NotEmpty(CountersBatchInput.Read(path, output).Groups);
        } else if (defect == "valid") {
            var batch = CountersBatchInput.Read(path, output);
            var selected = Assert.Single(Assert.Single(batch.Groups).Observations);
            Assert.Equal("medium-cache-pan", selected.Definition.Name);
            Assert.Equal(Path.GetFullPath(script).Replace('\\', '/'), selected.ScriptPath);
            Assert.Contains("world.wait indirect 180", selected.Script, StringComparison.Ordinal);
        } else {
            if (defect == "unknown") { Assert.Throws<JsonException>(() => CountersBatchInput.Read(path, output)); }
            else { Assert.Throws<FormatException>(() => CountersBatchInput.Read(path, output)); }
        }
    }
}
