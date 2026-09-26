using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Windows.Win32.Graphics.Dxgi.Common;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Pins, without a device, that every neutral pixel format maps to its own <c>DXGI_FORMAT</c>, the sRGB and
/// 10-bit formats a Vulkan swapchain may be created in included.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuFormatsLawTests {
    [Fact]
    public void EveryPixelFormatMapsToItsOwnDxgiFormat() {
        var formats = Enum.GetValues<GpuPixelFormat>();
        var dxgiFormats = formats.Select(selector: static format => DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format)).ToArray();

        Assert.Equal(
            expected: formats.Length,
            actual: dxgiFormats.Distinct().Count()
        );
        Assert.DoesNotContain(
            collection: dxgiFormats,
            expected: DXGI_FORMAT.DXGI_FORMAT_UNKNOWN
        );
    }
    [InlineData(GpuPixelFormat.R8G8B8A8Srgb, DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB)]
    [InlineData(GpuPixelFormat.B8G8R8A8Srgb, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)]
    [InlineData(GpuPixelFormat.R10G10B10A2Unorm, DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM)]
    [Theory]
    public void TheSwapchainFormatsMapToTheirDxgiFormats(GpuPixelFormat format, DXGI_FORMAT expected) =>
        Assert.Equal(
            expected: expected,
            actual: DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: format)
        );
}
