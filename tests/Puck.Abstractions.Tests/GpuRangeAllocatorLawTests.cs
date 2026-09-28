using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuRangeAllocator"/>: a request of exactly the free size fits and one more is refused by name
/// with the request, the largest free range and the size; a request larger than a freed middle range is refused until
/// its neighbours free and coalesce; a freed range is the one the next equal request receives, and a thousand
/// allocate-free cycles leave the span as they found it, allocating nothing; a double free and a free of a range never
/// allocated are refused by name.
/// </summary>
public sealed class GpuRangeAllocatorLawTests {
    [Fact]
    public void ARequestOfExactlyTheFreeSizeFitsAndOneMoreIsRefused() {
        var ranges = new GpuRangeAllocator(
            maxRanges: 8,
            size: 100
        );

        Assert.Equal(
            actual: ranges.Allocate(count: 40),
            expected: 0u
        );

        var refusal = Assert.Throws<GpuRangeExhaustedException>(testCode: () => ranges.Allocate(count: 61));

        Assert.Equal(
            actual: (refusal.Request, refusal.LargestFree, refusal.Size),
            expected: (61u, 60u, 100u)
        );
        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "A range of 61 is refused: the largest free range holds 60 of this span's 100"
        );
        Assert.Equal(
            actual: ranges.Allocate(count: 60),
            expected: 40u
        );
        Assert.Equal(
            actual: (ranges.FreeCount, ranges.FreeRanges, ranges.LargestFree),
            expected: (0u, 0, 0u)
        );
        Assert.Throws<GpuRangeExhaustedException>(testCode: () => ranges.Allocate(count: 1));
    }
    [Fact]
    public void TheMostLiveRangesAreRefusedByName() {
        var ranges = new GpuRangeAllocator(
            maxRanges: 2,
            size: 100
        );

        _ = ranges.Allocate(count: 1);
        _ = ranges.Allocate(count: 1);

        Assert.Contains(
            actualString: Assert.Throws<GpuRangeExhaustedException>(testCode: () => ranges.Allocate(count: 1)).Message,
            expectedSubstring: "2 ranges are already live"
        );
    }
    [Fact]
    public void AFragmentedSpanRefusesARequestUntilItsNeighboursCoalesce() {
        var ranges = new GpuRangeAllocator(
            maxRanges: 8,
            size: 30
        );
        var first = ranges.Allocate(count: 10);
        var middle = ranges.Allocate(count: 10);
        var last = ranges.Allocate(count: 10);

        _ = ranges.Free(start: middle);

        // Ten are free, but a request of fifteen fits no single range.
        Assert.Equal(
            actual: (ranges.FreeCount, ranges.LargestFree),
            expected: (10u, 10u)
        );
        Assert.Equal(
            actual: Assert.Throws<GpuRangeExhaustedException>(testCode: () => ranges.Allocate(count: 15)).LargestFree,
            expected: 10u
        );

        _ = ranges.Free(start: first);

        // The first range joins the freed middle into one free range of twenty.
        Assert.Equal(
            actual: (ranges.FreeCount, ranges.FreeRanges, ranges.LargestFree),
            expected: (20u, 1, 20u)
        );
        Assert.Equal(
            actual: ranges.Allocate(count: 15),
            expected: 0u
        );

        // The last range joins the free range before it, then the first joins the free range after it.
        _ = ranges.Free(start: last);
        _ = ranges.Free(start: 0);

        Assert.Equal(
            actual: (ranges.FreeCount, ranges.FreeRanges, ranges.LiveRanges),
            expected: (30u, 1, 0)
        );

        // A range freed between two free neighbours joins both into one.
        var x = ranges.Allocate(count: 10);
        var y = ranges.Allocate(count: 10);
        var z = ranges.Allocate(count: 10);

        _ = ranges.Free(start: x);
        _ = ranges.Free(start: z);

        Assert.Equal(
            actual: ranges.FreeRanges,
            expected: 2
        );

        _ = ranges.Free(start: y);

        Assert.Equal(
            actual: (ranges.FreeCount, ranges.FreeRanges, ranges.LargestFree),
            expected: (30u, 1, 30u)
        );
    }
    [Fact]
    public void AFreedRangeIsReusedAndAThousandCyclesAllocateNothing() {
        var ranges = new GpuRangeAllocator(
            maxRanges: 16,
            size: 1000
        );
        var kept = ranges.Allocate(count: 100);
        var freed = ranges.Allocate(count: 50);

        _ = ranges.Allocate(count: 100);
        _ = ranges.Free(start: freed);

        Assert.Equal(
            actual: ranges.Allocate(count: 50),
            expected: freed
        );
        _ = ranges.Free(start: freed);

        var free = ranges.FreeCount;
        var freeRanges = ranges.FreeRanges;
        var live = ranges.LiveRanges;

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => {
            for (var cycle = 0; (cycle < 1000); cycle++) {
                var a = ranges.Allocate(count: 7);
                var b = ranges.Allocate(count: 13);

                _ = ranges.Free(start: a);
                _ = ranges.Free(start: b);
            }
        }));
        Assert.Equal(
            actual: (ranges.FreeCount, ranges.FreeRanges, ranges.LiveRanges),
            expected: (free, freeRanges, live)
        );
        Assert.Equal(
            actual: ranges.Free(start: kept),
            expected: 100u
        );
    }
    [Fact]
    public void ADoubleFreeIsRefusedByName() {
        var ranges = new GpuRangeAllocator(
            maxRanges: 8,
            size: 100
        );
        var start = ranges.Allocate(count: 10);

        _ = ranges.Allocate(count: 10);
        _ = ranges.Free(start: start);

        Assert.Contains(
            actualString: Assert.Throws<InvalidOperationException>(testCode: () => ranges.Free(start: start)).Message,
            expectedSubstring: "The range at 0 is already free"
        );
    }
    [InlineData(5u)]
    [InlineData(100u)]
    [Theory]
    public void AFreeOfARangeNeverAllocatedIsRefusedByName(uint start) {
        var ranges = new GpuRangeAllocator(
            maxRanges: 8,
            size: 100
        );

        // A start inside a live range, and one past the span, start no live range.
        _ = ranges.Allocate(count: 10);
        _ = ranges.Allocate(count: 90);

        Assert.Contains(
            actualString: Assert.Throws<InvalidOperationException>(testCode: () => ranges.Free(start: start)).Message,
            expectedSubstring: $"No range was allocated at {start}"
        );
    }
}
