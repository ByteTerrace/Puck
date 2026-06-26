using System.Buffers.Binary;
using System.IO.Compression;

namespace Puck.HumbleGamingBrick.Conformance.Imaging;

/// <summary>A decoded image as packed RGBA pixels (<c>0xAABBGGRR</c>, row-major) — the same packing the emulator's
/// framebuffer uses, so a decoded reference compares directly against <c>IPpu.Framebuffer</c>.</summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="Pixels">The pixels, row-major, packed <c>0xAABBGGRR</c> with alpha forced opaque.</param>
public sealed record PngImage(int Width, int Height, uint[] Pixels);

/// <summary>A minimal PNG decoder for the test reference images — enough to read the non-interlaced grayscale,
/// palette, and truecolor PNGs the GB test suites ship (2-bit grayscale, 2/4-bit palette, 8-bit RGB/RGBA). Uses only
/// the BCL (<see cref="ZLibStream"/>); no third-party image dependency.</summary>
public static class PngDecoder {
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Decodes a PNG file.</summary>
    /// <param name="path">The PNG file path.</param>
    /// <returns>The decoded image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The file is not a PNG this decoder supports.</exception>
    public static PngImage Decode(string path) {
        ArgumentNullException.ThrowIfNull(argument: path);

        return Decode(data: File.ReadAllBytes(path: path));
    }

    /// <summary>Decodes a PNG from a byte buffer.</summary>
    /// <param name="data">The PNG file bytes.</param>
    /// <returns>The decoded image.</returns>
    /// <exception cref="InvalidDataException">The data is not a PNG this decoder supports.</exception>
    public static PngImage Decode(byte[] data) {
        ArgumentNullException.ThrowIfNull(argument: data);

        if ((data.Length < 8) || !data.AsSpan(start: 0, length: 8).SequenceEqual(other: Signature)) {
            throw new InvalidDataException(message: "Not a PNG file.");
        }

        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = 0;
        byte[]? palette = null;

        using var compressed = new MemoryStream();

        var position = 8;

        while ((position + 8) <= data.Length) {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(source: data.AsSpan(start: position, length: 4));
            var type = data.AsSpan(start: position + 4, length: 4);
            var payload = data.AsSpan(start: position + 8, length: length);

            if (IsType(type: type, a: 'I', b: 'H', c: 'D', d: 'R')) {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(source: payload[..4]);
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(source: payload.Slice(start: 4, length: 4));
                bitDepth = payload[8];
                colorType = payload[9];

                if (payload[12] != 0) {
                    throw new InvalidDataException(message: "Interlaced PNG is not supported.");
                }
            }
            else if (IsType(type: type, a: 'P', b: 'L', c: 'T', d: 'E')) {
                palette = payload.ToArray();
            }
            else if (IsType(type: type, a: 'I', b: 'D', c: 'A', d: 'T')) {
                compressed.Write(buffer: payload);
            }
            else if (IsType(type: type, a: 'I', b: 'E', c: 'N', d: 'D')) {
                break;
            }

            position += (12 + length);
        }

        compressed.Position = 0;

        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(stream: compressed, mode: CompressionMode.Decompress)) {
            zlib.CopyTo(destination: inflated);
        }

        return BuildImage(inflated: inflated.ToArray(), width: width, height: height, bitDepth: bitDepth, colorType: colorType, palette: palette);
    }

    private static PngImage BuildImage(byte[] inflated, int width, int height, int bitDepth, int colorType, byte[]? palette) {
        if (bitDepth == 16) {
            throw new InvalidDataException(message: "16-bit PNG is not supported.");
        }

        var channels = colorType switch {
            0 => 1, // grayscale
            2 => 3, // truecolor
            3 => 1, // palette index
            4 => 2, // grayscale + alpha
            6 => 4, // truecolor + alpha
            _ => throw new InvalidDataException(message: "Unsupported PNG color type."),
        };

        var bitsPerPixel = (channels * bitDepth);
        var stride = ((width * bitsPerPixel) + 7) / 8;
        var bytesPerPixel = Math.Max(val1: 1, val2: bitsPerPixel / 8);
        var rows = Unfilter(inflated: inflated, height: height, stride: stride, bytesPerPixel: bytesPerPixel);
        var pixels = new uint[width * height];
        var maximum = (1 << bitDepth) - 1;

        for (var y = 0; y < height; y += 1) {
            var rowBase = (y * stride);

            for (var x = 0; x < width; x += 1) {
                uint r;
                uint g;
                uint b;

                switch (colorType) {
                    case 0: {
                        var gray = (uint)(Sample(rows: rows, rowBase: rowBase, index: x, bitDepth: bitDepth) * 255 / maximum);

                        r = gray;
                        g = gray;
                        b = gray;

                        break;
                    }
                    case 3: {
                        var entry = Sample(rows: rows, rowBase: rowBase, index: x, bitDepth: bitDepth) * 3;

                        r = palette![entry];
                        g = palette[entry + 1];
                        b = palette[entry + 2];

                        break;
                    }
                    case 4: {
                        var gray = (uint)rows[rowBase + (x * 2)];

                        r = gray;
                        g = gray;
                        b = gray;

                        break;
                    }
                    default: {
                        var offset = rowBase + (x * channels);

                        r = rows[offset];
                        g = rows[offset + 1];
                        b = rows[offset + 2];

                        break;
                    }
                }

                pixels[(y * width) + x] = (0xFF000000u | (b << 16) | (g << 8) | r);
            }
        }

        return new PngImage(Width: width, Height: height, Pixels: pixels);
    }

    private static byte[] Unfilter(byte[] inflated, int height, int stride, int bytesPerPixel) {
        var rows = new byte[height * stride];

        for (var y = 0; y < height; y += 1) {
            var filter = inflated[y * (stride + 1)];
            var sourceBase = (y * (stride + 1)) + 1;
            var rowBase = (y * stride);
            var previousBase = (y - 1) * stride;

            for (var i = 0; i < stride; i += 1) {
                var left = (i >= bytesPerPixel) ? rows[rowBase + i - bytesPerPixel] : 0;
                var up = (y > 0) ? rows[previousBase + i] : 0;
                var upperLeft = ((y > 0) && (i >= bytesPerPixel)) ? rows[previousBase + i - bytesPerPixel] : 0;
                var value = inflated[sourceBase + i];

                var reconstructed = filter switch {
                    1 => value + left,
                    2 => value + up,
                    3 => value + ((left + up) / 2),
                    4 => value + Paeth(a: left, b: up, c: upperLeft),
                    _ => value,
                };

                rows[rowBase + i] = (byte)reconstructed;
            }
        }

        return rows;
    }

    // Extracts the index-th sample from a row packed MSB-first at the given bit depth (8 bits or fewer).
    private static int Sample(byte[] rows, int rowBase, int index, int bitDepth) {
        if (bitDepth == 8) {
            return rows[rowBase + index];
        }

        var bitOffset = (index * bitDepth);
        var current = rows[rowBase + (bitOffset / 8)];
        var shift = 8 - bitDepth - (bitOffset % 8);

        return (current >> shift) & ((1 << bitDepth) - 1);
    }

    private static int Paeth(int a, int b, int c) {
        var p = (a + b) - c;
        var pa = Math.Abs(value: p - a);
        var pb = Math.Abs(value: p - b);
        var pc = Math.Abs(value: p - c);

        if ((pa <= pb) && (pa <= pc)) {
            return a;
        }

        return (pb <= pc) ? b : c;
    }

    private static bool IsType(ReadOnlySpan<byte> type, char a, char b, char c, char d) =>
        (type[0] == a) && (type[1] == b) && (type[2] == c) && (type[3] == d);
}
