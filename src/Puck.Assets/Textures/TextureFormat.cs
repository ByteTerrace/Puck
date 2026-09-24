namespace Puck.Assets.Textures;

/// <summary>The layout of a texture level's bytes. The uncompressed formats are tightly packed texels, rows top to bottom;
/// the block-compressed formats are 4x4 texel blocks, rows of blocks top to bottom, a partial block at a right or bottom
/// edge padded to a whole one.</summary>
public enum TextureFormat : byte {
    /// <summary>One unsigned-normalized 8-bit channel per texel.</summary>
    R8Unorm = 0,
    /// <summary>Two unsigned-normalized 8-bit channels per texel, red first.</summary>
    Rg8Unorm = 1,
    /// <summary>Four unsigned-normalized 8-bit channels per texel, red first.</summary>
    Rgba8Unorm = 2,
    /// <summary>Four IEEE half-precision channels per texel, red first, each little-endian.</summary>
    Rgba16Float = 3,
    /// <summary>BC4: one unsigned-normalized channel, eight bytes a block (<see cref="Bc4Codec"/>).</summary>
    Bc4Unorm = 4,
    /// <summary>BC5: two unsigned-normalized channels, sixteen bytes a block, each channel a BC4 block
    /// (<see cref="Bc5Codec"/>).</summary>
    Bc5Unorm = 5,
    /// <summary>BC6H: three unsigned half-precision channels, sixteen bytes a block (<see cref="Bc6hCodec"/>).</summary>
    Bc6hUfloat = 6,
    /// <summary>BC7: four unsigned-normalized 8-bit channels, sixteen bytes a block (<see cref="Bc7Codec"/>).</summary>
    Bc7Unorm = 7,
}
/// <summary>How a texture's values map to light.</summary>
public enum TextureColorSpace : byte {
    /// <summary>The values are linear: data, or linear light.</summary>
    Linear = 0,
    /// <summary>The color channels are sRGB-encoded; alpha is linear.</summary>
    Srgb = 1,
}
/// <summary>The sizes and extents of <see cref="TextureFormat"/> levels.</summary>
public static class TextureFormats {
    /// <summary>The texels along each side of one compressed block.</summary>
    public const int BlockTexels = 4;

    /// <summary>Returns whether <paramref name="format"/> stores 4x4 blocks rather than texels.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for a block-compressed format.</returns>
    public static bool IsBlockCompressed(TextureFormat format) =>
        (format >= TextureFormat.Bc4Unorm);
    /// <summary>Returns the bytes one unit of <paramref name="format"/> takes: a texel of an uncompressed format, a 4x4
    /// block of a compressed one.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The bytes per texel or per block.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a declared format.</exception>
    public static int BytesPerUnit(TextureFormat format) => format switch {
        TextureFormat.R8Unorm => 1,
        TextureFormat.Rg8Unorm => 2,
        TextureFormat.Rgba8Unorm => 4,
        TextureFormat.Rgba16Float => 8,
        TextureFormat.Bc4Unorm => 8,
        TextureFormat.Bc5Unorm or TextureFormat.Bc6hUfloat or TextureFormat.Bc7Unorm => 16,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The texture format is not a declared format.",
            paramName: nameof(format)
        ),
    };
    /// <summary>Returns the uncompressed format a block-compressed format encodes and decodes: <see cref="TextureFormat.R8Unorm"/>
    /// for BC4, <see cref="TextureFormat.Rg8Unorm"/> for BC5, <see cref="TextureFormat.Rgba16Float"/> for BC6H (whose alpha
    /// decodes to one) and <see cref="TextureFormat.Rgba8Unorm"/> for BC7. An uncompressed format is its own.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The uncompressed format.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a declared format.</exception>
    public static TextureFormat SourceOf(TextureFormat format) => format switch {
        TextureFormat.R8Unorm or TextureFormat.Rg8Unorm or TextureFormat.Rgba8Unorm or TextureFormat.Rgba16Float => format,
        TextureFormat.Bc4Unorm => TextureFormat.R8Unorm,
        TextureFormat.Bc5Unorm => TextureFormat.Rg8Unorm,
        TextureFormat.Bc6hUfloat => TextureFormat.Rgba16Float,
        TextureFormat.Bc7Unorm => TextureFormat.Rgba8Unorm,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: format,
            message: "The texture format is not a declared format.",
            paramName: nameof(format)
        ),
    };
    /// <summary>Returns the extent of mip level <paramref name="level"/> of a texture whose first level is
    /// <paramref name="width"/> by <paramref name="height"/>: each side halved per level, rounded down, and never below
    /// one.</summary>
    /// <param name="width">The first level's width, in texels.</param>
    /// <param name="height">The first level's height, in texels.</param>
    /// <param name="level">The level, zero for the first.</param>
    /// <returns>The level's width and height, in texels.</returns>
    public static (int Width, int Height) LevelExtent(int width, int height, int level) =>
        (Math.Max(val1: 1, val2: (width >> level)), Math.Max(val1: 1, val2: (height >> level)));
    /// <summary>Returns the bytes a <paramref name="width"/> by <paramref name="height"/> level of
    /// <paramref name="format"/> takes.</summary>
    /// <param name="format">The format.</param>
    /// <param name="width">The level's width, in texels.</param>
    /// <param name="height">The level's height, in texels.</param>
    /// <returns>The level's bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a declared format.</exception>
    public static long LevelBytes(TextureFormat format, int width, int height) => (IsBlockCompressed(format: format)
        ? ((((long)BlocksAlong(texels: width)) * BlocksAlong(texels: height)) * BytesPerUnit(format: format))
        : ((((long)width) * height) * BytesPerUnit(format: format)));
    /// <summary>Returns the blocks along a side of <paramref name="texels"/> texels, a partial block counting as one.</summary>
    /// <param name="texels">The side, in texels.</param>
    /// <returns>The blocks.</returns>
    public static int BlocksAlong(int texels) =>
        ((texels + (BlockTexels - 1)) / BlockTexels);
}
