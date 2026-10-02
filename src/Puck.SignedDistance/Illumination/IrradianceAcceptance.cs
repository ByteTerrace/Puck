namespace Puck.SignedDistance.Illumination;

/// <summary>
/// The rule a probe ray's march accepts a surface by: only where the clamped field distance falls to an absolute
/// <see cref="SurfaceEpsilon"/>, so an accepted point lies within that distance of a surface whatever the ray has
/// travelled. A ray grazing past a surface keeps marching; one whose step budget ends first is unresolved, never a hit.
/// The GPU's float march uses the same absolute threshold in its clamped units; the CPU march accepts at its own
/// <c>HitEpsilon</c>, which is this value.
/// </summary>
public static class IrradianceAcceptance {
    /// <summary>The distance, in world units, every ray accepts a surface within.</summary>
    public const double SurfaceEpsilon = 0.001;

    /// <summary>Returns whether a ray accepts a surface at a sample.</summary>
    /// <param name="clampedDistance">The clamped field distance at the sample, in world units.</param>
    /// <returns><see langword="true"/> when the sample is a hit.</returns>
    public static bool Accepts(double clampedDistance) => (clampedDistance <= SurfaceEpsilon);
}
