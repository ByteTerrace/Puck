using Puck.Abstractions.Gpu;
using Puck.Assets.Textures;

namespace Puck.SignedDistance.Baking;

/// <summary>What a baked texture holds, which also decides how it is filtered into mips and stored
/// (<see cref="SdfBakedTexture.PlanFor"/>).</summary>
public enum SdfBakeTextureUsage : byte {
    /// <summary>The surface's base color, with coverage in alpha where the texture has any.</summary>
    Albedo = 0,
    /// <summary>The unit outward surface normal in the prototype's frame, as an octahedral pair
    /// (<see cref="OctahedralNormal"/>).</summary>
    Normal = 1,
    /// <summary>The ambient occlusion factor, one for an open surface and zero for a fully enclosed one.</summary>
    Occlusion = 2,
    /// <summary>The program material id of the winning shape. An identity, not a quantity: it is never blended or
    /// compressed lossily.</summary>
    Material = 3,
    /// <summary>The light the surface emits, in linear light: the material's linear albedo times its emissive
    /// strength, which may exceed one.</summary>
    Emission = 4,
    /// <summary>An impostor view's hit depth: 0 at the bounding sphere's near side, 1 at its far side and where the
    /// view's ray missed.</summary>
    Depth = 5,
}
/// <summary>How one usage of baked texture is baked, filtered and stored.</summary>
/// <param name="Source">The uncompressed format the baker writes the first level in and the mips are filtered in.</param>
/// <param name="Stored">The format the levels are stored in: a block-compressed format, or the source format itself
/// for data that must not change.</param>
/// <param name="ColorSpace">How the values map to light.</param>
/// <param name="Filter">How each mip level is filtered from the one above it.</param>
public readonly record struct SdfBakeTexturePlan(GpuPixelFormat Source, GpuPixelFormat Stored, TextureColorSpace ColorSpace, TextureMipFilter Filter);
/// <summary>
/// One baked texture: a tile-aware mip chain (<see cref="TextureMipChain"/>) whose first level is
/// <see cref="Width"/> by <see cref="Height"/> texels, each level stored in <see cref="Format"/>. The atlas is square
/// tiles of <see cref="TileTexels"/> on a side (a surface quad's tile, or an impostor's view), and the chain ends at
/// one texel per tile, so a renderer sampling level <c>l</c> clamps each lookup inside its tile's
/// <c>TileTexels &gt;&gt; l</c> texels.
/// </summary>
/// <param name="Usage">What the texture holds.</param>
/// <param name="Format">The format every level is stored in.</param>
/// <param name="ColorSpace">How the values map to light.</param>
/// <param name="Width">The first level's width, in texels.</param>
/// <param name="Height">The first level's height, in texels.</param>
/// <param name="TileTexels">The side of one tile, in texels of the first level.</param>
/// <param name="Levels">The levels' bytes, the first level first.</param>
public sealed record SdfBakedTexture(SdfBakeTextureUsage Usage, GpuPixelFormat Format, TextureColorSpace ColorSpace, int Width, int Height, int TileTexels, IReadOnlyList<byte[]> Levels) {
    /// <summary>Returns how textures of <paramref name="usage"/> are baked and stored. Color is BC7 in sRGB, normals are
    /// BC5 octahedral pairs, occlusion and depth are BC4, emission is BC6H, and material identity stays uncompressed
    /// with majority mips, since a lossy or blended identity names a material nothing authored.</summary>
    /// <param name="usage">The usage.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="usage"/> is not a declared usage.</exception>
    public static SdfBakeTexturePlan PlanFor(SdfBakeTextureUsage usage) => usage switch {
        SdfBakeTextureUsage.Albedo => new(ColorSpace: TextureColorSpace.Srgb, Filter: TextureMipFilter.Srgb, Source: GpuPixelFormat.R8G8B8A8Unorm, Stored: GpuPixelFormat.Bc7Unorm),
        SdfBakeTextureUsage.Normal => new(ColorSpace: TextureColorSpace.Linear, Filter: TextureMipFilter.OctahedralNormal, Source: GpuPixelFormat.R8G8Unorm, Stored: GpuPixelFormat.Bc5Unorm),
        SdfBakeTextureUsage.Occlusion => new(ColorSpace: TextureColorSpace.Linear, Filter: TextureMipFilter.Average, Source: GpuPixelFormat.R8Unorm, Stored: GpuPixelFormat.Bc4Unorm),
        SdfBakeTextureUsage.Material => new(ColorSpace: TextureColorSpace.Linear, Filter: TextureMipFilter.Majority, Source: GpuPixelFormat.R8Unorm, Stored: GpuPixelFormat.R8Unorm),
        SdfBakeTextureUsage.Emission => new(ColorSpace: TextureColorSpace.Linear, Filter: TextureMipFilter.Half, Source: GpuPixelFormat.R16G16B16A16Float, Stored: GpuPixelFormat.Bc6hUfloat),
        SdfBakeTextureUsage.Depth => new(ColorSpace: TextureColorSpace.Linear, Filter: TextureMipFilter.Average, Source: GpuPixelFormat.R8Unorm, Stored: GpuPixelFormat.Bc4Unorm),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: usage,
            message: "The texture usage is not a declared usage.",
            paramName: nameof(usage)
        ),
    };
    /// <summary>Stores a first level as a texture: builds its tile-aware mip chain with the usage's filter and
    /// compresses every level to the usage's stored format.</summary>
    /// <param name="usage">What the texture holds.</param>
    /// <param name="level0">The first level's texels in the usage's source format, rows top to bottom.</param>
    /// <param name="width">The first level's width, in texels; a whole number of tiles.</param>
    /// <param name="height">The first level's height, in texels; a whole number of tiles.</param>
    /// <param name="tileTexels">The side of one tile, in texels; a power of two.</param>
    /// <param name="coverage">Per-texel weights for every level but the last, or <see langword="null"/> for a texture
    /// covered everywhere.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="ArgumentException">The level is not whole tiles of the usage's source format, or
    /// <paramref name="coverage"/> lacks a level.</exception>
    public static SdfBakedTexture Store(SdfBakeTextureUsage usage, byte[] level0, int width, int height, int tileTexels, IReadOnlyList<byte[]>? coverage = null) =>
        Compress(chain: Chain(coverage: coverage, height: height, level0: level0, tileTexels: tileTexels, usage: usage, width: width), height: height, tileTexels: tileTexels, usage: usage, width: width);
    /// <summary>Builds the uncompressed mip chain <see cref="Store"/> compresses.</summary>
    /// <param name="usage">What the texture holds.</param>
    /// <param name="level0">The first level's texels in the usage's source format, rows top to bottom.</param>
    /// <param name="width">The first level's width, in texels; a whole number of tiles.</param>
    /// <param name="height">The first level's height, in texels; a whole number of tiles.</param>
    /// <param name="tileTexels">The side of one tile, in texels; a power of two.</param>
    /// <param name="coverage">Per-texel weights for every level but the last, or <see langword="null"/>.</param>
    /// <returns>The levels in the usage's source format.</returns>
    /// <exception cref="ArgumentException">The level is not whole tiles of the usage's source format, or
    /// <paramref name="coverage"/> lacks a level.</exception>
    public static byte[][] Chain(SdfBakeTextureUsage usage, byte[] level0, int width, int height, int tileTexels, IReadOnlyList<byte[]>? coverage = null) {
        var plan = PlanFor(usage: usage);

        return TextureMipChain.Build(
            coverage: coverage,
            filter: plan.Filter,
            format: plan.Source,
            height: height,
            level0: level0,
            tileTexels: tileTexels,
            width: width
        );
    }
    /// <summary>Compresses an uncompressed chain (<see cref="Chain"/>) to the usage's stored format.</summary>
    /// <param name="usage">What the texture holds.</param>
    /// <param name="chain">The levels in the usage's source format.</param>
    /// <param name="width">The first level's width, in texels.</param>
    /// <param name="height">The first level's height, in texels.</param>
    /// <param name="tileTexels">The side of one tile, in texels.</param>
    /// <returns>The texture.</returns>
    public static SdfBakedTexture Compress(SdfBakeTextureUsage usage, IReadOnlyList<byte[]> chain, int width, int height, int tileTexels) {
        ArgumentNullException.ThrowIfNull(argument: chain);

        var plan = PlanFor(usage: usage);
        var levels = new byte[chain.Count][];

        for (var level = 0; (level < levels.Length); level++) {
            var (levelWidth, levelHeight) = LevelExtentOf(height: height, level: level, width: width);

            levels[level] = (GpuPixelFormats.IsBlockCompressed(format: plan.Stored)
                ? TextureCompression.Encode(format: plan.Stored, height: levelHeight, texels: chain[level], width: levelWidth)
                : chain[level]);
        }

        return new SdfBakedTexture(
            ColorSpace: plan.ColorSpace,
            Format: plan.Stored,
            Height: height,
            Levels: levels,
            TileTexels: tileTexels,
            Usage: usage,
            Width: width
        );
    }
    /// <summary>Returns the extent of level <paramref name="level"/>.</summary>
    /// <param name="level">The level, zero for the first.</param>
    /// <returns>The level's width and height, in texels.</returns>
    public (int Width, int Height) LevelExtent(int level) =>
        LevelExtentOf(height: Height, level: level, width: Width);
    /// <summary>Decodes level <paramref name="level"/> to the usage's uncompressed source format.</summary>
    /// <param name="level">The level, zero for the first.</param>
    /// <returns>The level's texels, rows top to bottom.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a level of the texture.</exception>
    public byte[] Decode(int level) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: level);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: Levels.Count, value: level);

        var (width, height) = LevelExtent(level: level);

        return (GpuPixelFormats.IsBlockCompressed(format: Format)
            ? TextureCompression.Decode(blocks: Levels[level], format: Format, height: height, width: width)
            : Levels[level].ToArray());
    }

    private static (int Width, int Height) LevelExtentOf(int width, int height, int level) {
        var (levelWidth, levelHeight) = GpuPixelFormats.LevelExtent(height: ((uint)height), level: ((uint)level), width: ((uint)width));

        return (((int)levelWidth), ((int)levelHeight));
    }
}
