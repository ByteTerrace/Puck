using Puck.Abstractions.Gpu;
using Puck.SignedDistance.Baking;

namespace Puck.SdfVm;

/// <summary>
/// A baked mesh's five surface textures, which its vertices' texture coordinates address: albedo (sRGB-encoded BC7),
/// the octahedral normal pair (BC5), occlusion (BC4), each texel's palette entry (R8, never blended) and emitted light
/// (BC6H). All five share one extent and one tile-aware mip chain (<see cref="SdfBakedTexture"/>), so one placement in
/// the mesh atlases (<see cref="SdfMeshAtlas"/>) serves them all.
/// </summary>
public sealed record SdfMeshTextures {
    /// <summary>The usages, in the order the atlases hold them and a view binds them.</summary>
    public static IReadOnlyList<SdfBakeTextureUsage> Usages { get; } = [
        SdfBakeTextureUsage.Albedo,
        SdfBakeTextureUsage.Normal,
        SdfBakeTextureUsage.Occlusion,
        SdfBakeTextureUsage.Material,
        SdfBakeTextureUsage.Emission,
    ];

    /// <summary>Takes a bake's surface textures, refusing any set that is not one texture of each usage in
    /// <see cref="Usages"/>, stored as the bake plans it, all of one extent, tile and level count.</summary>
    /// <param name="textures">The bake's surface textures, in any order.</param>
    /// <exception cref="ArgumentException">A usage is missing or repeated, a texture is not stored in its usage's planned
    /// format or color space, the extents or level counts differ, the extent is not positive whole four-texel tiles,
    /// or a mip chain is empty, extends past one texel per tile, or holds a level of the wrong byte length.</exception>
    public SdfMeshTextures(IReadOnlyList<SdfBakedTexture> textures) {
        ArgumentNullException.ThrowIfNull(argument: textures);

        var ordered = new SdfBakedTexture[Usages.Count];

        foreach (var texture in textures) {
            ArgumentNullException.ThrowIfNull(argument: texture, paramName: nameof(textures));

            var index = IndexOf(usage: texture.Usage);

            if ((index < 0) || (ordered[index] is not null)) {
                throw new ArgumentException(
                    message: $"A mesh's surface textures hold one of each of {string.Join(separator: ", ", values: Usages)}; '{texture.Usage}' is {((index < 0) ? "not one of them" : "repeated")}.",
                    paramName: nameof(textures)
                );
            }
            var plan = SdfBakedTexture.PlanFor(usage: texture.Usage);

            if ((texture.Format != plan.Stored) || (texture.ColorSpace != plan.ColorSpace)) {
                throw new ArgumentException(
                    message: $"A mesh's {texture.Usage} texture is stored as {plan.Stored} in {plan.ColorSpace}; this one is {texture.Format} in {texture.ColorSpace}.",
                    paramName: nameof(textures)
                );
            }

            ordered[index] = texture;
        }

        var first = ordered[0];

        for (var index = 0; (index < ordered.Length); index++) {
            var texture = (ordered[index] ?? throw new ArgumentException(
                message: $"A mesh's surface textures lack its {Usages[index]} texture.",
                paramName: nameof(textures)
            ));

            if (
                (texture.Width <= 0) || (texture.Height <= 0) ||
                (texture.TileTexels != SdfBakeTier.TileTexels) ||
                ((texture.Width % SdfBakeTier.TileTexels) != 0) ||
                ((texture.Height % SdfBakeTier.TileTexels) != 0) ||
                (texture.Levels is null) || (texture.Levels.Count is < 1 or > 3)
            ) {
                throw new ArgumentException(
                    message: $"A mesh's surface textures require positive whole {SdfBakeTier.TileTexels}-texel tiles and one to three levels, ending no later than one texel per tile.",
                    paramName: nameof(textures)
                );
            }
            if ((texture.Width != first.Width) || (texture.Height != first.Height) || (texture.TileTexels != first.TileTexels) || (texture.Levels.Count != first.Levels.Count)) {
                throw new ArgumentException(
                    message: $"A mesh's surface textures share one extent, tile and level count; its {texture.Usage} texture is {texture.Width}x{texture.Height} in {texture.TileTexels}-texel tiles over {texture.Levels.Count} levels, its {first.Usage} texture {first.Width}x{first.Height} in {first.TileTexels}-texel tiles over {first.Levels.Count} levels.",
                    paramName: nameof(textures)
                );
            }
            for (var level = 0; (level < texture.Levels.Count); level++) {
                var (width, height) = texture.LevelExtent(level: level);
                var required = GpuPixelFormats.LevelByteLength(format: texture.Format, height: (uint)height, width: (uint)width);

                if ((texture.Levels[level] is not { } bytes) || ((ulong)bytes.LongLength != required)) {
                    throw new ArgumentException(
                        message: $"A mesh's {texture.Usage} level {level} requires exactly {required} bytes.",
                        paramName: nameof(textures)
                    );
                }
            }
        }

        Textures = ordered;
    }

    /// <summary>Gets the extent's height in texels.</summary>
    public int Height => Textures[0].Height;
    /// <summary>Gets the chain's level count.</summary>
    public int Levels => Textures[0].Levels.Count;
    /// <summary>Gets the textures in <see cref="Usages"/> order.</summary>
    public IReadOnlyList<SdfBakedTexture> Textures { get; }
    /// <summary>Gets the tile edge in texels at level 0.</summary>
    public int TileTexels => Textures[0].TileTexels;
    /// <summary>Gets the extent's width in texels.</summary>
    public int Width => Textures[0].Width;

    /// <summary>Returns the pixel format the atlas of a usage is stored in: its planned stored format.</summary>
    /// <param name="usage">The usage.</param>
    /// <returns>The format.</returns>
    public static GpuPixelFormat FormatOf(SdfBakeTextureUsage usage) =>
        SdfBakedTexture.PlanFor(usage: usage).Stored;

    private static int IndexOf(SdfBakeTextureUsage usage) {
        for (var index = 0; (index < Usages.Count); index++) {
            if (Usages[index] == usage) {
                return index;
            }
        }

        return -1;
    }
}
