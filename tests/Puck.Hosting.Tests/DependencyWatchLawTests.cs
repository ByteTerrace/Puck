namespace Puck.Hosting.Tests;

/// <summary>One quiet-period request covers a burst, including edits while its dependency set is refreshed.</summary>
public sealed class DependencyWatchLawTests {
    [Fact]
    public void BurstsCoalesceAndRefreshRetainsAnEditMadeDuringBuild() {
        var values = new Dictionary<string, int> { ["source"] = 1, ["import"] = 1, ["unrelated"] = 1 };
        var watch = new DependencyWatch<string, int>(key => values[key]);

        watch.Refresh(["source"]);
        values["unrelated"] = 2;
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 100, pollTicks: 5));
        values["source"] = 2;
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 105, pollTicks: 5));
        values["source"] = 3;
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 115, pollTicks: 5));
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 129, pollTicks: 5));
        Assert.True(condition: watch.Poll(debounceTicks: 15, now: 130, pollTicks: 5));
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 200, pollTicks: 5));
        // The build read import=1; the file was edited before the build finished.
        values["import"] = 2;
        watch.Refresh(["source", "import"], firstRead: _ => 1);
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 205, pollTicks: 5));
        Assert.True(condition: watch.Poll(debounceTicks: 15, now: 220, pollTicks: 5));
        Assert.Equal(3, watch.ChangeCount);
    }
    [Fact]
    public void StopDropsChangesAndRetriesButRetainsTheLifetimeCount() {
        var value = 1;
        var watch = new DependencyWatch<string, int>(_ => value);

        watch.Refresh(["source"]);
        value++;
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 10, pollTicks: 5));
        watch.Retry(now: 12);
        watch.Clear();
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 100, pollTicks: 5));
        Assert.Equal(1, watch.ChangeCount);
        watch.Refresh(["source"]);
        watch.Retry(now: 101);
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 115, pollTicks: 5));
        Assert.True(condition: watch.Poll(debounceTicks: 15, now: 116, pollTicks: 5));
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 200, pollTicks: 5));
    }
}
