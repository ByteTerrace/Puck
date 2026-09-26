using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Tests;

/// <summary>Laws for choosing a display output (<see cref="DisplayOutput.TrySelect"/>) over synthetic capability lists:
/// SDR in an 8-bit unsigned normalized format by default and as the fallback; HDR10 or scRGB only when requested and
/// reported in its own format; the preferred SDR format when reported; never the order a display reports in; and no
/// output when nothing a swapchain may take is reported. The presentation options ask for SDR.</summary>
public sealed class DisplayOutputLawTests {
    private static readonly DisplayOutput Hdr10 = new(
        ColorSpace: DisplayColorSpace.Hdr10,
        Format: GpuPixelFormat.R10G10B10A2Unorm
    );
    private static readonly DisplayOutput ScRgb = new(
        ColorSpace: DisplayColorSpace.ScRgb,
        Format: GpuPixelFormat.R16G16B16A16Float
    );
    private static readonly DisplayOutput SdrBgra = DisplayOutput.Sdr(format: GpuPixelFormat.B8G8R8A8Unorm);
    private static readonly DisplayOutput SdrRgba = DisplayOutput.Sdr(format: GpuPixelFormat.R8G8B8A8Unorm);

    private static DisplayOutput Chosen(IReadOnlyCollection<DisplayOutput> reported, DisplayColorSpace requested, GpuPixelFormat? preferred = null) {
        Assert.True(condition: DisplayOutput.TrySelect(
            chosen: out var chosen,
            preferredSdrFormat: preferred,
            reported: reported,
            requested: requested
        ));

        return chosen;
    }

    [Fact]
    public void AnSdrRequestTakesSdrWhateverTheDisplayReports() {
        DisplayOutput[] hdrDisplay = [Hdr10, ScRgb, SdrRgba, SdrBgra];

        Assert.Equal(
            actual: (Chosen(reported: hdrDisplay, requested: DisplayColorSpace.Srgb), Chosen(reported: [SdrRgba], requested: DisplayColorSpace.Srgb)),
            expected: (SdrBgra, SdrRgba)
        );
        Assert.False(condition: Chosen(reported: hdrDisplay, requested: DisplayColorSpace.Srgb).IsHdr);
    }
    [Fact]
    public void AnHdrOutputIsChosenOnlyWhenRequestedAndReported() {
        DisplayOutput[] hdrDisplay = [SdrBgra, Hdr10, ScRgb];

        Assert.Equal(
            actual: (Chosen(reported: hdrDisplay, requested: DisplayColorSpace.Hdr10), Chosen(reported: hdrDisplay, requested: DisplayColorSpace.ScRgb)),
            expected: (Hdr10, ScRgb)
        );
        // Requested but not reported, or reported only in the other HDR color space: SDR.
        Assert.Equal(
            actual: (Chosen(reported: [SdrBgra], requested: DisplayColorSpace.Hdr10), Chosen(reported: [SdrBgra, ScRgb], requested: DisplayColorSpace.Hdr10), Chosen(reported: [SdrBgra, Hdr10], requested: DisplayColorSpace.ScRgb)),
            expected: (SdrBgra, SdrBgra, SdrBgra)
        );
    }
    [Fact]
    public void AnHdrColorSpaceReportedInAnotherFormatIsNotChosen() {
        var hdr10InHalfFloat = new DisplayOutput(
            ColorSpace: DisplayColorSpace.Hdr10,
            Format: GpuPixelFormat.R16G16B16A16Float
        );
        var scRgbInEightBits = new DisplayOutput(
            ColorSpace: DisplayColorSpace.ScRgb,
            Format: GpuPixelFormat.B8G8R8A8Unorm
        );

        Assert.Equal(
            actual: (Chosen(reported: [hdr10InHalfFloat, SdrRgba], requested: DisplayColorSpace.Hdr10), Chosen(reported: [scRgbInEightBits, SdrRgba], requested: DisplayColorSpace.ScRgb)),
            expected: (SdrRgba, SdrRgba)
        );
    }
    [Fact]
    public void ThePreferredSdrFormatIsChosenWhenReportedAndTheOrderOfSdrFormatsOtherwise() {
        Assert.Equal(
            actual: Chosen(reported: [SdrBgra, SdrRgba], requested: DisplayColorSpace.Srgb, preferred: GpuPixelFormat.R8G8B8A8Unorm),
            expected: SdrRgba
        );
        // A preference not reported, or one no SDR swapchain takes, falls to the order of SdrFormats.
        Assert.Equal(
            actual: (Chosen(reported: [SdrRgba, SdrBgra], requested: DisplayColorSpace.Srgb, preferred: GpuPixelFormat.R10G10B10A2Unorm), Chosen(reported: [SdrRgba, DisplayOutput.Sdr(format: GpuPixelFormat.D32Float)], requested: DisplayColorSpace.Srgb, preferred: GpuPixelFormat.D32Float)),
            expected: (SdrBgra, SdrRgba)
        );
        // An SDR format reported only in an HDR color space is no SDR output.
        Assert.Equal(
            actual: Chosen(reported: [new DisplayOutput(ColorSpace: DisplayColorSpace.ScRgb, Format: GpuPixelFormat.B8G8R8A8Unorm), SdrRgba], requested: DisplayColorSpace.Srgb, preferred: GpuPixelFormat.B8G8R8A8Unorm),
            expected: SdrRgba
        );
    }
    [Fact]
    public void TheOrderADisplayReportsInNeverDecides() {
        DisplayOutput[] reported = [ScRgb, SdrRgba, Hdr10, SdrBgra, DisplayOutput.Sdr(format: GpuPixelFormat.R10G10B10A2Unorm)];

        foreach (var requested in Enum.GetValues<DisplayColorSpace>()) {
            Assert.Equal(
                actual: Chosen(reported: reported.Reverse().ToArray(), requested: requested),
                expected: Chosen(reported: reported, requested: requested)
            );
        }
    }
    [Fact]
    public void NothingASwapchainMayTakeChoosesNothing() {
        Assert.False(condition: DisplayOutput.TrySelect(
            chosen: out var chosen,
            preferredSdrFormat: GpuPixelFormat.B8G8R8A8Unorm,
            reported: [DisplayOutput.Sdr(format: GpuPixelFormat.D32Float), Hdr10],
            requested: DisplayColorSpace.ScRgb
        ));
        Assert.Equal(
            actual: chosen,
            expected: default
        );
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => DisplayOutput.HdrFormatOf(colorSpace: DisplayColorSpace.Srgb));
    }
    [Fact]
    public void ThePresentationOptionsAskForSdr() =>
        Assert.Equal(
            actual: new PresentationOptions().ColorSpace,
            expected: DisplayColorSpace.Srgb
        );
}
