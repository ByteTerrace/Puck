using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for the transfer pass's working values, the CPU reference an HDR capture converts through. A sample of a known
/// luminance, stored as an HDR10 perceptual-quantizer code or an scRGB half float, converts to the working value the
/// sRGB curve gives its luminance over the paper white, and the display encode's own decode of that value at the same
/// paper white returns the luminance, so nothing above SDR white is clipped and nothing is encoded twice. Each law carries
/// red legs: the clipped, linear, doubly encoded and fixed-white answers it must tell apart from the right one.
/// </summary>
public sealed class ImageSourceWorkingSpaceLawTests {
    // The luminances the laws convert, in cd/m²: SDR white, the BT.2408 HDR reference white, a common HDR peak and the
    // perceptual quantizer's ceiling.
    private static readonly double[] Luminances = [80.0, 203.0, 1_000.0, 10_000.0];
    // The paper-white levels the laws convert at: the default, and the HDR reference white.
    private static readonly double[] PaperWhites = [DisplayOutput.SdrWhiteNits, 203.0];

    // SMPTE ST 2084's inverse: the code that decodes to a luminance.
    private static double PqEncode(double nits) {
        const double M1 = (2610.0 / 16384.0);
        const double M2 = ((2523.0 / 4096.0) * 128.0);
        const double C1 = (3424.0 / 4096.0);
        const double C2 = ((2413.0 / 4096.0) * 32.0);
        const double C3 = ((2392.0 / 4096.0) * 32.0);
        var power = Math.Pow(
            x: (nits / 10_000.0),
            y: M1
        );

        return Math.Pow(
            x: ((C1 + (C2 * power)) / (1.0 + (C3 * power))),
            y: M2
        );
    }
    // The sRGB encode (IEC 61966-2-1) of a linear value at or above the linear segment, written out by hand.
    private static double SrgbEncode(double linear) =>
        ((1.055 * Math.Pow(
            x: linear,
            y: (1.0 / 2.4)
        )) - 0.055);
    // display-encode.frag.hlsl's displayToLinear: the decode the display encode applies to a working value.
    private static double DisplayToLinear(double value) {
        var magnitude = Math.Abs(value: value);
        var curve = ((magnitude <= 0.04045)
            ? (magnitude / 12.92)
            : Math.Pow(
                x: ((magnitude + 0.055) / 1.055),
                y: 2.4
            ));

        return ((value < 0.0) ? -curve : curve);
    }
    // The luminance an HDR output shows a working value at: the display encode's decode scaled by its white scale, in
    // scRGB, whose one is SDR white.
    private static double DisplayedNits(double working, double paperWhiteNits) =>
        ((DisplayToLinear(value: working) * new DisplayOutput(
            ColorSpace: DisplayColorSpace.ScRgb,
            Format: GpuPixelFormat.R16G16B16A16Float
        ).WhiteScale(paperWhiteNits: paperWhiteNits)) * DisplayOutput.SdrWhiteNits);
    // A red leg: the wrong answer lies outside the tolerance the right one is held to.
    private static void Differs(double expected, double actual, double tolerance) =>
        Assert.True(
            condition: (Math.Abs(value: (expected - actual)) > tolerance),
            userMessage: $"{actual} lies within {tolerance} of {expected}"
        );

    private static ImageColorEncoding Hdr10 => ImageColorEncoding.Of(colorSpace: DisplayColorSpace.Hdr10);
    private static ImageColorEncoding ScRgb => ImageColorEncoding.Of(colorSpace: DisplayColorSpace.ScRgb);

    // One neutral sample's working value, which every channel shares.
    private static double NeutralWorking(ImageColorEncoding color, double stored, double paperWhiteNits) {
        var (r, g, b) = ImageSourceConversion.ToWorking(
            b: stored,
            color: color,
            g: stored,
            paperWhiteNits: paperWhiteNits,
            r: stored
        );

        // BT.2020's neutral is BT.709's to within the matrix's rounding.
        Assert.Equal(expected: r, actual: g, tolerance: (Math.Abs(value: r) * 1e-6));
        Assert.Equal(expected: r, actual: b, tolerance: (Math.Abs(value: r) * 1e-6));

        return r;
    }
    private static byte[] Region(ImagePixelFormat format, ImageColorEncoding color, uint width, out ImageSourceUploadHeader header) {
        header = ImageSourceUploadLayout.HeaderOf(
            color: color,
            format: format,
            height: 1U,
            width: width
        );

        var region = new byte[ImageSourceUploadLayout.ByteCount(header: in header)];

        ImageSourceUploadLayout.Write(
            header: in header,
            region: region
        );

        return region;
    }

    /// <summary>The working value of a luminance is the sRGB curve at the luminance over the paper white: exactly one at
    /// the paper white, above one past it.</summary>
    [Fact]
    public void TheWorkingValuesAtKnownLuminancesAreTheSrgbCurveAtTheirRatioToPaperWhite() {
        // Closed forms the curve gives, by hand: 1.055 (L / W)^(1 / 2.4) - 0.055.
        (double PaperWhite, double Nits, double Working)[] expected = [
            (80.0, 80.0, 1.0),
            (80.0, 203.0, 1.500088038480396),
            (80.0, 1_000.0, 2.967026709176326),
            (80.0, 10_000.0, 7.832964820941938),
            (203.0, 80.0, 0.6607311820671116),
            (203.0, 203.0, 1.0),
            (203.0, 1_000.0, 1.9951978661584413),
            (203.0, 10_000.0, 5.296338753930395),
        ];

        foreach (var (paperWhite, nits, working) in expected) {
            Assert.Equal(expected: working, actual: SrgbEncode(linear: (nits / paperWhite)), tolerance: 1e-12);
            Assert.Equal(expected: working, actual: ImageSourceConversion.LinearToWorking(linear: (nits / paperWhite)), tolerance: 1e-12);
            Assert.Equal(
                expected: working,
                actual: NeutralWorking(
                    color: ScRgb,
                    paperWhiteNits: paperWhite,
                    stored: (nits / DisplayOutput.SdrWhiteNits)
                ),
                tolerance: 1e-12
            );
            // A perceptual-quantizer code round-trips its luminance to about 1e-12, and the matrix's rows sum to one within
            // 1e-6, which bounds a neutral's error.
            Assert.Equal(
                expected: working,
                actual: NeutralWorking(
                    color: Hdr10,
                    paperWhiteNits: paperWhite,
                    stored: PqEncode(nits: nits)
                ),
                tolerance: (working * 1e-6)
            );
        }
    }
    /// <summary>The display encode shows each converted luminance at that luminance, at every paper white, for both HDR
    /// encodings: the conversion neither clips above SDR white nor encodes twice. The red legs are the answers that would
    /// break it: a working value clipped to one, linear light written unencoded, the curve applied twice, and the
    /// perceptual quantizer divided by a fixed 203 cd/m² whatever the paper white.</summary>
    [Fact]
    public void TheDisplayEncodeShowsEachConvertedLuminanceAtThatLuminance() {
        foreach (var paperWhite in PaperWhites) {
            foreach (var nits in Luminances) {
                var linear = (nits / paperWhite);

                foreach (var (color, stored) in new[] { (ScRgb, (nits / DisplayOutput.SdrWhiteNits)), (Hdr10, PqEncode(nits: nits)) }) {
                    var working = NeutralWorking(
                        color: color,
                        paperWhiteNits: paperWhite,
                        stored: stored
                    );
                    var tolerance = (nits * 1e-5);

                    Assert.Equal(expected: nits, actual: DisplayedNits(paperWhiteNits: paperWhite, working: working), tolerance: tolerance);

                    // Red legs: each wrong answer shows another luminance, except where it happens to coincide.
                    if (linear > 1.0) {
                        Differs(expected: nits, actual: DisplayedNits(paperWhiteNits: paperWhite, working: Math.Min(val1: working, val2: 1.0)), tolerance: tolerance);
                    }

                    if (linear != 1.0) {
                        Differs(expected: nits, actual: DisplayedNits(paperWhiteNits: paperWhite, working: linear), tolerance: tolerance);
                        Differs(expected: nits, actual: DisplayedNits(paperWhiteNits: paperWhite, working: SrgbEncode(linear: SrgbEncode(linear: linear))), tolerance: tolerance);
                    }

                    if (paperWhite != 203.0) {
                        Differs(expected: nits, actual: DisplayedNits(paperWhiteNits: paperWhite, working: SrgbEncode(linear: (nits / 203.0))), tolerance: tolerance);
                    }
                }
            }
        }
    }
    /// <summary>A half-float scRGB region converts pixel by pixel: luminance above SDR white keeps its headroom, a channel
    /// below zero (a color outside BT.709) stays below zero on the mirrored curve, and alpha passes through.</summary>
    [Fact]
    public void AnScRgbHalfFloatRegionKeepsItsHeadroomAndItsNegativeChannels() {
        var region = Region(
            color: ScRgb,
            format: ImagePixelFormat.R16G16B16A16Float,
            header: out var header,
            width: 2U
        );
        var pixels = region.AsSpan(start: ((int)header.Plane0Offset));
        // 1000 cd/m² white, then 400 cd/m² red with a green channel below zero; 12.5 and 5 are exact halves.
        Half[] values = [((Half)12.5), ((Half)12.5), ((Half)12.5), Half.One, ((Half)5.0), ((Half)(-0.25)), Half.Zero, ((Half)0.5)];

        Assert.Equal(expected: 16U, actual: header.Plane0Stride);

        for (var index = 0; (index < values.Length); index++) {
            BinaryPrimitives.WriteHalfLittleEndian(
                destination: pixels[(index * 2)..],
                value: values[index]
            );
        }

        var working = new float[8];

        ImageSourceConversion.ToWorking(
            paperWhiteNits: DisplayOutput.SdrWhiteNits,
            region: region,
            working: working
        );

        Assert.Equal(expected: SrgbEncode(linear: 12.5), actual: working[0], tolerance: 1e-6);
        Assert.Equal(expected: working[0], actual: working[1]);
        Assert.Equal(expected: working[0], actual: working[2]);
        Assert.Equal(expected: 1f, actual: working[3]);
        Assert.Equal(expected: SrgbEncode(linear: 5.0), actual: working[4], tolerance: 1e-6);
        Assert.Equal(expected: -SrgbEncode(linear: 0.25), actual: working[5], tolerance: 1e-6);
        Assert.Equal(expected: 0f, actual: working[6]);
        Assert.Equal(expected: 0.5f, actual: working[7]);
        Assert.True(condition: (working[0] > 1f));
    }
    /// <summary>A BT.2020 primary moves to BT.709 in linear light before the encode: pure HDR10 red at the paper white is
    /// the first column of BT.2407's matrix, with green and blue below zero rather than clipped.</summary>
    [Fact]
    public void AnHdr10PrimaryMovesToBt709BeforeTheEncode() {
        var (r, g, b) = ImageSourceConversion.ToWorking(
            b: 0.0,
            color: Hdr10,
            g: 0.0,
            paperWhiteNits: 203.0,
            r: PqEncode(nits: 203.0)
        );

        Assert.Equal(expected: SrgbEncode(linear: 1.6604910), actual: r, tolerance: 1e-6);
        Assert.Equal(expected: -SrgbEncode(linear: 0.1245505), actual: g, tolerance: 1e-6);
        Assert.Equal(expected: -SrgbEncode(linear: 0.0181508), actual: b, tolerance: 1e-6);
    }
    /// <summary>The paper white must be a level the host accepts.</summary>
    [Fact]
    public void APaperWhiteOutsideTheHostsRangeIsRefused() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ImageSourceConversion.ToLinear(paperWhiteNits: 79.0, transfer: ImageTransferFunction.Pq, value: 0.5));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ImageSourceConversion.ToLinear(paperWhiteNits: double.NaN, transfer: ImageTransferFunction.Linear, value: 0.5));
    }
    /// <summary>Each display color space names the encoding a capture of it reads: sRGB over BT.709, the perceptual
    /// quantizer over BT.2020, and scRGB's linear light over BT.709. An undefined value is refused.</summary>
    [Fact]
    public void EachDisplayColorSpaceNamesTheEncodingACaptureOfItReads() {
        Assert.Equal(expected: ImageColorEncoding.Srgb, actual: ImageColorEncoding.Of(colorSpace: DisplayColorSpace.Srgb));
        Assert.Equal(expected: new ImageColorEncoding(Primaries: ImageColorPrimaries.Bt2020, Transfer: ImageTransferFunction.Pq), actual: Hdr10);
        Assert.Equal(expected: new ImageColorEncoding(Primaries: ImageColorPrimaries.Bt709, Transfer: ImageTransferFunction.Linear), actual: ScRgb);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ImageColorEncoding.Of(colorSpace: ((DisplayColorSpace)3)));
    }
    /// <summary>An SDR capture converts exactly as before: its B8G8R8A8 frames, in the encoding of an sRGB display, take the
    /// RGBA8 copy, which writes every code of every channel back bit for bit with red and blue swapped; no working-space
    /// arithmetic touches them. The red leg is the copy left unswapped, which the law tells apart.</summary>
    [Fact]
    public void AnSdrCaptureConvertsBitForBitAsBefore() {
        var color = ImageColorEncoding.Of(colorSpace: DisplayColorSpace.Srgb);

        Assert.Equal(expected: ImageSourceConversion.RgbaPass, actual: ImageSourceConversion.PassOf(color: color, format: ImagePixelFormat.B8G8R8A8Unorm));

        var region = Region(
            color: color,
            format: ImagePixelFormat.B8G8R8A8Unorm,
            header: out var header,
            width: 256U
        );
        var pixels = region.AsSpan(start: ((int)header.Plane0Offset));

        for (var code = 0; (code < 256); code++) {
            pixels[(code * 4)] = ((byte)code);
            pixels[((code * 4) + 1)] = ((byte)(255 - code));
            pixels[((code * 4) + 2)] = ((byte)((code * 7) & 0xFF));
            pixels[((code * 4) + 3)] = ((byte)((code * 13) & 0xFF));
        }

        var expected = new byte[1024];
        var rgba = new byte[1024];

        for (var code = 0; (code < 256); code++) {
            expected[(code * 4)] = ((byte)((code * 7) & 0xFF));
            expected[((code * 4) + 1)] = ((byte)(255 - code));
            expected[((code * 4) + 2)] = ((byte)code);
            expected[((code * 4) + 3)] = ((byte)((code * 13) & 0xFF));
        }

        ImageSourceConversion.ToRgba8(
            region: region,
            rgba: rgba
        );

        Assert.Equal(actual: rgba, expected: expected);
        Assert.NotEqual(expected: expected, actual: pixels[..1024].ToArray());
    }
}
