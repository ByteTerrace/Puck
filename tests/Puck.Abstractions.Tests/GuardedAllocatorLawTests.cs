using System.Runtime.InteropServices;
using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws over <see cref="GuardedAllocator"/> that hold without a finding (a finding ends the process): a request whose
/// guarded layout cannot be represented fails with null before the inner allocator is asked, a block keeps its alignment
/// and contents through reallocation, and the quarantine charges each block's whole inner request, alignment prefix
/// included, so a small byte limit releases aligned blocks to the inner allocator.
/// </summary>
public sealed unsafe class GuardedAllocatorLawTests {
    [Fact]
    public void AnUnrepresentableRequestFailsWithoutReachingTheInnerAllocator() {
        var inner = new CountingAllocator();
        var guard = new GuardedAllocator(inner: inner);

        Assert.True(condition: (guard.Allocate(size: (nuint.MaxValue - 47)) is null));
        Assert.True(condition: (guard.Allocate(size: 64, alignment: 48) is null));
        Assert.Equal(expected: 0, actual: inner.Allocations);
    }
    [Fact]
    public void AnAlignedBlockKeepsItsAlignmentAndContentsThroughReallocation() {
        var guard = new GuardedAllocator(inner: new CountingAllocator());
        var block = (byte*)guard.Allocate(size: 100, alignment: 256);

        Assert.Equal(expected: 0UL, actual: (((ulong)block) % 256));
        for (var index = 0; (index < 100); index++) { block[index] = (byte)index; }
        var grown = (byte*)guard.Reallocate(alignment: 256, newSize: 300, ptr: block);

        Assert.Equal(expected: 0UL, actual: (((ulong)grown) % 256));
        for (var index = 0; (index < 100); index++) { Assert.Equal(expected: (byte)index, actual: grown[index]); }
        guard.Free(ptr: grown);
        Assert.Equal(expected: 2, actual: guard.Verify());
    }
    [Fact]
    public void TheQuarantineChargesTheAlignmentPrefix() {
        var inner = new CountingAllocator();
        // Each 64-byte block aligned to 1 MiB holds a 1 MiB prefix; a 4 MiB limit keeps at most four of them.
        var guard = new GuardedAllocator(inner: inner, quarantineBytes: (4L << 20));

        for (var block = 0; (block < 16); block++) { guard.Free(ptr: guard.Allocate(size: 64, alignment: (1 << 20))); }
        Assert.InRange(actual: guard.Verify(), low: 1, high: 4);
        Assert.InRange(actual: inner.Frees, low: 12, high: 15);
    }

    private sealed class CountingAllocator : IAllocator {
        public int Allocations { get; private set; }
        public int Frees { get; private set; }

        public void* Allocate(nuint size, nuint alignment = 0) {
            Allocations++;
            return NativeMemory.AlignedAlloc(alignment: ((alignment == 0) ? 16 : alignment), byteCount: size);
        }
        public void Free(void* ptr) {
            if (ptr is null) { return; }
            Frees++;
            NativeMemory.AlignedFree(ptr: ptr);
        }
        public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => throw new NotSupportedException();
    }
}
