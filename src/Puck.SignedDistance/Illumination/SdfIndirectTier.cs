namespace Puck.SignedDistance;

/// <summary>The indirect-light tier a frame asks for: it sizes the baked-mesh path's irradiance volume. The volume
/// measures at every tier, since its probes are what occlude the traced lights and the reflected sky; Off applies none of
/// its light, and views add the authored fill in its place.</summary>
public enum SdfIndirectTier {
    /// <summary>No indirect light: the coarse volume occludes, and none of its light is applied.</summary>
    Off,
    /// <summary>The coarse volume.</summary>
    Medium,
    /// <summary>The fine volume.</summary>
    High,
}
