using Xunit;

namespace Puck.Assets.Tests;

public sealed class RgbaFrameDifferenceLawTests {
    [Fact]
    public void VisibleChangesUseTheLargestRgbDeltaAtTheInclusiveNoiseBoundary() {
        byte[] before = [10, 20, 30, 0, 10, 20, 30, 255, 10, 20, 30, 0, 10, 20, 30, 255];
        byte[] after = [11, 19, 31, 255, 12, 20, 30, 0, 10, 18, 30, 255, 10, 20, 255, 0];

        Assert.Equal(expected: new RgbaFrameDifference(ChangedPixels: 3, MaxDelta: 225) { MeanAbsoluteDelta = 232.0 / 12.0 },
            actual: RgbaFrameDifference.Measure(after: after, before: before));
        Assert.Equal(expected: new RgbaFrameDifference(ChangedPixels: 3, MaxDelta: 225) { MeanAbsoluteDelta = 232.0 / 12.0 },
            actual: RgbaFrameDifference.Measure(after: before, before: after));
    }
    [Fact]
    public void AlphaAndSubthresholdRgbChangesDoNotCountAsChangedPixels() {
        Assert.Equal(expected: new RgbaFrameDifference(ChangedPixels: 0, MaxDelta: 1) { MeanAbsoluteDelta = 1.0 / 3.0 },
            actual: RgbaFrameDifference.Measure(after: [1, 0, 0, 255], before: [0, 0, 0, 0]));
        Assert.Equal(expected: default, actual: RgbaFrameDifference.Measure(after: [], before: []));
    }
    [InlineData(4, 8)]
    [InlineData(3, 3)]
    [Theory]
    public void MalformedPackedFramesAreRefused(int beforeBytes, int afterBytes) =>
        Assert.Throws<ArgumentException>(testCode: () => RgbaFrameDifference.Measure(
            after: new byte[afterBytes], before: new byte[beforeBytes]));
}
