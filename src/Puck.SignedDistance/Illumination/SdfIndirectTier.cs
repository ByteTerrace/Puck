namespace Puck.SignedDistance;

/// <summary>The indirect-light tier a frame asks for: it sizes the baked-mesh path's irradiance volume. Off reads no
/// volume, and views light with the authored fill instead.</summary>
public enum SdfIndirectTier {
    /// <summary>No indirect light.</summary>
    Off,
    /// <summary>The coarse volume.</summary>
    Medium,
    /// <summary>The fine volume.</summary>
    High,
}
