using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class CameraFramePackingTests {
    [Fact]
    public void Packs_positive_stride_without_row_padding() {
        byte[] source = [1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 9, 10, 11, 12, 13, 14, 15, 16];
        var destination = new byte[16];

        Assert.True(CameraFramePacking.TryPackBgra(
            destination: destination,
            height: 2,
            source: source,
            sourceStride: 10,
            width: 2
        ));
        Assert.Equal(expected: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, actual: destination);
    }
    [Fact]
    public void Flips_negative_stride_to_top_down_bgra() {
        byte[] source = [9, 10, 11, 12, 13, 14, 15, 16, 1, 2, 3, 4, 5, 6, 7, 8];
        var destination = new byte[16];

        Assert.True(CameraFramePacking.TryPackBgra(
            destination: destination,
            height: 2,
            source: source,
            sourceStride: -8,
            width: 2
        ));
        Assert.Equal(expected: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, actual: destination);
    }
    [Fact]
    public void Expands_padded_bottom_up_luminance_and_reports_sum() {
        byte[] source = [30, 40, 99, 99, 10, 20];
        var destination = new byte[16];

        Assert.True(CameraFramePacking.TryExpandLuminance(
            destination: destination,
            height: 2,
            luminanceSum: out var sum,
            source: source,
            sourceStride: -4,
            width: 2
        ));
        Assert.Equal(expected: 100L, actual: sum);
        Assert.Equal(expected: new byte[] { 10, 10, 10, 255, 20, 20, 20, 255, 30, 30, 30, 255, 40, 40, 40, 255 }, actual: destination);
    }
    [Fact]
    public void Rejects_short_frames_and_impossible_stride() {
        Span<byte> destination = stackalloc byte[16];

        Assert.False(CameraFramePacking.TryPackBgra(source: new byte[15], width: 2, height: 2, sourceStride: 8, destination: destination));
        Assert.False(CameraFramePacking.TryPackBgra(source: new byte[16], width: 2, height: 2, sourceStride: 7, destination: destination));
    }
}
