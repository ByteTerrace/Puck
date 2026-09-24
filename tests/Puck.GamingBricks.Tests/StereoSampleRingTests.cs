namespace Puck.GamingBricks.Tests;

/// <summary>Pins the shared stereo output ring every GamingBrick audio stage buffers through: overflow drops the oldest
/// frame and keeps exactly the newest capacity, reads hand back whole frames only, and a disabled ring drops every
/// append.</summary>
public sealed class StereoSampleRingTests {
    private static short[] ReadAll(ref StereoSampleRing ring) {
        var destination = new short[ring.SampleCount];

        Assert.Equal(
            actual: ring.Read(destination: destination),
            expected: destination.Length
        );

        return destination;
    }

    [Fact]
    public void ADisabledRingDropsEveryAppend() {
        var ring = default(StereoSampleRing);

        ring.Push(
            left: 1,
            right: 2
        );
        ring.Configure(capacityFrames: 0);
        ring.Push(interleaved: [3, 4, 5, 6]);

        Assert.Equal(
            actual: ring.FrameCount,
            expected: 0
        );
        Assert.Equal(
            actual: ring.Read(destination: new short[8]),
            expected: 0
        );
    }
    [Fact]
    public void AnOddInterleavedAppendIsRefused() {
        var ring = default(StereoSampleRing);

        ring.Configure(capacityFrames: 4);

        Assert.Throws<ArgumentException>(testCode: () => {
            var local = ring;

            local.Push(interleaved: [1, 2, 3]);
        });
    }
    [Fact]
    public void AnOddReadCopiesWholeFramesAndKeepsTheChannelsInPlace() {
        var ring = default(StereoSampleRing);

        ring.Configure(capacityFrames: 4);
        ring.Push(interleaved: [10, -10, 20, -20, 30, -30]);

        var odd = new short[] { 0, 0, 0, 99 };

        Assert.Equal(
            actual: ring.Read(destination: odd.AsSpan(
                length: 3,
                start: 0
            )),
            expected: 2
        );
        Assert.Equal(
            actual: odd,
            expected: new short[] { 10, -10, 0, 99 }
        );
        Assert.Equal(
            actual: ReadAll(ring: ref ring),
            expected: new short[] { 20, -20, 30, -30 }
        );
    }
    [InlineData(1, 5)]
    [InlineData(3, 3)]
    [InlineData(3, 7)]
    [InlineData(4, 13)]
    [Theory]
    public void OverflowDropsTheOldestAndKeepsTheNewestCapacity(int capacity, int pushed) {
        var ring = default(StereoSampleRing);

        ring.Configure(capacityFrames: capacity);

        for (var frame = 0; (frame < pushed); ++frame) {
            ring.Push(
                left: ((short)frame),
                right: ((short)-frame)
            );
        }

        var kept = Math.Min(
            val1: capacity,
            val2: pushed
        );
        var expected = new short[(kept * 2)];

        for (var index = 0; (index < kept); ++index) {
            var frame = ((pushed - kept) + index);

            expected[(index * 2)] = ((short)frame);
            expected[((index * 2) + 1)] = ((short)-frame);
        }

        Assert.Equal(
            actual: ring.FrameCount,
            expected: kept
        );
        Assert.Equal(
            actual: ReadAll(ring: ref ring),
            expected: expected
        );
    }
    [Fact]
    public void ReconfiguringOrClearingDiscardsTheBufferedStream() {
        var ring = default(StereoSampleRing);

        ring.Configure(capacityFrames: 4);
        ring.Push(interleaved: [1, 2, 3, 4]);
        ring.Clear();

        Assert.Equal(
            actual: ring.SampleCount,
            expected: 0
        );

        ring.Push(interleaved: [5, 6]);
        ring.Configure(capacityFrames: 4);

        Assert.Equal(
            actual: ring.SampleCount,
            expected: 0
        );

        ring.Push(interleaved: [7, 8]);

        Assert.Equal(
            actual: ReadAll(ring: ref ring),
            expected: new short[] { 7, 8 }
        );
    }
    [Fact]
    public void ReadsInterleavedWithWritesPreserveOrderAcrossTheWrap() {
        var ring = default(StereoSampleRing);
        var next = ((short)0);
        var expected = ((short)0);
        var destination = new short[6];

        ring.Configure(capacityFrames: 3);

        for (var round = 0; (round < 20); ++round) {
            ring.Push(
                left: next,
                right: ((short)(next + 1_000))
            );
            ++next;

            if ((round % 3) != 0) {
                continue;
            }

            var read = ring.Read(destination: destination);

            for (var index = 0; (index < read); index += 2) {
                Assert.Equal(
                    actual: destination[index],
                    expected: expected
                );
                Assert.Equal(
                    actual: destination[(index + 1)],
                    expected: ((short)(expected + 1_000))
                );
                ++expected;
            }
        }

        // Every frame pushed up to the last read came back once, in order; the final push is still buffered.
        Assert.Equal(
            actual: expected,
            expected: ((short)19)
        );
        Assert.Equal(
            actual: ring.FrameCount,
            expected: 1
        );
    }
}
