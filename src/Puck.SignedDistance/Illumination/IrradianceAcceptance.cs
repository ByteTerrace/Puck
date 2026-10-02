namespace Puck.SignedDistance.Illumination;

/// <summary>
/// The rule a probe ray's march accepts a surface by: only where a surface-distance upper bound falls to an absolute
/// <see cref="SurfaceEpsilon"/>, so an accepted point lies within that distance of a surface whatever the ray has
/// travelled. A ray grazing past a surface keeps marching; one whose step budget ends first is unresolved, never a hit.
/// A small clamped field value is a lower bound and cannot certify acceptance. The CPU illumination field brackets
/// a zero within the threshold before accepting the shared march's candidate; an unproven candidate marches on or
/// ends unresolved. The GPU needs the same upper-bound certificate.
/// </summary>
public static class IrradianceAcceptance {
    /// <summary>The distance, in world units, every ray accepts a surface within.</summary>
    public const double SurfaceEpsilon = 0.001;

    /// <summary>Returns whether a ray accepts a surface at a sample.</summary>
    /// <param name="surfaceDistanceUpperBound">A certified upper bound on the distance to a surface, in world units.</param>
    /// <returns><see langword="true"/> when the sample is a hit.</returns>
    public static bool Accepts(double surfaceDistanceUpperBound) =>
        ((surfaceDistanceUpperBound >= 0.0) && (surfaceDistanceUpperBound <= SurfaceEpsilon));
}
