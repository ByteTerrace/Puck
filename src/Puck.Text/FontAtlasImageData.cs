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
/// <param name="rgbaPixels">
/// The tightly packed RGBA pixel buffer. Must be non-empty and exactly
/// <c><paramref name="width"/> * <paramref name="height"/> * 4</c> bytes long.
/// </param>
/// <param name="height">The image height in pixels. Must be greater than zero.</param>
/// <param name="width">The image width in pixels. Must be greater than zero.</param>
/// <param name="contentHash">
/// An optional precomputed content hash of <paramref name="rgbaPixels"/>. When omitted, a hash is
/// computed from the pixel buffer.
/// </param>
/// <exception cref="ArgumentException"><paramref name="rgbaPixels"/> is empty.</exception>
/// <exception cref="ArgumentOutOfRangeException">
/// <paramref name="height"/> or <paramref name="width"/> is not greater than zero.
/// </exception>
public sealed class FontAtlasImageData(byte[] rgbaPixels, int height, int width, AssetContentHash? contentHash = null) {
    /// <summary>Gets the image height in pixels.</summary>
    public int Height { get; } = ((height > 0)
        ? height
        : throw new ArgumentOutOfRangeException(
            message: "Font atlas image height must be greater than zero.",
            paramName: nameof(height)
        ));
    /// <summary>Gets the content hash of <see cref="RgbaPixels"/>, supplied by the caller or computed from the buffer.</summary>
    public AssetContentHash ContentHash { get; } = (contentHash ?? AssetContentHash.Compute(content: rgbaPixels));
    /// <summary>Gets the tightly packed, row-major, top-down RGBA pixel buffer.</summary>
    public byte[] RgbaPixels { get; } = ((rgbaPixels?.Length > 0)
        ? rgbaPixels
        : throw new ArgumentException(
            message: "Font atlas image pixels must be provided.",
            paramName: nameof(rgbaPixels)
        ));
    /// <summary>Gets the image width in pixels.</summary>
    public int Width { get; } = ((width > 0)
        ? width
        : throw new ArgumentOutOfRangeException(
            message: "Font atlas image width must be greater than zero.",
            paramName: nameof(width)
        ));
}
