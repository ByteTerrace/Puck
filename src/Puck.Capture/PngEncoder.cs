using System.Buffers.Binary;
using System.IO.Compression;

namespace Puck.Capture;

/// <summary>
/// A minimal, dependency-free PNG encoder for dumping captured frames to disk: 8-bit RGBA (color type 6), no
/// row filtering, zlib-compressed scanlines. Just enough to write a viewable file — not a general image library.
/// </summary>
public static class PngEncoder {
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes tightly packed 8-bit RGBA pixels to a PNG file.</summary>
    /// <param name="path">The output file path.</param>
    /// <param name="rgba">The pixels, row-major, 4 bytes (R, G, B, A) each, with no row padding.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <exception cref="ArgumentException"><paramref name="rgba"/> is not exactly <c>width * height * 4</c> bytes.</exception>
    public static void Write(string path, ReadOnlySpan<byte> rgba, int width, int height) {
        var rowBytes = (width * 4);

        if (rgba.Length != (rowBytes * height)) {
            throw new ArgumentException(
                message: $"Expected {(rowBytes * height)} bytes of RGBA for {width}x{height}, got {rgba.Length}.",
                paramName: nameof(rgba)
            );
        }

        using var stream = File.Create(path: path);

        stream.Write(buffer: [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];

        BinaryPrimitives.WriteUInt32BigEndian(destination: header.AsSpan(start: 0), value: (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(destination: header.AsSpan(start: 4), value: (uint)height);
        header[8] = 8; // bit depth
        header[9] = 6; // color type: truecolor with alpha
        WriteChunk(stream: stream, type: "IHDR", data: header);

        // Prefix each scanline with a "none" filter byte, then zlib-compress the lot.
        var filtered = new byte[(height * (1 + rowBytes))];

        for (var y = 0; (y < height); y++) {
            rgba.Slice(start: (y * rowBytes), length: rowBytes).CopyTo(destination: filtered.AsSpan(start: ((y * (1 + rowBytes)) + 1), length: rowBytes));
        }

        WriteChunk(stream: stream, type: "IDAT", data: ZlibCompress(data: filtered));
        WriteChunk(stream: stream, type: "IEND", data: []);
    }

    private static byte[] ZlibCompress(byte[] data) {
        using var buffer = new MemoryStream();

        buffer.WriteByte(value: 0x78); // zlib header: 32K window, deflate
        buffer.WriteByte(value: 0x01);

        using (var deflate = new DeflateStream(stream: buffer, compressionLevel: CompressionLevel.Optimal, leaveOpen: true)) {
            deflate.Write(buffer: data);
        }

        var adler = Adler32(data: data);
        Span<byte> trailer = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(destination: trailer, value: adler);
        buffer.Write(buffer: trailer);

        return buffer.ToArray();
    }
    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data) {
        Span<byte> length = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(destination: length, value: (uint)data.Length);
        stream.Write(buffer: length);

        var typeBytes = new byte[4];

        for (var index = 0; (index < 4); index++) {
            typeBytes[index] = (byte)type[index];
        }

        stream.Write(buffer: typeBytes);
        stream.Write(buffer: data);

        var crc = 0xFFFFFFFFu;

        crc = UpdateCrc(crc: crc, data: typeBytes);
        crc = UpdateCrc(crc: crc, data: data);

        Span<byte> crcBytes = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(destination: crcBytes, value: crc ^ 0xFFFFFFFFu);
        stream.Write(buffer: crcBytes);
    }
    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data) {
        foreach (var value in data) {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
    private static uint Adler32(ReadOnlySpan<byte> data) {
        const uint Modulo = 65521;

        var a = 1U;
        var b = 0U;

        foreach (var value in data) {
            a = ((a + value) % Modulo);
            b = ((b + a) % Modulo);
        }

        return (b << 16) | a;
    }
    private static uint[] BuildCrcTable() {
        var table = new uint[256];

        for (var index = 0u; (index < 256); index++) {
            var value = index;

            for (var bit = 0; (bit < 8); bit++) {
                value = ((0 != (value & 1))
                    ? 0xEDB88320u ^ (value >> 1)
                    : (value >> 1));
            }

            table[index] = value;
        }

        return table;
    }
}
