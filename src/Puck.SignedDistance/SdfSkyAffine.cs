using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>A resolved sky layer or field run's per-channel affine map: output = Scale × input + Offset.
/// Blend parameters and masks are resolved by the caller; this value introduces no opacity policy.</summary>
/// <param name="Scale">The multiplier of the colour beneath the layer or run.</param>
/// <param name="Offset">The colour added after multiplication.</param>
public readonly record struct SdfSkyAffine(Vector3 Scale, Vector3 Offset) {
    /// <summary>The map that leaves the colour beneath it unchanged.</summary>
    public static SdfSkyAffine Identity => new(Vector3.One, Vector3.Zero);

    /// <summary>Applies this map to the colour beneath it.</summary>
    /// <param name="beneath">The colour already composed from lower layers.</param>
    /// <returns>The colour including this layer or run.</returns>
    public Vector3 Apply(Vector3 beneath) => Scale * beneath + Offset;

    /// <summary>Composes another map above this one, preserving authored order.</summary>
    /// <param name="above">The next layer or run.</param>
    /// <returns>A map equivalent to applying this map and then <paramref name="above"/>.</returns>
    public SdfSkyAffine Then(in SdfSkyAffine above) => new(above.Scale * Scale, above.Scale * Offset + above.Offset);
}
