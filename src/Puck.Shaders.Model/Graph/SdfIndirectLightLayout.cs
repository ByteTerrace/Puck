namespace Puck.Shaders;

/// <summary>The bounded depth-map bank and metadata layout, shared by the host and generated shader declarations.</summary>
public static class SdfIndirectLightLayout {
    /// <summary>The finest and coarsest allocated receiver regions per held or incoming light.</summary>
    public const int RegionsPerLight = 2;
    /// <summary>The square depth map's edge in texels.</summary>
    public const int Resolution = 512;
    /// <summary>The maximum maps at the largest supported shadow-slot policy.</summary>
    public const int MaxMaps = 12;
    /// <summary>The float4 rows of projection, bounds, identity and generation metadata per map.</summary>
    public const int MetadataRows = 7;
    /// <summary>The bounded full-field evaluation allowance for a light-map texel or fallback shadow ray.</summary>
    public const int MarchSteps = 128;
    /// <summary>The bytes in one single-precision depth map.</summary>
    public const int MapBytes = ((Resolution * Resolution) * sizeof(float));
}
