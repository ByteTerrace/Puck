using System.Buffers.Binary;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for the CPU reference of the image-source conversion passes. Each case builds an uploaded region the way a
/// producer writes it and holds the reference's pixels to values derived from the arithmetic by hand: a palette lookup is
/// the palette entry itself, a gray NV12 sample is its luma rescaled, a chroma offset moves red and green by the closed
/// forms of its matrix, and a perceptual-quantizer code decodes to the luminance its inverse encodes.
/// <see cref="ImageSourceWorkingSpaceLawTests"/> holds the transfer pass's working values at known luminances.
/// </summary>
public sealed class ImageSourceConversionLawTests {
    private static byte[] Region(ImagePixelFormat format, ImageColorEncoding color, uint width, uint height, out ImageSourceUploadHeader header) {
        header = ImageSourceUploadLayout.HeaderOf(
            color: color,
            format: format,
            height: height,
            width: width
        );

        var region = new byte[ImageSourceUploadLayout.ByteCount(header: in header)];

        ImageSourceUploadLayout.Write(
            header: in header,
            region: region
        );

        return region;
    }
    private static byte[] Nv12(ImageYuvMatrix matrix, ImageYuvRange range, byte y, byte cb, byte cr) {
        var region = Region(
            color: ImageColorEncoding.Yuv(
                matrix: matrix,
                range: range
            ),
            format: ImagePixelFormat.Nv12,
            header: out var header,
            height: 2U,
            width: 2U
        );

        ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 0,
            region: region
        ).Fill(value: y);

        var chroma = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 1,
            region: region
        );

        chroma[0] = cb;
        chroma[1] = cr;

        var rgba = new byte[16];

        ImageSourceConversion.ToRgba8(
            region: region,
            rgba: rgba
        );

        return rgba;
    }
    private static byte Code(double value) => ((byte)Math.Round(
        value: (Math.Clamp(
            max: 1.0,
            min: 0.0,
            value: value
        ) * 255.0),
        mode: MidpointRounding.ToEven
    ));

    [Fact]
    public void APaletteIndexedSourceShowsEachIndexsPaletteEntry() {
        var region = Region(
            color: ImageColorEncoding.Srgb,
            format: ImagePixelFormat.Indexed8,
            header: out var header,
            height: 2U,
            width: 3U
        );
        var palette = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 0,
            region: region
        );

        BinaryPrimitives.WriteUInt32LittleEndian(destination: palette[(7 * 4)..], value: 0xFF0000FFU);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: palette[(9 * 4)..], value: 0x8000FF00U);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: palette[(255 * 4)..], value: 0xFFFF0000U);

        var indices = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 1,
            region: region
        );

        // Row 0: 7, 9, 255; row 1 (at the four-byte stride): 255, 7, 0.
        indices[0] = 7;
        indices[1] = 9;
        indices[2] = 255;
        indices[4] = 255;
        indices[5] = 7;
        indices[6] = 0;

        var rgba = new byte[24];

        ImageSourceConversion.ToRgba8(
            region: region,
            rgba: rgba
        );

        Assert.Equal(
            actual: rgba,
            expected: new byte[] {
                0xFF, 0x00, 0x00, 0xFF,
                0x00, 0xFF, 0x00, 0x80,
                0x00, 0x00, 0xFF, 0xFF,
                0x00, 0x00, 0xFF, 0xFF,
                0xFF, 0x00, 0x00, 0xFF,
                0x00, 0x00, 0x00, 0x00,
            }
        );
        Assert.Equal(
            expected: 4U,
            actual: header.Plane1Stride
        );
    }
    [InlineData(ImageYuvMatrix.Bt601)]
    [InlineData(ImageYuvMatrix.Bt709)]
    [InlineData(ImageYuvMatrix.Bt2020)]
    [Theory]
    public void AnNv12SourceSpansBlackToWhiteOverItsCodeRange(ImageYuvMatrix matrix) {
        Assert.Equal(
            expected: new byte[] { 0, 0, 0, 255 },
            actual: Nv12(cb: 128, cr: 128, matrix: matrix, range: ImageYuvRange.Limited, y: 16)[..4]
        );
        Assert.Equal(
            expected: new byte[] { 255, 255, 255, 255 },
            actual: Nv12(cb: 128, cr: 128, matrix: matrix, range: ImageYuvRange.Limited, y: 235)[..4]
        );
        Assert.Equal(
            expected: new byte[] { 0, 0, 0, 255 },
            actual: Nv12(cb: 128, cr: 128, matrix: matrix, range: ImageYuvRange.Full, y: 0)[..4]
        );
        Assert.Equal(
            expected: new byte[] { 255, 255, 255, 255 },
            actual: Nv12(cb: 128, cr: 128, matrix: matrix, range: ImageYuvRange.Full, y: 255)[..4]
        );
    }
    [InlineData(ImageYuvRange.Limited, 16)]
    [InlineData(ImageYuvRange.Limited, 126)]
    [InlineData(ImageYuvRange.Limited, 200)]
    [InlineData(ImageYuvRange.Full, 1)]
    [InlineData(ImageYuvRange.Full, 128)]
    [InlineData(ImageYuvRange.Full, 254)]
    [Theory]
    public void AGrayNv12SampleIsItsLumaRescaled(ImageYuvRange range, byte y) {
        var gray = Code(value: ((range == ImageYuvRange.Limited)
            ? ((y - 16.0) / 219.0)
            : (y / 255.0)
        ));
        var rgba = Nv12(
            cb: 128,
            cr: 128,
            matrix: ImageYuvMatrix.Bt709,
            range: range,
            y: y
        );

        Assert.Equal(
            actual: rgba,
            expected: new byte[] { gray, gray, gray, 255, gray, gray, gray, 255, gray, gray, gray, 255, gray, gray, gray, 255 }
        );
    }
    [InlineData(ImageYuvMatrix.Bt601, 0.299, 0.114)]
    [InlineData(ImageYuvMatrix.Bt709, 0.2126, 0.0722)]
    [InlineData(ImageYuvMatrix.Bt2020, 0.2627, 0.0593)]
    [Theory]
    public void ARedDifferenceMovesRedAndGreenByItsMatrixsClosedForm(ImageYuvMatrix matrix, double kr, double kb) {
        // Y at mid gray and Cr 40 codes above neutral: red rises by (2 - 2Kr) Cr', blue is untouched, and green falls by
        // Kr (2 - 2Kr) Cr' / Kg.
        const byte Y = 126;
        const byte Cr = 168;
        var luma = ((Y - 16.0) / 219.0);
        var red = ((Cr - 128.0) / 224.0);
        var expected = new byte[] {
            Code(value: (luma + ((2.0 - (2.0 * kr)) * red))),
            Code(value: (luma - (((kr * (2.0 - (2.0 * kr))) * red) / ((1.0 - kr) - kb)))),
            Code(value: luma),
            255,
        };

        Assert.Equal(
            expected: expected,
            actual: Nv12(cb: 128, cr: Cr, matrix: matrix, range: ImageYuvRange.Limited, y: Y)[..4]
        );
    }
    [Fact]
    public void TheMatrixAnNv12SourceNamesDecidesItsColor() {
        var bt601 = Nv12(cb: 90, cr: 200, matrix: ImageYuvMatrix.Bt601, range: ImageYuvRange.Limited, y: 100);
        var bt709 = Nv12(cb: 90, cr: 200, matrix: ImageYuvMatrix.Bt709, range: ImageYuvRange.Limited, y: 100);

        Assert.NotEqual(
            expected: bt601[..4],
            actual: bt709[..4]
        );
    }
    [Fact]
    public void AnNv12SourcesChromaIsSharedByEachTwoByTwoBlock() {
        var region = Region(
            color: ImageColorEncoding.Yuv(
                matrix: ImageYuvMatrix.Bt709,
                range: ImageYuvRange.Full
            ),
            format: ImagePixelFormat.Nv12,
            header: out var header,
            height: 3U,
            width: 3U
        );

        ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 0,
            region: region
        ).Fill(value: 128);

        var chroma = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 1,
            region: region
        );

        // Two chroma columns and two chroma rows cover the odd 3x3 extent; only the bottom-right pair differs.
        chroma.Fill(value: 128);
        chroma[(((int)header.Plane1Stride) + 2)] = 255;

        var rgba = new byte[36];

        ImageSourceConversion.ToRgba8(
            region: region,
            rgba: rgba
        );

        for (var y = 0; (y < 3); y++) {
            for (var x = 0; (x < 3); x++) {
                var pixel = rgba.AsSpan(
                    length: 4,
                    start: (((y * 3) + x) * 4)
                );
                var shifted = ((x == 2) && (y == 2));

                Assert.Equal(
                    expected: shifted,
                    actual: (pixel[2] != 128)
                );
            }
        }
    }
    [Fact]
    public void AnRgbaSourceCopiesAndABgraSourceSwapsRedAndBlue() {
        foreach (var format in new[] { ImagePixelFormat.R8G8B8A8Unorm, ImagePixelFormat.B8G8R8A8Unorm }) {
            var region = Region(
                color: ImageColorEncoding.Srgb,
                format: format,
                header: out var header,
                height: 1U,
                width: 1U
            );

            new byte[] { 10, 20, 30, 40 }.CopyTo(array: region, index: ((int)header.Plane0Offset));

            var rgba = new byte[4];

            ImageSourceConversion.ToRgba8(
                region: region,
                rgba: rgba
            );

            Assert.Equal(
                actual: rgba,
                expected: ((format == ImagePixelFormat.R8G8B8A8Unorm)
                    ? new byte[] { 10, 20, 30, 40 }
                    : new byte[] { 30, 20, 10, 40 }
                )
            );
        }
    }
    [Fact]
    public void APerceptualQuantizerCodeDecodesToTheLuminanceItsInverseEncodes() {
        const double M1 = (2610.0 / 16384.0);
        const double M2 = ((2523.0 / 4096.0) * 128.0);
        const double C1 = (3424.0 / 4096.0);
        const double C2 = ((2413.0 / 4096.0) * 32.0);
        const double C3 = ((2392.0 / 4096.0) * 32.0);

        static double Encode(double nits) {
            var power = Math.Pow(
                x: (nits / 10_000.0),
                y: M1
            );

            return Math.Pow(
                x: ((C1 + (C2 * power)) / (1.0 + (C3 * power))),
                y: M2
            );
        }

        foreach (var nits in new[] { 0.0, 0.1, 100.0, 203.0, 1_000.0, 10_000.0 }) {
            Assert.Equal(
                expected: nits,
                actual: ImageSourceConversion.PqToNits(value: Encode(nits: nits)),
                tolerance: ((nits * 1e-9) + 1e-12)
            );
        }

        // A 10-bit R10G10B10A2 pixel whose three channels encode the paper white converts to a working value of one.
        const double PaperWhite = 203.0;
        var code = ((uint)Math.Round(a: (Encode(nits: PaperWhite) * 1023.0)));
        var region = Region(
            color: new ImageColorEncoding(
                Primaries: ImageColorPrimaries.Bt2020,
                Transfer: ImageTransferFunction.Pq
            ),
            format: ImagePixelFormat.R10G10B10A2Unorm,
            header: out var header,
            height: 1U,
            width: 1U
        );

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: region.AsSpan(start: ((int)header.Plane0Offset)),
            value: code | (code << 10) | (code << 20) | (3U << 30)
        );

        var working = new float[4];

        ImageSourceConversion.ToWorking(
            paperWhiteNits: PaperWhite,
            region: region,
            working: working
        );

        // One 10-bit code near the paper white spans about 0.3% of its luminance, under 0.2% once encoded; a neutral
        // sample stays neutral through BT.2020 to BT.709 to within the matrix's rounding.
        Assert.Equal(expected: 1.0, actual: working[0], tolerance: 0.002);
        Assert.Equal(expected: working[0], actual: working[1], tolerance: 1e-5);
        Assert.Equal(expected: working[0], actual: working[2], tolerance: 1e-5);
        Assert.Equal(expected: 1.0f, actual: working[3]);
    }
    [Fact]
    public void AnSrgbCodeDecodesThroughTheIecCurve() {
        var region = Region(
            color: new ImageColorEncoding(
                Primaries: ImageColorPrimaries.Bt709,
                Transfer: ImageTransferFunction.Linear
            ),
            format: ImagePixelFormat.R8G8B8A8Unorm,
            header: out var header,
            height: 1U,
            width: 1U
        );

        new byte[] { 0, 51, 255, 128 }.CopyTo(array: region, index: ((int)header.Plane0Offset));

        var working = new float[4];

        ImageSourceConversion.ToWorking(
            paperWhiteNits: DisplayOutput.SdrWhiteNits,
            region: region,
            working: working
        );

        // A linear source at the SDR paper white is scRGB at its own scale, encoded on the sRGB curve: 0.2 lies above the
        // curve's linear segment, and alpha passes through.
        Assert.Equal(expected: 0f, actual: working[0]);
        Assert.Equal(expected: ((1.055 * Math.Pow(x: (51.0 / 255.0), y: (1.0 / 2.4))) - 0.055), actual: working[1], tolerance: 1e-6);
        Assert.Equal(expected: 1.0, actual: working[2], tolerance: 1e-6);
        Assert.Equal(expected: ((float)(128.0 / 255.0)), actual: working[3]);
        Assert.Equal(expected: (0.02 / 12.92), actual: ImageSourceConversion.SrgbToLinear(value: 0.02), tolerance: 1e-15);
        Assert.Equal(expected: Math.Pow(x: (0.555 / 1.055), y: 2.4), actual: ImageSourceConversion.SrgbToLinear(value: 0.5), tolerance: 1e-15);
    }
    [Fact]
    public void EachFormatAndTransferNamesOnePass() {
        Assert.Equal(expected: ImageSourceConversion.RgbaPass, actual: ImageSourceConversion.PassOf(color: ImageColorEncoding.Srgb, format: ImagePixelFormat.R8G8B8A8Unorm));
        Assert.Equal(expected: ImageSourceConversion.RgbaPass, actual: ImageSourceConversion.PassOf(color: ImageColorEncoding.Srgb, format: ImagePixelFormat.B8G8R8A8Unorm));
        Assert.Equal(expected: ImageSourceConversion.PalettePass, actual: ImageSourceConversion.PassOf(color: ImageColorEncoding.Srgb, format: ImagePixelFormat.Indexed8));
        Assert.Equal(expected: ImageSourceConversion.Nv12Pass, actual: ImageSourceConversion.PassOf(color: ImageColorEncoding.Yuv(matrix: ImageYuvMatrix.Bt601, range: ImageYuvRange.Full), format: ImagePixelFormat.Nv12));
        Assert.Equal(expected: ImageSourceConversion.TransferPass, actual: ImageSourceConversion.PassOf(color: new ImageColorEncoding(Primaries: ImageColorPrimaries.Bt2020, Transfer: ImageTransferFunction.Pq), format: ImagePixelFormat.R10G10B10A2Unorm));
        Assert.Equal(expected: ImageSourceConversion.TransferPass, actual: ImageSourceConversion.PassOf(color: new ImageColorEncoding(Primaries: ImageColorPrimaries.Bt709, Transfer: ImageTransferFunction.Linear), format: ImagePixelFormat.R8G8B8A8Unorm));
        Assert.Equal(expected: ImageSourceConversion.TransferPass, actual: ImageSourceConversion.PassOf(color: ImageColorEncoding.Of(colorSpace: DisplayColorSpace.ScRgb), format: ImagePixelFormat.R16G16B16A16Float));
        Assert.Equal(expected: ImageSourceConversion.TransferPass, actual: ImageSourceConversion.PassOf(color: ImageColorEncoding.Srgb, format: ImagePixelFormat.R16G16B16A16Float));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ImageSourceConversion.PassOf(color: new ImageColorEncoding(Primaries: ImageColorPrimaries.Bt2020, Transfer: ImageTransferFunction.Pq), format: ImagePixelFormat.Nv12));
    }
    /// <summary>An imported image converts on the device only as half-float scRGB, the format and encoding a capture of an
    /// HDR display hands over; anything else names no image pass.</summary>
    [Fact]
    public void AnImportedImageNamesItsPassOnlyAsHalfFloatScRgb() {
        Assert.Equal(expected: ImageSourceConversion.ScRgbImagePass, actual: ImageSourceConversion.ImagePassOf(color: ImageColorEncoding.Of(colorSpace: DisplayColorSpace.ScRgb), format: ImagePixelFormat.R16G16B16A16Float));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ImageSourceConversion.ImagePassOf(color: ImageColorEncoding.Srgb, format: ImagePixelFormat.R16G16B16A16Float));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ImageSourceConversion.ImagePassOf(color: ImageColorEncoding.Of(colorSpace: DisplayColorSpace.Hdr10), format: ImagePixelFormat.R16G16B16A16Float));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ImageSourceConversion.ImagePassOf(color: ImageColorEncoding.Of(colorSpace: DisplayColorSpace.ScRgb), format: ImagePixelFormat.B8G8R8A8Unorm));
    }
    [Fact]
    public void AHeaderRoundTripsAndARegionDisagreeingWithItsLayoutIsRefused() {
        var region = Region(
            color: ImageColorEncoding.Yuv(
                matrix: ImageYuvMatrix.Bt2020,
                range: ImageYuvRange.Full
            ),
            format: ImagePixelFormat.Nv12,
            header: out var header,
            height: 5U,
            width: 7U
        );

        Assert.Equal(
            expected: header,
            actual: ImageSourceUploadLayout.Read(region: region)
        );
        Assert.Equal(
            expected: ((32 + (8 * 5)) + (8 * 3)),
            actual: region.Length
        );

        var shifted = region.ToArray();

        BinaryPrimitives.WriteUInt32LittleEndian(destination: shifted.AsSpan(start: 24), value: (header.Plane1Offset + 4U));

        _ = Assert.Throws<ArgumentException>(testCode: () => ImageSourceUploadLayout.Read(region: shifted));
        _ = Assert.Throws<ArgumentException>(testCode: () => ImageSourceUploadLayout.Read(region: region.AsSpan(length: (region.Length - 4), start: 0)));
        _ = Assert.Throws<ArgumentException>(testCode: () => ImageSourceUploadLayout.Read(region: new byte[32]));
    }
}
