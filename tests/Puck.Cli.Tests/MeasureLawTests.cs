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
        Assert.Throws<KeyNotFoundException>(() => MeasureCommand.ReadMetric(json.RootElement, "/absent"));
        Assert.Throws<ArgumentException>(() => MeasureCommand.Summarize("bad", [1], [1, 2]));
        Assert.Throws<ArgumentException>(() => MeasureCommand.Summarize("bad", [double.NaN], [1]));
    }
}
