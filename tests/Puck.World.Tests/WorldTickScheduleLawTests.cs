using Puck.Abstractions.Counting;
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
        // A published row is claimed and never fires again, so every window publishes ticks of its own.
        const ulong TicksPerWindow = 63UL;
        var schedule = new WorldTickSchedule<int>();
        var counter = new int[1];

        for (var tick = 0UL; (tick <= (TicksPerWindow * AllocationWindow.MaximumWindows)); tick++) {
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

        var next = 1UL;

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                for (var end = (next + TicksPerWindow); (next < end); next++) {
                    _ = schedule.Publish(
                        fire: Accumulate,
                        state: counter,
                        tick: next
                    );
                }
            }),
            expected: 0L
        );
        Assert.Equal(
            actual: ((ulong)counter[0]),
            expected: next
        );
    }
}
