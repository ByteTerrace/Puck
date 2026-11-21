namespace Puck.Hosting.Tests;

/// <summary>One quiet-period request covers a burst, including edits while its dependency set is refreshed.</summary>
public sealed class DependencyWatchLawTests {
    // A poll whose stamp read, started off the caller's thread, is adopted at the same timestamp.
    private static bool Poll<TKey, TStamp>(DependencyWatch<TKey, TStamp> watch, long now) where TKey : notnull {
        var due = watch.Poll(debounceTicks: 15, now: now, pollTicks: 5);

        watch.WaitForRead();
        return (watch.Poll(debounceTicks: 15, now: now, pollTicks: 5) || due);
    }

    [Fact]
    public void BurstsCoalesceAndRefreshRetainsAnEditMadeDuringBuild() {
        var values = new System.Collections.Concurrent.ConcurrentDictionary<string, int> { ["source"] = 1, ["import"] = 1, ["unrelated"] = 1 };
        var watch = new DependencyWatch<string, int>(key => values[key]);

        watch.Refresh(["source"]);
        values["unrelated"] = 2;
        Assert.False(condition: Poll(watch, now: 100));
        values["source"] = 2;
        Assert.False(condition: Poll(watch, now: 105));
        values["source"] = 3;
        Assert.False(condition: Poll(watch, now: 115));
        Assert.False(condition: Poll(watch, now: 129));
        Assert.True(condition: Poll(watch, now: 130));
        Assert.False(condition: Poll(watch, now: 200));
        // The build read import=1; the file was edited before the build finished.
        values["import"] = 2;
        watch.Refresh(["source", "import"], firstRead: _ => 1);
        Assert.False(condition: Poll(watch, now: 205));
        Assert.True(condition: Poll(watch, now: 220));
        Assert.Equal(3, watch.ChangeCount);
    }
    [Fact]
    public void StopDropsChangesAndRetriesButRetainsTheLifetimeCount() {
        var value = 1;
        var watch = new DependencyWatch<string, int>(_ => Volatile.Read(location: ref value));

        watch.Refresh(["source"]);
        Interlocked.Increment(location: ref value);
        Assert.False(condition: Poll(watch, now: 10));
        watch.Retry(now: 12);
        watch.Clear();
        Assert.False(condition: Poll(watch, now: 100));
        Assert.Equal(1, watch.ChangeCount);
        watch.Refresh(["source"]);
        watch.Retry(now: 101);
        Assert.False(condition: Poll(watch, now: 115));
        Assert.True(condition: Poll(watch, now: 116));
        Assert.False(condition: Poll(watch, now: 200));
    }
    [Fact]
    public void AReadInFlightWhenTheWatchClearsIsDiscarded() {
        var gate = new ManualResetEventSlim();
        var value = 1;
        var watch = new DependencyWatch<string, int>(_ => { gate.Wait(); return Volatile.Read(location: ref value); });

        gate.Set();
        watch.Refresh(["source"]);
        gate.Reset();
        Interlocked.Increment(location: ref value);
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 10, pollTicks: 5));
        watch.Clear();
        gate.Set();
        watch.WaitForRead();
        Assert.False(condition: watch.Poll(debounceTicks: 15, now: 100, pollTicks: 5));
        Assert.Equal(0, watch.ChangeCount);
    }
}
