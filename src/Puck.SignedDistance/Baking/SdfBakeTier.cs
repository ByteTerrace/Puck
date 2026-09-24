namespace Puck.SignedDistance.Baking;

/// <summary>The quality tier a bake is made at. A tier is part of a bake's key, so the same prototype baked at two tiers
/// is two bakes.</summary>
public enum SdfBakeQuality : byte {
    /// <summary>A coarse bake: few cells, a small impostor. The laws bake at this tier.</summary>
    Preview = 0,
    /// <summary>The tier a compiled world ships and a device bakes a missing prototype at.</summary>
    Standard = 1,
}
/// <summary>The resolution one quality tier bakes at.</summary>
/// <param name="Quality">The tier.</param>
/// <param name="Cells">The mesh grid's cells along each axis of the bounding cube, padding included.</param>
/// <param name="ImpostorViews">The octahedral impostor's views along each side of its view grid.</param>
/// <param name="ImpostorTexels">The texels along each side of one impostor view.</param>
public readonly record struct SdfBakeTier(SdfBakeQuality Quality, int Cells, int ImpostorViews, int ImpostorTexels) {
    /// <summary>The texels along each side of one mesh quad's tile in the surface textures. Four makes each quad one
    /// 4x4 block of a block-compressed format, so compressing the atlas never mixes two quads in one block, and each
    /// halving of a mip chain keeps tile edges on texel edges down to one texel per quad.</summary>
    public const int TileTexels = 4;
    /// <summary>The empty cells kept between the prototype's reach and each face of the bounding cube, so the extracted
    /// surface is closed.</summary>
    public const int PaddingCells = 1;

    /// <summary>Returns the resolution of <paramref name="quality"/>.</summary>
    /// <param name="quality">The tier.</param>
    /// <returns>The tier's resolution.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quality"/> is not a declared tier.</exception>
    public static SdfBakeTier For(SdfBakeQuality quality) => quality switch {
        SdfBakeQuality.Preview => new SdfBakeTier(
            Cells: 12,
            ImpostorTexels: 8,
            ImpostorViews: 4,
            Quality: quality
        ),
        SdfBakeQuality.Standard => new SdfBakeTier(
            Cells: 32,
            ImpostorTexels: 16,
            ImpostorViews: 8,
            Quality: quality
        ),
        _ => throw new ArgumentOutOfRangeException(
            actualValue: quality,
            message: "The bake quality is not a declared tier.",
            paramName: nameof(quality)
        ),
    };
}
