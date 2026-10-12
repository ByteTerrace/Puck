using System.Buffers.Binary;
using Puck.Abstractions.Sources;

namespace Puck.Shaders.Tests;

/// <summary>
/// The <c>source-conversion</c> seed regions, rebuilt here from the rules each seed pass follows, carry the header words
/// of their upload layouts.
/// </summary>
public sealed class SourceConversionCanaryFixtureTests {
    private const int Extent = 32;

    private static (uint Y, uint Cb, uint Cr) Sample(int x, int y) => (((x >= 16) ? 1 : 0) + ((y >= 16) ? 2 : 0)) switch {
        0 => (235U, 128U, 128U),
        1 => (63U, 102U, 240U),
        2 => (126U, 128U, 168U),
        _ => (81U, 90U, 110U),
    };
    // seed.hlsli's palette region: entry i = (i, 255 - i, 3i mod 256, 255), index 5 + 64 (x / 16) + 128 (y / 16), shifted
    // by one in the discriminating leg.
    private static byte[] PaletteRegion(uint indexBase) {
        var header = ImageSourceUploadLayout.HeaderOf(
            color: ImageColorEncoding.Srgb,
            format: ImagePixelFormat.Indexed8,
            height: Extent,
            width: Extent
        );
        var region = new byte[ImageSourceUploadLayout.ByteCount(header: in header)];

        ImageSourceUploadLayout.Write(
            header: in header,
            region: region
        );

        var palette = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 0,
            region: region
        );

        for (var entry = 0U; (entry < 256U); entry++) {
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: palette[((int)(entry * 4U))..],
                value: entry | ((255U - entry) << 8) | (((entry * 3U) & 0xFFU) << 16) | 0xFF000000U
            );
        }

        var indices = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 1,
            region: region
        );

        for (var y = 0; (y < Extent); y++) {
            for (var x = 0; (x < Extent); x++) {
                indices[((y * ((int)header.Plane1Stride)) + x)] = ((byte)((indexBase + (64U * ((uint)(x / 16)))) + (128U * ((uint)(y / 16)))));
            }
        }

        return region;
    }
    // seed.hlsli's NV12 region, limited range under the matrix its header names.
    private static byte[] Nv12Region(ImageYuvMatrix matrix) {
        var header = ImageSourceUploadLayout.HeaderOf(
            color: ImageColorEncoding.Yuv(
                matrix: matrix,
                range: ImageYuvRange.Limited
            ),
            format: ImagePixelFormat.Nv12,
            height: Extent,
            width: Extent
        );
        var region = new byte[ImageSourceUploadLayout.ByteCount(header: in header)];

        ImageSourceUploadLayout.Write(
            header: in header,
            region: region
        );

        var luma = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 0,
            region: region
        );
        var chroma = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 1,
            region: region
        );

        for (var y = 0; (y < Extent); y++) {
            for (var x = 0; (x < Extent); x++) {
                luma[((y * ((int)header.Plane0Stride)) + x)] = ((byte)Sample(x: x, y: y).Y);
            }
        }

        for (var y = 0; (y < (Extent / 2)); y++) {
            for (var x = 0; (x < (Extent / 2)); x++) {
                var (_, cb, cr) = Sample(
                    x: (x * 2),
                    y: (y * 2)
                );
                var offset = ((y * ((int)header.Plane1Stride)) + (x * 2));

                chroma[offset] = ((byte)cb);
                chroma[(offset + 1)] = ((byte)cr);
            }
        }

        return region;
    }
    // seed.hlsli's four-byte regions: 32x32 pixels by quadrant, stored in the byte order written, under a header naming
    // the format and transfer function the leg declares.
    private static byte[] FourByteRegion(ImagePixelFormat format, ImageTransferFunction transfer, Func<int, (byte First, byte Second, byte Third)> bytesOf) {
        var header = ImageSourceUploadLayout.HeaderOf(
            color: new ImageColorEncoding(
                Primaries: ImageColorPrimaries.Bt709,
                Transfer: transfer
            ),
            format: format,
            height: Extent,
            width: Extent
        );
        var region = new byte[ImageSourceUploadLayout.ByteCount(header: in header)];

        ImageSourceUploadLayout.Write(
            header: in header,
            region: region
        );

        var pixels = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 0,
            region: region
        );

        for (var y = 0; (y < Extent); y++) {
            for (var x = 0; (x < Extent); x++) {
                var (first, second, third) = bytesOf(arg: (((x >= 16) ? 1 : 0) + ((y >= 16) ? 2 : 0)));
                var offset = ((y * ((int)header.Plane0Stride)) + (x * 4));

                pixels[offset] = first;
                pixels[(offset + 1)] = second;
                pixels[(offset + 2)] = third;
                pixels[(offset + 3)] = 0xFF;
            }
        }

        return region;
    }
    // rgbaOf's quadrant color, stored B, G, R.
    private static (byte, byte, byte) Bgra(int quadrant) => quadrant switch {
        0 => (0, 64, 255),
        1 => (255, 128, 0),
        2 => (96, 200, 32),
        _ => (140, 20, 180),
    };
    // seed.hlsli's half-float region: scRgbOf's quadrant values under a header naming the linear (scRGB) transfer function,
    // alpha one.
    private static byte[] HdrRegion() {
        var header = ImageSourceUploadLayout.HeaderOf(
            color: new ImageColorEncoding(
                Primaries: ImageColorPrimaries.Bt709,
                Transfer: ImageTransferFunction.Linear
            ),
            format: ImagePixelFormat.R16G16B16A16Float,
            height: Extent,
            width: Extent
        );
        var region = new byte[ImageSourceUploadLayout.ByteCount(header: in header)];

        ImageSourceUploadLayout.Write(
            header: in header,
            region: region
        );

        var pixels = ImageSourceUploadLayout.PlaneOf(
            header: in header,
            plane: 0,
            region: region
        );

        for (var y = 0; (y < Extent); y++) {
            for (var x = 0; (x < Extent); x++) {
                var (r, g, b) = ScRgb(quadrant: (((x >= 16) ? 1 : 0) + ((y >= 16) ? 2 : 0)));
                var pixel = pixels[((y * ((int)header.Plane0Stride)) + (x * 8))..];

                BinaryPrimitives.WriteHalfLittleEndian(destination: pixel, value: ((Half)r));
                BinaryPrimitives.WriteHalfLittleEndian(destination: pixel[2..], value: ((Half)g));
                BinaryPrimitives.WriteHalfLittleEndian(destination: pixel[4..], value: ((Half)b));
                BinaryPrimitives.WriteHalfLittleEndian(destination: pixel[6..], value: Half.One);
            }
        }

        return region;
    }
    // scRgbOf's quadrant values, each exact in a half float.
    private static (double, double, double) ScRgb(int quadrant) => quadrant switch {
        0 => (12.5, 12.5, 12.5),
        1 => (1.0, 0.5, 0.25),
        2 => (2.5, 1.25, 0.0),
        _ => (5.0, -0.25, 0.0625),
    };
    // encodedOf's quadrant code, stored R, G, B.
    private static (byte, byte, byte) Encoded(int quadrant) => quadrant switch {
        0 => (255, 255, 255),
        1 => (128, 64, 0),
        2 => (200, 32, 160),
        _ => (16, 240, 96),
    };
    // The seed writes these header words itself; they must be the layout's own.
    private static uint[] SeedHeaderWords(byte[] region) => [.. Enumerable.Range(count: 8, start: 0).Select(selector: word => BinaryPrimitives.ReadUInt32LittleEndian(source: region.AsSpan(start: (word * 4))))];

    [Fact]
    public void TheSeedsHeaderWordsAreTheUploadLayouts() {
        Assert.Equal(
            expected: [32U, 32U, 3U, 1U, 32U, 1024U, 1056U, 32U],
            actual: SeedHeaderWords(region: PaletteRegion(indexBase: 5U))
        );
        Assert.Equal(
            expected: [32U, 32U, 4U, 1U, 32U, 32U, 1056U, 32U],
            actual: SeedHeaderWords(region: Nv12Region(matrix: ImageYuvMatrix.Bt709))
        );
        Assert.Equal(
            expected: 0U,
            actual: SeedHeaderWords(region: Nv12Region(matrix: ImageYuvMatrix.Bt601))[3]
        );
        Assert.Equal(
            expected: [32U, 32U, 2U, 1U, 32U, 128U, 0U, 0U],
            actual: SeedHeaderWords(region: FourByteRegion(bytesOf: Bgra, format: ImagePixelFormat.B8G8R8A8Unorm, transfer: ImageTransferFunction.Srgb))
        );
        Assert.Equal(
            expected: [32U, 32U, 1U, (1U | (1U << 16)), 32U, 128U, 0U, 0U],
            actual: SeedHeaderWords(region: FourByteRegion(bytesOf: Encoded, format: ImagePixelFormat.R8G8B8A8Unorm, transfer: ImageTransferFunction.Linear))
        );
        Assert.Equal(
            expected: [32U, 32U, 6U, (1U | (1U << 16)), 32U, 256U, 0U, 0U],
            actual: SeedHeaderWords(region: HdrRegion())
        );
        Assert.Equal(
            expected: [2080, 1568, 4128, 8224],
            actual: [PaletteRegion(indexBase: 5U).Length, Nv12Region(matrix: ImageYuvMatrix.Bt709).Length, FourByteRegion(bytesOf: Bgra, format: ImagePixelFormat.B8G8R8A8Unorm, transfer: ImageTransferFunction.Srgb).Length, HdrRegion().Length]
        );
    }
}
