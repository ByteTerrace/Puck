using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Puck.Assets;

namespace Puck.Text;

/// <summary>
/// The self-contained <see cref="IFontAtlasImageDataLoader"/>: a minimal PNG decoder covering the subset
/// the font-atlas bake pipeline (<c>tools/font-atlas</c>) emits — 8-bit, non-interlaced, color types 0
/// (grayscale), 2 (RGB), 4 (grayscale + alpha), and 6 (RGBA), with all five scanline filters.
/// </summary>
/// <remarks>
/// This deliberately does not depend on a general-purpose image library: the atlas pipeline only ever
/// needs to read back exactly what that bake pipeline writes, so a small, dependency-free decoder is
/// both sufficient and easy to audit.
/// </remarks>
public sealed class FontAtlasImageDataLoader : IFontAtlasImageDataLoader {
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <inheritdoc/>
    public FontAtlasImageData Load(FontAtlas atlas) {
        ArgumentNullException.ThrowIfNull(atlas);

        if (atlas.ImageData is { } imageData) {
            ValidateDimensions(
                imageData: imageData,
                expectedWidth: atlas.Width,
                expectedHeight: atlas.Height,
                imageIdentifier: atlas.ImagePath
            );
            return imageData;
        }

        var pngBytes = File.ReadAllBytes(path: atlas.ImagePath);
        var image = Load(
            imageIdentifier: atlas.ImagePath,
            pngBytes: pngBytes
        );

        ValidateDimensions(
            imageData: image,
            expectedWidth: atlas.Width,
            expectedHeight: atlas.Height,
            imageIdentifier: atlas.ImagePath
        );
        return image;
    }

    /// <inheritdoc/>
    public FontAtlasImageData Load(string imageIdentifier, ReadOnlyMemory<byte> pngBytes) {
        if (string.IsNullOrWhiteSpace(value: imageIdentifier)) {
            throw new ArgumentException(
                message: "Font atlas image identifier must be provided.",
                paramName: nameof(imageIdentifier)
            );
        }

        if (
            (pngBytes.Length < PngSignature.Length) ||
            !pngBytes.Span[..PngSignature.Length].SequenceEqual(other: PngSignature)
        ) {
            throw new InvalidDataException(message: $"Font atlas image '{imageIdentifier}' is not a valid PNG.");
        }

        var parseResult = ParsePng(pngBytes: pngBytes.Span);

        return new FontAtlasImageData(
            rgbaPixels: parseResult.RgbaPixels,
            height: parseResult.Height,
            width: parseResult.Width,
            contentHash: AssetContentHash.Compute(content: pngBytes.Span)
        );
    }

    private static void ValidateDimensions(FontAtlasImageData imageData, int expectedWidth, int expectedHeight, string imageIdentifier) {
        if ((imageData.Width != expectedWidth) || (imageData.Height != expectedHeight)) {
            throw new InvalidDataException(
                message: $"Font atlas image dimensions {imageData.Width}x{imageData.Height} did not match metadata dimensions {expectedWidth}x{expectedHeight} for '{imageIdentifier}'.");
        }
    }
    private static ParsedPngImage ParsePng(ReadOnlySpan<byte> pngBytes) {
        var offset = PngSignature.Length;
        var idatBytes = new MemoryStream();
        var width = 0;
        var height = 0;
        var bitDepth = (byte)0;
        var colorType = (byte)0;
        var interlaceMethod = (byte)0;

        while ((offset + 12) <= pngBytes.Length) {
            var chunkLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source: pngBytes[offset..(offset + 4)]));

            offset += 4;

            var chunkType = Encoding.ASCII.GetString(bytes: pngBytes[offset..(offset + 4)]);

            offset += 4;

            if (((offset + chunkLength) + 4) > pngBytes.Length) {
                throw new InvalidDataException(message: "PNG chunk length exceeded the file size.");
            }

            var chunkData = pngBytes[offset..(offset + chunkLength)];

            offset += chunkLength;
            offset += 4; // CRC

            switch (chunkType) {
                case "IHDR":
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[..4]));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source: chunkData[4..8]));
                    bitDepth = chunkData[8];
                    colorType = chunkData[9];
                    interlaceMethod = chunkData[12];
                    break;
                case "IDAT":
                    idatBytes.Write(buffer: chunkData);
                    break;
                case "IEND":
                    return DecodeImageData(
                        width: width,
                        height: height,
                        bitDepth: bitDepth,
                        colorType: colorType,
                        interlaceMethod: interlaceMethod,
                        idatBytes: idatBytes.ToArray()
                    );
            }
        }

        throw new InvalidDataException(message: "PNG file did not contain a valid IEND chunk.");
    }
    private static ParsedPngImage DecodeImageData(int width, int height, byte bitDepth, byte colorType, byte interlaceMethod, byte[] idatBytes) {
        if ((width <= 0) || (height <= 0)) {
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

        using var idatStream = new MemoryStream(buffer: idatBytes, writable: false);
        using var zlibStream = new ZLibStream(mode: CompressionMode.Decompress, stream: idatStream);
        using var decodedStream = new MemoryStream();

        zlibStream.CopyTo(destination: decodedStream);

        var decodedBytes = decodedStream.ToArray();
        var stride = checked((width * bytesPerPixel));
        var expectedLength = checked(((stride + 1) * height));

        if (decodedBytes.Length != expectedLength) {
            throw new InvalidDataException(message: "Decoded PNG scanline length did not match the expected image size.");
        }

        var rgbaPixels = new byte[checked(((width * height) * 4))];
        var previousRow = new byte[stride];
        var currentRow = new byte[stride];
        var sourceOffset = 0;

        for (var rowIndex = 0; (rowIndex < height); rowIndex++) {
            var filterType = decodedBytes[sourceOffset++];

            Array.Copy(
                sourceArray: decodedBytes,
                sourceIndex: sourceOffset,
                destinationArray: currentRow,
                destinationIndex: 0,
                length: stride
            );
            sourceOffset += stride;
            UnfilterRow(
                filterType: filterType,
                currentRow: currentRow,
                previousRow: previousRow,
                bytesPerPixel: bytesPerPixel
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
                        rgbaPixels[(destinationPixelOffset + 3)] = byte.MaxValue;
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
                        rgbaPixels[(destinationPixelOffset + 3)] = byte.MaxValue;
                        break;
                }
            }

            Array.Copy(
                sourceArray: currentRow,
                destinationArray: previousRow,
                length: stride
            );
        }

        return new ParsedPngImage(
            RgbaPixels: rgbaPixels,
            Width: width,
            Height: height
        );
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
                    var left = ((index >= bytesPerPixel) ? currentRow[(index - bytesPerPixel)] : 0);
                    var up = previousRow[index];

                    currentRow[index] = unchecked((byte)(currentRow[index] + ((left + up) / 2)));
                }

                return;
            case 4:
                for (var index = 0; (index < currentRow.Length); index++) {
                    var left = ((index >= bytesPerPixel) ? currentRow[(index - bytesPerPixel)] : 0);
                    var up = previousRow[index];
                    var upperLeft = ((index >= bytesPerPixel) ? previousRow[(index - bytesPerPixel)] : 0);

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
    private static int PaethPredictor(int left, int up, int upperLeft) {
        var predictor = ((left + up) - upperLeft);
        var leftDistance = Math.Abs(value: (predictor - left));
        var upDistance = Math.Abs(value: (predictor - up));
        var upperLeftDistance = Math.Abs(value: (predictor - upperLeft));

        if ((leftDistance <= upDistance) && (leftDistance <= upperLeftDistance)) {
            return left;
        }

        return ((upDistance <= upperLeftDistance) ? up : upperLeft);
    }

    private readonly record struct ParsedPngImage(byte[] RgbaPixels, int Width, int Height);
}
