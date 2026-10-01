using System.Numerics;
using Puck.SignedDistance.Baking;

namespace Puck.SdfVm;

/// <summary>
/// A baked prototype's octahedral impostor as the mesh pass draws it: <see cref="Views"/> by <see cref="Views"/>
/// orthographic views of its bounding sphere (<see cref="SdfBakedImpostor"/>), each one tile of five textures that share
/// the mesh atlases' packing (<see cref="SdfMeshAtlas"/>): albedo (sRGB-encoded BC7, coverage in alpha), the octahedral
/// normal pair (BC5), the hit's depth across the sphere (BC4), each texel's material (R8, never blended) and emitted light
/// (BC6H). A draw carrying one rasterizes a
/// card in place of its mesh (<see cref="SdfMeshDraw.Impostor"/>), and the mesh pass and the hit passes find the surface
/// the card shows by marching the camera's ray through the three nearest views' depth.
/// </summary>
public sealed record SdfMeshImpostor : SdfTextureSet {
    /// <summary>The usages, in the order the impostor atlases hold them and a view binds them.</summary>
    public static IReadOnlyList<SdfBakeTextureUsage> Usages { get; } = [
        SdfBakeTextureUsage.Albedo,
        SdfBakeTextureUsage.Normal,
        SdfBakeTextureUsage.Depth,
        SdfBakeTextureUsage.Material,
        SdfBakeTextureUsage.Emission,
    ];

    /// <summary>Takes a bake's impostor, refusing a sphere or view grid that cannot be drawn.</summary>
    /// <param name="impostor">The baked impostor.</param>
    /// <exception cref="ArgumentNullException"><paramref name="impostor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The radius is not positive and finite, the center is not finite, the view grid
    /// is not at least two views of a power-of-two number of texels of at least four, the textures are not the grid's
    /// extent, or the textures are not one of each usage stored as the bake plans it.</exception>
    public SdfMeshImpostor(SdfBakedImpostor impostor) : base(
        owner: "An impostor's",
        textures: TexturesOf(impostor: impostor),
        tileTexels: impostor.ViewTexels,
        usages: Usages
    ) {
        if (
            !float.IsFinite(f: impostor.Radius) || (impostor.Radius <= 0f) ||
            !float.IsFinite(f: impostor.Center.X) || !float.IsFinite(f: impostor.Center.Y) || !float.IsFinite(f: impostor.Center.Z)
        ) {
            throw new ArgumentException(
                message: "An impostor's sphere needs a finite center and a positive finite radius.",
                paramName: nameof(impostor)
            );
        }
        if (
            (impostor.Views < 2) ||
            (impostor.ViewTexels < 4) ||
            !System.Numerics.BitOperations.IsPow2(value: impostor.ViewTexels) ||
            (Width != (impostor.Views * impostor.ViewTexels)) ||
            (Height != Width)
        ) {
            throw new ArgumentException(
                message: $"An impostor's textures are a square grid of at least two views of a power-of-two number of at least four texels; {impostor.Views} views of {impostor.ViewTexels} texels over {Width}x{Height} are not.",
                paramName: nameof(impostor)
            );
        }

        Center = impostor.Center;
        Radius = impostor.Radius;
        Views = impostor.Views;
        ViewTexels = impostor.ViewTexels;
    }

    // The impostor's five textures, which the base orders by usage.
    private static IReadOnlyList<SdfBakedTexture> TexturesOf(SdfBakedImpostor impostor) {
        ArgumentNullException.ThrowIfNull(argument: impostor);

        return [impostor.Albedo, impostor.Normal, impostor.Depth, impostor.Material, impostor.Emission];
    }

    /// <summary>Gets the bounding sphere's center, in the prototype's engine frame.</summary>
    public Vector3 Center { get; }
    /// <summary>Gets the bounding sphere's radius, in world units of the prototype's frame.</summary>
    public float Radius { get; }
    /// <summary>Gets the views along each side of the view grid.</summary>
    public int Views { get; }
    /// <summary>Gets the texels along each side of one view.</summary>
    public int ViewTexels { get; }
}
