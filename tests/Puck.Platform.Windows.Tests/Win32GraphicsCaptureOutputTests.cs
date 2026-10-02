using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>A desktop capture's frames take the format and color space of the display it reads: an SDR display the
/// B8G8R8A8 sRGB frames every capture had, converted by the RGBA8 copy, and an HDR display, in either HDR color space,
/// half-float scRGB, converted by the transfer pass into working values.</summary>
public sealed class Win32GraphicsCaptureOutputTests {
    [InlineData(DisplayColorSpace.Srgb, DisplayColorSpace.Hdr10)]
    [InlineData(DisplayColorSpace.Hdr10, DisplayColorSpace.Srgb)]
    [SupportedOSPlatform("windows10.0.19041")]
    [Theory]
    public void An_hdr_toggle_invalidates_the_open_capture(DisplayColorSpace opened, DisplayColorSpace current) {
        var display = new Win32CaptureDisplay(colorSpace: opened);

        Assert.True(condition: display.IsCurrent(colorSpace: opened));
        Assert.False(condition: display.IsCurrent(colorSpace: current));
    }
    [InlineData(DisplayColorSpace.Srgb, DisplayColorSpace.Srgb)]
    [InlineData(DisplayColorSpace.Hdr10, DisplayColorSpace.Hdr10)]
    [InlineData(DisplayColorSpace.Hdr10, DisplayColorSpace.ScRgb)]
    [InlineData(DisplayColorSpace.ScRgb, DisplayColorSpace.Hdr10)]
    [SupportedOSPlatform("windows10.0.19041")]
    [Theory]
    public void A_move_to_a_display_with_the_same_capture_encoding_keeps_the_open_capture(DisplayColorSpace opened, DisplayColorSpace current) {
        Assert.True(condition: new Win32CaptureDisplay(colorSpace: opened).IsCurrent(colorSpace: current));
    }
    [InlineData(DisplayColorSpace.Srgb)]
    [InlineData(DisplayColorSpace.Hdr10)]
    [SupportedOSPlatform("windows10.0.19041")]
    [Theory]
    public void Failed_discovery_invalidates_the_open_capture(DisplayColorSpace opened) {
        Assert.False(condition: new Win32CaptureDisplay(colorSpace: opened).IsCurrent(colorSpace: null));
    }
    [Fact]
    [SupportedOSPlatform("windows10.0.19041")]
    public void Unknown_display_discovery_cannot_open_an_sdr_capture() {
        _ = Assert.Throws<NotSupportedException>(testCode: () => new Win32CaptureDisplay(colorSpace: null));
    }
    [Fact]
    [SupportedOSPlatform("windows10.0.19041")]
    public void An_sdr_display_is_captured_as_before_and_converts_through_the_rgba_copy() {
        var output = Win32GraphicsCaptureFeed.CaptureOutputOf(display: DisplayColorSpace.Srgb);

        Assert.Equal(expected: DisplayOutput.Sdr(format: GpuPixelFormat.B8G8R8A8Unorm), actual: output);
        Assert.False(condition: output.IsHdr);
        Assert.Equal(
            expected: ImageSourceConversion.RgbaPass,
            actual: ImageSourceConversion.PassOf(
                color: ImageColorEncoding.Of(colorSpace: output.ColorSpace),
                format: ImagePixelFormat.B8G8R8A8Unorm
            )
        );
    }
    [InlineData(DisplayColorSpace.Hdr10)]
    [InlineData(DisplayColorSpace.ScRgb)]
    [SupportedOSPlatform("windows10.0.19041")]
    [Theory]
    public void An_hdr_display_is_captured_in_half_float_scrgb_and_converts_through_the_transfer_pass(DisplayColorSpace display) {
        var output = Win32GraphicsCaptureFeed.CaptureOutputOf(display: display);

        Assert.Equal(expected: new DisplayOutput(ColorSpace: DisplayColorSpace.ScRgb, Format: GpuPixelFormat.R16G16B16A16Float), actual: output);
        Assert.True(condition: output.IsHdr);
        Assert.Equal(
            expected: ImageSourceConversion.TransferPass,
            actual: ImageSourceConversion.PassOf(
                color: ImageColorEncoding.Of(colorSpace: output.ColorSpace),
                format: ImagePixelFormat.R16G16B16A16Float
            )
        );
    }
    [Fact]
    [SupportedOSPlatform("windows10.0.19041")]
    public void An_undefined_display_color_space_is_refused() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => Win32GraphicsCaptureFeed.CaptureOutputOf(display: ((DisplayColorSpace)3)));
    }
}
