using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class CameraFramePackingTests {
    [Fact]
    public void Packs_positive_stride_without_row_padding() {
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 9, 10, 11, 12, 13, 14, 15, 16];
        var destination = new byte[16];

        Assert.True(condition: CameraFramePacking.TryPackBgra(
            destination: destination,
            height: 2,
            source: source,
            sourceStride: 10,
            width: 2
        ));
        Assert.Equal(actual: destination, expected: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
    }
    [Fact]
    public void Flips_negative_stride_to_top_down_bgra() {
        byte[] source = [9, 10, 11, 12, 13, 14, 15, 16, 1, 2, 3, 4, 5, 6, 7, 8];
        var destination = new byte[16];

        Assert.True(condition: CameraFramePacking.TryPackBgra(
            destination: destination,
            height: 2,
            source: source,
            sourceStride: -8,
            width: 2
        ));
        Assert.Equal(actual: destination, expected: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
    }
    [Fact]
    public void Expands_padded_bottom_up_luminance_and_reports_sum() {
        byte[] source = [30, 40, 99, 99, 10, 20];
        var destination = new byte[16];

        Assert.True(condition: CameraFramePacking.TryExpandLuminance(
            destination: destination,
            height: 2,
            luminanceSum: out var sum,
            source: source,
            sourceStride: -4,
            width: 2
        ));
        Assert.Equal(actual: sum, expected: 100L);
        Assert.Equal(actual: destination, expected: new byte[] { 10, 10, 10, 255, 20, 20, 20, 255, 30, 30, 30, 255, 40, 40, 40, 255 });
    }
    [Fact]
    public void Rejects_short_frames_and_impossible_stride() {
        Span<byte> destination = stackalloc byte[16];

        Assert.False(condition: CameraFramePacking.TryPackBgra(destination: destination, height: 2, source: new byte[15], sourceStride: 8, width: 2));
        Assert.False(condition: CameraFramePacking.TryPackBgra(destination: destination, height: 2, source: new byte[16], sourceStride: 7, width: 2));
    }
}
