using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Cli.Counters;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

public sealed partial class CountersLawTests {
    private static WorldCountersRun DetailRun(long stars = 0, long clouds = 9, GpuPassState state = GpuPassState.Executed) => Run("vulkan") with {
        Counts = [
            new WorldCount("gpu", "world", "sky", GpuWork.SkyHashes.Name, WorkClass.PerBackendDeterministic, 0),
            .. (state == GpuPassState.Executed ? new[] {
                new WorldCount("gpu", "world", "sky", GpuWork.SkyHashes.Name, WorkClass.PerBackendDeterministic, stars, "stars"),
                new WorldCount("gpu", "world", "sky", GpuWork.SkyHashes.Name, WorkClass.PerBackendDeterministic, clouds, "clouds")
            } : []),
        ],
        Passes = [
            new WorldCountersPass("world", "sky", GpuPassState.Executed, WorkClass.PerBackendDeterministic),
            new WorldCountersPass("world", "sky", state, WorkClass.PerBackendDeterministic, "stars"),
            new WorldCountersPass("world", "sky", state, WorkClass.PerBackendDeterministic, "clouds")
        ],
    };
    [Fact]
    public void DetailNamesSurviveReadingAndDoNotMergeCountsOrStates() {
        const string Ordinary = "{\"label\":\"mask\",\"class\":\"deterministic\",\"state\":\"executed\",\"counts\":{\"gpu.dispatches\":1}}";
        var detailed = Reading.Replace(Ordinary, Ordinary + "," + Ordinary.Replace("\"mask\"", "\"mask\",\"detail\":\"stars\"") + "," + Ordinary.Replace("\"mask\"", "\"mask\",\"detail\":\"clouds\""), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(detailed);
        Assert.True(CountersReading.TryRead(document.RootElement, "vulkan", 256, 144, "toolchain", out var run, out var reason), reason);
        Assert.Equal(new string?[] { null, "stars", "clouds" }, run.Counts.Where(static row => row.Pass == "mask").Select(static row => row.Detail));
        Assert.Equal(new string?[] { null, "stars", "clouds" }, run.Passes.Where(static row => row.Label == "mask").Select(static row => row.Detail));
        using var empty = JsonDocument.Parse(detailed.Replace("\"detail\":\"stars\"", "\"detail\":\"\"", StringComparison.Ordinal));
        Assert.False(CountersReading.TryRead(empty.RootElement, "vulkan", 256, 144, "toolchain", out _, out reason));
        Assert.Contains("empty detail", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void ComparisonNamesTheOneDetailWhoseCountOrStateChanged() {
        var left = Report(DetailRun());
        var raised = Report(DetailRun(stars: 1));
        var count = Assert.Single(CountersComparison.Reports(left, raised));
        Assert.Contains("detail=stars", count, StringComparison.Ordinal);
        Assert.Contains("left=0 right=1", count, StringComparison.Ordinal);
        var reordered = left with { Runs = [left.Runs[0] with { Counts = left.Runs[0].Counts.Reverse().ToArray(), Passes = left.Runs[0].Passes.Reverse().ToArray() }, left.Runs[1]] };
        Assert.Empty(CountersComparison.Reports(left, reordered));
        var changed = left.Runs[0] with { Passes = left.Runs[0].Passes.Select(static row => row.Detail == "stars" ? row with { State = GpuPassState.Standing } : row).ToArray() };
        var state = Assert.Single(CountersComparison.Reports(left, Report(changed)));
        Assert.Contains("detail=stars", state, StringComparison.Ordinal);
        Assert.Contains("left=executed right=standing", state, StringComparison.Ordinal);
    }
    [Fact]
    public void ADetailRequiredZeroAndAnUnavailableDetailAreNotAnotherLayersCount() {
        var report = Report(DetailRun());
        var ceilings = CountersCeilings.Record(report);
        Assert.Empty(CountersCeilings.Check(report, ceilings).Failures);
        var broken = Assert.Single(CountersCeilings.Check(Report(DetailRun(stars: 1)), ceilings).Failures);
        Assert.Contains("detail=stars", broken, StringComparison.Ordinal);
        Assert.Contains("required zero", broken, StringComparison.Ordinal);
        Assert.Empty(CountersCeilings.Check(Report(DetailRun(state: GpuPassState.Standing)), ceilings).Failures);
        var missing = DetailRun(state: GpuPassState.Standing) with { Passes = [new WorldCountersPass("world", "sky", GpuPassState.Standing, WorkClass.PerBackendDeterministic)] };
        var absent = CountersCeilings.Check(Report(missing), ceilings).Failures;
        Assert.Equal(2, absent.Count);
        Assert.All(absent, message => Assert.Contains("recorded but not measured", message, StringComparison.Ordinal));
    }
}
