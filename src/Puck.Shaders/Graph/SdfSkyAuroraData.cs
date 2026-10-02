using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>Resolved parameters for a stretched periodic aurora curtain.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyAuroraData {
    /// <summary>The linear straight curtain color.</summary>
    public Vector3 Color;
    /// <summary>The linear emission multiplier.</summary>
    public float Intensity;
    /// <summary>The resolved offset in periodic lattice cells.</summary>
    public Vector3 Offset;
    /// <summary>The admitted finite positive normal reciprocal of the authored lattice cell size.</summary>
    public float InverseScale;
    /// <summary>The admitted finite positive normal reciprocal of the ridge half-width.</summary>
    public float InverseWidth;
    /// <summary>The positive exponent shaping intrinsic ridge coverage.</summary>
    public float Sharpness;
    /// <summary>The nonnegative vertical noise-coordinate multiplier.</summary>
    public float HeightScale;
    /// <summary>The ridge center in the signed noise interval.</summary>
    public float Bias;
    /// <summary>The exact integer lattice seed.</summary>
    public uint Seed;
    /// <summary>The structural octave count, from one through eight.</summary>
    public uint Octaves;
    /// <summary>Zeroed trailing alignment.</summary>
    public uint Reserved0;
    /// <summary>Zeroed trailing alignment.</summary>
    public uint Reserved1;
}
