using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Proves a tick schedule hands each row out once, in the order it was added, however often its tick is
/// published, and allocates nothing doing it.</summary>
public sealed class WorldTickScheduleLawTests {
    // One delegate for the warm-up and the measured loop: each static lambda in source caches its own instance on
    // first use, and that one-time cost is not the schedule's.
    private static readonly Action<int[], int> Accumulate = static (sum, row) => sum[0] += row;

    [Fact]
    public void ATickPublishedTwiceHandsItsRowsOutOnce() {
        var schedule = new WorldTickSchedule<string>();
        var fired = new List<string>();

        schedule.Add(
            row: "first",
            tick: 5UL
        );
        schedule.Add(
            row: "second",
            tick: 5UL
        );
        schedule.Add(
            row: "later",
            tick: 9UL
        );

        Assert.Equal(
            actual: schedule.Publish(
                fire: static (sink, row) => sink.Add(item: row),
                state: fired,
                tick: 5UL
            ),
            expected: 2
        );
        // The authority's timeline rewinds and the tick completes again.
        Assert.Equal(
            actual: schedule.Publish(
                fire: static (sink, row) => sink.Add(item: row),
                state: fired,
                tick: 5UL
            ),
            expected: 0
        );
        Assert.Equal(
            actual: schedule.Publish(
                fire: static (sink, row) => sink.Add(item: row),
                state: fired,
                tick: 9UL
            ),
            expected: 1
        );
        Assert.Equal(
            actual: fired,
            expected: ["first", "second", "later"]
        );
    }
    [Fact]
    public void PublishingAllocatesNothing() {
        var schedule = new WorldTickSchedule<int>();
        var counter = new int[1];

        for (var tick = 0UL; (tick < 64UL); tick++) {
            schedule.Add(
                row: 1,
                tick: tick
            );
        }

        _ = schedule.Publish(
            fire: Accumulate,
            state: counter,
            tick: 0UL
        );

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var tick = 1UL; (tick < 64UL); tick++) {
            _ = schedule.Publish(
                fire: Accumulate,
                state: counter,
                tick: tick
            );
        }

        Assert.Equal(
            actual: (GC.GetAllocatedBytesForCurrentThread() - before),
            expected: 0L
        );
        Assert.Equal(
            actual: counter[0],
            expected: 64
        );
    }
}
