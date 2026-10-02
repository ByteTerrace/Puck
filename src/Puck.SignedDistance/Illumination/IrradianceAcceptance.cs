namespace Puck.SignedDistance.Illumination;

/// <summary>
/// The rule a probe ray's march accepts a surface by on the GPU: where the clamped field distance falls to
/// <c>max(SurfaceEpsilon, AngularEpsilon × t)</c>. The angular term is a pixel's size at the floor tier's render scale,
/// so an accepted point is never farther than <c>AngularEpsilon × t</c> from its surface, and a ray grazing past a
/// surface is not stopped on it. The CPU model's rays accept at the CPU march's own threshold, which is tighter.
/// </summary>
public static class IrradianceAcceptance {
    /// <summary>The distance, in world units, every ray accepts a surface within.</summary>
    public const double SurfaceEpsilon = 0.001;
    /// <summary>The accept threshold's growth with distance, in world units per world unit travelled.</summary>
    public const double AngularEpsilon = 0.004;

    /// <summary>Returns whether a ray accepts a surface at a sample.</summary>
    /// <param name="clampedDistance">The clamped field distance at the sample, in world units.</param>
    /// <param name="travelled">The distance, in world units, the ray has travelled.</param>
    /// <returns><see langword="true"/> when the sample is a hit.</returns>
    public static bool Accepts(double clampedDistance, double travelled) =>
        (clampedDistance <= Math.Max(val1: SurfaceEpsilon, val2: (AngularEpsilon * travelled)));
}
