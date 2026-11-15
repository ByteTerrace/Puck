using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuRangeAllocator"/>: a request of exactly the free size fits and one more is refused by name
/// with the request, the largest free range and the size; a request larger than a freed middle range is refused until
/// its neighbours free and coalesce; a freed range is the one the next equal request receives, and a thousand
/// allocate-free cycles leave the span as they found it, allocating nothing; ranges fit first at their alignment, the gap
/// it leaves takes a later fit, and an alignment past a run or the address space skips it; live ranges past the first
/// arrays grow them; a double free and a free of a range never allocated are refused by name.
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
            expected: 0UL
        );

        var refusal = Assert.Throws<GpuRangeExhaustedException>(testCode: () => ranges.Allocate(count: 61));

        Assert.Equal(
            actual: (refusal.Request, refusal.LargestFree, refusal.Size),
            expected: (61UL, 60UL, 100UL)
        );
        Assert.Contains(
            actualString: refusal.Message,
            expectedSubstring: "A range of 61 is refused: the largest free range holds 60 of this span's 100"
        );
        Assert.Equal(
            actual: ranges.Allocate(count: 60),
            expected: 40UL
        );
        Assert.Equal(
            actual: (ranges.FreeCount, ranges.FreeRanges, ranges.LargestFree),
            expected: (0UL, 0, 0UL)
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
            expected: (10UL, 10UL)
        );
        Assert.Equal(
            actual: Assert.Throws<GpuRangeExhaustedException>(testCode: () => ranges.Allocate(count: 15)).LargestFree,
            expected: 10UL
        );

        _ = ranges.Free(start: first);

        // The first range joins the freed middle into one free range of twenty.
        Assert.Equal(
            actual: (ranges.FreeCount, ranges.FreeRanges, ranges.LargestFree),
            expected: (20UL, 1, 20UL)
        );
        Assert.Equal(
            actual: ranges.Allocate(count: 15),
            expected: 0UL
        );

        // The last range joins the free range before it, then the first joins the free range after it.
        _ = ranges.Free(start: last);
        _ = ranges.Free(start: 0);

        Assert.Equal(
            actual: (ranges.FreeCount, ranges.FreeRanges, ranges.LiveRanges),
            expected: (30UL, 1, 0)
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
            expected: (30UL, 1, 30UL)
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
            expected: 100UL
        );
    }
    [Fact]
    public void RangesFitFirstAtTheirAlignmentAndTheGapItLeavesTakesALaterFit() {
        var ranges = new GpuRangeAllocator(size: 256UL);

        Assert.Equal(expected: 0UL, actual: ranges.Allocate(alignment: 0UL, count: 16UL));
        Assert.Equal(expected: 64UL, actual: ranges.Allocate(alignment: 64UL, count: 32UL));
        // The gap the alignment left before 64 takes the next range that fits it.
        Assert.Equal(expected: 16UL, actual: ranges.Allocate(count: 40UL));
        Assert.False(condition: ranges.TryAllocate(count: 300UL, start: out var none));
        Assert.Equal(expected: 0UL, actual: none);

        // Freed in an order that merges with the range after, both sides, and the range before.
        Assert.Equal(expected: 32UL, actual: ranges.Free(start: 64UL));
        Assert.Equal(expected: 40UL, actual: ranges.Free(start: 16UL));
        Assert.Equal(expected: 1, actual: ranges.LiveRanges);
        Assert.Equal(expected: 16UL, actual: ranges.Free(start: 0UL));
        Assert.Equal(expected: (0, 1, 256UL), actual: (ranges.LiveRanges, ranges.FreeRanges, ranges.LargestFree));

        // An alignment past a free run, or past the address space, skips it; one not a power of two is refused.
        var small = new GpuRangeAllocator(size: 8UL);

        _ = small.Allocate(count: 4UL);
        Assert.False(condition: small.TryAllocate(alignment: 16UL, count: 1UL, start: out _));

        var huge = new GpuRangeAllocator(size: ulong.MaxValue);

        _ = huge.Allocate(count: (ulong.MaxValue - 1UL));
        Assert.False(condition: huge.TryAllocate(alignment: 4UL, count: 1UL, start: out _));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => huge.TryAllocate(alignment: 3UL, count: 1UL, start: out _));
    }
    [Fact]
    public void LiveRangesPastTheFirstArraysGrowThemAndStayExact() {
        var ranges = new GpuRangeAllocator(size: 4096UL);
        var starts = new List<ulong>();

        // Aligned ranges each leave a gap, so the free runs grow with the live ones past the arrays' first length.
        for (var index = 0; (index < 100); index++) {
            starts.Add(item: ranges.Allocate(alignment: 32UL, count: 17UL));
        }
        Assert.Equal(expected: Enumerable.Range(start: 0, count: 100).Select(selector: static index => ((ulong)index * 32UL)), actual: starts);
        Assert.Equal(expected: (100, 100), actual: (ranges.LiveRanges, ranges.FreeRanges));
        foreach (var start in starts) { _ = ranges.Free(start: start); }
        Assert.Equal(expected: (0, 1, 4096UL), actual: (ranges.LiveRanges, ranges.FreeRanges, ranges.FreeCount));
    }
    [Fact]
    public void AFreeNamingAnEarlierTicketLeavesTheRangeGivenAgainLive() {
        var ranges = new GpuRangeAllocator(size: 100UL);
        var start = ranges.Allocate(count: 10UL);
        var stale = ranges.TicketOf(start: start);

        _ = ranges.Free(start: start, ticket: stale);
        Assert.Equal(expected: start, actual: ranges.Allocate(count: 10UL));

        var fresh = ranges.TicketOf(start: start);

        Assert.NotEqual(expected: stale, actual: fresh);
        Assert.Contains(
            actualString: Assert.Throws<InvalidOperationException>(testCode: () => ranges.Free(start: start, ticket: stale)).Message,
            expectedSubstring: "was freed and given again"
        );
        Assert.Equal(expected: 1, actual: ranges.LiveRanges);
        Assert.Equal(expected: 10UL, actual: ranges.Free(start: start, ticket: fresh));
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
    [InlineData(5UL)]
    [InlineData(100UL)]
    [Theory]
    public void AFreeOfARangeNeverAllocatedIsRefusedByName(ulong start) {
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
