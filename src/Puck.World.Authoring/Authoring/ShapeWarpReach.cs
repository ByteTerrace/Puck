namespace Puck.World.Authoring;

/// <summary>Bounds the inverse image of the authored profile, shear, and bump sequence.</summary>
public static class ShapeWarpReach {
    /// <summary>Expands a local radius in reverse point-evaluation order: bumps, shear, then profile.</summary>
    public static float Expand(float radius, ShapeFlareDocument? flare, ShapeShearDocument? shear, float bumpReach) {
        radius += bumpReach;
        radius += shear?.ReachExtra(radius) ?? 0f;
        return radius * ShapeFlareDocument.ReachFactor(flare);
    }
}
