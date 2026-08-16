using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Xunit;

namespace Puck.Assets.Tests;

public sealed class PngCodecLawTests {
    private static void AppendChunk(MemoryStream stream, string type, ReadOnlySpan<byte> data) {
        Span<byte> word = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(
            destination: word,
            value: ((uint)data.Length)
        );
        stream.Write(buffer: word);

        var typeBytes = Encoding.ASCII.GetBytes(s: type);

        stream.Write(buffer: typeBytes);
        stream.Write(buffer: data);

        var crc = new Crc32();

        crc.Append(source: typeBytes);
        crc.Append(source: data);
        BinaryPrimitives.WriteUInt32BigEndian(
            destination: word,
            value: crc.GetCurrentHashAsUInt32()
        );
        stream.Write(buffer: word);
    }
    private static byte[] BuildFromChunks(params (string Type, byte[] Data)[] chunks) {
        using var stream = new MemoryStream();

        stream.Write(buffer: [137, 80, 78, 71, 13, 10, 26, 10]);

        foreach (var (type, data) in chunks) {
            AppendChunk(
                data: data,
                stream: stream,
                type: type
            );
        }

        return stream.ToArray();
    }
    private static byte[] BuildHeader(int width, int height, byte colorType, byte compressionMethod = 0) {
        var header = new byte[13];

        BinaryPrimitives.WriteUInt32BigEndian(
            destination: header.AsSpan(start: 0),
            value: ((uint)width)
        );
        BinaryPrimitives.WriteUInt32BigEndian(
            destination: header.AsSpan(start: 4),
            value: ((uint)height)
        );
        header[8] = 8;
        header[9] = colorType;
        header[10] = compressionMethod;
        return header;
    }
    private static (string Type, byte[] Data) Chunk(string type, params byte[] data) =>
        (type, data);
    private static byte[] Compress(byte[] data) {
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
    private static byte[] EncodeAnimationToBytes(IReadOnlyList<ReadOnlyMemory<byte>> frames, int width, int height) {
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: Path.GetRandomFileName()
        );

        try {
            PngEncoder.WriteAnimation(
                path: path,
                frames: frames,
                width: width,
                height: height,
                delayNumerator: 1,
                delayDenominator: 30
            );

            return File.ReadAllBytes(path: path);
        } finally {
            File.Delete(path: path);
        }
    }
    private static byte[] EncodeToBytes(byte[] rgba, int width, int height) {
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: Path.GetRandomFileName()
        );

        try {
            PngEncoder.Write(
                height: height,
                path: path,
                rgba: rgba,
                width: width
            );

            return File.ReadAllBytes(path: path);
        } finally {
            File.Delete(path: path);
        }
    }
    // The forward filters, independent of the decoder's inverse; row index selects the filter type, so five
    // rows exercise all five filters.
    private static byte[] FilterForward(byte[][] rows, int bytesPerPixel) {
        var stride = rows[0].Length;
        var output = new MemoryStream();
        var previous = new byte[stride];

        for (var rowIndex = 0; (rowIndex < rows.Length); rowIndex++) {
            var raw = rows[rowIndex];
            var filterType = ((byte)(rowIndex % 5));

            output.WriteByte(value: filterType);

            for (var index = 0; (index < stride); index++) {
                var left = ((index >= bytesPerPixel)
                    ? raw[(index - bytesPerPixel)]
                    : 0
                );
                var up = previous[index];
                var upperLeft = ((index >= bytesPerPixel)
                    ? previous[(index - bytesPerPixel)]
                    : 0
                );
                var predictor = filterType switch {
                    1 => left,
                    2 => up,
                    3 => ((left + up) / 2),
                    4 => Paeth(
                    left: left,
                    up: up,
                    upperLeft: upperLeft
                ),
                    _ => 0,
                };

                output.WriteByte(value: unchecked((byte)(raw[index] - predictor)));
            }

            previous = raw;
        }

        return output.ToArray();
    }
    private static byte[] MakePixels(int width, int height) {
        var rgba = new byte[((width * height) * 4)];

        for (var index = 0; (index < rgba.Length); index++) {
            rgba[index] = unchecked((byte)((index * 31) + 7));
        }

        return rgba;
    }
    private static int Paeth(int left, int up, int upperLeft) {
        var predictor = ((left + up) - upperLeft);
        var leftDistance = Math.Abs(value: (predictor - left));
        var upDistance = Math.Abs(value: (predictor - up));
        var upperLeftDistance = Math.Abs(value: (predictor - upperLeft));

        if (
            (leftDistance <= upDistance) &&
            (leftDistance <= upperLeftDistance)
        ) {
            return left;
        }

        return ((upDistance <= upperLeftDistance)
            ? up
            : upperLeft
        );
    }
    private static List<(string Type, byte[] Data)> WalkChunks(ReadOnlySpan<byte> pngBytes) {
        var chunks = new List<(string Type, byte[] Data)>();
        var offset = 8;

        while (offset < pngBytes.Length) {
            var length = ((int)BinaryPrimitives.ReadUInt32BigEndian(source: pngBytes[offset..(offset + 4)]));
            var type = Encoding.ASCII.GetString(bytes: pngBytes[(offset + 4)..(offset + 8)]);

            chunks.Add(item: (type, pngBytes[(offset + 8)..((offset + 8) + length)].ToArray()));
            offset += (12 + length);
        }

        return chunks;
    }

    [Fact]
    public void AnimationCarriesTheDeclaredFrameCountAndAnUnbrokenSequence() {
        const int Width = 4;
        const int Height = 2;

        var frames = new ReadOnlyMemory<byte>[3];

        for (var frameIndex = 0; (frameIndex < frames.Length); frameIndex++) {
            var rgba = MakePixels(
                height: Height,
                width: Width
            );

            rgba[0] = ((byte)frameIndex);
            frames[frameIndex] = rgba;
        }

        var chunks = WalkChunks(pngBytes: EncodeAnimationToBytes(
            frames: frames,
            height: Height,
            width: Width
        ));

        Assert.Equal(
            expected: ["IHDR", "acTL", "fcTL", "IDAT", "fcTL", "fdAT", "fcTL", "fdAT", "IEND"],
            actual: chunks.Select(selector: chunk => chunk.Type)
        );

        var animationControl = chunks.Single(predicate: chunk => (chunk.Type == "acTL")).Data;

        Assert.Equal(
            expected: ((uint)frames.Length),
            actual: BinaryPrimitives.ReadUInt32BigEndian(source: animationControl.AsSpan(start: 0))
        );

        var sequenceNumbers = chunks
            .Where(predicate: chunk => (chunk.Type is "fcTL" or "fdAT"))
            .Select(selector: chunk => BinaryPrimitives.ReadUInt32BigEndian(source: chunk.Data.AsSpan(start: 0)));

        Assert.Equal(
            actual: sequenceNumbers,
            expected: [0u, 1u, 2u, 3u, 4u]
        );
    }
    [Fact]
    public void AnimationDecodesToItsEncodedFramesAndTiming() {
        const int Width = 4;
        const int Height = 2;

        var frames = new ReadOnlyMemory<byte>[3];

        for (var frameIndex = 0; (frameIndex < frames.Length); frameIndex++) {
            var rgba = MakePixels(
                height: Height,
                width: Width
            );

            rgba[0] = ((byte)frameIndex);
            frames[frameIndex] = rgba;
        }

        var animation = PngDecoder.DecodeAnimation(pngBytes: EncodeAnimationToBytes(
            frames: frames,
            height: Height,
            width: Width
        ));

        Assert.Equal(
            expected: Width,
            actual: animation.Width
        );
        Assert.Equal(
            expected: Height,
            actual: animation.Height
        );
        Assert.Equal(
            expected: 0u,
            actual: animation.PlayCount
        );
        Assert.Equal(
            expected: frames.Length,
            actual: animation.Frames.Count
        );

        for (var frameIndex = 0; (frameIndex < frames.Length); frameIndex++) {
            Assert.Equal(
                expected: frames[frameIndex].ToArray(),
                actual: animation.Frames[frameIndex].RgbaPixels
            );
            Assert.Equal(
                expected: ((ushort)1),
                actual: animation.Frames[frameIndex].DelayNumerator
            );
            Assert.Equal(
                expected: ((ushort)30),
                actual: animation.Frames[frameIndex].DelayDenominator
            );
        }
    }
    [Fact]
    public void AnimationDecodesToItsFirstFrameAsTheStill() {
        const int Width = 4;
        const int Height = 2;

        var firstFrame = MakePixels(
            height: Height,
            width: Width
        );
        var secondFrame = new byte[firstFrame.Length];

        Array.Fill(
            array: secondFrame,
            value: ((byte)0xAB)
        );

        var image = PngDecoder.Decode(pngBytes: EncodeAnimationToBytes(
            frames: [firstFrame, secondFrame],
            height: Height,
            width: Width
        ));

        Assert.Equal(
            expected: firstFrame,
            actual: image.RgbaPixels
        );
    }
    [Fact]
    public void CompressedPayloadMustMatchTheDeclaredSizeExactly() {
        var header = BuildHeader(
            width: 1,
            height: 1,
            colorType: 0
        );

        _ = Assert.Throws<InvalidDataException>(testCode: () => PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                header
            ),
            Chunk(
                "IDAT",
                Compress(data: [0, 7, 99])
            ),
            Chunk("IEND")
        )));
        _ = Assert.Throws<InvalidDataException>(testCode: () => PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                header
            ),
            Chunk(
                "IDAT",
                Compress(data: [0])
            ),
            Chunk("IEND")
        )));
    }
    [Fact]
    public void CorruptedChunkIsRefused() {
        var pngBytes = EncodeToBytes(
            rgba: MakePixels(
                height: 4,
                width: 4
            ),
            width: 4,
            height: 4
        );

        pngBytes[^1] ^= byte.MaxValue;

        _ = Assert.Throws<InvalidDataException>(testCode: () => PngDecoder.Decode(pngBytes: pngBytes));
    }
    [Fact]
    public void EncodedFrameDecodesToTheIdenticalPixels() {
        const int Width = 5;
        const int Height = 3;

        var rgba = MakePixels(
            height: Height,
            width: Width
        );
        var image = PngDecoder.Decode(pngBytes: EncodeToBytes(
            height: Height,
            rgba: rgba,
            width: Width
        ));

        Assert.Equal(
            expected: Width,
            actual: image.Width
        );
        Assert.Equal(
            expected: Height,
            actual: image.Height
        );
        Assert.Equal(
            expected: rgba,
            actual: image.RgbaPixels
        );
    }
    [Fact]
    public void EncoderRefusesNonPositiveOrMismatchedDimensions() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => PngEncoder.Write(
            height: 1,
            path: "unused.png",
            rgba: [],
            width: 0
        ));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => PngEncoder.Write(
            height: -1,
            path: "unused.png",
            rgba: [],
            width: 1
        ));
        _ = Assert.Throws<ArgumentException>(testCode: () => PngEncoder.Write(
            height: 2,
            path: "unused.png",
            rgba: [],
            width: 0x40000000
        ));
    }
    [Fact]
    public void ForeignColorTypesDecodeAcrossAllFilters() {
        (byte ColorType, int BytesPerPixel)[] subjects = [(0, 1), (2, 3), (4, 2)];

        foreach (var (colorType, bytesPerPixel) in subjects) {
            const int Width = 4;
            const int Height = 5;

            var rows = new byte[Height][];

            for (var rowIndex = 0; (rowIndex < Height); rowIndex++) {
                var row = new byte[(Width * bytesPerPixel)];

                for (var index = 0; (index < row.Length); index++) {
                    row[index] = unchecked((byte)(((rowIndex * 67) + (index * 13)) + colorType));
                }

                rows[rowIndex] = row;
            }

            var image = PngDecoder.Decode(pngBytes: BuildFromChunks(
                Chunk(
                    "IHDR",
                    BuildHeader(
                        width: Width,
                        height: Height,
                        colorType: colorType
                    )
                ),
                Chunk(
                    "IDAT",
                    Compress(data: FilterForward(
                        bytesPerPixel: bytesPerPixel,
                        rows: rows
                    ))
                ),
                Chunk("IEND")
            ));

            var expected = new byte[((Width * Height) * 4)];

            for (var rowIndex = 0; (rowIndex < Height); rowIndex++) {
                for (var pixelIndex = 0; (pixelIndex < Width); pixelIndex++) {
                    var source = rows[rowIndex].AsSpan(
                        length: bytesPerPixel,
                        start: (pixelIndex * bytesPerPixel)
                    );
                    var destination = expected.AsSpan(
                        length: 4,
                        start: (((rowIndex * Width) + pixelIndex) * 4)
                    );

                    switch (colorType) {
                        case 0:
                            destination[0] = source[0];
                            destination[1] = source[0];
                            destination[2] = source[0];
                            destination[3] = byte.MaxValue;
                            break;
                        case 2:
                            destination[0] = source[0];
                            destination[1] = source[1];
                            destination[2] = source[2];
                            destination[3] = byte.MaxValue;
                            break;
                        case 4:
                            destination[0] = source[0];
                            destination[1] = source[0];
                            destination[2] = source[0];
                            destination[3] = source[1];
                            break;
                    }
                }
            }

            Assert.Equal(
                expected: expected,
                actual: image.RgbaPixels
            );
        }
    }
    [Fact]
    public void MalformedHeadersAreRefused() {
        var header = BuildHeader(
            width: 1,
            height: 1,
            colorType: 0
        );
        var scanline = Compress(data: [0, 7]);

        _ = Assert.Throws<InvalidDataException>(testCode: () => PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "gAMA",
                0,
                0,
                0,
                1
            ),
            Chunk(
                "IHDR",
                header
            ),
            Chunk(
                "IDAT",
                scanline
            ),
            Chunk("IEND")
        )));
        _ = Assert.Throws<InvalidDataException>(testCode: () => PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                header
            ),
            Chunk(
                "IHDR",
                header
            ),
            Chunk(
                "IDAT",
                scanline
            ),
            Chunk("IEND")
        )));
        _ = Assert.Throws<InvalidDataException>(testCode: () => PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                [.. header, 0]
            ),
            Chunk(
                "IDAT",
                scanline
            ),
            Chunk("IEND")
        )));
        _ = Assert.Throws<InvalidDataException>(testCode: () => PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                BuildHeader(
                    colorType: 0,
                    compressionMethod: 1,
                    height: 1,
                    width: 1
                )
            ),
            Chunk(
                "IDAT",
                scanline
            ),
            Chunk("IEND")
        )));
    }
    [Fact]
    public void StillDecodesAsASingleFrameAnimation() {
        const int Width = 5;
        const int Height = 3;

        var rgba = MakePixels(
            height: Height,
            width: Width
        );
        var animation = PngDecoder.DecodeAnimation(pngBytes: EncodeToBytes(
            height: Height,
            rgba: rgba,
            width: Width
        ));

        var frame = Assert.Single(collection: animation.Frames);

        Assert.Equal(
            expected: rgba,
            actual: frame.RgbaPixels
        );
    }
    [Fact]
    public void TransparentColorMetadataDrivesAlpha() {
        var gray = PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                BuildHeader(
                    width: 3,
                    height: 1,
                    colorType: 0
                )
            ),
            Chunk(
                "tRNS",
                0,
                10
            ),
            Chunk(
                "IDAT",
                Compress(data: [0, 10, 20, 10])
            ),
            Chunk("IEND")
        ));

        Assert.Equal(
            expected: [10, 10, 10, 0, 20, 20, 20, 255, 10, 10, 10, 0],
            actual: gray.RgbaPixels
        );

        var rgb = PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                BuildHeader(
                    width: 2,
                    height: 1,
                    colorType: 2
                )
            ),
            Chunk(
                "tRNS",
                0,
                1,
                0,
                2,
                0,
                3
            ),
            Chunk(
                "IDAT",
                Compress(data: [0, 1, 2, 3, 9, 9, 9])
            ),
            Chunk("IEND")
        ));

        Assert.Equal(
            expected: [1, 2, 3, 0, 9, 9, 9, 255],
            actual: rgb.RgbaPixels
        );

        _ = Assert.Throws<InvalidDataException>(testCode: () => PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                BuildHeader(
                    width: 1,
                    height: 1,
                    colorType: 6
                )
            ),
            Chunk(
                "tRNS",
                0,
                0
            ),
            Chunk(
                "IDAT",
                Compress(data: [0, 1, 2, 3, 4])
            ),
            Chunk("IEND")
        )));
    }
    [Fact]
    public void UnknownCriticalChunkIsRefusedAndAncillaryIsIgnored() {
        var header = BuildHeader(
            width: 1,
            height: 1,
            colorType: 0
        );
        var scanline = Compress(data: [0, 7]);
        var exception = Assert.Throws<InvalidDataException>(testCode: () => PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                header
            ),
            Chunk(
                "MEOW",
                1,
                2,
                3
            ),
            Chunk(
                "IDAT",
                scanline
            ),
            Chunk("IEND")
        )));

        Assert.Contains(
            expectedSubstring: "MEOW",
            actualString: exception.Message
        );

        var image = PngDecoder.Decode(pngBytes: BuildFromChunks(
            Chunk(
                "IHDR",
                header
            ),
            Chunk(
                "meOW",
                1,
                2,
                3
            ),
            Chunk(
                "IDAT",
                scanline
            ),
            Chunk("IEND")
        ));

        Assert.Equal(
            expected: [7, 7, 7, 255],
            actual: image.RgbaPixels
        );
    }
}
