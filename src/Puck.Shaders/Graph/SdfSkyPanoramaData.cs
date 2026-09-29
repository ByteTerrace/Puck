using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>Resolved parameters for one equirectangular image sky layer.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyPanoramaData {
    /// <summary>The linear color multiplier.</summary>
    public Vector3 Tint;
    /// <summary>The linear emission multiplier.</summary>
    public float Intensity;
    /// <summary>The wrapper-owned image source index; the kind receives the resolved texture.</summary>
    public uint SourceIndex;
    /// <summary>The existing nearest or linear GPU sampler-filter ordinal.</summary>
    public uint Filter;
    /// <summary>Zeroed trailing alignment.</summary>
    public uint Reserved0;
    /// <summary>Zeroed trailing alignment.</summary>
    public uint Reserved1;
}
