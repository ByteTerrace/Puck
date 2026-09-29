using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>Resolved parameters for a periodic three-dimensional noise sky layer.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyNoiseData {
    /// <summary>The linear color at a shaped value of zero.</summary>
    public Vector3 ColorLow;
    /// <summary>The linear emission multiplier.</summary>
    public float Intensity;
    /// <summary>The linear color at a shaped value of one.</summary>
    public Vector3 ColorHigh;
    /// <summary>The admitted finite positive normal reciprocal of the authored lattice cell size.</summary>
    public float InverseScale;
    /// <summary>The resolved offset in periodic lattice cells.</summary>
    public Vector3 Offset;
    /// <summary>The exact integer lattice seed.</summary>
    public uint Seed;
    /// <summary>The structural octave count, from one through eight.</summary>
    public uint Octaves;
    /// <summary>The multiplier applied to the signed normalized fractal sum before clamping.</summary>
    public float Contrast;
    /// <summary>The additive bias applied before clamping to the unit interval.</summary>
    public float Bias;
    /// <summary>Zeroed trailing alignment.</summary>
    public uint Reserved;
}
