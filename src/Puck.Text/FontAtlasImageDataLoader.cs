using Puck.Assets;

namespace Puck.Text;

/// <summary>
/// The self-contained <see cref="IFontAtlasImageDataLoader"/>: decodes an atlas PNG through
/// <see cref="PngDecoder"/> (whose supported subset covers what <see cref="FontAtlasArtifactWriter"/> emits) and
/// validates the decoded dimensions against the atlas metadata.
/// </summary>
public sealed class FontAtlasImageDataLoader : IFontAtlasImageDataLoader {
    private static void ValidateDimensions(FontAtlasImageData imageData, int expectedWidth, int expectedHeight, string imageIdentifier) {
        if (
            (imageData.Width != expectedWidth) ||
            (imageData.Height != expectedHeight)
        ) {
            throw new InvalidDataException(message: $"Font atlas image dimensions {imageData.Width}x{imageData.Height} did not match metadata dimensions {expectedWidth}x{expectedHeight} for '{imageIdentifier}'.");
        }
    }

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

        if (!PngDecoder.HasSignature(bytes: pngBytes.Span)) {
            throw new InvalidDataException(message: $"Font atlas image '{imageIdentifier}' is not a valid PNG.");
        }

        var image = PngDecoder.Decode(pngBytes: pngBytes.Span);

        return new FontAtlasImageData(
            rgbaPixels: image.RgbaPixels,
            height: image.Height,
            width: image.Width,
            contentHash: AssetContentHash.Compute(content: pngBytes.Span)
        );
    }
}
