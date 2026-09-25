using System.Buffers.Binary;
using System.Text.Json;
using Puck.Abstractions.Sources;

namespace Puck.Shaders.Tests;

/// <summary>
/// The <c>source-conversion</c> canary's expectations are the CPU reference's, not recorded numbers. Each leg's regions
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
            expected: [2080, 1568],
            actual: [PaletteRegion(indexBase: 5U).Length, Nv12Region(matrix: ImageYuvMatrix.Bt709).Length]
        );
    }
    [InlineData("positive", 5U, ImageYuvMatrix.Bt709)]
    [InlineData("discriminating", 6U, ImageYuvMatrix.Bt601)]
    [Theory]
    public void EveryImageRegionBoundIsTheCpuReferencesPixels(string leg, uint indexBase, ImageYuvMatrix matrix) {
        var captures = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal) {
            ["nv12.png"] = Convert(region: Nv12Region(matrix: matrix)),
            ["palette.png"] = Convert(region: PaletteRegion(indexBase: indexBase)),
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
            expected: ((leg == "positive") ? 8 : 15)
        );
    }
}
