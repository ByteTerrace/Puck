using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance.Baking;

namespace Puck.SdfVm;

/// <summary>
/// A set of baked textures the mesh atlases (<see cref="SdfMeshAtlas"/>) pack as one rectangle: one texture of each usage
/// its kind lists, stored as the baker plans that usage (<see cref="SdfBakedTexture.PlanFor"/>), all of one extent, tile
/// and level count, in whole tiles, with a chain that ends no later than one texel per tile. A kind of set states its
/// usages and its tile once: <see cref="SdfMeshTextures"/> for a mesh's surface, <see cref="SdfMeshImpostor"/> for an
/// impostor's views.
/// </summary>
public abstract record SdfTextureSet {
    /// <summary>Takes a set's textures, refusing any that is not one texture of each of <paramref name="usages"/>.</summary>
    /// <param name="usages">The usages the set holds, in the order the atlases hold them.</param>
    /// <param name="textures">The textures, in any order.</param>
    /// <param name="owner">What the set belongs to, for messages: a possessive noun phrase such as <c>A mesh's</c>.</param>
    /// <param name="tileTexels">The tile edge, in texels, every texture must have.</param>
    /// <exception cref="ArgumentException">A usage is missing or repeated, a texture is not stored in its usage's planned
    /// format or color space, the extents, tiles or level counts differ, the extent is not positive whole tiles, a tile
    /// is not <paramref name="tileTexels"/>, or a mip chain is empty, extends past one texel per tile, or holds a level of
    /// the wrong byte length.</exception>
    protected SdfTextureSet(IReadOnlyList<SdfBakeTextureUsage> usages, IReadOnlyList<SdfBakedTexture> textures, string owner, int tileTexels) {
        ArgumentNullException.ThrowIfNull(argument: textures);

        var ordered = new SdfBakedTexture[usages.Count];

        foreach (var texture in textures) {
            ArgumentNullException.ThrowIfNull(argument: texture, paramName: nameof(textures));

            var index = -1;

            for (var candidate = 0; (candidate < usages.Count); candidate++) {
                if (usages[candidate] == texture.Usage) {
                    index = candidate;
                }
            }

            if ((index < 0) || (ordered[index] is not null)) {
                throw new ArgumentException(
                    message: $"{owner} textures hold one of each of {string.Join(separator: ", ", values: usages)}; '{texture.Usage}' is {((index < 0) ? "not one of them" : "repeated")}.",
                    paramName: nameof(textures)
                );
            }

            var plan = SdfBakedTexture.PlanFor(usage: texture.Usage);

            if ((texture.Format != plan.Stored) || (texture.ColorSpace != plan.ColorSpace)) {
                throw new ArgumentException(
                    message: $"{owner} {texture.Usage} texture is stored as {plan.Stored} in {plan.ColorSpace}; this one is {texture.Format} in {texture.ColorSpace}.",
                    paramName: nameof(textures)
                );
            }

            ordered[index] = texture;
        }

        var first = ordered[0];
        var deepest = (BitOperations.Log2(value: ((uint)tileTexels)) + 1);

        for (var index = 0; (index < ordered.Length); index++) {
            var texture = (ordered[index] ?? throw new ArgumentException(
                message: $"{owner} textures lack its {usages[index]} texture.",
                paramName: nameof(textures)
            ));

            if (
                (texture.Width <= 0) || (texture.Height <= 0) ||
                (texture.TileTexels != tileTexels) ||
                ((texture.Width % tileTexels) != 0) || ((texture.Height % tileTexels) != 0) ||
                (texture.Levels is null) || (texture.Levels.Count < 1) || (texture.Levels.Count > deepest)
            ) {
                throw new ArgumentException(
                    message: $"{owner} textures require positive whole {tileTexels}-texel tiles and one to {deepest} levels, ending no later than one texel per tile.",
                    paramName: nameof(textures)
                );
            }
            if ((texture.Width != first.Width) || (texture.Height != first.Height) || (texture.TileTexels != first.TileTexels) || (texture.Levels.Count != first.Levels.Count)) {
                throw new ArgumentException(
                    message: $"{owner} textures share one extent, tile and level count; its {texture.Usage} texture is {texture.Width}x{texture.Height} in {texture.TileTexels}-texel tiles over {texture.Levels.Count} levels, its {first.Usage} texture {first.Width}x{first.Height} in {first.TileTexels}-texel tiles over {first.Levels.Count} levels.",
                    paramName: nameof(textures)
                );
            }
            for (var level = 0; (level < texture.Levels.Count); level++) {
                var (width, height) = texture.LevelExtent(level: level);
                var required = GpuPixelFormats.LevelByteLength(format: texture.Format, height: ((uint)height), width: ((uint)width));

                if ((texture.Levels[level] is not { } bytes) || (((ulong)bytes.LongLength) != required)) {
                    throw new ArgumentException(
                        message: $"{owner} {texture.Usage} level {level} requires exactly {required} bytes.",
                        paramName: nameof(textures)
                    );
                }
            }
        }

        Textures = ordered;
        UsageOrder = usages;
    }

    /// <summary>Gets the extent's height in texels.</summary>
    public int Height => Textures[0].Height;
    /// <summary>Gets the chain's level count.</summary>
    public int Levels => Textures[0].Levels.Count;
    /// <summary>Gets the textures in the order the set's kind lists its usages.</summary>
    public IReadOnlyList<SdfBakedTexture> Textures { get; }
    /// <summary>Gets the usages the set holds, in the order of <see cref="Textures"/> and the atlases.</summary>
    public IReadOnlyList<SdfBakeTextureUsage> UsageOrder { get; }
    /// <summary>Gets the tile edge in texels at level 0.</summary>
    public int TileTexels => Textures[0].TileTexels;
    /// <summary>Gets the extent's width in texels.</summary>
    public int Width => Textures[0].Width;

    /// <summary>Returns the pixel format the atlas of a usage is stored in: its planned stored format.</summary>
    /// <param name="usage">The usage.</param>
    /// <returns>The format.</returns>
    public static GpuPixelFormat FormatOf(SdfBakeTextureUsage usage) =>
        SdfBakedTexture.PlanFor(usage: usage).Stored;
}
