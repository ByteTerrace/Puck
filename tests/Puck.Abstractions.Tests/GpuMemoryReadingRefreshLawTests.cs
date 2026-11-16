using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuMemoryReadingRefresh"/>, the one refresh rule both backends follow: the first read is due, a
/// signal makes a read due at once, a read is otherwise due once an interval has passed, so usage never goes stale
/// behind a budget-change event that never fires, and the serial moves for a changed budget or segments, never for usage
/// alone, continuing from a predecessor's.
/// </summary>
public sealed class GpuMemoryReadingRefreshLawTests {
    private static GpuSegmentReading[] Readings(GpuMemorySegments segments, ulong budget, ulong usage) =>
        [.. segments.Segments.Select(selector: segment => new GpuSegmentReading(AvailableForReservation: null, Budget: budget, CurrentReservation: null, Segment: segment, Usage: usage))];

    [Fact]
    public void UsageIsReadEachIntervalAndOnlyBudgetsMoveTheSerial() {
        var segments = GpuMemorySegments.FromDirectX(profile: default);
        var refresh = new GpuMemoryReadingRefresh(serial: 7UL);
        var interval = GpuMemoryReadingRefresh.Interval;

        Assert.True(condition: refresh.Due(signalled: false, timestamp: 0L));

        var first = refresh.Publish(estimated: false, last: GpuMemoryReading.Absent, readings: Readings(budget: 1000UL, segments: segments, usage: 10UL), segments: segments, timestamp: 0L);

        Assert.Equal(expected: 8UL, actual: first.Serial);
        Assert.False(condition: refresh.Due(signalled: false, timestamp: (interval - 1L)));
        Assert.True(condition: refresh.Due(signalled: true, timestamp: (interval - 1L)));
        Assert.True(condition: refresh.Due(signalled: false, timestamp: interval));

        var usage = refresh.Publish(estimated: false, last: first, readings: Readings(budget: 1000UL, segments: segments, usage: 900UL), segments: segments, timestamp: interval);

        Assert.Equal(expected: (8UL, 900UL), actual: (usage.Serial, usage.Segments[0].Usage!.Value));
        Assert.False(condition: refresh.Due(signalled: false, timestamp: ((2L * interval) - 1L)));

        var budget = refresh.Publish(estimated: false, last: usage, readings: Readings(budget: 800UL, segments: segments, usage: 900UL), segments: segments, timestamp: (2L * interval));
        var recreated = GpuMemorySegments.FromDirectX(profile: default);
        var device = refresh.Publish(estimated: false, last: budget, readings: Readings(budget: 800UL, segments: recreated, usage: 0UL), segments: recreated, timestamp: (3L * interval));

        Assert.Equal(expected: (9UL, 10UL, 10UL), actual: (budget.Serial, device.Serial, refresh.Serial));
    }
}
