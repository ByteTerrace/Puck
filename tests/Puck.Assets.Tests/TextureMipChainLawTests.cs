using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Assets.Textures;
using Xunit;

namespace Puck.Assets.Tests;

/// <summary>
/// Laws for <see cref="TextureMipChain"/> and <see cref="OctahedralNormal"/>: a chain ends at one texel per tile and no
/// level mixes two tiles, under every filter; sRGB color averages in linear light through the exact encode; normals
/// renormalize and ignore uncovered texels; an identity keeps the majority, the smallest winning a tie; and the
/// octahedral pair stores every direction within its stated angle.
/// </summary>
public sealed class TextureMipChainLawTests {
    // An atlas of `columns` by `rows` tiles, each filled with its own texel from `tileTexel`.
    private static byte[] Tiled(int columns, int rows, int tile, int texelBytes, Func<int, byte[]> tileTexel) {
        var width = (columns * tile);
        var texels = new byte[(((width * rows) * tile) * texelBytes)];

        for (var y = 0; (y < (rows * tile)); y++) {
            for (var x = 0; (x < width); x++) {
                tileTexel(arg: (((y / tile) * columns) + (x / tile))).CopyTo(array: texels, index: (((y * width) + x) * texelBytes));
            }
        }

        return texels;
    }
    private static byte[] Half(double r, double g, double b, double a) {
        var bytes = new byte[8];

        BinaryPrimitives.WriteUInt16LittleEndian(destination: bytes, value: BitConverter.HalfToUInt16Bits(value: ((Half)r)));
        BinaryPrimitives.WriteUInt16LittleEndian(destination: bytes.AsSpan(start: 2), value: BitConverter.HalfToUInt16Bits(value: ((Half)g)));
        BinaryPrimitives.WriteUInt16LittleEndian(destination: bytes.AsSpan(start: 4), value: BitConverter.HalfToUInt16Bits(value: ((Half)b)));
        BinaryPrimitives.WriteUInt16LittleEndian(destination: bytes.AsSpan(start: 6), value: BitConverter.HalfToUInt16Bits(value: ((Half)a)));

        return bytes;
    }
    private static double Degrees((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        ((Math.Acos(d: Math.Clamp(max: 1.0, min: -1.0, value: (((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z)))) * 180.0) / Math.PI);

    public static TheoryData<TextureMipFilter> Filters => [TextureMipFilter.Average, TextureMipFilter.Srgb, TextureMipFilter.OctahedralNormal, TextureMipFilter.Majority, TextureMipFilter.Half];

    [MemberData(memberName: nameof(Filters))]
    [Theory]
    public void AChainEndsAtOneTexelPerTileAndNoLevelMixesTiles(TextureMipFilter filter) {
        const int Tile = 8;
        const int Columns = 5;
        const int Rows = 3;

        var (format, texelBytes) = filter switch {
            TextureMipFilter.Srgb => (GpuPixelFormat.R8G8B8A8Unorm, 4),
            TextureMipFilter.OctahedralNormal => (GpuPixelFormat.R8G8Unorm, 2),
            TextureMipFilter.Half => (GpuPixelFormat.R16G16B16A16Float, 8),
            _ => (GpuPixelFormat.R8Unorm, 1),
        };
        Func<int, byte[]> tileTexel = filter switch {
            TextureMipFilter.Srgb => static tile => [((byte)(tile * 17)), ((byte)(255 - (tile * 13))), ((byte)(tile * 5)), ((byte)(255 - tile))],
            TextureMipFilter.OctahedralNormal => static tile => [((byte)(40 + (tile * 11))), ((byte)(200 - (tile * 7)))],
            TextureMipFilter.Half => static tile => Half(a: 1.0, b: (tile * 0.25), g: 2.0, r: (tile + 0.5)),
            _ => static tile => [((byte)(tile * 16))],
        };
        var levels = TextureMipChain.Build(
            filter: filter,
            format: format,
            height: (Rows * Tile),
            level0: Tiled(columns: Columns, rows: Rows, texelBytes: texelBytes, tile: Tile, tileTexel: tileTexel),
            tileTexels: Tile,
            width: (Columns * Tile)
        );

        Assert.Equal(expected: 4, actual: levels.Length);
        Assert.Equal(expected: TextureMipChain.LevelCount(tileTexels: Tile), actual: levels.Length);

        for (var level = 0; (level < levels.Length); level++) {
            var tile = (Tile >> level);

            // A constant tile filters to itself, so every level holds exactly the tiled constants.
            Assert.Equal(
                expected: Tiled(columns: Columns, rows: Rows, texelBytes: texelBytes, tile: tile, tileTexel: tileTexel),
                actual: levels[level]
            );
        }
    }
    [Fact]
    public void SrgbColorAveragesInLinearLightAndAlphaWeightsIt() {
        // Black and white in a checker average to linear one half, sRGB 188; alpha averages as a code.
        byte[] checker = [0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255];
        var levels = TextureMipChain.Build(filter: TextureMipFilter.Srgb, format: GpuPixelFormat.R8G8B8A8Unorm, height: 2, level0: checker, tileTexels: 2, width: 2);

        Assert.Equal(expected: ImageSourceConversion.LinearToSrgb8(value: 0.5), actual: levels[1][0]);
        Assert.Equal(expected: ((byte)188), actual: levels[1][0]);
        Assert.Equal(expected: ((byte)255), actual: levels[1][3]);

        // A transparent texel's color does not reach the average: three red opaque texels and one transparent blue.
        byte[] edge = [255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 0, 0, 255, 0];

        levels = TextureMipChain.Build(filter: TextureMipFilter.Srgb, format: GpuPixelFormat.R8G8B8A8Unorm, height: 2, level0: edge, tileTexels: 2, width: 2);
        Assert.Equal(expected: [255, 0, 0, 191], actual: levels[1]);
    }
    [Fact]
    public void NormalsRenormalizeAndIgnoreUncoveredTexels() {
        var (xu, xv) = OctahedralNormal.Encode(x: 1.0, y: 0.0, z: 0.0);
        var (yu, yv) = OctahedralNormal.Encode(x: 0.0, y: 1.0, z: 0.0);
        var (zu, zv) = OctahedralNormal.Encode(x: 0.0, y: 0.0, z: -1.0);
        byte[] level0 = [xu, xv, yu, yv, xu, xv, yu, yv];
        var levels = TextureMipChain.Build(filter: TextureMipFilter.OctahedralNormal, format: GpuPixelFormat.R8G8Unorm, height: 2, level0: level0, tileTexels: 2, width: 2);
        var average = OctahedralNormal.Decode(u: levels[1][0], v: levels[1][1]);

        Assert.InRange(actual: Degrees(a: average, b: (Math.Sqrt(d: 0.5), Math.Sqrt(d: 0.5), 0.0)), high: 1.0, low: 0.0);

        // With the -z texels uncovered, the +x texels alone set the result.
        byte[] mixed = [xu, xv, zu, zv, zu, zv, xu, xv];
        byte[] coverage = [255, 0, 0, 255];

        levels = TextureMipChain.Build(coverage: [coverage], filter: TextureMipFilter.OctahedralNormal, format: GpuPixelFormat.R8G8Unorm, height: 2, level0: mixed, tileTexels: 2, width: 2);
        Assert.Equal(expected: [xu, xv], actual: levels[1]);
    }
    [Fact]
    public void AnIdentityKeepsItsMajorityAndTheSmallestWinsATie() {
        Assert.Equal(expected: [7], actual: TextureMipChain.Build(filter: TextureMipFilter.Majority, format: GpuPixelFormat.R8Unorm, height: 2, level0: [9, 7, 7, 3], tileTexels: 2, width: 2)[1]);
        Assert.Equal(expected: [3], actual: TextureMipChain.Build(filter: TextureMipFilter.Majority, format: GpuPixelFormat.R8Unorm, height: 2, level0: [9, 3, 9, 3], tileTexels: 2, width: 2)[1]);
        Assert.Equal(expected: [1], actual: TextureMipChain.Build(filter: TextureMipFilter.Majority, format: GpuPixelFormat.R8Unorm, height: 2, level0: [4, 3, 2, 1], tileTexels: 2, width: 2)[1]);
    }
    [Fact]
    public void AChainRefusesWhatItCannotTile() {
        Assert.Throws<ArgumentException>(testCode: () => TextureMipChain.Build(filter: TextureMipFilter.Average, format: GpuPixelFormat.R8Unorm, height: 4, level0: new byte[24], tileTexels: 4, width: 6));
        Assert.Throws<ArgumentException>(testCode: () => TextureMipChain.Build(filter: TextureMipFilter.Srgb, format: GpuPixelFormat.R8Unorm, height: 4, level0: new byte[16], tileTexels: 4, width: 4));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => TextureMipChain.Build(filter: TextureMipFilter.Average, format: GpuPixelFormat.R8Unorm, height: 6, level0: new byte[36], tileTexels: 6, width: 6));
        Assert.Throws<ArgumentException>(testCode: () => TextureMipChain.Build(coverage: [], filter: TextureMipFilter.Average, format: GpuPixelFormat.R8Unorm, height: 4, level0: new byte[16], tileTexels: 4, width: 4));
    }
    [Fact]
    public void TheOctahedralPairStoresEveryDirectionWithinItsAngle() {
        var worst = 0.0;

        for (var i = 0; (i <= 64); i++) {
            for (var j = 0; (j <= 128); j++) {
                var theta = ((Math.PI * i) / 64.0);
                var phi = (((2.0 * Math.PI) * j) / 128.0);
                var direction = ((Math.Sin(a: theta) * Math.Cos(d: phi)), (Math.Sin(a: theta) * Math.Sin(a: phi)), Math.Cos(d: theta));

                var (u, v) = OctahedralNormal.Encode(x: direction.Item1, y: direction.Item2, z: direction.Item3);
                var decoded = OctahedralNormal.Decode(u: u, v: v);

                Assert.Equal(expected: 1.0, actual: Math.Sqrt(d: (((decoded.X * decoded.X) + (decoded.Y * decoded.Y)) + (decoded.Z * decoded.Z))), tolerance: 1e-12);
                worst = Math.Max(val1: worst, val2: Degrees(a: direction, b: decoded));
            }
        }

        Assert.True(condition: (worst <= 1.0), userMessage: $"the octahedral pair strays {worst:F3} degrees");
        Assert.Equal(expected: OctahedralNormal.Encode(x: 0.0, y: 0.0, z: 1.0), actual: OctahedralNormal.Encode(x: 0.0, y: 0.0, z: 0.0));
    }
}
