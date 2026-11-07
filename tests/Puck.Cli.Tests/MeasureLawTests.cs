using System.Text.Json;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class MeasureLawTests {
    [Fact]
    public async Task QuietDefaultsAllowTheWholeIdleSettleAfterCpuSamplerSetup() {
        var clock = new Puck.Testing.VirtualClock();
        using var output = new StringWriter();
        var sample = new Puck.Cli.Host.HostSample(DateTimeOffset.UnixEpoch, 0, 16, 100, null, 0);
        var settle = TimeSpan.FromSeconds(30);
        var run = Puck.Cli.Host.HostQuiet.SettleAsync(settle, settle, output, clock, TestContext.Current.CancellationToken,
            () => Task.FromResult(sample), new Puck.Cli.Host.HostLoadThresholds(60, 5, 2, 10));
        for (var index = 0; index < 17; index++) {
            await clock.WhenArmedAsync(count: 1, ct: TestContext.Current.CancellationToken, dueTime: TimeSpan.FromSeconds(2));
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        Assert.Null(await run.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Contains("idle for 30 seconds", output.ToString());
    }

    [Fact]
    public void WindowedPointersUseTheSelectedStopsAndPassesBeforeFreezingBuilds() {
        MeasureCommand.ValidateMetric(["bench", "frames"], "/metrics/boot.frames.p99/median");
        var error = Assert.Throws<InvalidDataException>(() => MeasureCommand.ValidateMetric(["bench", "frames"], "/metrics/boot.frames.p95/median"));
        Assert.Contains("/metrics/boot.frames.p50/median", error.Message);
        Assert.Contains("puck.bench.v1", error.Message);
        Assert.Throws<InvalidDataException>(() => MeasureCommand.ValidateMetric(["bench", "frames"], "/metrics/boot.frames.p99/samples/1"));
        MeasureCommand.ValidateMetric(["bench", "frames", "--samples", "2"], "/metrics/boot.frames.p99/samples/1");
        MeasureCommand.ValidateMetric(["bench", "frames", "--gpu-pass", "world/glass"], "/metrics/boot.gpu.world~1glass/median");
        Assert.Throws<InvalidDataException>(() => MeasureCommand.ValidateMetric(["bench", "frames"], "/metrics/boot.gpu.world~1glass/median"));
        using var temporary = new Puck.Testing.TemporaryDirectory(prefix: "puck-frames-plan-");
        var plan = temporary.PathOf("plan.json");
        File.WriteAllText(plan, """{"tour":{"stops":[{"name":"pool","lines":[]},{"name":"lake","lines":[]}]}}""");
        MeasureCommand.ValidateMetric(["bench", "frames", "--plan", plan, "--stop", "pool"], "/metrics/pool.frames.p50/median");
        Assert.Throws<InvalidDataException>(() => MeasureCommand.ValidateMetric(["bench", "frames", "--plan", plan, "--stop", "pool"], "/metrics/lake.frames.p50/median"));
    }

    [Fact]
    public void GpuMetricsRequireAFullCompletedWindowForEverySelectedPass() {
        const string Pending = "[world.gpu-timing: on window=32\nworld/glass: 0.125 ms samples=31]";
        Assert.Null(Puck.Cli.Bench.FrameBenchmarks.ReadGpu(Pending, ["world/glass"]));
        Assert.Null(Puck.Cli.Bench.FrameBenchmarks.ReadGpu(Pending.Replace("31", "32", StringComparison.Ordinal), ["world/missing"]));
        var measured = Puck.Cli.Bench.FrameBenchmarks.ReadGpu(Pending.Replace("31", "32", StringComparison.Ordinal), ["world/glass"]);
        Assert.Equal(0.125, measured!["world/glass"]);
        Assert.Null(Puck.Cli.Bench.FrameBenchmarks.ReadGpu("[world.gpu-timing: on window=32\nworld: refused timestamps unavailable]", ["world/glass"]));
    }

    [Fact]
    public void DifferencesStayPairedRatherThanSubtractingIndependentMedians() {
        var summary = MeasureCommand.Summarize("work", [1, 100, 101], [2, 2, 102]);
        Assert.Equal(100, summary.Before.Median);
        Assert.Equal(2, summary.After.Median);
        Assert.Equal(1, summary.Difference.Median);
        Assert.Equal(49.5, summary.Difference.Iqr);
        var control = MeasureCommand.Summarize("control", [1, 2, 4, 9], [1, 2, 4, 9]);
        Assert.Equal(0, control.Difference.Median);
        Assert.Equal(0, control.Difference.Iqr);
        Assert.Equal(3, control.Before.Median);
        Assert.Equal(3.5, control.Before.Iqr);
    }

    [Fact]
    public void ABenchReportsMetricIsReadThroughOnePointer() {
        var report = Puck.Cli.Bench.BenchReport.Start(build: new Puck.Cli.Bench.BenchBuild(Path: "Puck.World.dll", Sha256: "00", Version: "1.0.0+abc"), repeats: 5, settings: new Dictionary<string, string>(), verb: "bench world") with {
            Complete = true,
            Metrics = new Dictionary<string, Puck.Cli.Bench.BenchMetric> { ["idle-tick"] = Puck.Cli.Bench.BenchMetric.Of(samples: [5, 1, 4, 2, 3], unit: "ms") },
        };
        using var json = JsonDocument.Parse(report.ToJson());

        Assert.Equal(3, MeasureCommand.ReadMetric(json.RootElement, "/metrics/idle-tick/median"));
        Assert.Equal(1.4, MeasureCommand.ReadMetric(json.RootElement, "/metrics/idle-tick/p10"), precision: 9);
        Assert.Equal(4.6, MeasureCommand.ReadMetric(json.RootElement, "/metrics/idle-tick/p90"), precision: 9);
        Assert.Equal("1.0.0+abc", json.RootElement.GetProperty("build").GetProperty("version").GetString());
    }

    [Fact]
    public void MetricsReadExistingStartupAndSweepReportsIncludingEscapedKeys() {
        using var json = JsonDocument.Parse("""{"Summary":{"MedianMilliseconds":2.5},"signals":[{"metrics":{"a/b~c":"3.25"}}]}""");
        Assert.Equal(2.5, MeasureCommand.ReadMetric(json.RootElement, "/Summary/MedianMilliseconds"));
        Assert.Equal(3.25, MeasureCommand.ReadMetric(json.RootElement, "/signals/0/metrics/a~1b~0c"));
        Assert.Throws<InvalidDataException>(() => MeasureCommand.ReadMetric(json.RootElement, "/absent"));
        Assert.Throws<ArgumentException>(() => MeasureCommand.Summarize("bad", [1], [1, 2]));
        Assert.Throws<ArgumentException>(() => MeasureCommand.Summarize("bad", [double.NaN], [1]));
    }

    [Theory]
    [InlineData("/Summary/MedianMilliseconds")]
    [InlineData("/metrics/idle-tick/median/0")]
    [InlineData("/metrics/idle-tick/unit")]
    [InlineData("/metrics/idle-tick/samples/900")]
    public void AStaleOrNonnumericPointerNamesTheReportAndAvailableMetrics(string pointer) {
        using var json = JsonDocument.Parse("""{"schema":"puck.bench.v1","metrics":{"idle-tick":{"median":2.5,"unit":"ms","samples":[2.5]}}}""");
        var error = Assert.Throws<InvalidDataException>(() => MeasureCommand.ReadMetric(json.RootElement, pointer));
        Assert.Contains(pointer, error.Message);
        Assert.Contains("puck.bench.v1", error.Message);
        Assert.Contains("/metrics/idle-tick/median", error.Message);
    }

    [Fact]
    public void FilingRefusesTheRetiredStartupShapeBeforeFreezingBuilds() {
        var error = Assert.Throws<InvalidDataException>(() => MeasureCommand.ValidateMetric(["bench", "startup"], "/Summary/MedianMilliseconds"));
        Assert.Contains("puck.bench.v1", error.Message);
        Assert.Contains("/metrics/startup/median", error.Message);
        MeasureCommand.ValidateMetric(["bench", "world"], "/metrics/idle-tick/median");
        MeasureCommand.ValidateMetric(["sweep"], "/signals/0/metrics/a~1b~0c");
    }

    [Fact]
    public async Task AStaleQuestionFailsByNameAndTheNextQuestionIsStillAnswered() {
        using var temporary = new Puck.Testing.TemporaryDirectory(prefix: "puck-measure-law-");
        var first = new MeasurementRequest("a", "stale control", temporary.RootPath, DateTimeOffset.UnixEpoch, new("a", "tree-a", null), new("b", "tree-b", null), ["bench", "world"], ["/Summary/MedianMilliseconds"], "zero", null);
        var second = first with { Id = "b", Name = "current control", Metrics = ["/metrics/idle-tick/median"] };
        var a = temporary.PathOf("a/request.json");
        var b = temporary.PathOf("b/request.json");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        var (exit, output, error) = await ConsoleCapture.RunSplitAsync(() => MeasureCommand.DrainAsync([(a, first), (b, second)], 2,
            (item, side, round, directory, token) => {
                using var json = JsonDocument.Parse("""{"schema":"puck.bench.v1","metrics":{"idle-tick":{"median":2}}}""");
                return Task.FromResult(new MeasurementSample(side, round, item.Metrics.ToDictionary(p => p, p => MeasureCommand.ReadMetric(json.RootElement, p)), "fixture"));
            }, CancellationToken.None));
        Assert.Equal(CliExit.Refused, exit);
        Assert.Contains("stale control", error);
        Assert.Contains("/Summary/MedianMilliseconds", error);
        Assert.Contains("/metrics/idle-tick/median", error);
        Assert.Contains("answered b", output);
        using var failed = JsonDocument.Parse(File.ReadAllText(a));
        Assert.Equal(JsonValueKind.Null, failed.RootElement.GetProperty("Report").ValueKind);
        Assert.Contains("stale control", failed.RootElement.GetProperty("Failure").GetString());
        using var answered = JsonDocument.Parse(File.ReadAllText(b));
        Assert.NotNull(answered.RootElement.GetProperty("Report").GetString());
    }

    [Fact]
    public void AColdIdleSamplerNeverClaimsThatTheMachineIsLoaded() {
        var thresholds = new Puck.Cli.Host.HostLoadThresholds(60, 10, 4, 10, 75);
        var sample = new Puck.Cli.Host.HostSample(DateTimeOffset.UnixEpoch, 4, 20, 100, null, 0, CommitChargeGb: 20, CommitLimitGb: 100);
        var monitor = new Puck.Cli.Host.HostLoadMonitor(thresholds, 3);
        monitor.Observe(sample);
        var why = Puck.Cli.Host.HostQuiet.Busy(sample, monitor);
        Assert.DoesNotContain("loaded", why);
        Assert.Contains("CPU", why);
        Assert.Contains("1 of 3", why);
        monitor.Observe(sample);
        monitor.Observe(sample);
        Assert.Null(Puck.Cli.Host.HostQuiet.Busy(sample, monitor));
        monitor.Observe(sample with { CpuPercent = 100 });
        monitor.Observe(sample with { CpuPercent = 100 });
        why = Puck.Cli.Host.HostQuiet.Busy(sample, monitor);
        Assert.Contains("cpu mean=68.0%", why);
        Assert.DoesNotContain("freeRAM", why);
    }

    [Fact]
    public async Task AnArtifactSideDeclaresAndFreezesItsWorldsAndPlanInputs() {
        using var tree = new GitScratchCheckout();
        tree.Write("Puck.slnx", "<Solution />");
        tree.Write("worlds/debug/debug.puck", "frozen world");
        tree.Write("plans/measure.json", "frozen plan");
        tree.Write("build/Puck.World.dll", "fixture build");
        tree.Commit("inputs");
        var run = await CliProcess.RunAsync("dotnet", [typeof(MeasureCommand).Assembly.Location, "measure", "request", "--name", "frozen inputs",
            "--before-world-artifact", Path.Combine(tree.Root, "build/Puck.World.dll"), "--before-input-tree", tree.Root,
            "--after-world-artifact", Path.Combine(tree.Root, "build/Puck.World.dll"), "--after-input-tree", tree.Root,
            "--metric", "/metrics/idle-tick/median", "--decides", "zero", "--", "bench", "world", "worlds/debug"],
            workingDirectory: tree.Root, timeout: TimeSpan.FromSeconds(30), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(run.ExitCode == 0, run.Stdout + run.Stderr);
        tree.Write("worlds/debug/debug.puck", "changed world");
        tree.Write("plans/measure.json", "changed plan");
        var request = Directory.GetFiles(Path.Combine(tree.Root, ".claude/scratch/measure"), "request.json", SearchOption.AllDirectories).Single();
        using var json = JsonDocument.Parse(File.ReadAllText(request));
        foreach (var side in new[] { "Before", "After" }) {
            var input = json.RootElement.GetProperty(side).GetProperty("Tree").GetString()!;
            Assert.Equal("frozen world", File.ReadAllText(Path.Combine(input, "worlds/debug/debug.puck")));
            Assert.Equal("frozen plan", File.ReadAllText(Path.Combine(input, "plans/measure.json")));
        }
    }

    [Fact]
    public void ACommitFreezesItsWholeTreeAndAbsoluteInputsBecomeRelativeToThatTree() {
        using var tree = new GitScratchCheckout();
        tree.Write("Puck.slnx", "<Solution />");
        tree.Write("worlds/debug/debug.puck", "old world");
        tree.Write("plans/measure.json", "old plan");
        tree.Write("src/Puck.World/input.cs", "old code");
        var revision = tree.Commit("before");
        tree.Write("worlds/debug/debug.puck", "today's world");
        tree.Write("plans/measure.json", "today's plan");
        using var target = new Puck.Testing.TemporaryDirectory(prefix: "puck-measure-tree-");
        var frozen = MeasureCommand.FreezeTree(tree.Root, target.PathOf("tree"), revision, null);
        Assert.Equal("old world", File.ReadAllText(Path.Combine(frozen, "worlds/debug/debug.puck")));
        Assert.Equal("old plan", File.ReadAllText(Path.Combine(frozen, "plans/measure.json")));
        Assert.Equal("old code", File.ReadAllText(Path.Combine(frozen, "src/Puck.World/input.cs")));
        var command = MeasureCommand.FreezeCommand(tree.Root, ["sweep", "--plan=" + Path.Combine(tree.Root, "plans/measure.json"), "--world", Path.Combine(tree.Root, "worlds/debug")]);
        Assert.Equal(new[] { "sweep", "--plan=plans/measure.json", "--world", "worlds/debug" }, command);
        Assert.Throws<ArgumentException>(() => MeasureCommand.FreezeCommand(tree.Root, ["bench", "world", "../outside.puck"]));
        Assert.Throws<ArgumentException>(() => MeasureCommand.FreezeCommand(tree.Root, ["sweep", "--plan", target.PathOf("external.json")]));
    }
}
