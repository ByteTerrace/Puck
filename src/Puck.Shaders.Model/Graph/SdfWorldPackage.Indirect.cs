namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The pass-block value holding the enabled indirect source-category bits (<c>SdfIndirectSources</c>) the mesh
    /// path's views pass reads beside the irradiance volume (<c>uint</c>).</summary>
    public const string IndirectSources = "indirectSources";
    /// <summary>The pass-block value holding the origin gains for direct lights, material emission, screen emission and
    /// sky exits (<c>float4</c>); views reads the sky's.</summary>
    public const string IndirectSourceGains = "indirectSourceGains";
    /// <summary>The pass-block value holding the receiver-only tint RGB and intensity (<c>float4</c>).</summary>
    public const string IndirectApply = "indirectApply";
    /// <summary>The pass-block value holding the receiver-only strength of existing ambient-occlusion attenuation
    /// (<c>float</c>).</summary>
    public const string IndirectContact = "indirectContact";
}
