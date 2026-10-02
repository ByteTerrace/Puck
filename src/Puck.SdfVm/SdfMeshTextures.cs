using Puck.SignedDistance.Baking;

namespace Puck.SdfVm;

/// <summary>
/// A baked mesh's five surface textures, which its vertices' texture coordinates address: albedo (sRGB-encoded BC7),
/// the octahedral normal pair (BC5), occlusion (BC4), each texel's palette entry (R8, never blended) and emitted light
/// (BC6H). All five share one extent and one tile-aware mip chain (<see cref="SdfBakedTexture"/>), so one placement in
/// the mesh atlases (<see cref="SdfMeshAtlas"/>) serves them all.
/// </summary>
public sealed record SdfMeshTextures : SdfTextureSet {
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
    public SdfMeshTextures(IReadOnlyList<SdfBakedTexture> textures) : base(
        owner: "A mesh's surface",
        textures: textures,
        tileTexels: SdfBakeTier.TileTexels,
        usages: Usages
    ) {
    }
}
