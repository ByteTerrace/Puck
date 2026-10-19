using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuMemoryRanges"/>: first fit at each alignment, the gap an alignment leaves kept free and handed to
/// a later fit, a freed range merged with both neighbours until the block is one free range again, and a refusal of every
/// range that is empty, misaligned, past the block or already free.
/// </summary>
public sealed class GpuMemoryRangesLawTests {
    [Fact]
    public void RangesFitFirstAtTheirAlignmentAndMergeBackIntoOneFreeBlock() {
        var ranges = new GpuMemoryRanges(capacity: 256UL);

        Assert.Equal(expected: 256UL, actual: ranges.Capacity);
        Assert.True(condition: ranges.IsEmpty);
        Assert.True(condition: ranges.TryAllocate(alignment: 0UL, offset: out var first, size: 16UL));
        Assert.True(condition: ranges.TryAllocate(alignment: 64UL, offset: out var aligned, size: 32UL));
        // The gap the alignment left before 64 takes the next resource that fits it.
        Assert.True(condition: ranges.TryAllocate(alignment: 1UL, offset: out var gap, size: 40UL));
        Assert.Equal(expected: (0UL, 64UL, 16UL), actual: (first, aligned, gap));
        Assert.False(condition: ranges.TryAllocate(alignment: 1UL, offset: out var none, size: 300UL));
        Assert.Equal(expected: 0UL, actual: none);
        Assert.False(condition: ranges.IsEmpty);

        // Freed in an order that merges with the range after, both sides, and the range before.
        ranges.Free(offset: 64UL, size: 32UL);
        ranges.Free(offset: 16UL, size: 40UL);
        Assert.False(condition: ranges.IsEmpty);
        ranges.Free(offset: 0UL, size: 16UL);
        Assert.True(condition: ranges.IsEmpty);
        Assert.True(condition: ranges.TryAllocate(alignment: 1UL, offset: out var whole, size: 256UL));
        Assert.Equal(expected: 0UL, actual: whole);
    }
    [Fact]
    public void AnAlignmentPastAFreeRangeOrPastTheAddressSpaceSkipsIt() {
        var small = new GpuMemoryRanges(capacity: 8UL);

        Assert.True(condition: small.TryAllocate(alignment: 1UL, offset: out _, size: 4UL));
        Assert.False(condition: small.TryAllocate(alignment: 16UL, offset: out _, size: 1UL));

        var huge = new GpuMemoryRanges(capacity: ulong.MaxValue);

        Assert.True(condition: huge.TryAllocate(alignment: 1UL, offset: out _, size: (ulong.MaxValue - 1UL)));
        Assert.False(condition: huge.TryAllocate(alignment: 4UL, offset: out _, size: 1UL));
    }
    [Fact]
    public void EmptyMisalignedOutOfBlockAndAlreadyFreeRangesAreRefused() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new GpuMemoryRanges(capacity: 0UL));

        var ranges = new GpuMemoryRanges(capacity: 64UL);

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ranges.TryAllocate(alignment: 1UL, offset: out _, size: 0UL));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ranges.TryAllocate(alignment: 3UL, offset: out _, size: 1UL));
        Assert.True(condition: ranges.TryAllocate(alignment: 1UL, offset: out _, size: 16UL));
        Assert.True(condition: ranges.TryAllocate(alignment: 1UL, offset: out _, size: 16UL));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ranges.Free(offset: 0UL, size: 0UL));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ranges.Free(offset: 65UL, size: 1UL));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ranges.Free(offset: 60UL, size: 8UL));
        // A range reaching into the free tail, and one overlapping a free range before it.
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ranges.Free(offset: 16UL, size: 20UL));
        ranges.Free(offset: 0UL, size: 16UL);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ranges.Free(offset: 8UL, size: 8UL));
    }
}
