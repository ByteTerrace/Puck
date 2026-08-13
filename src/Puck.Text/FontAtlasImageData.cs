using Puck.Assets;

namespace Puck.Text;

/// <summary>
/// The decoded RGBA pixels of a generated atlas image together with their dimensions and a content hash.
/// </summary>
/// <remarks>
/// Pixels are stored as tightly packed 32-bit RGBA, row-major and top-down, so the byte length is always
/// <c><see cref="Width"/> * <see cref="Height"/> * 4</c>. This type lets a <see cref="FontAtlas"/> carry
/// its rasterized image in memory — for example for upload to a GPU texture — instead of referencing it
/// only by <see cref="FontAtlas.ImagePath"/>. The <see cref="ContentHash"/> enables content-addressed
/// identity and caching of the image.
/// </remarks>
public sealed class FontAtlasImageData {
    /// <summary>Gets the image height in pixels.</summary>
    public int Height { get; }
    /// <summary>Gets the content hash of <see cref="RgbaPixels"/>, supplied by the caller or computed from the buffer.</summary>
    public AssetContentHash ContentHash { get; }
    /// <summary>Gets the tightly packed, row-major, top-down RGBA pixel buffer.</summary>
    public byte[] RgbaPixels { get; }
    /// <summary>Gets the image width in pixels.</summary>
    public int Width { get; }

    /// <summary>Initializes a new <see cref="FontAtlasImageData"/> from a tightly packed RGBA pixel buffer.</summary>
    /// <param name="rgbaPixels">The tightly packed RGBA pixel buffer. Must contain exactly <paramref name="width"/> × <paramref name="height"/> × 4 bytes.</param>
    /// <param name="height">The image height in pixels. Must be greater than zero.</param>
    /// <param name="width">The image width in pixels. Must be greater than zero.</param>
    /// <param name="contentHash">An optional precomputed content hash of <paramref name="rgbaPixels"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rgbaPixels"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rgbaPixels"/> does not contain exactly <paramref name="width"/> × <paramref name="height"/> × 4 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="height"/> or <paramref name="width"/> is not greater than zero.</exception>
    public FontAtlasImageData(byte[] rgbaPixels, int height, int width, AssetContentHash? contentHash = null) {
        ArgumentNullException.ThrowIfNull(rgbaPixels);

        if (height <= 0) {
            throw new ArgumentOutOfRangeException(
                message: "Font atlas image height must be greater than zero.",
                paramName: nameof(height)
            );
        }

        if (width <= 0) {
            throw new ArgumentOutOfRangeException(
                message: "Font atlas image width must be greater than zero.",
                paramName: nameof(width)
            );
        }

        var expectedLength = (((ulong)(uint)width * (uint)height) * 4u);

        if ((ulong)rgbaPixels.LongLength != expectedLength) {
            throw new ArgumentException(
                message: $"Font atlas image pixels must contain exactly {expectedLength} bytes for a {width}x{height} RGBA image.",
                paramName: nameof(rgbaPixels)
            );
        }

        Height = height;
        ContentHash = (contentHash ?? AssetContentHash.Compute(content: rgbaPixels));
        RgbaPixels = rgbaPixels;
        Width = width;
    }
}
