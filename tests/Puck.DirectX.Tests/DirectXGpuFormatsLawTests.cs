using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Windows.Win32.Graphics.Dxgi.Common;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Pins, without a device, that every neutral pixel format maps to its own <c>DXGI_FORMAT</c>, the sRGB and
/// 10-bit formats a Vulkan swapchain may be created in included, and every display color space to its own
/// <c>DXGI_COLOR_SPACE_TYPE</c>.</summary>
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
    [InlineData(DisplayColorSpace.Srgb, DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709)]
    [InlineData(DisplayColorSpace.Hdr10, DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020)]
    [InlineData(DisplayColorSpace.ScRgb, DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709)]
    [Theory]
    public void EveryDisplayColorSpaceMapsToItsDxgiColorSpace(DisplayColorSpace colorSpace, DXGI_COLOR_SPACE_TYPE expected) =>
        Assert.Equal(
            expected: expected,
            actual: DirectXGpuFormats.ToDxgiColorSpace(colorSpace: colorSpace)
        );
}
