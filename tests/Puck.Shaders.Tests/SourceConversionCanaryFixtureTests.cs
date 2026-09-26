using System.Buffers.Binary;
using System.Text.Json;
using Puck.Abstractions.Sources;

namespace Puck.Shaders.Tests;

/// <summary>
/// The <c>source-conversion</c> canary's expectations are the CPU reference's, not recorded numbers. Each leg's four regions
/// are rebuilt here from the rules its seed pass follows, converted through <see cref="ImageSourceConversion"/>, and every
/// <c>imageRegion</c> bound in the manifest is held to the reference's pixels over that region: a bound that holds must
/// equal every pixel, and a bound that must fail must differ from them by more than its tolerance.
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
    // encodedOf's quadrant code, stored R, G, B.
    private static (byte, byte, byte) Encoded(int quadrant) => quadrant switch {
        0 => (255, 255, 255),
        1 => (128, 64, 0),
        2 => (200, 32, 160),
        _ => (16, 240, 96),
    };
    // The transfer pass's linear light as the float preview reads it back: clamped to 0-1 and alpha opaque.
    private static byte[] Preview(byte[] region) {
        var linear = new float[((Extent * Extent) * 4)];
        var rgba = new byte[linear.Length];

        ImageSourceConversion.ToLinear(
            linear: linear,
            region: region
        );

        for (var index = 0; (index < linear.Length); index++) {
            rgba[index] = (((index % 4) == 3)
                ? ((byte)0xFF)
                : ImageSourceConversion.ToUnorm8(value: Math.Clamp(
                    max: 1.0,
                    min: 0.0,
                    value: linear[index]
                )));
        }

        return rgba;
    }
    private static byte[] Convert(byte[] region) {
        var rgba = new byte[((Extent * Extent) * 4)];

        ImageSourceConversion.ToRgba8(
            region: region,
            rgba: rgba
        );

        return rgba;
    }
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
            expected: [2080, 1568, 4128],
            actual: [PaletteRegion(indexBase: 5U).Length, Nv12Region(matrix: ImageYuvMatrix.Bt709).Length, FourByteRegion(bytesOf: Bgra, format: ImagePixelFormat.B8G8R8A8Unorm, transfer: ImageTransferFunction.Srgb).Length]
        );
    }
    [InlineData("positive", 5U, ImageYuvMatrix.Bt709, ImagePixelFormat.B8G8R8A8Unorm, ImageTransferFunction.Srgb)]
    [InlineData("discriminating", 6U, ImageYuvMatrix.Bt601, ImagePixelFormat.R8G8B8A8Unorm, ImageTransferFunction.Linear)]
    [Theory]
    public void EveryImageRegionBoundIsTheCpuReferencesPixels(string leg, uint indexBase, ImageYuvMatrix matrix, ImagePixelFormat fourByte, ImageTransferFunction transfer) {
        var captures = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal) {
            ["nv12.png"] = Convert(region: Nv12Region(matrix: matrix)),
            ["palette.png"] = Convert(region: PaletteRegion(indexBase: indexBase)),
            ["rgba.png"] = Convert(region: FourByteRegion(bytesOf: Bgra, format: fourByte, transfer: ImageTransferFunction.Srgb)),
            ["transfer.png"] = Preview(region: FourByteRegion(bytesOf: Encoded, format: ImagePixelFormat.R8G8B8A8Unorm, transfer: transfer)),
        };

        using var manifest = JsonDocument.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Canaries/source-conversion/canary.json")));
        var checkedRegions = 0;

        foreach (var expectation in manifest.RootElement.GetProperty(propertyName: leg).GetProperty(propertyName: "expect").EnumerateArray()) {
            if (expectation.GetProperty(propertyName: "type").GetString() != "imageRegion") {
                continue;
            }

            var name = expectation.GetProperty(propertyName: "name").GetString()!;
            var pixels = captures[expectation.GetProperty(propertyName: "capture").GetString()!];
            var bounds = expectation.GetProperty(propertyName: "region").EnumerateArray().Select(selector: static value => value.GetDouble()).ToArray();
            var minimum = expectation.GetProperty(propertyName: "minimum").EnumerateArray().Select(selector: static value => (value.GetDouble() * 255.0)).ToArray();
            var maximum = expectation.GetProperty(propertyName: "maximum").EnumerateArray().Select(selector: static value => (value.GetDouble() * 255.0)).ToArray();
            var tolerance = expectation.GetProperty(propertyName: "toleranceCodes").GetInt32();
            var holds = expectation.GetProperty(propertyName: "holds").GetBoolean();
            var inside = 0;
            var outside = 0;

            for (var y = 0; (y < Extent); y++) {
                for (var x = 0; (x < Extent); x++) {
                    var (u, v) = (((x + 0.5) / Extent), ((y + 0.5) / Extent));

                    if ((u < bounds[0]) || (v < bounds[1]) || (u > bounds[2]) || (v > bounds[3])) {
                        continue;
                    }

                    var within = Enumerable.Range(count: 4, start: 0).All(predicate: channel => {
                        var code = pixels[((((y * Extent) + x) * 4) + channel)];

                        return ((code >= (minimum[channel] - tolerance)) && (code <= (maximum[channel] + tolerance)));
                    });

                    if (within) {
                        inside++;
                    } else {
                        outside++;
                    }
                }
            }

            Assert.True(
                condition: (holds ? (outside == 0) : (inside == 0)),
                userMessage: $"{leg} {name}: {inside} pixel(s) inside the bounds and {outside} outside"
            );
            checkedRegions++;
        }

        Assert.Equal(
            actual: checkedRegions,
            expected: ((leg == "positive") ? 16 : 30)
        );
    }
}
