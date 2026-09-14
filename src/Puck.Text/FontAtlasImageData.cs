using Puck.Assets;
using System.Numerics;

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
    private readonly Lazy<Vector2> m_alphaGradientBound;
    private readonly byte[] m_rgbaPixels;

    /// <summary>Gets upper bounds on the horizontal and vertical derivatives of bilinearly reconstructed alpha,
    /// in encoded units per texel. Includes quantization and image-wide cell transitions; edge clamping adds no slope.</summary>
    public Vector2 AlphaGradientBound => m_alphaGradientBound.Value;
    /// <summary>Gets the content hash computed from the owned pixel buffer.</summary>
    public AssetContentHash ContentHash { get; }
    /// <summary>Gets the image height in pixels.</summary>
    public int Height { get; }
    /// <summary>Gets the tightly packed, row-major, top-down RGBA pixel buffer.</summary>
    public ReadOnlySpan<byte> RgbaPixels => m_rgbaPixels;
    /// <summary>Gets the image width in pixels.</summary>
    public int Width { get; }

    /// <summary>Initializes a new <see cref="FontAtlasImageData"/> from a tightly packed RGBA pixel buffer.</summary>
    /// <param name="rgbaPixels">The tightly packed RGBA pixel buffer, copied into privately owned storage. Must contain exactly <paramref name="width"/> × <paramref name="height"/> × 4 bytes.</param>
    /// <param name="height">The image height in pixels. Must be greater than zero.</param>
    /// <param name="width">The image width in pixels. Must be greater than zero.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rgbaPixels"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rgbaPixels"/> does not contain exactly <paramref name="width"/> × <paramref name="height"/> × 4 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="height"/> or <paramref name="width"/> is not greater than zero.</exception>
    public FontAtlasImageData(byte[] rgbaPixels, int height, int width) : this(
        rgbaPixels,
        height,
        width,
        false
    ) { }

    // Only for fresh buffers whose producer relinquishes all mutable references after this call.
    internal static FontAtlasImageData TakeOwnership(byte[] rgbaPixels, int height, int width) => new(
        height: height,
        rgbaPixels: rgbaPixels,
        takeOwnership: true,
        width: width
    );

    private Vector2 ComputeAlphaGradientBound() {
        var horizontal = 0;
        var vertical = 0;

        for (var y = 0; (y < Height); y++) {
            for (var x = 0; (x < Width); x++) {
                var offset = ((((y * Width) + x) * 4) + 3);
                var alpha = m_rgbaPixels[offset];

                if ((x + 1) < Width) { horizontal = Math.Max(
                    val1: horizontal,
                    val2: Math.Abs(value: (alpha - m_rgbaPixels[(offset + 4)]))
                ); }
                if ((y + 1) < Height) { vertical = Math.Max(
                    val1: vertical,
                    val2: Math.Abs(value: (alpha - m_rgbaPixels[(offset + (Width * 4))]))
                ); }
            }
        }
        // A bilinear partial derivative is a convex combination of the two parallel edge differences.
        return new(
            x: ((horizontal == 0)
            ? 0
            : MathF.BitIncrement(x: (horizontal / 255f))),
            y: ((vertical == 0)
            ? 0
            : MathF.BitIncrement(x: (vertical / 255f)))
        );
    }

    private FontAtlasImageData(byte[] rgbaPixels, int height, int width, bool takeOwnership) {
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

        var expectedLength = ((((ulong)((uint)width)) * ((uint)height)) * 4u);

        if (((ulong)rgbaPixels.LongLength) != expectedLength) {
            throw new ArgumentException(
                message: $"Font atlas image pixels must contain exactly {expectedLength} bytes for a {width}x{height} RGBA image.",
                paramName: nameof(rgbaPixels)
            );
        }

        Height = height;
        m_rgbaPixels = (takeOwnership
            ? rgbaPixels
            : (byte[])rgbaPixels.Clone()
        );
        ContentHash = AssetContentHash.Compute(content: m_rgbaPixels);
        Width = width;
        m_alphaGradientBound = new Lazy<Vector2>(valueFactory: ComputeAlphaGradientBound);
    }
}
