using System.Text.Json;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class MeasureLawTests {
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
        var first = new MeasurementRequest("a", "stale control", temporary.RootPath, DateTimeOffset.UnixEpoch, "a", "b", ["bench", "world"], ["/Summary/MedianMilliseconds"], "zero", null);
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
}
