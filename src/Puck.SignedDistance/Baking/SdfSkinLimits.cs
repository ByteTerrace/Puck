namespace Puck.SignedDistance;

/// <summary>The limits of a skinned mesh's representation, held where a document is checked against them before any
/// bake as well as where a mesh is sealed.</summary>
public static class SdfSkinLimits {
    /// <summary>The most members one mesh's vertices ride: a vertex names its member in a byte, and the byte's last value
    /// marks a vertex that rides its draw.</summary>
    public const int MaxMembers = byte.MaxValue;
}
