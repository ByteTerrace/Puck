using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Puck.Assets;

/// <summary>
/// A minimal PNG decoder — <see cref="PngEncoder"/>'s read-side half: 8-bit, non-interlaced,
/// color types 0 (grayscale), 2 (RGB), 4 (grayscale + alpha), and 6 (RGBA), with all five scanline filters,
/// every chunk CRC-checked, tRNS transparent-color metadata applied, and unknown critical chunks refused.
/// APNG animations are read back as full-size, source-blended frames; sub-rectangle and 'over'-blended frames
/// are refused. Output is always tightly packed 8-bit RGBA. Just enough to read the files Puck itself writes
/// and bakes — not a general image library.
/// </summary>
public static class PngDecoder {
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    private sealed class FrameAccumulator {
        public MemoryStream Data { get; } = new();
        public required ushort DelayDenominator { get; init; }
        public required ushort DelayNumerator { get; init; }
        public required bool UsesIdat { get; init; }
    }

    private static PngImage DecodeImageData(int width, int height, byte bitDepth, byte colorType, byte interlaceMethod, byte[] idatBytes, byte[]? transparency) {
        if (
            (width <= 0) ||
            (height <= 0)
        ) {
            throw new InvalidDataException(message: "PNG image dimensions must be greater than zero.");
        }

        if (bitDepth != 8) {
            throw new InvalidDataException(message: $"Unsupported PNG bit depth '{bitDepth}'. Only 8-bit PNGs are supported.");
        }

        if (interlaceMethod != 0) {
            throw new InvalidDataException(message: "Interlaced PNG images are not supported.");
        }

        var bytesPerPixel = colorType switch {
            6 => 4,
            4 => 2,
            2 => 3,
            0 => 1,
            _ => throw new InvalidDataException(message: $"Unsupported PNG color type '{colorType}'.")
        };

        var transparentGray = -1;
        var transparentRed = -1;
        var transparentGreen = -1;
        var transparentBlue = -1;

        if (transparency is not null) {
            switch (colorType) {
                case 0:
                    if (transparency.Length != 2) {
                        throw new InvalidDataException(message: "PNG tRNS chunk length must be 2 for grayscale.");
                    }

                    transparentGray = BinaryPrimitives.ReadUInt16BigEndian(source: transparency);
                    break;
                case 2:
                    if (transparency.Length != 6) {
                        throw new InvalidDataException(message: "PNG tRNS chunk length must be 6 for truecolor.");
                    }

                    transparentRed = BinaryPrimitives.ReadUInt16BigEndian(source: transparency.AsSpan(start: 0));
                    transparentGreen = BinaryPrimitives.ReadUInt16BigEndian(source: transparency.AsSpan(start: 2));
                    transparentBlue = BinaryPrimitives.ReadUInt16BigEndian(source: transparency.AsSpan(start: 4));
                    break;
                default:
                    throw new InvalidDataException(message: "PNG tRNS chunk is prohibited for color types that carry alpha.");
            }
        }

        // The declared dimensions bound the inflation: read exactly the expected bytes, then probe for excess,
        // so a small file cannot claim a small image while carrying an arbitrarily expanding stream.
        var stride = checked((width * bytesPerPixel));
        var expectedLength = checked(((stride + 1) * height));
        var decodedBytes = new byte[expectedLength];

        using (var idatStream = new MemoryStream(
            buffer: idatBytes,
            writable: false
        ))
        using (var zlibStream = new ZLibStream(
            mode: CompressionMode.Decompress,
            stream: idatStream
        )) {
            var totalRead = 0;

            while (totalRead < expectedLength) {
                var read = zlibStream.Read(buffer: decodedBytes.AsSpan(start: totalRead));

                if (read == 0) {
                    break;
                }

                totalRead += read;
            }

            if (
                (totalRead != expectedLength) ||
                (zlibStream.ReadByte() != -1)
            ) {
                throw new InvalidDataException(message: "Decoded PNG scanline length did not match the expected image size.");
            }
        }

        var rgbaPixels = new byte[checked(((width * height) * 4))];
        var previousRow = new byte[stride];
        var currentRow = new byte[stride];
        var sourceOffset = 0;

        for (var rowIndex = 0; (rowIndex < height); rowIndex++) {
            var filterType = decodedBytes[sourceOffset++];

            Array.Copy(
                destinationArray: currentRow,
                destinationIndex: 0,
                length: stride,
                sourceArray: decodedBytes,
                sourceIndex: sourceOffset
            );
            sourceOffset += stride;
            UnfilterRow(
                bytesPerPixel: bytesPerPixel,
                currentRow: currentRow,
                filterType: filterType,
                previousRow: previousRow
            );

            for (var pixelIndex = 0; (pixelIndex < width); pixelIndex++) {
                var sourcePixelOffset = (pixelIndex * bytesPerPixel);
                var destinationPixelOffset = (((rowIndex * width) + pixelIndex) * 4);

                switch (colorType) {
                    case 6:
                        rgbaPixels[destinationPixelOffset] = currentRow[sourcePixelOffset];
                        rgbaPixels[(destinationPixelOffset + 1)] = currentRow[(sourcePixelOffset + 1)];
                        rgbaPixels[(destinationPixelOffset + 2)] = currentRow[(sourcePixelOffset + 2)];
                        rgbaPixels[(destinationPixelOffset + 3)] = currentRow[(sourcePixelOffset + 3)];
                        break;
                    case 2:
                        rgbaPixels[destinationPixelOffset] = currentRow[sourcePixelOffset];
                        rgbaPixels[(destinationPixelOffset + 1)] = currentRow[(sourcePixelOffset + 1)];
                        rgbaPixels[(destinationPixelOffset + 2)] = currentRow[(sourcePixelOffset + 2)];
                        rgbaPixels[(destinationPixelOffset + 3)] = ((
                            (currentRow[sourcePixelOffset] == transparentRed) &&
                            (currentRow[(sourcePixelOffset + 1)] == transparentGreen) &&
                            (currentRow[(sourcePixelOffset + 2)] == transparentBlue)
                        )
                            ? byte.MinValue
                            : byte.MaxValue
                        );
                        break;
                    case 4:
                        rgbaPixels[destinationPixelOffset] = currentRow[sourcePixelOffset];
                        rgbaPixels[(destinationPixelOffset + 1)] = currentRow[sourcePixelOffset];
                        rgbaPixels[(destinationPixelOffset + 2)] = currentRow[sourcePixelOffset];
                        rgbaPixels[(destinationPixelOffset + 3)] = currentRow[(sourcePixelOffset + 1)];
                        break;
                    case 0:
                        rgbaPixels[destinationPixelOffset] = currentRow[sourcePixelOffset];
                        rgbaPixels[(destinationPixelOffset + 1)] = currentRow[sourcePixelOffset];
                        rgbaPixels[(destinationPixelOffset + 2)] = currentRow[sourcePixelOffset];
                        rgbaPixels[(destinationPixelOffset + 3)] = ((currentRow[sourcePixelOffset] == transparentGray)
                            ? byte.MinValue
                            : byte.MaxValue
                        );
                        break;
                }
            }

            Array.Copy(
                destinationArray: previousRow,
                length: stride,
                sourceArray: currentRow
            );
        }

        return new PngImage(
            Height: height,
            RgbaPixels: rgbaPixels,
            Width: width
        );
    }
    private static int PaethPredictor(int left, int up, int upperLeft) {
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
    private static (int Width, int Height, byte BitDepth, byte ColorType, byte InterlaceMethod) ParseHeaderChunk(ReadOnlySpan<byte> chunkData) {
        if (chunkData.Length != 13) {
            throw new InvalidDataException(message: "PNG IHDR chunk length must be 13.");
        }

        if (chunkData[10] != 0) {
            throw new InvalidDataException(message: $"Unsupported PNG compression method '{chunkData[10]}'.");
        }

        if (chunkData[11] != 0) {
            throw new InvalidDataException(message: $"Unsupported PNG filter method '{chunkData[11]}'.");
        }

        return (
            Width: checked((int)BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[..4])),
            Height: checked((int)BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[4..8])),
            BitDepth: chunkData[8],
            ColorType: chunkData[9],
            InterlaceMethod: chunkData[12]
        );
    }
    private static bool TryReadChunk(ReadOnlySpan<byte> pngBytes, ref int offset, out string chunkType, out ReadOnlySpan<byte> chunkData) {
        chunkType = string.Empty;
        chunkData = default;

        if ((offset + 12) > pngBytes.Length) {
            return false;
        }

        var chunkLengthValue = BinaryPrimitives.ReadUInt32BigEndian(source: pngBytes[offset..(offset + 4)]);

        if (chunkLengthValue > int.MaxValue) {
            throw new InvalidDataException(message: "PNG chunk length exceeded the supported size.");
        }

        var chunkLength = ((int)chunkLengthValue);

        offset += 4;

        var chunkTypeBytes = pngBytes[offset..(offset + 4)];

        chunkType = Encoding.ASCII.GetString(bytes: chunkTypeBytes);
        offset += 4;

        if (chunkLength > ((pngBytes.Length - offset) - 4)) {
            throw new InvalidDataException(message: "PNG chunk length exceeded the file size.");
        }

        chunkData = pngBytes[offset..(offset + chunkLength)];
        offset += chunkLength;

        var storedCrc = pngBytes[offset..(offset + 4)];

        offset += 4;
        ValidateChunkCrc(
            chunkData: chunkData,
            chunkType: chunkTypeBytes,
            storedCrc: storedCrc
        );
        return true;
    }
    private static void UnfilterRow(byte filterType, Span<byte> currentRow, ReadOnlySpan<byte> previousRow, int bytesPerPixel) {
        switch (filterType) {
            case 0:
                return;
            case 1:
                for (var index = bytesPerPixel; (index < currentRow.Length); index++) {
                    currentRow[index] = unchecked((byte)(currentRow[index] + currentRow[(index - bytesPerPixel)]));
                }

                return;
            case 2:
                for (var index = 0; (index < currentRow.Length); index++) {
                    currentRow[index] = unchecked((byte)(currentRow[index] + previousRow[index]));
                }

                return;
            case 3:
                for (var index = 0; (index < currentRow.Length); index++) {
                    var left = ((index >= bytesPerPixel)
                        ? currentRow[(index - bytesPerPixel)]
                        : 0
                    );
                    var up = previousRow[index];

                    currentRow[index] = unchecked((byte)(currentRow[index] + ((left + up) / 2)));
                }

                return;
            case 4:
                for (var index = 0; (index < currentRow.Length); index++) {
                    var left = ((index >= bytesPerPixel)
                        ? currentRow[(index - bytesPerPixel)]
                        : 0
                    );
                    var up = previousRow[index];
                    var upperLeft = ((index >= bytesPerPixel)
                        ? previousRow[(index - bytesPerPixel)]
                        : 0
                    );

                    currentRow[index] = unchecked((byte)(currentRow[index] + PaethPredictor(
                        left: left,
                        up: up,
                        upperLeft: upperLeft
                    )));
                }

                return;
            default:
                throw new InvalidDataException(message: $"Unsupported PNG filter type '{filterType}'.");
        }
    }
    private static void ValidateChunkCrc(ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> chunkData, ReadOnlySpan<byte> storedCrc) {
        var crc = new Crc32();

        crc.Append(source: chunkType);
        crc.Append(source: chunkData);

        if (BinaryPrimitives.ReadUInt32BigEndian(source: storedCrc) != crc.GetCurrentHashAsUInt32()) {
            throw new InvalidDataException(message: $"PNG chunk '{Encoding.ASCII.GetString(bytes: chunkType)}' failed its CRC check.");
        }
    }

    /// <summary>Decodes a PNG file's still image into tightly packed 8-bit RGBA pixels; for an APNG this is the default image.</summary>
    /// <param name="pngBytes">The complete PNG file bytes, signature included.</param>
    /// <returns>The decoded image.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a PNG this decoder supports, or a chunk is malformed.</exception>
    public static PngImage Decode(ReadOnlySpan<byte> pngBytes) {
        if (!HasSignature(bytes: pngBytes)) {
            throw new InvalidDataException(message: "PNG signature was missing or malformed.");
        }

        var offset = Signature.Length;
        var idatBytes = new MemoryStream();
        var header = default((int Width, int Height, byte BitDepth, byte ColorType, byte InterlaceMethod));
        var headerSeen = false;
        byte[]? transparency = null;

        while (TryReadChunk(
            chunkData: out var chunkData,
            chunkType: out var chunkType,
            offset: ref offset,
            pngBytes: pngBytes
        )) {
            if (
                !headerSeen &&
                (chunkType != "IHDR")
            ) {
                throw new InvalidDataException(message: "PNG file must begin with an IHDR chunk.");
            }

            switch (chunkType) {
                case "IHDR":
                    if (headerSeen) {
                        throw new InvalidDataException(message: "PNG file carried more than one IHDR chunk.");
                    }

                    headerSeen = true;
                    header = ParseHeaderChunk(chunkData: chunkData);
                    break;
                case "PLTE": // a suggested palette for truecolor; palette-indexed images are refused by color type
                    break;
                case "tRNS":
                    transparency = chunkData.ToArray();
                    break;
                case "IDAT":
                    idatBytes.Write(buffer: chunkData);
                    break;
                case "IEND":
                    return DecodeImageData(
                        width: header.Width,
                        height: header.Height,
                        bitDepth: header.BitDepth,
                        colorType: header.ColorType,
                        interlaceMethod: header.InterlaceMethod,
                        idatBytes: idatBytes.ToArray(),
                        transparency: transparency
                    );
                default:
                    if ((chunkType[0] & 0x20) == 0) {
                        throw new InvalidDataException(message: $"Unknown critical PNG chunk '{chunkType}'.");
                    }

                    break;
            }
        }

        throw new InvalidDataException(message: "PNG file did not contain a valid IEND chunk.");
    }
    /// <summary>Decodes a PNG file's frames into tightly packed 8-bit RGBA pixels; a non-animated PNG decodes as one zero-delay frame.</summary>
    /// <param name="pngBytes">The complete PNG file bytes, signature included.</param>
    /// <returns>The decoded animation.</returns>
    /// <exception cref="InvalidDataException">The bytes are not a PNG this decoder supports, a chunk is malformed, or the animation uses sub-rectangle or 'over'-blended frames.</exception>
    public static PngAnimation DecodeAnimation(ReadOnlySpan<byte> pngBytes) {
        if (!HasSignature(bytes: pngBytes)) {
            throw new InvalidDataException(message: "PNG signature was missing or malformed.");
        }

        var offset = Signature.Length;
        var idatBytes = new MemoryStream();
        var idatSeen = false;
        var header = default((int Width, int Height, byte BitDepth, byte ColorType, byte InterlaceMethod));
        var headerSeen = false;
        byte[]? transparency = null;
        var declaredFrameCount = -1;
        var playCount = 0u;
        var expectedSequenceNumber = 0u;
        var frames = new List<FrameAccumulator>();
        var sawIend = false;

        while (TryReadChunk(
            chunkData: out var chunkData,
            chunkType: out var chunkType,
            offset: ref offset,
            pngBytes: pngBytes
        )) {
            if (
                !headerSeen &&
                (chunkType != "IHDR")
            ) {
                throw new InvalidDataException(message: "PNG file must begin with an IHDR chunk.");
            }

            switch (chunkType) {
                case "IHDR":
                    if (headerSeen) {
                        throw new InvalidDataException(message: "PNG file carried more than one IHDR chunk.");
                    }

                    headerSeen = true;
                    header = ParseHeaderChunk(chunkData: chunkData);
                    break;
                case "PLTE": // a suggested palette for truecolor; palette-indexed images are refused by color type
                    break;
                case "tRNS":
                    transparency = chunkData.ToArray();
                    break;
                case "acTL":
                    if (chunkData.Length < 8) {
                        throw new InvalidDataException(message: "APNG acTL chunk was truncated.");
                    }

                    declaredFrameCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[..4]));
                    playCount = BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[4..8]);
                    break;
                case "fcTL": {
                        if (chunkData.Length < 26) {
                            throw new InvalidDataException(message: "APNG fcTL chunk was truncated.");
                        }

                        if (BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[..4]) != expectedSequenceNumber++) {
                            throw new InvalidDataException(message: "APNG fcTL/fdAT sequence numbers were not consecutive from zero.");
                        }

                        var frameWidth = BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[4..8]);
                        var frameHeight = BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[8..12]);
                        var xOffset = BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[12..16]);
                        var yOffset = BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[16..20]);

                        if (
                            (frameWidth != ((uint)header.Width)) ||
                            (frameHeight != ((uint)header.Height)) ||
                            (xOffset != 0) ||
                            (yOffset != 0)
                        ) {
                            throw new InvalidDataException(message: "APNG sub-rectangle frames are not supported.");
                        }

                        // blend_op 'over' on the first frame is read as 'source' per the APNG specification.
                        if (
                            (chunkData[25] != 0) &&
                            (frames.Count > 0)
                        ) {
                            throw new InvalidDataException(message: "APNG 'over'-blended frames are not supported.");
                        }

                        frames.Add(item: new FrameAccumulator {
                            DelayDenominator = BinaryPrimitives.ReadUInt16BigEndian(source: chunkData[22..24]),
                            DelayNumerator = BinaryPrimitives.ReadUInt16BigEndian(source: chunkData[20..22]),
                            UsesIdat = (!idatSeen && (frames.Count == 0)),
                        });
                        break;
                    }
                case "IDAT":
                    idatBytes.Write(buffer: chunkData);
                    idatSeen = true;
                    break;
                case "fdAT": {
                        if (chunkData.Length < 4) {
                            throw new InvalidDataException(message: "APNG fdAT chunk was truncated.");
                        }

                        if (BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[..4]) != expectedSequenceNumber++) {
                            throw new InvalidDataException(message: "APNG fcTL/fdAT sequence numbers were not consecutive from zero.");
                        }

                        if (
                            (frames.Count == 0) ||
                            frames[^1].UsesIdat
                        ) {
                            throw new InvalidDataException(message: "APNG fdAT chunk had no preceding fcTL to belong to.");
                        }

                        frames[^1].Data.Write(buffer: chunkData[4..]);
                        break;
                    }
                case "IEND":
                    sawIend = true;
                    break;
                default:
                    if ((chunkType[0] & 0x20) == 0) {
                        throw new InvalidDataException(message: $"Unknown critical PNG chunk '{chunkType}'.");
                    }

                    break;
            }

            if (sawIend) {
                break;
            }
        }

        if (!sawIend) {
            throw new InvalidDataException(message: "PNG file did not contain a valid IEND chunk.");
        }

        if (declaredFrameCount < 0) {
            if (frames.Count > 0) {
                throw new InvalidDataException(message: "APNG fcTL chunk appeared without an acTL chunk.");
            }

            var still = DecodeImageData(
                width: header.Width,
                height: header.Height,
                bitDepth: header.BitDepth,
                colorType: header.ColorType,
                interlaceMethod: header.InterlaceMethod,
                idatBytes: idatBytes.ToArray(),
                transparency: transparency
            );

            return new PngAnimation(
                Frames: [new PngAnimationFrame(
                        DelayDenominator: 0,
                        DelayNumerator: 0,
                        RgbaPixels: still.RgbaPixels
                    )],
                Height: header.Height,
                PlayCount: 0,
                Width: header.Width
            );
        }

        if (
            (frames.Count == 0) ||
            (frames.Count != declaredFrameCount)
        ) {
            throw new InvalidDataException(message: $"APNG acTL declared {declaredFrameCount} frames but the file carried {frames.Count}.");
        }

        var decodedFrames = new PngAnimationFrame[frames.Count];

        for (var frameIndex = 0; (frameIndex < frames.Count); frameIndex++) {
            var frame = frames[frameIndex];
            var image = DecodeImageData(
                width: header.Width,
                height: header.Height,
                bitDepth: header.BitDepth,
                colorType: header.ColorType,
                interlaceMethod: header.InterlaceMethod,
                idatBytes: (frame.UsesIdat
                ? idatBytes.ToArray()
                : frame.Data.ToArray()),
                transparency: transparency
            );

            decodedFrames[frameIndex] = new PngAnimationFrame(
                DelayDenominator: frame.DelayDenominator,
                DelayNumerator: frame.DelayNumerator,
                RgbaPixels: image.RgbaPixels
            );
        }

        return new PngAnimation(
            Frames: decodedFrames,
            Height: header.Height,
            PlayCount: playCount,
            Width: header.Width
        );
    }
    /// <summary>Determines whether the bytes begin with the PNG file signature.</summary>
    /// <param name="bytes">The candidate file bytes.</param>
    /// <returns><see langword="true"/> when the PNG signature is present.</returns>
    public static bool HasSignature(ReadOnlySpan<byte> bytes) =>
        ((bytes.Length >= Signature.Length) && bytes[..Signature.Length].SequenceEqual(other: Signature));
}
