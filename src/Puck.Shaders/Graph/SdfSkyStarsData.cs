using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>Resolved parameters for one analytic star layer. Spectrum colors are linear RGB;
/// the seed remains an integer, and the shared timeline resolver supplies the unit twinkle phase.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyStarsData {
    /// <summary>The number of lattice cells across the octahedral projection.</summary>
    public float Density;
    /// <summary>The peak linear emission.</summary>
    public float Brightness;
    /// <summary>The exact integer lattice seed.</summary>
    public uint Seed;
    /// <summary>The fraction of lattice cells containing a star.</summary>
    public float Sparsity;
    /// <summary>The star center's minimum distance from a cell wall, in cells.</summary>
    public float Inset;
    /// <summary>The largest angular radius as a fraction of one cell's angular pitch.</summary>
    public float RadiusFraction;
    /// <summary>The faintest star's luminosity as a fraction of the peak.</summary>
    public float LuminosityFloor;
    /// <summary>The fraction of stars that twinkle.</summary>
    public float TwinkleShare;
    /// <summary>The maximum fractional luminosity reduction during a twinkle.</summary>
    public float TwinkleDepth;
    /// <summary>The host-resolved phase in the unit interval.</summary>
    public float TwinklePhase;
    /// <summary>The faintest star's radius as a fraction of its peak radius.</summary>
    public float RadiusFloor;
    /// <summary>Zeroed scalar alignment.</summary>
    public uint Reserved;
    /// <summary>The first, warmest spectrum color.</summary>
    public Vector3 Spectrum0;
    /// <summary>Zeroed spectrum alignment.</summary>
    public uint Reserved0;
    /// <summary>The second spectrum color.</summary>
    public Vector3 Spectrum1;
    /// <summary>Zeroed spectrum alignment.</summary>
    public uint Reserved1;
    /// <summary>The third spectrum color.</summary>
    public Vector3 Spectrum2;
    /// <summary>Zeroed spectrum alignment.</summary>
    public uint Reserved2;
    /// <summary>The fourth spectrum color.</summary>
    public Vector3 Spectrum3;
    /// <summary>Zeroed spectrum alignment.</summary>
    public uint Reserved3;
    /// <summary>The fifth spectrum color.</summary>
    public Vector3 Spectrum4;
    /// <summary>Zeroed spectrum alignment.</summary>
    public uint Reserved4;
    /// <summary>The sixth spectrum color.</summary>
    public Vector3 Spectrum5;
    /// <summary>Zeroed spectrum alignment.</summary>
    public uint Reserved5;
    /// <summary>The seventh, coolest spectrum color.</summary>
    public Vector3 Spectrum6;
    /// <summary>Zeroed spectrum alignment.</summary>
    public uint Reserved6;
}
