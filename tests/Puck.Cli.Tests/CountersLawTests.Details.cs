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
            .. ((state == GpuPassState.Executed) ? new[] {
                new WorldCount("gpu", "world", "sky", GpuWork.SkyHashes.Name, WorkClass.PerBackendDeterministic, stars, "stars"),
                new WorldCount("gpu", "world", "sky", GpuWork.SkyHashes.Name, WorkClass.PerBackendDeterministic, clouds, "clouds"),
            } : []),
        ],
        Passes = [
            new WorldCountersPass("world", "sky", GpuPassState.Executed, WorkClass.PerBackendDeterministic),
            new WorldCountersPass(Class: WorkClass.PerBackendDeterministic, Detail: "stars", Label: "sky", Node: "world", State: state),
            new WorldCountersPass(Class: WorkClass.PerBackendDeterministic, Detail: "clouds", Label: "sky", Node: "world", State: state)
        ],
    };

    [Fact]
    public void DetailNamesSurviveReadingAndDoNotMergeCountsOrStates() {
        const string Ordinary = "{\"label\":\"mask\",\"class\":\"deterministic\",\"state\":\"executed\",\"counts\":{\"gpu.dispatches\":1}}";
        var detailed = Reading.Replace(Ordinary, ((((Ordinary + ",") + Ordinary.Replace(newValue: "\"mask\",\"detail\":\"stars\"", oldValue: "\"mask\"")) + ",") + Ordinary.Replace(newValue: "\"mask\",\"detail\":\"clouds\"", oldValue: "\"mask\"")), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(detailed);

        Assert.True(condition: CountersReading.TryRead(document.RootElement, "vulkan", 256, 144, "toolchain", out var run, out var reason), userMessage: reason);
        Assert.Equal(new string?[] { null, "stars", "clouds" }, run.Counts.Where(predicate: static row => (row.Pass == "mask")).Select(selector: static row => row.Detail));
        Assert.Equal(new string?[] { null, "stars", "clouds" }, run.Passes.Where(predicate: static row => (row.Label == "mask")).Select(selector: static row => row.Detail));
        using var empty = JsonDocument.Parse(detailed.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"detail\":\"\"", oldValue: "\"detail\":\"stars\""));

        Assert.False(condition: CountersReading.TryRead(empty.RootElement, "vulkan", 256, 144, "toolchain", out _, out reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "empty detail");
    }
    [Fact]
    public void ComparisonNamesTheOneDetailWhoseCountOrStateChanged() {
        var left = Report(vulkan: DetailRun());
        var raised = Report(vulkan: DetailRun(stars: 1));
        var count = Assert.Single(collection: CountersComparison.Reports(left: left, right: raised));

        Assert.Contains(actualString: count, comparisonType: StringComparison.Ordinal, expectedSubstring: "detail=stars");
        Assert.Contains(actualString: count, comparisonType: StringComparison.Ordinal, expectedSubstring: "left=0 right=1");
        var reordered = left with { Runs = [left.Runs[0] with { Counts = left.Runs[0].Counts.Reverse().ToArray(), Passes = left.Runs[0].Passes.Reverse().ToArray() }, left.Runs[1]] };

        Assert.Empty(collection: CountersComparison.Reports(left: left, right: reordered));
        var changed = left.Runs[0] with { Passes = left.Runs[0].Passes.Select(selector: static row => ((row.Detail == "stars") ? row with { State = GpuPassState.Standing } : row)).ToArray() };
        var state = Assert.Single(collection: CountersComparison.Reports(left: left, right: Report(vulkan: changed)));

        Assert.Contains(actualString: state, comparisonType: StringComparison.Ordinal, expectedSubstring: "detail=stars");
        Assert.Contains(actualString: state, comparisonType: StringComparison.Ordinal, expectedSubstring: "left=executed right=standing");
    }
    [Fact]
    public void ADetailRequiredZeroAndAnUnavailableDetailAreNotAnotherLayersCount() {
        var report = Report(vulkan: DetailRun());
        var ceilings = CountersCeilings.Record(report: report);

        Assert.Empty(collection: CountersCeilings.Check(ceilings: ceilings, report: report).Failures);
        var broken = Assert.Single(collection: CountersCeilings.Check(Report(vulkan: DetailRun(stars: 1)), ceilings).Failures);

        Assert.Contains(actualString: broken, comparisonType: StringComparison.Ordinal, expectedSubstring: "detail=stars");
        Assert.Contains(actualString: broken, comparisonType: StringComparison.Ordinal, expectedSubstring: "required zero");
        Assert.Empty(collection: CountersCeilings.Check(Report(vulkan: DetailRun(state: GpuPassState.Standing)), ceilings).Failures);
        var missing = DetailRun(state: GpuPassState.Standing) with { Passes = [new WorldCountersPass("world", "sky", GpuPassState.Standing, WorkClass.PerBackendDeterministic)] };
        var absent = CountersCeilings.Check(Report(vulkan: missing), ceilings).Failures;

        Assert.Equal(2, absent.Count);
        Assert.All(absent, message => Assert.Contains(actualString: message, comparisonType: StringComparison.Ordinal, expectedSubstring: "recorded but not measured"));
    }
}
