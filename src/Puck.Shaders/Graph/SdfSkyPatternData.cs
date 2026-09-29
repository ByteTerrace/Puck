using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Shaders;

/// <summary>Resolved parameters for a checker pattern in the octahedral sky projection.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public record struct SdfSkyPatternData {
    /// <summary>The linear color of even checker cells.</summary>
    public Vector3 ColorA;
    /// <summary>The linear emission multiplier.</summary>
    public float Intensity;
    /// <summary>The linear color of odd checker cells.</summary>
    public Vector3 ColorB;
    /// <summary>The positive structural cell count across the projection.</summary>
    public uint Cells;
    /// <summary>The resolved translation in checker cells.</summary>
    public Vector2 Offset;
    /// <summary>Zeroed alignment.</summary>
    public uint Reserved0;
    /// <summary>Zeroed alignment.</summary>
    public uint Reserved1;
}
