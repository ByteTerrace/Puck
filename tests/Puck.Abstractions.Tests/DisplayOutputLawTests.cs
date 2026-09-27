using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Tests;

/// <summary>Laws for choosing a display output (<see cref="DisplayOutput.TrySelect"/>) over synthetic capability lists:
/// SDR in an 8-bit unsigned normalized format by default and as the fallback; HDR10 or scRGB only when requested and
/// reported in its own format; the preferred SDR format when reported; never the order a display reports in; and no
/// output when nothing a swapchain may take is reported. The presentation options ask for SDR, and their paper white
/// defaults to the SDR white level, refuses a level outside its range, and leaves SDR white at one.</summary>
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
            actual: Chosen(preferred: GpuPixelFormat.R8G8B8A8Unorm, reported: [SdrBgra, SdrRgba], requested: DisplayColorSpace.Srgb),
            expected: SdrRgba
        );
        // A preference not reported, or one no SDR swapchain takes, falls to the order of SdrFormats.
        Assert.Equal(
            actual: (Chosen(preferred: GpuPixelFormat.R10G10B10A2Unorm, reported: [SdrRgba, SdrBgra], requested: DisplayColorSpace.Srgb), Chosen(reported: [SdrRgba, DisplayOutput.Sdr(format: GpuPixelFormat.D32Float)], requested: DisplayColorSpace.Srgb, preferred: GpuPixelFormat.D32Float)),
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
    public void PaperWhiteDefaultsToTheSdrWhiteLevelAndRefusesALevelOutsideItsRange() {
        Assert.Equal(
            actual: (new PresentationOptions().PaperWhiteNits, new PresentationOptions { PaperWhiteNits = 203.0 }.PaperWhiteNits, new PresentationOptions { PaperWhiteNits = DisplayOutput.MaxPaperWhiteNits }.PaperWhiteNits),
            expected: (DisplayOutput.SdrWhiteNits, 203.0, 10_000.0)
        );

        foreach (var level in ((ReadOnlySpan<double>)[79.999, 10_000.001, double.NaN, double.PositiveInfinity, 0.0, -80.0])) {
            Assert.Equal(
                actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new PresentationOptions { PaperWhiteNits = level }).ParamName,
                expected: "nits"
            );
        }
    }
    [Fact]
    public void SdrWhiteIsOneAtEveryPaperWhiteAndHdrWhiteFollowsTheLevel() {
        foreach (var level in ((ReadOnlySpan<double>)[DisplayOutput.SdrWhiteNits, 203.0, 1_000.0, DisplayOutput.MaxPaperWhiteNits])) {
            Assert.Equal(
                actual: (SdrBgra.WhiteScale(paperWhiteNits: level), SdrRgba.WhiteScale(paperWhiteNits: level)),
                expected: (1.0, 1.0)
            );
            Assert.Equal(
                actual: (ScRgb.WhiteScale(paperWhiteNits: level), Hdr10.WhiteScale(paperWhiteNits: level)),
                expected: ((level / 80.0), (level / 10_000.0))
            );
        }

        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => SdrBgra.WhiteScale(paperWhiteNits: 40.0));
    }
    [Fact]
    public void ThePresentationOptionsAskForSdr() =>
        Assert.Equal(
            actual: new PresentationOptions().ColorSpace,
            expected: DisplayColorSpace.Srgb
        );
}
