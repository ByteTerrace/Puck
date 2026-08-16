using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace Puck.Assets;

/// <summary>
/// A minimal PNG encoder for dumping captured frames to disk: 8-bit RGBA (color type 6), no row filtering,
/// zlib-compressed scanlines — as a still, or as an APNG animation (acTL/fcTL/fdAT, every frame full-size).
/// Just enough to write a viewable file — not a general image library.
/// </summary>
public static class PngEncoder {
    private static byte[] FilterScanlines(ReadOnlySpan<byte> rgba, int width, int height) {
        var rowBytes = (width * 4);

        // Prefix each scanline with a "none" filter byte.
        var filtered = new byte[checked((height * (1 + rowBytes)))];

        for (var y = 0; (y < height); y++) {
            rgba.Slice(
                length: rowBytes,
                start: (y * rowBytes)
            ).CopyTo(destination: filtered.AsSpan(
                length: rowBytes,
                start: ((y * (1 + rowBytes)) + 1)
            ));
        }

        return filtered;
    }
    private static void ValidateFrame(ReadOnlySpan<byte> rgba, int width, int height, string paramName) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: height);

        var pixelCount = (((long)width) * height);

        if (
            (pixelCount > (int.MaxValue / 4)) ||
            (rgba.Length != (pixelCount * 4))
        ) {
            throw new ArgumentException(
                message: $"RGBA byte count {rgba.Length} did not match {width}x{height}.",
                paramName: paramName
            );
        }
    }
    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data) {
        Span<byte> length = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(
            destination: length,
            value: ((uint)data.Length)
        );
        stream.Write(buffer: length);

        var typeBytes = new byte[4];

        for (var index = 0; (index < 4); index++) {
            typeBytes[index] = ((byte)type[index]);
        }

        stream.Write(buffer: typeBytes);
        stream.Write(buffer: data);

        var crc = new Crc32();

        crc.Append(source: typeBytes);
        crc.Append(source: data);

        Span<byte> crcBytes = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(
            destination: crcBytes,
            value: crc.GetCurrentHashAsUInt32()
        );
        stream.Write(buffer: crcBytes);
    }
    private static void WriteSignatureAndHeader(Stream stream, int width, int height) {
        stream.Write(buffer: [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];

        BinaryPrimitives.WriteUInt32BigEndian(
            destination: header.AsSpan(start: 0),
            value: ((uint)width)
        );
        BinaryPrimitives.WriteUInt32BigEndian(
            destination: header.AsSpan(start: 4),
            value: ((uint)height)
        );
        header[8] = 8; // bit depth
        header[9] = 6; // color type: truecolor with alpha
        WriteChunk(
            data: header,
            stream: stream,
            type: "IHDR"
        );
    }
    private static byte[] ZlibCompress(byte[] data) {
        using var buffer = new MemoryStream();

        using (var zlib = new ZLibStream(
            compressionLevel: CompressionLevel.Optimal,
            leaveOpen: true,
            stream: buffer
        )) {
            zlib.Write(buffer: data);
        }

        return buffer.ToArray();
    }

    /// <summary>Writes tightly packed 8-bit RGBA pixels to a PNG file.</summary>
    /// <param name="path">The output file path.</param>
    /// <param name="rgba">The pixels, row-major, 4 bytes (R, G, B, A) each, with no row padding.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <exception cref="ArgumentException"><paramref name="rgba"/> is not exactly <c>width * height * 4</c> bytes.</exception>
    public static void Write(string path, ReadOnlySpan<byte> rgba, int width, int height) {
        ValidateFrame(
            rgba: rgba,
            width: width,
            height: height,
            paramName: nameof(rgba)
        );

        using var stream = File.Create(path: path);

        WriteSignatureAndHeader(
            height: height,
            stream: stream,
            width: width
        );
        WriteChunk(
            stream: stream,
            type: "IDAT",
            data: ZlibCompress(data: FilterScanlines(
                height: height,
                rgba: rgba,
                width: width
            ))
        );
        WriteChunk(
            data: [],
            stream: stream,
            type: "IEND"
        );
    }
    /// <summary>Writes tightly packed 8-bit RGBA frames to an APNG file, every frame full-size at a uniform delay.</summary>
    /// <param name="path">The output file path.</param>
    /// <param name="frames">The frames, each row-major RGBA with no row padding; the first frame is also the still a non-animated viewer shows.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="delayNumerator">The per-frame delay numerator, in <paramref name="delayDenominator"/>ths of a second.</param>
    /// <param name="delayDenominator">The per-frame delay denominator; 0 reads as 100 per the APNG specification.</param>
    /// <param name="playCount">How many times the animation loops; 0 loops forever.</param>
    /// <exception cref="ArgumentException"><paramref name="frames"/> is empty, or a frame is not exactly <c>width * height * 4</c> bytes.</exception>
    public static void WriteAnimation(string path, IReadOnlyList<ReadOnlyMemory<byte>> frames, int width, int height, ushort delayNumerator, ushort delayDenominator, uint playCount = 0) {
        ArgumentNullException.ThrowIfNull(frames);

        if (frames.Count == 0) {
            throw new ArgumentException(
                message: "An animation needs at least one frame.",
                paramName: nameof(frames)
            );
        }

        foreach (var frame in frames) {
            ValidateFrame(
                rgba: frame.Span,
                width: width,
                height: height,
                paramName: nameof(frames)
            );
        }

        using var stream = File.Create(path: path);

        WriteSignatureAndHeader(
            height: height,
            stream: stream,
            width: width
        );

        var animationControl = new byte[8];

        BinaryPrimitives.WriteUInt32BigEndian(
            destination: animationControl.AsSpan(start: 0),
            value: ((uint)frames.Count)
        );
        BinaryPrimitives.WriteUInt32BigEndian(
            destination: animationControl.AsSpan(start: 4),
            value: playCount
        );
        WriteChunk(
            data: animationControl,
            stream: stream,
            type: "acTL"
        );

        // fcTL and fdAT chunks share one sequence counter, in file order.
        var sequenceNumber = 0u;

        for (var frameIndex = 0; (frameIndex < frames.Count); frameIndex++) {
            var frameControl = new byte[26];

            BinaryPrimitives.WriteUInt32BigEndian(
                destination: frameControl.AsSpan(start: 0),
                value: sequenceNumber++
            );
            BinaryPrimitives.WriteUInt32BigEndian(
                destination: frameControl.AsSpan(start: 4),
                value: ((uint)width)
            );
            BinaryPrimitives.WriteUInt32BigEndian(
                destination: frameControl.AsSpan(start: 8),
                value: ((uint)height)
            );
            BinaryPrimitives.WriteUInt16BigEndian(
                destination: frameControl.AsSpan(start: 20),
                value: delayNumerator
            );
            BinaryPrimitives.WriteUInt16BigEndian(
                destination: frameControl.AsSpan(start: 22),
                value: delayDenominator
            );
            WriteChunk(
                data: frameControl,
                stream: stream,
                type: "fcTL"
            );

            var compressed = ZlibCompress(data: FilterScanlines(
                rgba: frames[frameIndex].Span,
                width: width,
                height: height
            ));

            if (frameIndex == 0) {
                WriteChunk(
                    data: compressed,
                    stream: stream,
                    type: "IDAT"
                );
            } else {
                var frameData = new byte[(4 + compressed.Length)];

                BinaryPrimitives.WriteUInt32BigEndian(
                    destination: frameData.AsSpan(start: 0),
                    value: sequenceNumber++
                );
                compressed.CopyTo(
                    array: frameData,
                    index: 4
                );
                WriteChunk(
                    data: frameData,
                    stream: stream,
                    type: "fdAT"
                );
            }
        }

        WriteChunk(
            data: [],
            stream: stream,
            type: "IEND"
        );
    }
}
