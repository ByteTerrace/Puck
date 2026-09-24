using System.Buffers.Binary;
using Puck.Abstractions.Sources;

namespace Puck.Assets.Textures;

/// <summary>How a mip level's texel is filtered from the four texels of the level above it.</summary>
public enum TextureMipFilter : byte {
    /// <summary>Unsigned-normalized 8-bit channels (<see cref="TextureFormat.R8Unorm"/>,
    /// <see cref="TextureFormat.Rg8Unorm"/>, <see cref="TextureFormat.Rgba8Unorm"/>), each the coverage-weighted mean of
    /// its four codes rounded half up.</summary>
    Average = 0,
    /// <summary>sRGB color with linear alpha (<see cref="TextureFormat.Rgba8Unorm"/>): each color channel decoded to
    /// linear light (<c>ImageSourceConversion.Srgb8ToLinear</c>), averaged weighted by alpha, and encoded again through
    /// <c>ImageSourceConversion.LinearToSrgb8</c>; alpha is the mean of its four codes rounded half up.</summary>
    Srgb = 1,
    /// <summary>An octahedral unit direction (<see cref="TextureFormat.Rg8Unorm"/>, see <see cref="OctahedralNormal"/>):
    /// the four directions decoded, summed weighted by coverage, and the sum encoded again, which renormalizes it. A sum of
    /// zero keeps the first texel's codes.</summary>
    OctahedralNormal = 2,
    /// <summary>An identity (<see cref="TextureFormat.R8Unorm"/>), never blended: the value most of the four texels hold,
    /// the smallest winning a tie.</summary>
    Majority = 3,
    /// <summary>Linear half-precision channels (<see cref="TextureFormat.Rgba16Float"/>), each the coverage-weighted mean
    /// of its four values in double, rounded to the nearest half.</summary>
    Half = 4,
}
/// <summary>
/// Builds a tile-aware mip chain. The first level is an atlas of square tiles, each <c>tileTexels</c> on a side (a power
/// of two), whose sides are whole tiles. Each level halves the one above it with a 2x2 box, so every destination texel
/// reads four texels of one tile and no level mixes two tiles. The chain ends at the level where each tile is one texel
/// (<see cref="LevelCount"/>): below it a texel would span several tiles. A renderer sampling a level keeps each lookup
/// inside its tile at that level (clamped per-tile filtering), because a bilinear tap at a tile's edge otherwise reads its
/// neighbor.
/// <para>Coverage weights, when given, are one byte per texel of each level but the last (the albedo's alpha, for an
/// impostor whose empty texels must not darken or bend its edges); a group of four texels with no coverage is weighted
/// evenly. Every filter is integer arithmetic or scalar double addition, multiplication, division and square root in a
/// written order, and sRGB goes through the exact encode, so a chain is the same bytes on every machine.</para>
/// </summary>
public static class TextureMipChain {
    /// <summary>Returns the levels a chain over tiles of <paramref name="tileTexels"/> holds: one per halving down to
    /// one texel per tile, the first level included.</summary>
    /// <param name="tileTexels">The tile's side, in texels; a power of two.</param>
    /// <returns>The level count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileTexels"/> is not a positive power of
    /// two.</exception>
    public static int LevelCount(int tileTexels) {
        if ((tileTexels <= 0) || !int.IsPow2(value: tileTexels)) {
            throw new ArgumentOutOfRangeException(actualValue: tileTexels, message: "A tile's side must be a positive power of two.", paramName: nameof(tileTexels));
        }

        return (int.Log2(value: tileTexels) + 1);
    }
    /// <summary>Builds the chain.</summary>
    /// <param name="level0">The first level's texels, rows top to bottom.</param>
    /// <param name="format">The texels' uncompressed format.</param>
    /// <param name="width">The first level's width, in texels; a whole number of tiles.</param>
    /// <param name="height">The first level's height, in texels; a whole number of tiles.</param>
    /// <param name="tileTexels">The tile's side, in texels; a power of two.</param>
    /// <param name="filter">The filter, which must suit <paramref name="format"/>.</param>
    /// <param name="coverage">Per-texel weights for every level but the last, or <see langword="null"/> to weight
    /// evenly. <see cref="TextureMipFilter.Srgb"/> weights by its own alpha and <see cref="TextureMipFilter.Majority"/>
    /// ignores weights.</param>
    /// <returns>The levels, the first being <paramref name="level0"/> itself.</returns>
    /// <exception cref="ArgumentException">The extent is not whole tiles, <paramref name="level0"/> is not exactly the
    /// level, the filter does not suit the format, or <paramref name="coverage"/> lacks a level.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileTexels"/> is not a positive power of
    /// two.</exception>
    public static byte[][] Build(byte[] level0, TextureFormat format, int width, int height, int tileTexels, TextureMipFilter filter, IReadOnlyList<byte[]>? coverage = null) {
        ArgumentNullException.ThrowIfNull(argument: level0);

        var levels = new byte[LevelCount(tileTexels: tileTexels)][];
        var texelBytes = TextureFormats.BytesPerUnit(format: format);

        if ((width <= 0) || (height <= 0) || ((width % tileTexels) != 0) || ((height % tileTexels) != 0) || (level0.Length != ((width * height) * texelBytes))) {
            throw new ArgumentException(message: $"{level0.Length} bytes of {format} are not a {width}x{height} atlas of {tileTexels}-texel tiles.", paramName: nameof(level0));
        }

        var suits = filter switch {
            TextureMipFilter.Average => (format is TextureFormat.R8Unorm or TextureFormat.Rg8Unorm or TextureFormat.Rgba8Unorm),
            TextureMipFilter.Srgb => (format == TextureFormat.Rgba8Unorm),
            TextureMipFilter.OctahedralNormal => (format == TextureFormat.Rg8Unorm),
            TextureMipFilter.Majority => (format == TextureFormat.R8Unorm),
            TextureMipFilter.Half => (format == TextureFormat.Rgba16Float),
            _ => false,
        };

        if (!suits) {
            throw new ArgumentException(message: $"the {filter} filter does not read {format} texels.", paramName: nameof(filter));
        }

        levels[0] = level0;

        for (var level = 1; (level < levels.Length); level++) {
            var (sourceWidth, sourceHeight) = TextureFormats.LevelExtent(height: height, level: (level - 1), width: width);
            var weights = ((coverage is null) ? null : ((level <= coverage.Count) ? coverage[(level - 1)] : null));

            if ((coverage is not null) && ((weights is null) || (weights.Length != (sourceWidth * sourceHeight)))) {
                throw new ArgumentException(message: $"coverage level {(level - 1)} is not {sourceWidth}x{sourceHeight} weights.", paramName: nameof(coverage));
            }

            levels[level] = Halve(filter: filter, source: levels[(level - 1)], sourceHeight: sourceHeight, sourceWidth: sourceWidth, texelBytes: texelBytes, weights: weights);
        }

        return levels;
    }
    /// <summary>Returns one channel of every level of a chain, for use as another chain's coverage.</summary>
    /// <param name="levels">The chain's levels, each of <paramref name="channels"/> 8-bit channels per texel.</param>
    /// <param name="channels">The channels per texel.</param>
    /// <param name="channel">The channel to extract.</param>
    /// <returns>One byte per texel of each level.</returns>
    public static byte[][] Channel(IReadOnlyList<byte[]> levels, int channels, int channel) {
        ArgumentNullException.ThrowIfNull(argument: levels);

        var extracted = new byte[levels.Count][];

        for (var level = 0; (level < levels.Count); level++) {
            var source = levels[level];
            var values = new byte[(source.Length / channels)];

            for (var texel = 0; (texel < values.Length); texel++) {
                values[texel] = source[((texel * channels) + channel)];
            }

            extracted[level] = values;
        }

        return extracted;
    }

    private static byte[] Halve(byte[] source, int sourceWidth, int sourceHeight, int texelBytes, TextureMipFilter filter, byte[]? weights) {
        var width = (sourceWidth / 2);
        var height = (sourceHeight / 2);
        var destination = new byte[((width * height) * texelBytes)];
        Span<int> at = stackalloc int[4];
        Span<int> weight = stackalloc int[4];

        for (var y = 0; (y < height); y++) {
            for (var x = 0; (x < width); x++) {
                at[0] = (((2 * y) * sourceWidth) + (2 * x));
                at[1] = (at[0] + 1);
                at[2] = (at[0] + sourceWidth);
                at[3] = (at[2] + 1);

                var total = 0;

                for (var index = 0; (index < 4); index++) {
                    weight[index] = (filter switch {
                        TextureMipFilter.Srgb => source[((at[index] * 4) + 3)],
                        _ => ((weights is null) ? 1 : weights[at[index]]),
                    });
                    total += weight[index];
                }

                if (total == 0) {
                    weight.Fill(value: 1);
                    total = 4;
                }

                var target = destination.AsSpan(length: texelBytes, start: (((y * width) + x) * texelBytes));

                switch (filter) {
                    case TextureMipFilter.Average:
                        for (var channel = 0; (channel < texelBytes); channel++) {
                            var sum = 0;

                            for (var index = 0; (index < 4); index++) {
                                sum += (source[((at[index] * texelBytes) + channel)] * weight[index]);
                            }

                            target[channel] = ((byte)(((2 * sum) + total) / (2 * total)));
                        }

                        break;
                    case TextureMipFilter.Srgb:
                        for (var channel = 0; (channel < 3); channel++) {
                            var sum = 0.0;

                            for (var index = 0; (index < 4); index++) {
                                sum += (ImageSourceConversion.Srgb8ToLinear(code: source[((at[index] * 4) + channel)]) * weight[index]);
                            }

                            target[channel] = ImageSourceConversion.LinearToSrgb8(value: (sum / total));
                        }

                        target[3] = ((byte)(((((source[((at[0] * 4) + 3)] + source[((at[1] * 4) + 3)]) + source[((at[2] * 4) + 3)]) + source[((at[3] * 4) + 3)]) + 2) >> 2));
                        break;
                    case TextureMipFilter.OctahedralNormal: {
                            double sx = 0.0, sy = 0.0, sz = 0.0;

                            for (var index = 0; (index < 4); index++) {
                                var (nx, ny, nz) = OctahedralNormal.Decode(u: source[(at[index] * 2)], v: source[((at[index] * 2) + 1)]);

                                sx += (nx * weight[index]);
                                sy += (ny * weight[index]);
                                sz += (nz * weight[index]);
                            }

                            if (((sx == 0.0) && (sy == 0.0)) && (sz == 0.0)) {
                                target[0] = source[(at[0] * 2)];
                                target[1] = source[((at[0] * 2) + 1)];
                            } else {
                                (target[0], target[1]) = OctahedralNormal.Encode(x: sx, y: sy, z: sz);
                            }

                            break;
                        }
                    case TextureMipFilter.Majority: {
                            var best = source[at[0]];
                            var bestCount = 0;

                            for (var index = 0; (index < 4); index++) {
                                var value = source[at[index]];
                                var count = 0;

                                for (var other = 0; (other < 4); other++) {
                                    count += ((source[at[other]] == value) ? 1 : 0);
                                }

                                if ((count > bestCount) || ((count == bestCount) && (value < best))) {
                                    best = value;
                                    bestCount = count;
                                }
                            }

                            target[0] = best;
                            break;
                        }
                    default:
                        for (var channel = 0; (channel < 4); channel++) {
                            var sum = 0.0;

                            for (var index = 0; (index < 4); index++) {
                                var half = BitConverter.UInt16BitsToHalf(value: BinaryPrimitives.ReadUInt16LittleEndian(source: source.AsSpan(start: ((at[index] * 8) + (channel * 2)))));

                                sum += (((double)half) * weight[index]);
                            }

                            BinaryPrimitives.WriteUInt16LittleEndian(destination: target[(channel * 2)..], value: BitConverter.HalfToUInt16Bits(value: ((Half)(sum / total))));
                        }

                        break;
                }
            }
        }

        return destination;
    }
}
