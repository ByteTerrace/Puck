using System.Buffers;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Tests;

public sealed class RentedArraysLawTests {
    private readonly record struct Pixel(byte R, byte G, byte B);

    [Fact]
    public void GrowthSkipsTheFirstBucketOverTheByteLimit() {
        // Three-byte elements put the last eligible bucket at 768 KiB and the next at 1.5 MiB.
        const int PooledLength = (1 << 18);
        const int GrownLength = (PooledLength + 1);
        var small = RentedArrays.Rent<Pixel>(minimumLength: PooledLength);
        var pixel = new Pixel(1, 2, 3);

        small[0] = pixel;
        var grown = RentedArrays.Grow(array: small, keep: 1, minimumLength: GrownLength);

        using var smallLease = RentedArrays.Rent<Pixel>(minimumLength: PooledLength, array: out var reused);
        Assert.Same(expected: small, actual: reused);
        Assert.Equal(expected: 786_432, actual: reused.Length * Unsafe.SizeOf<Pixel>());
        Assert.Equal(expected: 786_435, actual: grown.Length * Unsafe.SizeOf<Pixel>());
        Assert.Equal(expected: pixel, actual: grown[0]);
        RentedArrays.Return(array: grown);
    }

    [Fact]
    public void ReturningLargeArraysLeavesNoArrayInTheSharedBucket() {
        const int LargeLength = (1 << 19);
        var shared = ArrayPool<Pixel>.Shared;

        // Re-rent on the same thread to count the array and payload the shared pool would otherwise keep.
        var control = shared.Rent(minimumLength: LargeLength);
        shared.Return(array: control);
        var checkedOut = shared.Rent(minimumLength: LargeLength);

        try {
            Assert.Same(expected: control, actual: checkedOut);
            Assert.Equal(expected: 1_572_864, actual: checkedOut.Length * Unsafe.SizeOf<Pixel>());

            var large = RentedArrays.Rent<Pixel>(minimumLength: LargeLength);
            RentedArrays.Return(array: large);
            var next = shared.Rent(minimumLength: LargeLength);

            try {
                Assert.NotSame(expected: large, actual: next);
            } finally {
                shared.Return(array: next);
            }
        } finally {
            shared.Return(array: checkedOut);
        }
    }
}
