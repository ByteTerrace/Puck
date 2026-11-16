
namespace Puck.World.Authoring;

/// <summary>Bounds the inverse image of the authored profile, shear, bump, warp and elongation sequence.</summary>
public static class ShapeWarpReach {
    /// <summary>Expands a local radius in reverse point-evaluation order: elongation, warp and bumps (each a bounded
    /// move of the point), shear, then profile.</summary>
    /// <param name="radius">The primitive's own reach, creation units.</param>
    /// <param name="flare">The shape's flare, or <see langword="null"/>.</param>
    /// <param name="shear">The shape's shear, or <see langword="null"/>.</param>
    /// <param name="pointReach">The farthest the shape's bumps, warp and elongation together carry a point
    /// (<see cref="PointReach"/>).</param>
    /// <returns>The reach of the warped shape.</returns>
    public static float Expand(float radius, ShapeFlareDocument? flare, ShapeShearDocument? shear, float pointReach) {
        radius += pointReach;
        radius += (shear?.ReachExtra(reach: radius) ?? 0f);
        return (radius * ShapeFlareDocument.ReachFactor(flare: flare));
    }
    /// <summary>Returns the farthest a shape's bumps, warp and elongation together carry a point: the pushes' lengths,
    /// the warp's reach and the elongation's half-diagonal, summed.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>The point reach, creation units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    public static float PointReach(ShapeDocument shape) {
        ArgumentNullException.ThrowIfNull(shape);

        return ((ShapeBumpDocument.ReachExtra(bumps: shape.Bumps) + (shape.Warp?.Reach ?? 0f)) + (shape.Elongate?.Value.Length() ?? 0f));
    }
}
