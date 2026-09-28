using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Pins, without a device, the rectangle a Direct3D 12 scissor and render-pass clear read from a pixel
/// rectangle: exactly its pixels, so a clear touches the render area and nothing outside it, as Vulkan's does.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXRectLawTests {
    [InlineData(0, 0, 64u, 32u)]
    [InlineData(5, 7, 1u, 1u)]
    [InlineData(32, 0, 32u, 32u)]
    [InlineData((int.MaxValue - 1), (int.MaxValue - 1), 1u, 1u)]
    [Theory]
    public void TheRectangleCoversExactlyThePixels(int x, int y, uint width, uint height) {
        var rect = DirectXGpuRecorder.ToRect(rect: new GpuPixelRect(
            Height: height,
            Width: width,
            X: x,
            Y: y
        ));

        Assert.Equal(
            actual: (rect.left, rect.top, ((long)rect.right), ((long)rect.bottom)),
            expected: (x, y, (((long)x) + width), (((long)y) + height))
        );
    }
    [InlineData(int.MaxValue, 0, 1u, 1u)]
    [InlineData(0, int.MaxValue, 1u, 1u)]
    [InlineData(0, 0, uint.MaxValue, 1u)]
    [InlineData(0, 0, 1u, (((uint)int.MaxValue) + 1u))]
    [Theory]
    public void AnEdgePastTheLargestCoordinateIsRefusedRatherThanWrapped(int x, int y, uint width, uint height) {
        var rect = new GpuPixelRect(
            Height: height,
            Width: width,
            X: x,
            Y: y
        );

        _ = Assert.Throws<OverflowException>(testCode: () => DirectXGpuRecorder.ToRect(rect: rect));
    }
}
