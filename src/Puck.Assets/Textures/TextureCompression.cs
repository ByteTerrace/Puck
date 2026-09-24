using System.Buffers.Binary;

namespace Puck.Assets.Textures;

/// <summary>Compresses and decompresses whole texture levels block by block through <see cref="Bc4Codec"/>,
/// <see cref="Bc5Codec"/>, <see cref="Bc6hCodec"/> and <see cref="Bc7Codec"/>. A level's texels are in the uncompressed
/// format its block format encodes (<see cref="TextureFormats.SourceOf"/>); a block that runs past the level's right or
/// bottom edge repeats the edge texels, and decompression writes only the texels inside the level.</summary>
public static class TextureCompression {
    /// <summary>Compresses one level.</summary>
    /// <param name="texels">The level's texels in <c>TextureFormats.SourceOf(format)</c>, rows top to bottom.</param>
    /// <param name="format">The block-compressed format.</param>
    /// <param name="width">The level's width, in texels.</param>
    /// <param name="height">The level's height, in texels.</param>
    /// <returns>The blocks, rows of blocks top to bottom.</returns>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not block-compressed, a side is not positive, or
    /// <paramref name="texels"/> does not hold exactly the level.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> texels, TextureFormat format, int width, int height) {
        var source = Require(format: format, height: height, length: texels.Length, width: width);
        var texelBytes = TextureFormats.BytesPerUnit(format: source);
        var blockBytes = TextureFormats.BytesPerUnit(format: format);
        var columns = TextureFormats.BlocksAlong(texels: width);
        var rows = TextureFormats.BlocksAlong(texels: height);
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
                    case TextureFormat.Bc4Unorm:
                        Bc4Codec.EncodeBlock(block: block, values: gathered);
                        break;
                    case TextureFormat.Bc5Unorm:
                        Bc5Codec.EncodeBlock(block: block, values: gathered);
                        break;
                    case TextureFormat.Bc6hUfloat:
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
    /// <returns>The texels in <c>TextureFormats.SourceOf(format)</c>, rows top to bottom; BC6H's alpha is one.</returns>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not block-compressed, a side is not positive, or
    /// <paramref name="blocks"/> does not hold exactly the level.</exception>
    /// <exception cref="NotSupportedException">A block is in a mode the codec's decoder does not read.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> blocks, TextureFormat format, int width, int height) {
        if (!TextureFormats.IsBlockCompressed(format: format) || (width <= 0) || (height <= 0) || (blocks.Length != TextureFormats.LevelBytes(format: format, height: height, width: width))) {
            throw new ArgumentException(message: $"{blocks.Length} bytes are not a {width}x{height} {format} level.", paramName: nameof(blocks));
        }

        var source = TextureFormats.SourceOf(format: format);
        var texelBytes = TextureFormats.BytesPerUnit(format: source);
        var blockBytes = TextureFormats.BytesPerUnit(format: format);
        var columns = TextureFormats.BlocksAlong(texels: width);
        var rows = TextureFormats.BlocksAlong(texels: height);
        var texels = new byte[((width * height) * texelBytes)];
        Span<byte> decoded = stackalloc byte[(16 * 8)];
        Span<ushort> halves = stackalloc ushort[48];

        for (var row = 0; (row < rows); row++) {
            for (var column = 0; (column < columns); column++) {
                var block = blocks.Slice(length: blockBytes, start: (((row * columns) + column) * blockBytes));

                switch (format) {
                    case TextureFormat.Bc4Unorm:
                        Bc4Codec.DecodeBlock(block: block, values: decoded);
                        break;
                    case TextureFormat.Bc5Unorm:
                        Bc5Codec.DecodeBlock(block: block, values: decoded);
                        break;
                    case TextureFormat.Bc6hUfloat:
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

    private static TextureFormat Require(TextureFormat format, int width, int height, int length) {
        if (!TextureFormats.IsBlockCompressed(format: format) || (width <= 0) || (height <= 0)) {
            throw new ArgumentException(message: $"a {width}x{height} {format} level is not a block-compressed level.", paramName: nameof(format));
        }

        var source = TextureFormats.SourceOf(format: format);

        if (length != TextureFormats.LevelBytes(format: source, height: height, width: width)) {
            throw new ArgumentException(message: $"{length} bytes are not a {width}x{height} {source} level.", paramName: nameof(length));
        }

        return source;
    }
}
