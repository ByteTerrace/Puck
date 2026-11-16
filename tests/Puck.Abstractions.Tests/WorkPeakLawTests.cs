using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Tests;

// A peak holds the largest value raised since its source cleared its peaks; a total only adds. A set refuses a write of
// the wrong form, and a forwarder reads its instances' largest peak and clears through to them.
public sealed class WorkPeakLawTests {
    private static readonly WorkKindRegistry Registry = new();
    private static readonly WorkKind Total = Registry.Declare(name: "test.peaks.total", unit: "count", workClass: WorkClass.Pacing);
    private static readonly WorkKind Peak = Registry.DeclarePeak(name: "test.peaks.peak", unit: "count", workClass: WorkClass.Pacing);

    [Fact]
    public void APeakHoldsTheLargestRaiseUntilClearedAndATotalIsNeverCleared() {
        var set = new WorkCounterSet(name: "test.peaks", kinds: Registry.Kinds);

        Assert.True(Peak.IsPeak);
        Assert.False(Total.IsPeak);
        set.Raise(kind: Peak, value: 5);
        set.Raise(kind: Peak, value: 3);
        set.Add(kind: Total, amount: 4);
        Assert.Equal(expected: 5L, actual: set.Read(kind: Peak));
        set.ClearPeaks();
        Assert.Equal(expected: 0L, actual: set.Read(kind: Peak));
        Assert.Equal(expected: 4L, actual: set.Read(kind: Total));
        set.Raise(kind: Peak, value: 2);
        Assert.Equal(expected: 2L, actual: set.Read(kind: Peak));
        Assert.Throws<ArgumentException>(() => set.Add(kind: Peak, amount: 1));
        Assert.Throws<ArgumentException>(() => set.Raise(kind: Total, value: 1));
    }
    [Fact]
    public void AForwarderReadsTheLargestPeakOfItsInstancesAndClearsThem() {
        var forwarder = new ForwardingWorkCounterSource(name: "test.peaks", kinds: Registry.Kinds);
        var first = new WorkCounterSet(name: "test.peaks", kinds: Registry.Kinds);
        var second = new WorkCounterSet(name: "test.peaks", kinds: Registry.Kinds);

        forwarder.Retarget(target: first);
        forwarder.Attach(instance: second);
        first.Raise(kind: Peak, value: 7);
        second.Raise(kind: Peak, value: 9);
        first.Add(kind: Total, amount: 1);
        second.Add(kind: Total, amount: 2);
        Assert.True(forwarder.TryRead(kind: Peak, value: out var peak));
        Assert.Equal(expected: 9L, actual: peak);
        Assert.True(forwarder.TryRead(kind: Total, value: out var total));
        Assert.Equal(expected: 3L, actual: total);
        // A retired instance's peak is carried forward until the forwarder's peaks are cleared.
        forwarder.Detach(instance: second);
        Assert.True(forwarder.TryRead(kind: Peak, value: out peak));
        Assert.Equal(expected: 9L, actual: peak);
        ((IWorkCounterSource)forwarder).ClearPeaks();
        Assert.True(forwarder.TryRead(kind: Peak, value: out peak));
        Assert.Equal(expected: 0L, actual: peak);
        Assert.Equal(expected: 0L, actual: first.Read(kind: Peak));
        Assert.True(forwarder.TryRead(kind: Total, value: out total));
        Assert.Equal(expected: 3L, actual: total);
    }
}
