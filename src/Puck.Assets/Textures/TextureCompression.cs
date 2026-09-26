using System.Buffers.Binary;
using Puck.Abstractions.Gpu;

namespace Puck.Assets.Textures;

/// <summary>Compresses and decompresses whole texture levels block by block through <see cref="Bc4Codec"/>,
/// <see cref="Bc5Codec"/>, <see cref="Bc6hCodec"/> and <see cref="Bc7Codec"/>. A level's texels are in the uncompressed
/// format its block format encodes (<see cref="SourceOf"/>); a block that runs past the level's right or bottom edge
/// repeats the edge texels, and decompression writes only the texels inside the level. Levels are laid out as
/// <see cref="GpuPixelFormats.LevelByteLength"/> states: rows top to bottom, of texels or of 4x4 blocks.</summary>
public static class TextureCompression {
    /// <summary>Returns the uncompressed format a block-compressed format encodes and decodes:
    /// <see cref="GpuPixelFormat.R8Unorm"/> for BC4, <see cref="GpuPixelFormat.R8G8Unorm"/> for BC5,
    /// <see cref="GpuPixelFormat.R16G16B16A16Float"/> for BC6H (whose alpha decodes to one) and
    /// <see cref="GpuPixelFormat.R8G8B8A8Unorm"/> for BC7.</summary>
    /// <param name="format">The block-compressed format.</param>
    /// <returns>The uncompressed format.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a block-compressed format.</exception>
    public static GpuPixelFormat SourceOf(GpuPixelFormat format) => format switch {
        GpuPixelFormat.Bc4Unorm => GpuPixelFormat.R8Unorm,
        GpuPixelFormat.Bc5Unorm => GpuPixelFormat.R8G8Unorm,
        GpuPixelFormat.Bc6hUfloat => GpuPixelFormat.R16G16B16A16Float,
        GpuPixelFormat.Bc7Unorm => GpuPixelFormat.R8G8B8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The format is not block-compressed.",
            paramName: nameof(format)
        ),
    };
    /// <summary>Compresses one level.</summary>
    /// <param name="texels">The level's texels in <c>SourceOf(format)</c>, rows top to bottom.</param>
    /// <param name="format">The block-compressed format.</param>
    /// <param name="width">The level's width, in texels.</param>
    /// <param name="height">The level's height, in texels.</param>
    /// <returns>The blocks, rows of blocks top to bottom.</returns>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not block-compressed, a side is not positive, or
    /// <paramref name="texels"/> does not hold exactly the level.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> texels, GpuPixelFormat format, int width, int height) {
        var source = Require(format: format, height: height, length: texels.Length, width: width);
        var texelBytes = ((int)GpuPixelFormats.UnitBytes(format: source));
        var blockBytes = ((int)GpuPixelFormats.UnitBytes(format: format));
        var columns = ((int)GpuPixelFormats.BlocksAcross(texels: ((uint)width)));
        var rows = ((int)GpuPixelFormats.BlocksAcross(texels: ((uint)height)));
        var blocks = new byte[((columns * rows) * blockBytes)];
        Span<byte> gathered = stackalloc byte[(16 * 8)];
        Span<ushort> halves = stackalloc ushort[48];

        for (var row = 0; (row < rows); row++) {
            for (var column = 0; (column < columns); column++) {
                for (var texel = 0; (texel < 16); texel++) {
                    var x = Math.Min(val1: ((column * 4) + (texel & 3)), val2: (width - 1));
                    var y = Math.Min(val1: ((row * 4) + (texel >> 2)), val2: (height - 1));

                    texels.Slice(length: texelBytes, start: (((y * width) + x) * texelBytes)).CopyTo(destination: gathered[(texel * texelBytes)..]);
                }

                var block = blocks.AsSpan(length: blockBytes, start: (((row * columns) + column) * blockBytes));

                switch (format) {
                    case GpuPixelFormat.Bc4Unorm:
                        Bc4Codec.EncodeBlock(block: block, values: gathered);
                        break;
                    case GpuPixelFormat.Bc5Unorm:
                        Bc5Codec.EncodeBlock(block: block, values: gathered);
                        break;
                    case GpuPixelFormat.Bc6hUfloat:
                        for (var texel = 0; (texel < 16); texel++) {
                            for (var channel = 0; (channel < 3); channel++) {
                                halves[((texel * 3) + channel)] = BinaryPrimitives.ReadUInt16LittleEndian(source: gathered[((texel * 8) + (channel * 2))..]);
                            }
                        }

                        Bc6hCodec.EncodeBlock(block: block, rgb: halves);
                        break;
                    default:
                        Bc7Codec.EncodeBlock(block: block, rgba: gathered);
                        break;
                }
            }
        }

        return blocks;
    }
    /// <summary>Decompresses one level.</summary>
    /// <param name="blocks">The level's blocks.</param>
    /// <param name="format">The block-compressed format.</param>
    /// <param name="width">The level's width, in texels.</param>
    /// <param name="height">The level's height, in texels.</param>
    /// <returns>The texels in <c>SourceOf(format)</c>, rows top to bottom; BC6H's alpha is one.</returns>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not block-compressed, a side is not positive, or
    /// <paramref name="blocks"/> does not hold exactly the level.</exception>
    /// <exception cref="NotSupportedException">A block is in a mode the codec's decoder does not read.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> blocks, GpuPixelFormat format, int width, int height) {
        if (!GpuPixelFormats.IsBlockCompressed(format: format) || (width <= 0) || (height <= 0) || (((ulong)blocks.Length) != GpuPixelFormats.LevelByteLength(format: format, height: ((uint)height), width: ((uint)width)))) {
            throw new ArgumentException(message: $"{blocks.Length} bytes are not a {width}x{height} {format} level.", paramName: nameof(blocks));
        }

        var source = SourceOf(format: format);
        var texelBytes = ((int)GpuPixelFormats.UnitBytes(format: source));
        var blockBytes = ((int)GpuPixelFormats.UnitBytes(format: format));
        var columns = ((int)GpuPixelFormats.BlocksAcross(texels: ((uint)width)));
        var rows = ((int)GpuPixelFormats.BlocksAcross(texels: ((uint)height)));
        var texels = new byte[((width * height) * texelBytes)];
        Span<byte> decoded = stackalloc byte[(16 * 8)];
        Span<ushort> halves = stackalloc ushort[48];

        for (var row = 0; (row < rows); row++) {
            for (var column = 0; (column < columns); column++) {
                var block = blocks.Slice(length: blockBytes, start: (((row * columns) + column) * blockBytes));

                switch (format) {
                    case GpuPixelFormat.Bc4Unorm:
                        Bc4Codec.DecodeBlock(block: block, values: decoded);
                        break;
                    case GpuPixelFormat.Bc5Unorm:
                        Bc5Codec.DecodeBlock(block: block, values: decoded);
                        break;
                    case GpuPixelFormat.Bc6hUfloat:
                        Bc6hCodec.DecodeBlock(block: block, rgb: halves);

                        for (var texel = 0; (texel < 16); texel++) {
                            for (var channel = 0; (channel < 3); channel++) {
                                BinaryPrimitives.WriteUInt16LittleEndian(destination: decoded[((texel * 8) + (channel * 2))..], value: halves[((texel * 3) + channel)]);
                            }

                            BinaryPrimitives.WriteUInt16LittleEndian(destination: decoded[((texel * 8) + 6)..], value: 0x3C00);
                        }

                        break;
                    default:
                        Bc7Codec.DecodeBlock(block: block, rgba: decoded);
                        break;
                }

                for (var texel = 0; (texel < 16); texel++) {
                    var x = ((column * 4) + (texel & 3));
                    var y = ((row * 4) + (texel >> 2));

                    if ((x < width) && (y < height)) {
                        decoded.Slice(length: texelBytes, start: (texel * texelBytes)).CopyTo(destination: texels.AsSpan(start: (((y * width) + x) * texelBytes)));
                    }
                }
            }
        }

        return texels;
    }

    private static GpuPixelFormat Require(GpuPixelFormat format, int width, int height, int length) {
        if (!GpuPixelFormats.IsBlockCompressed(format: format) || (width <= 0) || (height <= 0)) {
            throw new ArgumentException(message: $"a {width}x{height} {format} level is not a block-compressed level.", paramName: nameof(format));
        }

        var source = SourceOf(format: format);

        if (((ulong)length) != GpuPixelFormats.LevelByteLength(format: source, height: ((uint)height), width: ((uint)width))) {
            throw new ArgumentException(message: $"{length} bytes are not a {width}x{height} {source} level.", paramName: nameof(length));
        }

        return source;
    }
}
