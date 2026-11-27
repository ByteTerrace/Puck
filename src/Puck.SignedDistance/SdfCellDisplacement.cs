namespace Puck.SignedDistance;

/// <summary>The feature measure a cellular pattern reads.</summary>
public enum SdfCellMode : uint {
    /// <summary>The distance to the nearest feature.</summary>
    F1,
    /// <summary>The gap between the nearest two feature distances, zero on a border but not a distance to it: its slope
    /// across a border varies with the two features' spacing.</summary>
    F2MinusF1,
    /// <summary>The distance to the nearest border of the nearest feature's cell, the plane between that feature and the
    /// neighbour that bounds it nearest, so a band of it is one width all along every border.</summary>
    Border,
}
/// <summary>Cellular field relief with an exact fixed 27-cell neighborhood, and for the border distance the 125 cells
/// about the nearest feature's.</summary>
/// <param name="Frequency">Positive cells per local unit.</param>
/// <param name="Amplitude">Nonnegative displacement in field units.</param>
/// <param name="Seed">PCG3D lattice seed.</param>
/// <param name="Mode">The feature distance to evaluate.</param>
/// <param name="Randomness">Side length of the centered feature-position box within each unit cell.</param>
public readonly record struct SdfCellDisplacement(float Frequency, float Amplitude, uint Seed, SdfCellMode Mode, float Randomness) {
    /// <summary>A conservative F1 randomness ceiling: sqrt(3)*(0.5+j/2) &lt; 1.5-j/2.</summary>
    public const float MaxF1Randomness = 0.46f;
    /// <summary>A conservative F2 ceiling: sqrt((1+j/2)²+2*(0.5+j/2)²) &lt; 1.5-j/2.
    /// The containing cell and the closest face neighbor supply two points inside that radius;
    /// every cell outside the 27-cell neighborhood starts beyond the right-hand bound.</summary>
    public const float MaxF2Randomness = 0.2f;
    /// <summary>A conservative border ceiling: the nearest feature is exact under <see cref="MaxF1Randomness"/>, and every
    /// point of its cell lies within R = sqrt(3)*(0.5+j/2) of it, so a neighbour bounding the cell lies within 2R, under
    /// the 3-j that separates it from a feature three cells off: the 5x5x5 cells about the nearest feature's hold every
    /// neighbour that bounds its cell.</summary>
    public const float MaxBorderRandomness = 0.46f;

    /// <summary>The mode's exact-neighborhood admission ceiling.</summary>
    public static float MaxRandomness(SdfCellMode mode) => mode switch {
        SdfCellMode.F1 => MaxF1Randomness,
        SdfCellMode.F2MinusF1 => MaxF2Randomness,
        SdfCellMode.Border => MaxBorderRandomness,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(mode)),
    };
    /// <summary>Refuses nonfinite inputs, negative amplitude, unsupported modes, or excessive randomness.</summary>
    public void Validate() {
        if (
            !float.IsFinite(f: Frequency) ||
            (Frequency <= 0f)
        ) { throw new ArgumentOutOfRangeException(paramName: nameof(Frequency)); }
        if (
            !float.IsFinite(f: Amplitude) ||
            (Amplitude < 0f)
        ) { throw new ArgumentOutOfRangeException(paramName: nameof(Amplitude)); }
        var maximum = MaxRandomness(mode: Mode);

        if (
            !float.IsFinite(f: Randomness) ||
            (Randomness < 0f) ||
            (Randomness > maximum)
        ) { throw new ArgumentOutOfRangeException(paramName: nameof(Randomness)); }
        if (!float.IsFinite(f: StepFactor)) {
            throw new ArgumentOutOfRangeException(
            nameof(Amplitude),
            "The cellular derivative bound must be finite."
        );
        }
    }

    /// <summary>The relief slope a creation's cells stay under (<c>CreationCanonicalizer</c>). Where the relief's slope
    /// reaches the surface's own, the field's gradient vanishes: the surface pinches, folds over itself and sheds islands
    /// no bake contours cleanly, and its ridges turn by more than a right angle.</summary>
    public const float MaxReliefSlope = 1f;

    /// <summary>The greatest outward relief: both distance functions are nonnegative.</summary>
    public float OutwardReach => (0.5f * Amplitude);
    /// <summary>The bound on the relief's own slope, amplitude times frequency times the feature distance's: one for F1
    /// and for the border distance, two for F2 - F1.</summary>
    public float ReliefSlope => ((Amplitude * Frequency) * ((Mode == SdfCellMode.F2MinusF1)
        ? 2f
        : 1f));
    /// <summary>The local derivative bound including an initially unit-Lipschitz field.</summary>
    public float StepFactor => (1f + ReliefSlope);
}
